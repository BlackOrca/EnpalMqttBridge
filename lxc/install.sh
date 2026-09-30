#!/usr/bin/env bash
# Installiert (oder aktualisiert) die Enpal MQTT Bridge als systemd-Dienst
# auf Debian/Ubuntu - gedacht fuer einen Proxmox-LXC, laeuft aber auf jeder
# Debian/Ubuntu-Maschine. Im Container als root ausfuehren:
#
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/BlackOrca/EnpalMqttBridge/main/lxc/install.sh)"
#
# Erneut ausgefuehrt aktualisiert das Skript Bridge und Chromium auf das
# neueste Release und laesst die Konfiguration unangetastet.
#
# Werte fuer die Erstkonfiguration koennen per Umgebungsvariable vorgegeben
# werden (ENPAL_URL, MQTT_HOST, MQTT_PORT, MQTT_USERNAME, MQTT_PASSWORD,
# MQTT_TOPIC_PREFIX, POLL_INTERVAL_SECONDS), sonst wird interaktiv gefragt.
# ENPAL_BRIDGE_NONINTERACTIVE=1 unterdrueckt alle Rueckfragen (so ruft
# create-lxc.sh das Skript auf).
set -euo pipefail

REPO="BlackOrca/EnpalMqttBridge"
RELEASE_URL="${ENPAL_BRIDGE_RELEASE_URL:-https://github.com/$REPO/releases/latest/download}"
INSTALL_DIR=/opt/enpal-mqtt-bridge
APP_DIR=$INSTALL_DIR/app
BROWSERS_DIR=$INSTALL_DIR/browsers
CONFIG_DIR=/etc/enpal-mqtt-bridge
CONFIG_FILE=$CONFIG_DIR/bridge.env
SERVICE=enpal-mqtt-bridge
SERVICE_USER=enpal-bridge
UPDATE_CMD=/usr/local/bin/enpal-bridge-update

msg()  { printf '\033[1;32m[+]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[!]\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

interactive() { [ "${ENPAL_BRIDGE_NONINTERACTIVE:-0}" != "1" ] && [ -t 0 ]; }

# ask VAR "Frage" [Default] - uebernimmt einen vorgegebenen Wert, fragt
# sonst interaktiv nach bzw. nimmt ohne Terminal den Default.
ask() {
    local var=$1 prompt=$2 default=${3-} value
    [ -n "${!var-}" ] && return
    if interactive; then
        read -rp "$prompt${default:+ [$default]}: " value
        printf -v "$var" '%s' "${value:-$default}"
    else
        printf -v "$var" '%s' "$default"
    fi
}

ask_secret() {
    local var=$1 prompt=$2 value
    [ -n "${!var-}" ] && return
    if interactive; then
        read -rsp "$prompt: " value
        echo
        printf -v "$var" '%s' "$value"
    fi
}

# Schreibt KEY="VALUE" im systemd-EnvironmentFile-Format (in doppelten
# Anfuehrungszeichen muessen \ " ` und $ escaped werden).
env_line() {
    local value=$2
    value=${value//\\/\\\\}
    value=${value//\"/\\\"}
    value=${value//\`/\\\`}
    value=${value//\$/\\\$}
    printf '%s="%s"\n' "$1" "$value"
}

[ "$(id -u)" -eq 0 ] || die "Bitte als root ausfuehren."
[ -r /etc/os-release ] && . /etc/os-release
case " ${ID:-} ${ID_LIKE:-} " in
    *" debian "* | *" ubuntu "*) ;;
    *) die "Nur Debian/Ubuntu werden unterstuetzt (gefunden: ${PRETTY_NAME:-unbekannt})." ;;
esac

case "$(uname -m)" in
    x86_64) RID=linux-x64 ;;
    aarch64 | arm64) RID=linux-arm64 ;;
    *) die "Nicht unterstuetzte Architektur: $(uname -m)" ;;
esac

export DEBIAN_FRONTEND=noninteractive

msg "Installiere Basis-Pakete ..."
apt-get update -qq
apt-get install -y -qq --no-install-recommends curl ca-certificates tar gzip >/dev/null

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

if [ -n "${ENPAL_BRIDGE_TARBALL:-}" ]; then
    msg "Verwende lokales Paket $ENPAL_BRIDGE_TARBALL ..."
    cp "$ENPAL_BRIDGE_TARBALL" "$TMP_DIR/bridge.tar.gz"
else
    msg "Lade neueste Bridge-Version ($RID) ..."
    curl -fsSL "$RELEASE_URL/enpal-mqtt-bridge-$RID.tar.gz" -o "$TMP_DIR/bridge.tar.gz" \
        || die "Download fehlgeschlagen: $RELEASE_URL/enpal-mqtt-bridge-$RID.tar.gz"
fi
mkdir -p "$TMP_DIR/app"
tar -xzf "$TMP_DIR/bridge.tar.gz" -C "$TMP_DIR/app"
[ -x "$TMP_DIR/app/EnpalMqttBridge" ] || die "Paket unvollstaendig (EnpalMqttBridge fehlt)."
NEW_VERSION=$(cat "$TMP_DIR/app/VERSION" 2>/dev/null || echo unbekannt)
OLD_VERSION=$(cat "$APP_DIR/VERSION" 2>/dev/null || echo "")

HAS_SYSTEMD=0
[ -d /run/systemd/system ] && HAS_SYSTEMD=1

if [ "$HAS_SYSTEMD" = 1 ] && systemctl is-active --quiet "$SERVICE"; then
    msg "Stoppe laufende Bridge ${OLD_VERSION:+(Version $OLD_VERSION)} ..."
    systemctl stop "$SERVICE"
fi

msg "Installiere Bridge $NEW_VERSION nach $APP_DIR ..."
mkdir -p "$INSTALL_DIR"
rm -rf "$APP_DIR"
mv "$TMP_DIR/app" "$APP_DIR"
chmod -R a+rX "$APP_DIR"

# Chromium + Systembibliotheken ueber den mitgelieferten Playwright-Treiber
# installieren - so passt die Chromium-Version immer zur Playwright-Version
# der Bridge. Nur die Headless-Shell, die Bridge braucht kein volles
# Browserfenster.
msg "Installiere Chromium und Abhaengigkeiten (dauert beim ersten Mal etwas) ..."
PLAYWRIGHT_BROWSERS_PATH=$BROWSERS_DIR \
    "$APP_DIR/.playwright/node/$RID/node" "$APP_DIR/.playwright/package/cli.js" \
    install --with-deps --only-shell chromium
chmod -R a+rX "$BROWSERS_DIR"

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    msg "Lege Dienstbenutzer $SERVICE_USER an ..."
    useradd --system --home-dir "/var/lib/$SERVICE" --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if [ -f "$CONFIG_FILE" ]; then
    msg "Konfiguration $CONFIG_FILE bleibt unveraendert."
else
    msg "Erstelle Konfiguration $CONFIG_FILE ..."
    if [ -z "${ENPAL_URL:-}" ]; then
        ask ENPAL_BOX "IP-Adresse der Enpal-Box"
        [ -n "${ENPAL_BOX:-}" ] && ENPAL_URL="http://$ENPAL_BOX"
    fi
    ask MQTT_HOST "MQTT-Broker (Host/IP)"
    ask MQTT_PORT "MQTT-Port" 1883
    ask MQTT_USERNAME "MQTT-Benutzer (leer = ohne Anmeldung)"
    [ -n "${MQTT_USERNAME:-}" ] && ask_secret MQTT_PASSWORD "MQTT-Passwort"
    [ -n "${ENPAL_URL:-}" ] || die "Keine Enpal-Box angegeben (ENPAL_URL)."
    [ -n "${MQTT_HOST:-}" ] || die "Kein MQTT-Broker angegeben (MQTT_HOST)."
    case "$ENPAL_URL" in *://*) ;; *) ENPAL_URL="http://$ENPAL_URL" ;; esac

    mkdir -p "$CONFIG_DIR"
    {
        echo "# Konfiguration der Enpal MQTT Bridge - Bedeutung der Werte siehe"
        echo "# https://github.com/$REPO/blob/main/.env.example"
        echo "# Nach Aenderungen: systemctl restart $SERVICE"
        env_line ENPAL_URL "$ENPAL_URL"
        env_line MQTT_HOST "$MQTT_HOST"
        env_line MQTT_PORT "${MQTT_PORT:-1883}"
        env_line MQTT_USERNAME "${MQTT_USERNAME:-}"
        env_line MQTT_PASSWORD "${MQTT_PASSWORD:-}"
        env_line MQTT_CLIENT_ID "${MQTT_CLIENT_ID:-enpal-mqtt-bridge}"
        env_line MQTT_TOPIC_PREFIX "${MQTT_TOPIC_PREFIX:-enpal}"
        env_line MQTT_OPERATION_TIMEOUT_SECONDS "${MQTT_OPERATION_TIMEOUT_SECONDS:-20}"
        env_line POLL_INTERVAL_SECONDS "${POLL_INTERVAL_SECONDS:-20}"
        env_line RESTART_DELAY_SECONDS "${RESTART_DELAY_SECONDS:-15}"
        env_line HA_DISCOVERY_ENABLED "${HA_DISCOVERY_ENABLED:-true}"
        env_line HA_DISCOVERY_PREFIX "${HA_DISCOVERY_PREFIX:-homeassistant}"
    } > "$CONFIG_FILE"
fi
chown "root:$SERVICE_USER" "$CONFIG_FILE"
chmod 640 "$CONFIG_FILE"

msg "Richte systemd-Dienst $SERVICE ein ..."
cat > "/etc/systemd/system/$SERVICE.service" <<EOF
[Unit]
Description=Enpal MQTT Bridge
Documentation=https://github.com/$REPO
Wants=network-online.target
After=network-online.target

[Service]
User=$SERVICE_USER
EnvironmentFile=$CONFIG_FILE
Environment=PLAYWRIGHT_BROWSERS_PATH=$BROWSERS_DIR
Environment=DOTNET_EnableWriteXorExecute=0
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/EnpalMqttBridge
Restart=always
RestartSec=15
# SIGTERM nur an die Bridge, die Chromium dann selbst sauber beendet und
# "offline" meldet; uebrig gebliebene Prozesse raeumt systemd danach ab.
KillMode=mixed

[Install]
WantedBy=multi-user.target
EOF

cat > "$UPDATE_CMD" <<EOF
#!/usr/bin/env bash
# Aktualisiert die Enpal MQTT Bridge auf das neueste Release.
exec bash -c "\$(curl -fsSL https://raw.githubusercontent.com/$REPO/main/lxc/install.sh)"
EOF
chmod 755 "$UPDATE_CMD"

if [ "$HAS_SYSTEMD" = 1 ]; then
    systemctl daemon-reload
    systemctl enable --quiet "$SERVICE"
    systemctl restart "$SERVICE"
    msg "Bridge $NEW_VERSION laeuft."
else
    warn "Kein systemd gefunden - Dienst eingerichtet, aber nicht gestartet."
fi

cat <<EOF

  Logs anzeigen:          journalctl -u $SERVICE -f
  Konfiguration:          $CONFIG_FILE  (danach: systemctl restart $SERVICE)
  Auf neueste Version:    enpal-bridge-update

EOF
