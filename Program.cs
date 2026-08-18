using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MQTTnet;

namespace EnpalMqttBridge;

/// <summary>
/// Liest die Enpal-Box "deviceMessages"-Seite per echtem Headless-Browser
/// (Playwright) aus, inklusive der Werte, die erst nach Anhaken von
/// "Show internal values" / "Show unsupported values" sichtbar werden
/// (Batterie-Ladezustand/SOC, Batterie laden/entladen, PV-DC-Leistung ...).
/// Diese Checkboxen hängen an einer aktiven Blazor-Server-Verbindung
/// (SignalR-Circuit) und sind deshalb per einfachem HTTP-GET nicht
/// erreichbar - deshalb der "echte Browser"-Ansatz statt HTML-Scraping.
///
/// Die gefundenen Sensorwerte werden per MQTT veröffentlicht (ein JSON-
/// Payload pro Sensor unter "&lt;prefix&gt;/&lt;Sensorname&gt;"), damit
/// Symcon sie über die MQTT-Splitter-Instanz übernehmen kann (siehe
/// enpal_mqtt_receiver.php).
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var config = BridgeConfig.FromEnvironment();
        config.LogSummary();

        using var cts = new CancellationTokenSource();
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            Log("SIGTERM empfangen - fahre sauber herunter...");
            cts.Cancel();
            ctx.Cancel = true;
        });
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            Log("SIGINT empfangen - fahre sauber herunter...");
            cts.Cancel();
            ctx.Cancel = true;
        });

        var mqtt = new MqttPublisher(config);

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(config, mqtt, cts.Token);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Sitzung mit Fehler beendet, starte in {config.RestartDelaySeconds}s neu: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.RestartDelaySeconds), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await mqtt.DisposeAsync();
        Log("Beendet.");
        return 0;
    }

    /// <summary>
    /// Ein "Session"-Durchlauf: Browser starten, Checkboxen anhaken,
    /// danach in einer Schleife periodisch den aktuellen DOM-Zustand
    /// auslesen und veröffentlichen. Bricht die Schleife (Exception) ab,
    /// wird im Aufrufer eine komplett neue Sitzung gestartet (neuer
    /// Browser, neue Verbindung) - robuster als zu versuchen, eine kaputte
    /// Blazor-Verbindung zu reparieren.
    /// </summary>
    private static async Task RunSessionAsync(BridgeConfig config, MqttPublisher mqtt, CancellationToken token)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();

        Log($"Öffne {config.EnpalUrl} ...");
        await page.GotoAsync(config.EnpalUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
        });

        await RevealInternalValuesAsync(page);

        Log("Verbindung steht, beginne mit periodischem Auslesen.");

        while (!token.IsCancellationRequested)
        {
            var rows = await ReadRowsAsync(page);
            if (rows.Count == 0)
            {
                // Leere Ergebnisse deuten meist auf eine abgebrochene
                // Blazor-Verbindung hin (z.B. "blazor-error-ui" sichtbar) -
                // Exception werfen, damit der Aufrufer eine frische Sitzung
                // startet, statt stumm nichts mehr zu veröffentlichen.
                throw new InvalidOperationException("Keine Tabellenzeilen gefunden - Verbindung vermutlich verloren.");
            }

            var readings = ParseRows(rows);
            Log($"{readings.Count} von {rows.Count} Zeilen erfolgreich geparst.");

            foreach (var reading in readings)
            {
                await mqtt.PublishAsync(reading, token);
            }

            await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), token);
        }
    }

    /// <summary>
    /// Hakt alle "Show internal values" / "Show unsupported values"
    /// Checkboxen an (eine je Geräte-Karte). Das löst je einen Blazor-
    /// Server-Roundtrip aus, danach zeigt die Seite zusätzliche Sensoren
    /// wie SOC (Energy.Battery.Charge.Level) an.
    /// </summary>
    private static async Task RevealInternalValuesAsync(IPage page)
    {
        var checkboxes = page.Locator(
            "input[type=checkbox][id^='showInternal_'], input[type=checkbox][id^='showUnsupported_']");
        var count = await checkboxes.CountAsync();
        Log($"{count} Checkbox(en) für interne/nicht unterstützte Werte gefunden.");

        for (var i = 0; i < count; i++)
        {
            var checkbox = checkboxes.Nth(i);
            if (await checkbox.IsCheckedAsync())
            {
                continue;
            }
            await checkbox.CheckAsync();
            // Kleine Pause je Checkbox, damit der Circuit einzeln
            // reagieren kann statt mehrere Events zu überlappen.
            await page.WaitForTimeoutAsync(300);
        }

        // Sammelpause, bis alle Nachlade-Roundtrips durch sind.
        await page.WaitForTimeoutAsync(1_000);
    }

    /// <summary>
    /// Liest alle Tabellenzeilen mit mindestens 2 &lt;td&gt;-Zellen in
    /// einem einzigen Browser-Roundtrip aus (schneller/robuster als pro
    /// Zeile einzeln über die Playwright-API zu gehen).
    /// </summary>
    private static async Task<List<List<string>>> ReadRowsAsync(IPage page)
    {
        const string script = """
            () => {
                const rows = [];
                document.querySelectorAll('tr').forEach(tr => {
                    const tds = Array.from(tr.querySelectorAll('td'));
                    if (tds.length < 2) return;
                    rows.push(tds.map(td => td.textContent.trim().replace(/\s+/g, ' ')));
                });
                return rows;
            }
            """;

        var result = await page.EvaluateAsync<List<List<string>>>(script);
        return result ?? new List<List<string>>();
    }

    // Gleiche Logik wie im begleitenden Symcon-PHP-Skript: die Box nutzt
    // zwei Tabellenformate parallel (siehe dortige Kommentare).
    private static readonly Regex ValueRegex =
        new(@"^([\d.\-]+)\s*(Wh|kWh|kW|W|Hz|°C|%|V|A)$", RegexOptions.Compiled);

    private static readonly Regex SiteDataTimestampRegex =
        new(@"^(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2}:\d{2}(?:\.\d+)?)Z", RegexOptions.Compiled);

    private static readonly Regex TimeOnlyRegex =
        new(@"^(\d{2}:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

    private static List<SensorReading> ParseRows(List<List<string>> rows)
    {
        var readings = new List<SensorReading>();

        foreach (var cells in rows)
        {
            if (cells.Count < 3)
            {
                continue; // Notiz-/Fehlerzeile ohne Messwert (2 Zellen) - überspringen.
            }

            var name = cells[0];
            var rawValue = cells.Count > 1 ? cells[1] : string.Empty;

            DateTimeOffset? timestamp = cells.Count == 3
                ? ParseSiteDataTimestamp(cells[2])
                : cells.Count == 4
                    ? ParseTimeOnlyTimestamp(cells[2])
                    : null;

            if (timestamp is null)
            {
                continue;
            }

            var match = ValueRegex.Match(rawValue);
            if (!match.Success)
            {
                continue;
            }

            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            readings.Add(new SensorReading(name, value, match.Groups[2].Value, timestamp.Value));
        }

        return readings;
    }

    private static DateTimeOffset? ParseSiteDataTimestamp(string raw)
    {
        var match = SiteDataTimestampRegex.Match(raw.Trim());
        if (!match.Success)
        {
            return null;
        }

        var iso = $"{match.Groups[1].Value}T{match.Groups[2].Value}Z";
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : null;
    }

    private static DateTimeOffset? ParseTimeOnlyTimestamp(string raw)
    {
        var match = TimeOnlyRegex.Match(raw.Trim());
        if (!match.Success)
        {
            return null;
        }

        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var iso = $"{today}T{match.Groups[1].Value}Z";
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            return null;
        }

        // Mitternachts-Sprung korrigieren (siehe PHP-Skript für Details).
        if (dt > DateTimeOffset.UtcNow.AddSeconds(300))
        {
            dt = dt.AddDays(-1);
        }

        return dt;
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | {message}");
}

internal sealed record SensorReading(string Name, double Value, string Unit, DateTimeOffset Timestamp);

internal sealed class BridgeConfig
{
    public required string EnpalUrl { get; init; }
    public required string MqttHost { get; init; }
    public required int MqttPort { get; init; }
    public string? MqttUsername { get; init; }
    public string? MqttPassword { get; init; }
    public required string MqttClientId { get; init; }
    public required string MqttTopicPrefix { get; init; }
    public required int PollIntervalSeconds { get; init; }
    public required int RestartDelaySeconds { get; init; }

    public static BridgeConfig FromEnvironment()
    {
        return new BridgeConfig
        {
            EnpalUrl = GetEnv("ENPAL_URL", "http://10.1.2.11/deviceMessages"),
            MqttHost = GetEnv("MQTT_HOST", "localhost"),
            MqttPort = int.TryParse(GetEnv("MQTT_PORT", "1883"), out var p) ? p : 1883,
            MqttUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
            MqttPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD"),
            MqttClientId = GetEnv("MQTT_CLIENT_ID", "enpal-mqtt-bridge"),
            MqttTopicPrefix = GetEnv("MQTT_TOPIC_PREFIX", "enpal"),
            PollIntervalSeconds = int.TryParse(GetEnv("POLL_INTERVAL_SECONDS", "20"), out var i) ? i : 20,
            RestartDelaySeconds = int.TryParse(GetEnv("RESTART_DELAY_SECONDS", "15"), out var r) ? r : 15,
        };
    }

    public void LogSummary()
    {
        Console.WriteLine(
            $"Konfiguration: ENPAL_URL={EnpalUrl}, MQTT={MqttHost}:{MqttPort}, " +
            $"Topic-Prefix={MqttTopicPrefix}, Intervall={PollIntervalSeconds}s");
    }

    private static string GetEnv(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

internal sealed class MqttPublisher
{
    private readonly BridgeConfig _config;
    private readonly IMqttClient _client;

    public MqttPublisher(BridgeConfig config)
    {
        _config = config;
        _client = new MqttClientFactory().CreateMqttClient();
    }

    public async Task PublishAsync(SensorReading reading, CancellationToken token)
    {
        await EnsureConnectedAsync(token);

        var topic = $"{_config.MqttTopicPrefix}/{SanitizeTopicSegment(reading.Name)}";
        var payload = JsonSerializer.Serialize(new
        {
            value = reading.Value,
            unit = reading.Unit,
            timestamp = reading.Timestamp.ToUnixTimeSeconds(),
        });

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, token);
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_client.IsConnected)
        {
            return;
        }

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_config.MqttHost, _config.MqttPort)
            .WithClientId(_config.MqttClientId)
            .WithCleanSession();

        if (!string.IsNullOrEmpty(_config.MqttUsername))
        {
            optionsBuilder = optionsBuilder.WithCredentials(_config.MqttUsername, _config.MqttPassword);
        }

        await _client.ConnectAsync(optionsBuilder.Build(), token);
    }

    private static string SanitizeTopicSegment(string name) =>
        name.Replace('+', '_').Replace('#', '_').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }
        _client.Dispose();
    }
}
