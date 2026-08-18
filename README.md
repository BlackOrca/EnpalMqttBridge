# Enpal MQTT Bridge

Liest die Enpal-Box-Seite `deviceMessages` per echtem Headless-Browser
(Playwright/Chromium) aus - inklusive der Werte, die erst nach Anhaken von
„Show internal values" / „Show unsupported values" sichtbar werden
(Batterie-Ladezustand/SOC, Batterie laden/entladen, PV-DC-Leistung, ...).
Diese Checkboxen hängen an einer aktiven Blazor-Server-Verbindung
(SignalR-Circuit) und sind deshalb per einfachem HTTP-GET/Scraping nicht
erreichbar - deshalb hier ein echter Browser statt eines HTML-Parsers.

Die gefundenen Werte werden per MQTT veröffentlicht; ein separates
Symcon-Skript (`symcon/enpal_mqtt_receiver.php`) holt sie ab und schreibt
sie in dieselbe „Enpal-Box"-Instanzstruktur wie das bestehende
HTML-Scraping-Skript (`enpal_box_optimiert.php`).

## Wichtiger Hinweis zum Testgrad

- **Build/Docker verifiziert:** Läuft auf .NET 10 (SDK/Runtime), Microsoft.Playwright
  1.62.0 und MQTTnet 5.2.0. `dotnet build`/`dotnet publish` sowie
  `docker build` wurden erfolgreich durchgeführt; ein Testcontainer
  wurde gestartet und Chromium konnte darin erfolgreich launchen und
  eine Zielseite ansteuern (Verbindungsfehler kam erwartungsgemäß nur,
  weil im Test keine echte Enpal-Box erreichbar war).
- **Nicht verifiziert:** Das eigentliche Verhalten gegen eine echte
  Enpal-Box - insbesondere ob die "Show internal/unsupported values"-
  Checkboxen zuverlässig erkannt/angeklickt werden und ob danach wirklich
  alle gewünschten Sensoren (SOC, Batterie laden/entladen, PV-DC-Leistung)
  im Ergebnis auftauchen. Bitte einmal gegen die echte Box laufen lassen,
  bevor du dich darauf verlässt.
- **`symcon/enpal_mqtt_receiver.php` fehlt noch** - im Projektverzeichnis
  aktuell nicht vorhanden, obwohl weiter unten referenziert. Muss noch
  ergänzt werden, bevor der Symcon-Teil nutzbar ist.

## 1. Bridge bauen und starten

```bash
cp .env.example .env
# .env anpassen: ENPAL_URL, MQTT_HOST, ggf. Zugangsdaten

docker compose up -d --build
docker compose logs -f
```

Beim Start öffnet die Bridge die Enpal-Seite, hakt alle „Show internal
values" / „Show unsupported values" Checkboxen an und beginnt danach,
den aktuellen Zustand der (weiterhin offenen) Seite alle
`POLL_INTERVAL_SECONDS` Sekunden auszulesen und zu veröffentlichen. Da
die Blazor-Verbindung ohnehin laufend Live-Updates in die Seite pusht,
ist das kein "neu laden", sondern nur ein Auslesen des aktuellen DOM -
entsprechend leichtgewichtig.

Bricht die Verbindung ab (z.B. Netzwerkproblem, Box-Neustart), startet
die Bridge nach `RESTART_DELAY_SECONDS` automatisch eine komplett neue
Sitzung (neuer Browser, neue Verbindung, Checkboxen erneut anklicken).

## 2. Werte manuell prüfen (ohne Symcon)

```bash
mosquitto_sub -h <MQTT_HOST> -t 'enpal/#' -v
```

Jede Nachricht ist ein JSON-Objekt, z.B.:

```
enpal/Energy.Battery.Charge.Level {"value":100,"unit":"%","timestamp":1734000005}
```

## 3. Symcon-Skript einrichten

1. Neues Skript in Symcon anlegen, Inhalt von
   `symcon/enpal_mqtt_receiver.php` hineinkopieren.
2. Am Kopf des Skripts die Konstanten `MQTT_HOST`, `MQTT_PORT`,
   `MQTT_USERNAME`, `MQTT_PASSWORD`, `MQTT_TOPIC_PREFIX` an eure Umgebung
   anpassen (gleiche Werte wie in `.env` der Bridge).
3. Einen Zeitplan/Ereignis-Trigger einrichten, der das Skript
   regelmäßig ausführt - empfohlen alle 20-30 Sekunden, passend zu
   `POLL_INTERVAL_SECONDS` der Bridge.
4. Nach dem ersten Lauf sollte unter der (bestehenden oder neu
   angelegten) „Enpal-Box"-Instanz für jeden per MQTT veröffentlichten
   Sensor eine Unterinstanz mit den Variablen „Wert" / „Zeitstempel" /
   „Aktuell?" auftauchen - genau wie beim HTML-Scraping-Skript.

Das Skript bringt (wie das HTML-Skript) eine `IPS_Semaphore`-Sperre
mit, falls sich zwei Durchläufe mal überlappen sollten.

## Aufbau

```
Dockerfile              Multi-Stage-Build: .NET SDK zum Bauen,
                         Playwright-Runtime-Image (mit vorinstalliertem
                         Chromium) zum Ausführen.
docker-compose.yml       Startet den Container mit den Werten aus .env.
.env.example              Vorlage für die Konfiguration.
EnpalMqttBridge.csproj    Projektdatei (Microsoft.Playwright, MQTTnet).
Program.cs                Kompletter Bridge-Code (Browser-Steuerung,
                           Parsing, MQTT-Publisher, Reconnect-Logik).
symcon/
  enpal_mqtt_receiver.php  Symcon-Skript: verbindet sich kurz per MQTT,
                            liest retained Nachrichten, schreibt sie in
                            die Enpal-Box-Instanzstruktur.
```

## Falls Enpal die Seite nochmal ändert

Die Checkbox-Erkennung sucht gezielt nach
`input[id^='showInternal_']` / `input[id^='showUnsupported_']` und die
Tabellen-Erkennung nach `tr`-Zeilen mit mindestens 2 `td`-Zellen
(3 Zellen = "Site Data"-Format mit komplettem Zeitstempel, 4 Zellen =
Format mit Uhrzeit-ohne-Datum + Notiz-Spalte). Ändert sich das erneut,
zuerst per `docker compose logs -f` schauen, wie viele Zeilen/Sensoren
erkannt werden, und bei Bedarf die Selektoren/Regex in `Program.cs`
(bzw. das PHP-Pendant in `enpal_box_optimiert.php`) anpassen - das
gleiche Vorgehen wie bei den bisherigen Anpassungen an diesem Projekt.
