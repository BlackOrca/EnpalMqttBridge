# Enpal MQTT Bridge

Liest die Enpal-Box-Seite `deviceMessages` per echtem Headless-Browser
(Playwright/Chromium) aus: die Seite zeigt pro Gerät (SiteData, Battery,
IoTEdgeDevice, PowerSensor, Inverter) eine Tabelle mit allen
Sensorwerten. Die Bridge hakt dort „Show internal values" an, damit auch
Batterie-Ladezustand/SOC, Batterie laden/entladen, PV-DC-Leistung usw.
erscheinen, und liest die Tabellen danach periodisch aus. Checkboxen und
Live-Aktualisierung hängen an einer aktiven Blazor-Server-Verbindung
(SignalR-Circuit) und sind deshalb per einfachem HTTP-GET/Scraping nicht
erreichbar - deshalb hier ein echter Browser statt eines HTML-Parsers.

> Bis Firmware **Solar Rel. 8.51.4** (ausgerollt am 30.09.2026) las die
> Bridge stattdessen die Seite `collector` („Load Current Collector
> State"). Die hat Enpal mit diesem Update entfernt, `/collector` leitet
> seitdem auf `/` um. Sensornamen (z.B. `Energy.Battery.Charge.Level`)
> sind unverändert; die Einheiten im Payload lauten jetzt so, wie die
> Seite sie anzeigt (`%` statt `Percent`, `°C` statt `Celcius`).

Die gefundenen Werte werden per MQTT veröffentlicht. Zusätzlich sendet die
Bridge für jeden Sensor eine Home-Assistant-MQTT-Discovery-Konfiguration
(retained), sodass z.B. das HA-Discovery-Modul in Symcon die passenden
Objekte inkl. Name, Einheit und Gerätklasse automatisch anlegt - siehe
[Home-Assistant-MQTT-Discovery](#home-assistant-mqtt-discovery).

## Wichtiger Hinweis zum Testgrad

- **Ende-zu-Ende gegen die echte Box getestet (Firmware 8.51.4):** Läuft
  auf .NET 10 (SDK/Runtime), Microsoft.Playwright 1.62.0 und MQTTnet 5.2.0.
  Ein kompletter Lauf gegen eine echte Enpal-Box + einen lokalen
  Test-Broker liefert ~119 Sensorwerte (Zahlen wie Text) pro Zyklus, die
  sich zwischen den Zyklen live aktualisieren, und die MQTT-Payloads kommen
  korrekt formatiert an.
- **Proxmox-LXC (`lxc/`) - teilweise getestet:** `install.sh` wurde in
  einem frischen Debian-12-Container (x64) getestet (Neuinstallation und
  Update, Bridge als Dienstbenutzer gegen die echte Box); `create-lxc.sh`
  (`pct`/`pveam`-Teil) ist noch nicht auf einem echten Proxmox-Host gelaufen.
- **linux/arm64 (z.B. Raspberry Pi 3, 64-bit OS) - Build verifiziert,
  Laufzeit nicht auf echter Hardware getestet:** Das Image wird als
  Multi-Platform-Manifest (amd64 + arm64) gebaut, siehe
  [Raspberry Pi / arm64](#raspberry-pi--arm64). Der arm64-Build wurde
  erfolgreich durchgeführt und der arm64-Chromium im Playwright-Image
  läuft eigenstaendig nachweislich. Ein voller Laufzeittest war nur per
  QEMU-Emulation auf einem amd64-Rechner möglich, und dort stürzt QEMU
  selbst (nicht die Bridge) beim Zusammenspiel von Node-Treiber und
  Chromium ab (Segfault in QEMU) - ein bekanntes Limit von
  QEMU-User-Mode-Emulation für solche Workloads, keine .NET/Playwright-
  Fehlermeldung. Bitte einmal auf echter Pi-Hardware testen, bevor du dich
  darauf verlässt (auf echtem Silizium gibt es keine Emulation mehr, die
  gefundenen Fehlerquellen - falsches Playwright-Treiber-Binary,
  fehlender `--no-sandbox`/`--disable-dev-shm-usage` - sind bereits
  behoben).

## 1. Bridge bauen und starten

```bash
cp .env.example .env
# .env anpassen: ENPAL_URL, MQTT_HOST, ggf. Zugangsdaten

docker compose up -d --build
docker compose logs -f
```

`ENPAL_URL` ist die Basis-URL der Box (z.B. `http://10.1.2.11`); ein Pfad
dahinter wird ignoriert, ältere `.env`-Dateien mit `.../collector`
funktionieren also unverändert weiter.

Beim Start öffnet die Bridge die `deviceMessages`-Seite der Enpal-Box,
hakt alle Geräte und „Show internal values" an und liest danach alle
`POLL_INTERVAL_SECONDS` Sekunden die (von der Box live aktualisierten)
Tabellen aus. Die Bridge erzwingt dabei keine zusätzliche
Geräte-Kommunikation, sondern liest nur aus, was die Box ohnehin laufend
selbst einsammelt. Zeilen ohne Wert („missing: ...", „unsupported: ...")
werden übersprungen.

Bricht die Verbindung ab (z.B. Netzwerkproblem, Box-Neustart), startet
die Bridge nach `RESTART_DELAY_SECONDS` automatisch eine komplett neue
Sitzung (neuer Browser, neue Verbindung). Dasselbe passiert, wenn
mindestens 5 Minuten lang kein Wert einen neueren Zeitstempel bekommt
(Verbindung tot, ohne dass die Seite es merkt).

Jeder MQTT-Connect/Publish wird nach `MQTT_OPERATION_TIMEOUT_SECONDS`
(Default 20s) hart abgebrochen, falls der Broker nicht antwortet - ohne
diesen Timeout kann eine TCP-Verbindung, die weder sauber abgelehnt noch
beantwortet wird, die Bridge stunden- statt sekundenlang blockieren, da
MQTTnets eingebautes Timeout (Default 100s) diesen Fall nicht zuverlässig
abdeckt.

## Alternativ: als Proxmox-LXC (ohne Docker)

Ähnlich wie die [Proxmox VE Community Scripts](https://community-scripts.github.io/ProxmoxVE/):
ein Befehl in der **Proxmox-Shell** (Web-UI → Node → Shell) legt einen
Debian-12-Container an, installiert darin Bridge + Chromium und startet sie
als systemd-Dienst:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/BlackOrca/EnpalMqttBridge/main/lxc/create-lxc.sh)"
```

Das Skript fragt nach IP der Enpal-Box und MQTT-Broker (+ ggf.
Zugangsdaten) und bietet optional erweiterte Container-Einstellungen an
(ID, Speicher, Netzwerk/statische IP, ...). Standard: unprivilegierter
Container, 2 Kerne, 1 GB RAM, 512 MB Swap, 4 GB Disk, DHCP auf `vmbr0`,
Autostart beim Booten des Hosts.

Wer den Container lieber selbst anlegt (Debian 12/Ubuntu, unprivilegiert,
≥ 1 GB RAM), führt **in dessen Konsole** nur die Installation aus:

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/BlackOrca/EnpalMqttBridge/main/lxc/install.sh)"
```

Im Container dann:

| Was | Befehl |
| --- | --- |
| Logs anzeigen | `journalctl -u enpal-mqtt-bridge -f` |
| Konfiguration | `/etc/enpal-mqtt-bridge/bridge.env` (gleiche Werte wie `.env.example`), danach `systemctl restart enpal-mqtt-bridge` |
| Update auf neueste Version | `enpal-bridge-update` (Konfiguration bleibt erhalten) |

Vom Proxmox-Host aus geht das jeweils mit vorangestelltem
`pct exec <CTID> -- ...`.

Die Bridge kommt als fertiges, self-contained Paket (inkl. .NET-Runtime)
aus dem neuesten [GitHub-Release](https://github.com/BlackOrca/EnpalMqttBridge/releases);
Chromium installiert `install.sh` passend zur Playwright-Version der
Bridge. `build-and-push.ps1` erstellt diese Releases automatisch zusammen
mit dem Docker-Image.

## 2. Werte manuell prüfen

```bash
mosquitto_sub -h <MQTT_HOST> -t 'enpal/#' -v
```

Jede Nachricht ist ein JSON-Objekt, z.B.:

```text
enpal/Energy.Battery.Charge.Level {"value":75,"unit":"%","timestamp":1790788907}
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
`frequency`, °C → `temperature`, der Batterie-Ladestand zusätzlich als
`battery`); Text-Sensoren (LTE-Status etc.) bleiben unklassifiziert.

Die Bridge setzt außerdem einen Verfügbarkeits-Status unter `enpal/status`
(`online` beim Verbinden, `offline` per MQTT-Last-Will bzw. beim sauberen
Beenden) - Discovery-Objekte zeigen sich dadurch in Symcon/Home Assistant
automatisch als "nicht verfügbar", sobald die Bridge nicht läuft.

Per `HA_DISCOVERY_ENABLED=false` lässt sich die Discovery-Veröffentlichung
komplett abschalten, `HA_DISCOVERY_PREFIX` ändert den Topic-Präfix (Default
`homeassistant`, wie vom HA-Discovery-Modul in Symcon erwartet).

## Raspberry Pi / arm64

`build-and-push.ps1` baut das Image als Multi-Platform-Manifest fuer
`linux/amd64` und `linux/arm64` (u.a. Raspberry Pi 3 mit 64-Bit-OS) und
pusht es in einem Rutsch zu ghcr.io. Da der Standard-„docker"-Buildx-
Treiber keine Multi-Platform-Pushes unterstuetzt, legt das Skript beim
ersten Lauf automatisch einen `docker-container`-Builder namens
`enpal-multiarch` an.

Auf dem Pi selbst reicht danach das normale `docker compose pull && docker
compose up -d` - Docker waehlt automatisch das passende arm64-Image aus
dem Manifest aus.

Ein paar arm64-spezifische Anpassungen im Dockerfile/Code:

- Die Build-Stage laeuft immer nativ auf der Architektur des bauenden
  Rechners (`--platform=$BUILDPLATFORM`), `dotnet publish` wird aber
  trotzdem explizit mit `-r linux-$TARGETARCH` aufgerufen - sonst landet
  der falsche (Ziel-)architekturspezifische Playwright-Treiber im Image.
- `DOTNET_EnableWriteXorExecute=0` (Laufzeit) und `--no-sandbox` /
  `--disable-dev-shm-usage` beim Chromium-Start vermeiden bekannte
  Abstuerze von .NET/Chromium in Containern auf ARM-Systemen bzw. mit
  begrenztem `/dev/shm`.

Der Raspberry Pi 3 hat nur 1 GB RAM - Chromium ist vergleichsweise
speicherhungrig, daher im Zweifel Swap einrichten und die Bridge im Auge
behalten (`docker stats`), falls sie unter Last neu startet.

## Aufbau

```text
Dockerfile               Multi-Stage-Build: .NET SDK zum Bauen,
                          Playwright-Runtime-Image (mit vorinstalliertem
                          Chromium) zum Ausführen.
docker-compose.yml        Startet den Container mit den Werten aus .env.
.env.example               Vorlage für die Konfiguration.
EnpalMqttBridge.csproj     Projektdatei (Microsoft.Playwright, MQTTnet).
Program.cs                 Kompletter Bridge-Code (Browser-Steuerung,
                            Tabellen-Parsing, MQTT-Publisher,
                            Reconnect-Logik).
VERSION                    Aktuelle Image-Version (SemVer), wird von
                            build-and-push.ps1 automatisch hochgezählt.
build-and-push.ps1         Baut das Docker-Image mit hochgezählter
                            Version und pusht es zu ghcr.io.
```

## Falls Enpal die Seite nochmal ändert

Die Bridge öffnet `/deviceMessages`, hakt alle Checkboxen außer
`showUnsupported_*` an (Geräteauswahl + `showInternal_<Gerät>`) und liest
danach aus allen `table tbody tr` die ersten drei Zellen (Sensorname,
Wert inkl. Einheit wie `389W`/`75%`/`46.8°C`, Zeitstempel). Ändert sich
die Seite erneut, zuerst per `docker compose logs -f` schauen, wie viele
Sensorwerte pro Zyklus erkannt werden bzw. welcher Fehler beim Start
kommt, dann im Browser unter `http://<box>/` nachsehen, welche Seiten es
noch gibt, und die Selektoren in `Program.cs` anpassen.
