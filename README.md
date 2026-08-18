# Enpal MQTT Bridge

Liest die Enpal-Box-Seite `collector` per echtem Headless-Browser
(Playwright/Chromium) aus: ein Klick auf „Load Current Collector State"
liefert ein vollständiges JSON mit allen Sensorwerten - inklusive der
Werte, die auf der `deviceMessages`-Seite erst nach Anhaken von „Show
internal values" / „Show unsupported values" sichtbar wären
(Batterie-Ladezustand/SOC, Batterie laden/entladen, PV-DC-Leistung, ...).
Dieser Button hängt an einer aktiven Blazor-Server-Verbindung
(SignalR-Circuit) und ist deshalb per einfachem HTTP-GET/Scraping nicht
erreichbar - deshalb hier ein echter Browser statt eines HTML-Parsers.
Die Sensornamen im JSON (z.B. `Energy.Battery.Charge.Level`) entsprechen
denen der bisherigen `deviceMessages`-Tabelle.

Die gefundenen Werte werden per MQTT veröffentlicht. Zusätzlich sendet die
Bridge für jeden Sensor eine Home-Assistant-MQTT-Discovery-Konfiguration
(retained), sodass z.B. das HA-Discovery-Modul in Symcon die passenden
Objekte inkl. Name, Einheit und Gerätklasse automatisch anlegt - siehe
[Home-Assistant-MQTT-Discovery](#home-assistant-mqtt-discovery).

## Wichtiger Hinweis zum Testgrad

- **Ende-zu-Ende gegen die echte Box getestet:** Läuft auf .NET 10
  (SDK/Runtime), Microsoft.Playwright 1.62.0 und MQTTnet 5.2.0.
  `dotnet build`/`dotnet publish`, `docker build` und ein kompletter Lauf
  gegen eine echte Enpal-Box + einen lokalen Test-Broker wurden erfolgreich
  durchgeführt: der „Load Current Collector State"-Button liefert
  zuverlässig alle ~69 Sensorwerte (Zahlen wie Text) pro Zyklus, und die
  MQTT-Payloads kommen korrekt formatiert an.

## 1. Bridge bauen und starten

```bash
cp .env.example .env
# .env anpassen: ENPAL_URL, MQTT_HOST, ggf. Zugangsdaten

docker compose up -d --build
docker compose logs -f
```

Beim Start öffnet die Bridge die Collector-Seite der Enpal-Box und klickt
danach alle `POLL_INTERVAL_SECONDS` Sekunden erneut auf „Load Current
Collector State", um den zuletzt von der Box gesammelten Sensorstand als
JSON abzuholen und zu veröffentlichen. Das ist bewusst der passive
„Load State"-Button statt „Run Collection Cycle" - die Bridge erzwingt
also keine zusätzliche Geräte-Kommunikation, sondern liest nur aus, was
die Box ohnehin laufend selbst einsammelt.

Bricht die Verbindung ab (z.B. Netzwerkproblem, Box-Neustart), startet
die Bridge nach `RESTART_DELAY_SECONDS` automatisch eine komplett neue
Sitzung (neuer Browser, neue Verbindung).

## 2. Werte manuell prüfen

```bash
mosquitto_sub -h <MQTT_HOST> -t 'enpal/#' -v
```

Jede Nachricht ist ein JSON-Objekt, z.B.:

```text
enpal/Energy.Battery.Charge.Level {"value":96,"unit":"Percent","timestamp":1734000005}
```

## Home-Assistant-MQTT-Discovery

Für jeden Sensor wird beim ersten Auftreten (pro Bridge-Neustart) einmalig
eine retained Discovery-Config veröffentlicht:

```text
homeassistant/sensor/enpal-mqtt-bridge/energy_battery_charge_level/config
{"name":"Energy Battery Charge Level","unique_id":"enpal-mqtt-bridge_energy_battery_charge_level",
 "state_topic":"enpal/Energy.Battery.Charge.Level","value_template":"{{ value_json.value }}",
 "availability_topic":"enpal/status","payload_available":"online","payload_not_available":"offline",
 "device":{"identifiers":["enpal-mqtt-bridge"],"name":"Enpal Solar","manufacturer":"Enpal","model":"Solar Box"},
 "unit_of_measurement":"%","device_class":"battery","state_class":"measurement"}
```

Alle Sensoren landen dadurch unter einem gemeinsamen Gerät „Enpal Solar".
`unit_of_measurement`/`device_class`/`state_class` werden automatisch aus
der von der Box gelieferten Einheit abgeleitet (W/kW → `power`, Wh/kWh →
`energy` + `total_increasing`, V → `voltage`, A → `current`, Hz →
`frequency`, Celcius → `temperature`, der Batterie-Ladestand zusätzlich als
`battery`); Text-Sensoren (LTE-Status etc.) bleiben unklassifiziert.

Die Bridge setzt außerdem einen Verfügbarkeits-Status unter `enpal/status`
(`online` beim Verbinden, `offline` per MQTT-Last-Will bzw. beim sauberen
Beenden) - Discovery-Objekte zeigen sich dadurch in Symcon/Home Assistant
automatisch als "nicht verfügbar", sobald die Bridge nicht läuft.

Per `HA_DISCOVERY_ENABLED=false` lässt sich die Discovery-Veröffentlichung
komplett abschalten, `HA_DISCOVERY_PREFIX` ändert den Topic-Präfix (Default
`homeassistant`, wie vom HA-Discovery-Modul in Symcon erwartet).

## Aufbau

```text
Dockerfile               Multi-Stage-Build: .NET SDK zum Bauen,
                          Playwright-Runtime-Image (mit vorinstalliertem
                          Chromium) zum Ausführen.
docker-compose.yml        Startet den Container mit den Werten aus .env.
.env.example               Vorlage für die Konfiguration.
EnpalMqttBridge.csproj     Projektdatei (Microsoft.Playwright, MQTTnet).
Program.cs                 Kompletter Bridge-Code (Browser-Steuerung,
                            Collector-JSON-Parsing, MQTT-Publisher,
                            Reconnect-Logik).
VERSION                    Aktuelle Image-Version (SemVer), wird von
                            build-and-push.ps1 automatisch hochgezählt.
build-and-push.ps1         Baut das Docker-Image mit hochgezählter
                            Version und pusht es zu ghcr.io.
```

## Falls Enpal die Seite nochmal ändert

Die Bridge klickt gezielt auf `#collectorLoadStateButton` auf der
`collector`-Seite und liest danach den Inhalt des Monaco-Editors aus
(`window.monaco.editor.getModels()[0].getValue()`) - das erwartete JSON
enthält unter `DeviceCollections[].numberDataPoints` /
`DeviceCollections[].textDataPoints` je Gerät die Sensorwerte (Name ->
`{timeStampUtcOfMeasurement, unit, value}`). Ändert sich die Seite oder
das JSON-Format erneut, zuerst per `docker compose logs -f` schauen, wie
viele Sensorwerte pro Zyklus erkannt werden, und bei Bedarf den Selektor/
das JSON-Mapping in `Program.cs` anpassen.
