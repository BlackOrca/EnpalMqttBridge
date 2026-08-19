using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using MQTTnet;

namespace EnpalMqttBridge;

/// <summary>
/// Liest die Enpal-Box "collector"-Seite per echtem Headless-Browser
/// (Playwright) aus: Klick auf "Load Current Collector State" liefert ein
/// vollstaendiges JSON mit allen Sensorwerten (inkl. Batterie-SOC, Batterie
/// laden/entladen, PV-DC-Leistung ...) in einem Monaco-Editor. Der Button
/// haengt an einer aktiven Blazor-Server-Verbindung (SignalR-Circuit) und
/// ist deshalb per einfachem HTTP-GET nicht erreichbar - deshalb der
/// "echte Browser"-Ansatz statt HTML-Scraping.
///
/// Die gefundenen Sensorwerte werden per MQTT veroeffentlicht (ein JSON-
/// Payload pro Sensor unter "&lt;prefix&gt;/&lt;Sensorname&gt;"). Die
/// Sensornamen entsprechen denen der bisherigen "deviceMessages"-Tabelle
/// (z.B. "Energy.Battery.Charge.Level"). Zusaetzlich veroeffentlicht die
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
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
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
    /// Ein "Session"-Durchlauf: Browser starten, Collector-Seite oeffnen,
    /// danach in einer Schleife periodisch per "Load Current Collector
    /// State"-Button den zuletzt gesammelten Stand abrufen und
    /// veroeffentlichen. Bewusst der passive "Load State"-Button statt
    /// "Run Collection Cycle" - liest nur aus, was die Box ohnehin schon
    /// eingesammelt hat, statt bei jedem Poll zusaetzlich Geraete-
    /// Kommunikation zu erzwingen. Bricht die Schleife (Exception) ab,
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

        Log($"Öffne {config.EnpalUrl} ...");
        await page.GotoAsync(config.EnpalUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000,
        });
        await page.Locator("#collectorLoadStateButton").WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });

        Log("Verbindung steht, beginne mit periodischem Auslesen.");

        while (!token.IsCancellationRequested)
        {
            var json = await LoadCollectorStateAsync(page);
            var readings = ParseCollectorState(json);
            Log($"{readings.Count} Sensorwerte aus Collector-JSON geparst.");

            foreach (var reading in readings)
            {
                await mqtt.PublishAsync(reading, token);
            }

            await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), token);
        }
    }

    // Liest den Monaco-Editor-Inhalt der Collector-Seite aus - siehe
    // window.monaco JS-API, mit der die Seite den Editor selbst befuellt.
    private const string GetEditorValueScript =
        "() => (window.monaco && monaco.editor.getModels().length) ? monaco.editor.getModels()[0].getValue() : null";

    /// <summary>
    /// Klickt "Load Current Collector State" und wartet, bis der Monaco-
    /// Editor daraufhin (per Blazor-Server-Roundtrip) mit JSON befuellt
    /// wurde.
    /// </summary>
    private static async Task<string> LoadCollectorStateAsync(IPage page)
    {
        await page.Locator("#collectorLoadStateButton").ClickAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.WaitForTimeoutAsync(500);
            var content = await page.EvaluateAsync<string?>(GetEditorValueScript);
            if (!string.IsNullOrWhiteSpace(content) && content.TrimStart().StartsWith('{'))
            {
                return content;
            }
        }

        throw new InvalidOperationException("Kein Collector-JSON erhalten - Verbindung vermutlich verloren.");
    }

    private static readonly JsonSerializerOptions CollectorJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Extrahiert alle Sensorwerte aus den Geraete-Sektionen
    /// (DeviceCollections[].numberDataPoints/textDataPoints) des Collector-
    /// JSON. Mehrere Geraete spiegeln teils dieselben Werte (z.B. Batterie-
    /// Werte tauchen sowohl bei "Battery" als auch bei "Inverter" auf) -
    /// bei doppelten Sensornamen gewinnt der letzte Eintrag, die Werte sind
    /// ohnehin identisch.
    /// </summary>
    private static List<SensorReading> ParseCollectorState(string json)
    {
        var state = JsonSerializer.Deserialize<CollectorState>(json, CollectorJsonOptions);
        var readings = new Dictionary<string, SensorReading>();

        foreach (var device in state?.DeviceCollections ?? [])
        {
            foreach (var (name, point) in device.NumberDataPoints ?? [])
            {
                readings[name] = new SensorReading(name, point.Value, point.Unit, point.Timestamp);
            }

            foreach (var (name, point) in device.TextDataPoints ?? [])
            {
                readings[name] = new SensorReading(name, point.Value, point.Unit, point.Timestamp);
            }
        }

        return [.. readings.Values];
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | {message}");
}

internal sealed record SensorReading(string Name, object Value, string Unit, DateTimeOffset Timestamp);

internal sealed record CollectorState(
    [property: JsonPropertyName("DeviceCollections")] List<DeviceCollection>? DeviceCollections);

internal sealed record DeviceCollection(
    [property: JsonPropertyName("numberDataPoints")] Dictionary<string, NumberDataPoint>? NumberDataPoints,
    [property: JsonPropertyName("textDataPoints")] Dictionary<string, TextDataPoint>? TextDataPoints);

internal sealed record NumberDataPoint(
    [property: JsonPropertyName("timeStampUtcOfMeasurement")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("value")] double Value);

internal sealed record TextDataPoint(
    [property: JsonPropertyName("timeStampUtcOfMeasurement")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("value")] string Value);

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

    public static BridgeConfig FromEnvironment()
    {
        return new BridgeConfig
        {
            EnpalUrl = GetEnv("ENPAL_URL", "http://10.1.2.11/collector"),
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
            $"Konfiguration: ENPAL_URL={EnpalUrl}, MQTT={MqttHost}:{MqttPort}, " +
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
            "Celcius" => ("°C", "temperature", "measurement"),
            "Percent" when reading.Name == "Energy.Battery.Charge.Level" => ("%", "battery", "measurement"),
            "Percent" => ("%", null, "measurement"),
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
