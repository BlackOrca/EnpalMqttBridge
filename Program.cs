using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MQTTnet;

namespace EnpalMqttBridge;

/// <summary>
/// Liest die Enpal-Box "deviceMessages"-Seite per echtem Headless-Browser
/// (Playwright) aus. Die Seite zeigt pro Geraet (SiteData, Battery,
/// Inverter, ...) eine Tabelle mit allen Sensorwerten, die sich ueber eine
/// Blazor-Server-Verbindung (SignalR-Circuit) live aktualisiert. Erst mit
/// angehaktem "Show internal values" erscheinen auch Batterie-SOC,
/// Batterie laden/entladen, PV-DC-Leistung usw. - diese Checkboxen und
/// die Live-Aktualisierung gibt es nur mit aktivem Circuit, deshalb der
/// "echte Browser"-Ansatz statt HTML-Scraping.
///
/// Bis Firmware 8.51.4 (09/2026) gab es dafuer die "collector"-Seite mit
/// einem JSON-Export ("Load Current Collector State"); die hat Enpal
/// entfernt ("/collector" leitet seitdem auf "/" um).
///
/// Die gefundenen Sensorwerte werden per MQTT veroeffentlicht (ein JSON-
/// Payload pro Sensor unter "&lt;prefix&gt;/&lt;Sensorname&gt;", z.B.
/// "Energy.Battery.Charge.Level"). Zusaetzlich veroeffentlicht die
/// Bridge (sofern nicht per HA_DISCOVERY_ENABLED deaktiviert) fuer jeden
/// Sensor eine Home-Assistant-MQTT-Discovery-Konfiguration unter
/// "&lt;HA_DISCOVERY_PREFIX&gt;/sensor/&lt;MQTT_CLIENT_ID&gt;/&lt;objectId&gt;/config",
/// damit das HA-Discovery-Modul in Symcon (oder Home Assistant selbst) die
/// Objekte automatisch mit Name, Einheit und Geraeteklasse anlegt.
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
            // Beim Herunterfahren beendet das Signal oft auch Chromium, bevor
            // die Sitzung selbst den Abbruch bemerkt - dann kommt statt
            // OperationCanceledException z.B. TargetClosedException. Das ist
            // kein Sitzungsfehler.
            catch (Exception) when (cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Sitzung mit Fehler beendet, starte in {config.RestartDelaySeconds}s neu: {ex}");
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
    /// Ein "Session"-Durchlauf: Browser starten, deviceMessages-Seite
    /// oeffnen, alle Werte einblenden und danach in einer Schleife
    /// periodisch den (von der Box live aktualisierten) Tabelleninhalt
    /// auslesen und veroeffentlichen. Die Bridge liest dabei nur, was die
    /// Box ohnehin laufend selbst einsammelt - sie erzwingt keine
    /// zusaetzliche Geraete-Kommunikation. Bricht die Schleife (Exception)
    /// ab, wird im Aufrufer eine komplett neue Sitzung gestartet (neuer
    /// Browser, neue Verbindung) - robuster als zu versuchen, eine kaputte
    /// Blazor-Verbindung zu reparieren.
    /// </summary>
    private static async Task RunSessionAsync(BridgeConfig config, MqttPublisher mqtt, CancellationToken token)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // Im Container laeuft Chromium als root (kein eigener USER im
            // Dockerfile) und der Standard-/dev/shm ist mit 64 MB oft zu
            // klein - beides fuehrt sonst zu Abstuerzen beim Browser-Start.
            Args = ["--no-sandbox", "--disable-dev-shm-usage"],
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();

        // Die Seite kommt zunaechst serverseitig vorgerendert (statisches
        // HTML) - Klicks auf die Checkboxen gehen verloren, bis der Blazor-
        // Circuit per WebSocket steht. "NetworkIdle" wartet nicht auf
        // WebSockets, deshalb hier explizit auf die erste Antwort des
        // Blazor-Hubs warten.
        var circuitConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.WebSocket += (_, webSocket) =>
        {
            if (webSocket.Url.Contains("/_blazor", StringComparison.OrdinalIgnoreCase))
            {
                webSocket.FrameReceived += (_, _) => circuitConnected.TrySetResult();
            }
        };

        Log($"Öffne {config.DeviceMessagesUrl} ...");
        await page.GotoAsync(config.DeviceMessagesUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
        });
        await page.Locator("input[id^=showInternal_]").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await circuitConnected.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        // Erster Interaktiv-Render nach dem Circuit-Handshake.
        await page.WaitForTimeoutAsync(1000);

        Log("Verbindung steht, beginne mit periodischem Auslesen.");

        // Stirbt der Blazor-Circuit, ohne dass die Seite das merkt, bleiben
        // die Tabellenwerte einfach stehen - das wird hier an nicht mehr
        // fortschreitenden Zeitstempeln erkannt und fuehrt zum Neustart.
        var staleAfter = TimeSpan.FromSeconds(Math.Max(300, 3 * config.PollIntervalSeconds));
        var newestTimestamp = DateTimeOffset.MinValue;
        var lastProgress = DateTimeOffset.UtcNow;

        while (!token.IsCancellationRequested)
        {
            await EnsureAllValuesVisibleAsync(page);
            var readings = await ReadDeviceMessagesAsync(page);
            if (readings.Count == 0)
            {
                throw new InvalidOperationException(
                    "Keine Sensorwerte in den deviceMessages-Tabellen gefunden - Verbindung verloren oder Seite geändert.");
            }
            Log($"{readings.Count} Sensorwerte aus deviceMessages gelesen.");

            var newest = readings.Max(r => r.Timestamp);
            if (newest > newestTimestamp)
            {
                newestTimestamp = newest;
                lastProgress = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - lastProgress > staleAfter)
            {
                throw new InvalidOperationException(
                    $"Seit {staleAfter.TotalMinutes:0} min keine neuen Werte (neuester Zeitstempel {newestTimestamp:u}) - Verbindung vermutlich tot.");
            }

            foreach (var reading in readings)
            {
                await mqtt.PublishAsync(reading, token);
            }

            await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), token);
        }
    }

    // Alle Checkboxen ausser "Show unsupported values" (blendet nur Zeilen
    // ohne Wert ein): Geraeteauswahl (SiteData, Battery, ...) und "Show
    // internal values" pro Geraet.
    private const string UncheckedValueCheckboxSelector =
        "input[type=checkbox]:not([id^=showUnsupported_]):not(:checked)";

    /// <summary>
    /// Hakt alle noch nicht gesetzten Checkboxen an. Wird vor jedem Poll
    /// aufgerufen, da die Seite nach einem Verbindungsabbruch per eigenem
    /// Reconnect-Handler neu laedt und die Checkboxen dann wieder leer sind.
    /// Nach einem Klick immer wieder das erste ungesetzte Element neu
    /// suchen, da das Anhaken eines Geraets weitere Checkboxen einfuegt.
    /// </summary>
    private static async Task EnsureAllValuesVisibleAsync(IPage page)
    {
        var uncheckedBoxes = page.Locator(UncheckedValueCheckboxSelector);
        var changed = false;
        for (var i = 0; i < 50 && await uncheckedBoxes.CountAsync() > 0; i++)
        {
            await uncheckedBoxes.First.CheckAsync();
            changed = true;
        }

        if (changed)
        {
            // Die Seite zeichnet die Tabellen erst beim naechsten Datenpaket
            // der Box neu (je nach Geraet 1-20s) - bis dahin fehlen die
            // internen Werte bzw. frisch verbundene Geraete stehen noch auf
            // "No messages available". Die ersten ein, zwei Polls nach dem
            // Start veroeffentlichen deshalb ggf. nur einen Teil der Werte.
            await page.WaitForTimeoutAsync(2000);
        }
    }

    // Liefert pro Tabellenzeile [Name, Wert, Zeitstempel]. Zeilen ohne Wert
    // ("missing: ...", "unsupported: ...") bestehen nur aus Name + einer
    // Notiz-Zelle mit colspan und fallen durch den Filter.
    private const string ReadTableRowsScript = """
        () => [...document.querySelectorAll('table tbody tr')]
            .map(tr => [...tr.cells].map(td => td.innerText.trim()))
            .filter(cells => cells.length >= 3 && cells[0] && cells[1])
            .map(cells => cells.slice(0, 3))
        """;

    /// <summary>
    /// Liest alle Sensorwerte aus den Geraete-Tabellen. Mehrere Geraete
    /// spiegeln teils dieselben Werte (z.B. Power.AC.Max.Battery) - bei
    /// doppelten Sensornamen gewinnt der letzte Eintrag, die Werte sind
    /// ohnehin identisch.
    /// </summary>
    private static async Task<List<SensorReading>> ReadDeviceMessagesAsync(IPage page)
    {
        var rows = await page.EvaluateAsync<string[][]>(ReadTableRowsScript);
        var nowUtc = DateTimeOffset.UtcNow;
        var readings = new Dictionary<string, SensorReading>();

        foreach (var row in rows ?? [])
        {
            var (value, unit) = ParseValue(row[1]);
            readings[row[0]] = new SensorReading(row[0], value, unit, ParseTimestamp(row[2], nowUtc));
        }

        return [.. readings.Values];
    }

    // Zahl mit optionaler, direkt angehaengter Einheit: "389W", "-0.6A",
    // "75%", "46.8°C", "17299.24kWh", "-101". Alles andere (z.B.
    // "Running (2)", Seriennummern, ISO-Datumswerte) bleibt Text.
    private static readonly Regex NumberWithUnit = new(@"^(-?\d+(?:\.\d+)?)\s*([A-Za-z%°]{0,4})$");

    private static (object Value, string Unit) ParseValue(string text)
    {
        var match = NumberWithUnit.Match(text);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return (number, match.Groups[2].Value);
        }

        return (text, "");
    }

    /// <summary>
    /// SiteData liefert vollstaendige UTC-Zeitstempel ("2026-09-30
    /// 17:17:29.398Z"), die Geraete-Tabellen nur die UTC-Uhrzeit
    /// ("17:17:29.38") - die wird auf heute gesetzt bzw. auf gestern, falls
    /// sie sonst (kurz nach Mitternacht) in der Zukunft laege.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string text, DateTimeOffset nowUtc)
    {
        if (TimeSpan.TryParseExact(text, [@"hh\:mm\:ss\.FFF", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var timeOfDay))
        {
            var timestamp = new DateTimeOffset(nowUtc.UtcDateTime.Date + timeOfDay, TimeSpan.Zero);
            return timestamp > nowUtc.AddMinutes(5) ? timestamp.AddDays(-1) : timestamp;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var full))
        {
            return full;
        }

        return nowUtc;
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | {message}");
}

internal sealed record SensorReading(string Name, object Value, string Unit, DateTimeOffset Timestamp);

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
    public required int MqttOperationTimeoutSeconds { get; init; }
    public required bool HaDiscoveryEnabled { get; init; }
    public required string HaDiscoveryPrefix { get; init; }

    // ENPAL_URL darf die Basis-URL der Box oder eine beliebige Seite darauf
    // sein - ein Pfad (z.B. das fruehere "/collector" aus aelteren .env-
    // Dateien) wird ignoriert, gelesen wird immer "/deviceMessages".
    public string DeviceMessagesUrl => new Uri(new Uri(EnpalUrl), "/deviceMessages").ToString();

    public static BridgeConfig FromEnvironment()
    {
        return new BridgeConfig
        {
            EnpalUrl = GetEnv("ENPAL_URL", "http://10.1.2.11"),
            MqttHost = GetEnv("MQTT_HOST", "localhost"),
            MqttPort = int.TryParse(GetEnv("MQTT_PORT", "1883"), out var p) ? p : 1883,
            MqttUsername = Environment.GetEnvironmentVariable("MQTT_USERNAME"),
            MqttPassword = Environment.GetEnvironmentVariable("MQTT_PASSWORD"),
            MqttClientId = GetEnv("MQTT_CLIENT_ID", "enpal-mqtt-bridge"),
            MqttTopicPrefix = GetEnv("MQTT_TOPIC_PREFIX", "enpal"),
            PollIntervalSeconds = int.TryParse(GetEnv("POLL_INTERVAL_SECONDS", "20"), out var i) ? i : 20,
            RestartDelaySeconds = int.TryParse(GetEnv("RESTART_DELAY_SECONDS", "15"), out var r) ? r : 15,
            MqttOperationTimeoutSeconds = int.TryParse(GetEnv("MQTT_OPERATION_TIMEOUT_SECONDS", "20"), out var m) ? m : 20,
            HaDiscoveryEnabled = bool.TryParse(GetEnv("HA_DISCOVERY_ENABLED", "true"), out var d) ? d : true,
            HaDiscoveryPrefix = GetEnv("HA_DISCOVERY_PREFIX", "homeassistant"),
        };
    }

    public void LogSummary()
    {
        Console.WriteLine(
            $"Konfiguration: Seite={DeviceMessagesUrl}, MQTT={MqttHost}:{MqttPort}, " +
            $"Topic-Prefix={MqttTopicPrefix}, Intervall={PollIntervalSeconds}s, " +
            $"MQTT-Operation-Timeout={MqttOperationTimeoutSeconds}s, " +
            $"HA-Discovery={(HaDiscoveryEnabled ? $"an ({HaDiscoveryPrefix})" : "aus")}");
    }

    private static string GetEnv(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

internal sealed class MqttPublisher
{
    private readonly BridgeConfig _config;
    private readonly IMqttClient _client;
    private readonly HashSet<string> _discoveredSensors = [];
    private readonly string _statusTopic;

    public MqttPublisher(BridgeConfig config)
    {
        _config = config;
        _client = new MqttClientFactory().CreateMqttClient();
        _statusTopic = $"{_config.MqttTopicPrefix}/status";
    }

    public async Task PublishAsync(SensorReading reading, CancellationToken token)
    {
        await EnsureConnectedAsync(token);

        if (_config.HaDiscoveryEnabled && _discoveredSensors.Add(reading.Name))
        {
            await PublishDiscoveryConfigAsync(reading, token);
        }

        var topic = $"{_config.MqttTopicPrefix}/{SanitizeTopicSegment(reading.Name)}";
        var payload = JsonSerializer.Serialize(new
        {
            value = reading.Value,
            unit = reading.Unit,
            timestamp = reading.Timestamp.ToUnixTimeSeconds(),
        });

        await PublishRawAsync(topic, payload, token);
    }

    /// <summary>
    /// Veroeffentlicht die Home-Assistant-MQTT-Discovery-Konfiguration fuer
    /// einen Sensor (rueckwirkungskompatibel zum HA-Discovery-Modul in
    /// Symcon, das dieselben retained Config-Topics auswertet). Wird pro
    /// Sensorname nur einmal pro Prozesslaufzeit gesendet - der Broker haelt
    /// die restlichen Nachrichten ohnehin retained vor.
    /// </summary>
    private async Task PublishDiscoveryConfigAsync(SensorReading reading, CancellationToken token)
    {
        var objectId = ToObjectId(reading.Name);
        var (unit, deviceClass, stateClass) = ClassifyReading(reading);

        var payload = new Dictionary<string, object>
        {
            ["name"] = reading.Name.Replace('.', ' '),
            ["unique_id"] = $"{_config.MqttClientId}_{objectId}",
            ["state_topic"] = $"{_config.MqttTopicPrefix}/{SanitizeTopicSegment(reading.Name)}",
            ["value_template"] = "{{ value_json.value }}",
            ["availability_topic"] = _statusTopic,
            ["payload_available"] = "online",
            ["payload_not_available"] = "offline",
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { _config.MqttClientId },
                ["name"] = "Enpal Solar",
                ["manufacturer"] = "Enpal",
                ["model"] = "Solar Box",
            },
        };

        if (unit is not null)
        {
            payload["unit_of_measurement"] = unit;
        }
        if (deviceClass is not null)
        {
            payload["device_class"] = deviceClass;
        }
        if (stateClass is not null)
        {
            payload["state_class"] = stateClass;
        }

        var configTopic = $"{_config.HaDiscoveryPrefix}/sensor/{_config.MqttClientId}/{objectId}/config";
        await PublishRawAsync(configTopic, JsonSerializer.Serialize(payload), token);
    }

    // Ordnet einer Box-Einheit die passende(n) Home-Assistant-Metadaten zu.
    // Textwerte (LTE-Status etc.) bleiben unklassifiziert - ganz normale
    // Text-Sensoren ohne Einheit/Geraeteklasse.
    private static (string? Unit, string? DeviceClass, string? StateClass) ClassifyReading(SensorReading reading)
    {
        if (reading.Value is string)
        {
            return (null, null, null);
        }

        return reading.Unit switch
        {
            "W" or "kW" => (reading.Unit, "power", "measurement"),
            "Wh" or "kWh" => (reading.Unit, "energy", "total_increasing"),
            "V" => ("V", "voltage", "measurement"),
            "A" => ("A", "current", "measurement"),
            "Hz" => ("Hz", "frequency", "measurement"),
            "°C" => ("°C", "temperature", "measurement"),
            "%" when reading.Name == "Energy.Battery.Charge.Level" => ("%", "battery", "measurement"),
            "%" => ("%", null, "measurement"),
            _ => (null, null, null),
        };
    }

    // Home-Assistant-Discovery-Objekt-IDs duerfen nur a-z/0-9/_ enthalten.
    private static string ToObjectId(string sensorName) =>
        new(sensorName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (_client.IsConnected)
        {
            return;
        }

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_config.MqttHost, _config.MqttPort)
            .WithClientId(_config.MqttClientId)
            // MQTTnet verwendet standardmaessig MQTT 5.0.0 - Symcons
            // MQTT-Server-Modul (und viele andere einfache/eingebettete
            // Broker) sprechen aber nur MQTT 3.1.1. Ein v5-CONNECT-Paket
            // gegen so einen Broker wird dort offenbar nicht als Fehler
            // abgelehnt, sondern beantwortet gar nicht erst - genau das
            // "TCP offen, aber MQTT-Handshake haengt"-Verhalten, das wir
            // beobachtet haben.
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithWillTopic(_statusTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(_config.MqttUsername))
        {
            optionsBuilder = optionsBuilder.WithCredentials(_config.MqttUsername, _config.MqttPassword);
        }

        await WithTimeoutAsync(
            ct => _client.ConnectAsync(optionsBuilder.Build(), ct),
            "Verbindungsaufbau",
            token);
        await PublishRawAsync(_statusTopic, "online", token);
    }

    private async Task PublishRawAsync(string topic, string payload, CancellationToken token)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await WithTimeoutAsync(
            ct => _client.PublishAsync(message, ct),
            $"Publish auf '{topic}'",
            token);
    }

    /// <summary>
    /// MQTTnet-Operationen (Connect/Publish) haengen im Feld beobachtet
    /// teils stunden- statt sekundenlang fest (z.B. wenn ein TCP-Handshake
    /// zum Broker nie sauber abgelehnt, aber auch nie beantwortet wird) -
    /// das eingebaute MQTTnet-Timeout (Default 100s) greift dabei nicht
    /// zuverlaessig, da es offenbar nicht die komplette Verbindungs-
    /// aufbauphase abdeckt. Deshalb hier ein eigener, garantierter
    /// Timeout: bricht per CancellationToken ab (das entspricht genau dem
    /// Verhalten, das ein SIGTERM ohnehin schon zuverlaessig ausloest -
    /// nur eben nach MqttOperationTimeoutSeconds statt erst beim
    /// Herunterfahren) und wirft danach eine aussagekraeftige Exception,
    /// die die Session-Restart-Logik in Main() greifen laesst.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> action, string operationName, CancellationToken token)
    {
        var timeout = TimeSpan.FromSeconds(_config.MqttOperationTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            return await action(cts.Token);
        }
        // MQTTnet wandelt eine waehrend Connect/Authenticate per Cancellation
        // abgebrochene Operation nicht in ein sauberes OperationCanceledException
        // um, sondern in eine MqttConnectingFailedException/-CommunicationException
        // ("Connection closed") - identisch zu dem, was ein echter Broker bei
        // z.B. abgelehnter Authentifizierung werfen wuerde. Deshalb hier statt
        // eines Typ-Filters ueber cts.IsCancellationRequested pruefen, ob
        // *unser* Timeout (nicht der externe token) die Ursache war, und das im
        // Log unmissverstaendlich von einer echten Broker-Ablehnung abgrenzen.
        catch (Exception ex) when (cts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MQTT '{operationName}' nach {timeout.TotalSeconds:0}s abgebrochen - Broker antwortet nicht " +
                $"(kein echter Ablehnungsgrund, reines Timeout). Letzte Exception dabei: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    private static string SanitizeTopicSegment(string name) =>
        name.Replace('+', '_').Replace('#', '_').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            await PublishRawAsync(_statusTopic, "offline", CancellationToken.None);
            await _client.DisconnectAsync();
        }
        _client.Dispose();
    }
}
