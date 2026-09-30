#!/usr/bin/env bash
# Legt auf einem Proxmox-VE-Host einen Debian-LXC an und installiert darin
# die Enpal MQTT Bridge (per lxc/install.sh). In der Proxmox-Shell als root
# ausfuehren:
#
#   bash -c "$(curl -fsSL https://raw.githubusercontent.com/BlackOrca/EnpalMqttBridge/main/lxc/create-lxc.sh)"
#
# Alle Einstellungen koennen auch per Umgebungsvariable vorgegeben werden
# (CTID, CT_HOSTNAME, CT_STORAGE, TEMPLATE_STORAGE, DISK_GB, CORES,
# MEMORY_MB, SWAP_MB, BRIDGE, CT_IP, CT_GATEWAY, DEBIAN_VERSION sowie
# ENPAL_URL/MQTT_*), dann wird dafuer nicht nachgefragt.
set -euo pipefail

REPO_RAW="${ENPAL_BRIDGE_RAW_URL:-https://raw.githubusercontent.com/BlackOrca/EnpalMqttBridge/main}"

msg()  { printf '\033[1;32m[+]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[!]\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[x]\033[0m %s\n' "$*" >&2; exit 1; }

# ask VAR "Frage" [Default] - uebernimmt einen vorgegebenen Wert, fragt
# sonst interaktiv nach.
ask() {
    local var=$1 prompt=$2 default=${3-} value
    [ -n "${!var-}" ] && return
    read -rp "$prompt${default:+ [$default]}: " value
    printf -v "$var" '%s' "${value:-$default}"
}

ask_secret() {
    local var=$1 prompt=$2 value
    [ -n "${!var-}" ] && return
    read -rsp "$prompt: " value
    echo
    printf -v "$var" '%s' "$value"
}

[ "$(id -u)" -eq 0 ] || die "Bitte als root in der Proxmox-Shell ausfuehren."
for cmd in pct pveam pvesm pvesh; do
    command -v "$cmd" >/dev/null || die "'$cmd' nicht gefunden - das Skript muss auf einem Proxmox-VE-Host laufen."
done
[ "$(dpkg --print-architecture)" = amd64 ] \
    || die "Nur amd64-Hosts werden unterstuetzt. Alternativ einen Debian-LXC selbst anlegen und darin lxc/install.sh ausfuehren."

cat <<'EOF'

  Enpal MQTT Bridge - Proxmox-LXC-Installation
  ---------------------------------------------
  Legt einen Debian-Container an, installiert die Bridge samt Chromium
  und startet sie als Dienst.

EOF

# --- Bridge-Konfiguration ---
if [ -z "${ENPAL_URL:-}" ]; then
    ask ENPAL_BOX "IP-Adresse der Enpal-Box"
    [ -n "${ENPAL_BOX:-}" ] || die "Keine Enpal-Box angegeben."
    ENPAL_URL="http://$ENPAL_BOX"
fi
ask MQTT_HOST "MQTT-Broker (Host/IP)"
[ -n "${MQTT_HOST:-}" ] || die "Kein MQTT-Broker angegeben."
ask MQTT_PORT "MQTT-Port" 1883
if [ -z "${MQTT_USERNAME+x}" ]; then
    ask MQTT_USERNAME "MQTT-Benutzer (leer = ohne Anmeldung)"
fi
if [ -n "${MQTT_USERNAME:-}" ]; then
    ask_secret MQTT_PASSWORD "MQTT-Passwort"
fi

# --- Container-Einstellungen (Standardwerte oder erweitert) ---
DEFAULT_STORAGE=$(pvesm status -content rootdir 2>/dev/null | awk 'NR>1 && $3=="active" {print $1}' \
    | { grep -x local-lvm || true; } | head -n1)
[ -n "$DEFAULT_STORAGE" ] || DEFAULT_STORAGE=$(pvesm status -content rootdir 2>/dev/null | awk 'NR>1 && $3=="active" {print $1; exit}')
DEFAULT_TEMPLATE_STORAGE=$(pvesm status -content vztmpl 2>/dev/null | awk 'NR>1 && $3=="active" {print $1}' \
    | { grep -x local || true; } | head -n1)
[ -n "$DEFAULT_TEMPLATE_STORAGE" ] || DEFAULT_TEMPLATE_STORAGE=$(pvesm status -content vztmpl 2>/dev/null | awk 'NR>1 && $3=="active" {print $1; exit}')

ADVANCED=n
read -rp "Container-Einstellungen anpassen (ID, Speicher, Netzwerk, ...)? [j/N]: " ADVANCED
if [[ "$ADVANCED" =~ ^[jJyY] ]]; then
    ask CTID "Container-ID" "$(pvesh get /cluster/nextid)"
    ask CT_HOSTNAME "Hostname" enpal-mqtt-bridge
    ask CT_STORAGE "Speicher fuer den Container" "$DEFAULT_STORAGE"
    ask TEMPLATE_STORAGE "Speicher fuer das Template" "$DEFAULT_TEMPLATE_STORAGE"
    ask DISK_GB "Festplatte (GB)" 4
    ask CORES "CPU-Kerne" 2
    ask MEMORY_MB "RAM (MB)" 1024
    ask SWAP_MB "Swap (MB)" 512
    ask BRIDGE "Netzwerk-Bridge" vmbr0
    ask CT_IP "IPv4 (dhcp oder CIDR, z.B. 192.168.1.50/24)" dhcp
    if [ "$CT_IP" != dhcp ]; then
        ask CT_GATEWAY "Gateway"
    fi
fi
CTID=${CTID:-$(pvesh get /cluster/nextid)}
CT_HOSTNAME=${CT_HOSTNAME:-enpal-mqtt-bridge}
CT_STORAGE=${CT_STORAGE:-$DEFAULT_STORAGE}
TEMPLATE_STORAGE=${TEMPLATE_STORAGE:-$DEFAULT_TEMPLATE_STORAGE}
DISK_GB=${DISK_GB:-4}
CORES=${CORES:-2}
MEMORY_MB=${MEMORY_MB:-1024}
SWAP_MB=${SWAP_MB:-512}
BRIDGE=${BRIDGE:-vmbr0}
CT_IP=${CT_IP:-dhcp}
DEBIAN_VERSION=${DEBIAN_VERSION:-12}

[ -n "$CT_STORAGE" ] || die "Kein Speicher fuer Container gefunden (Inhalt 'rootdir')."
[ -n "$TEMPLATE_STORAGE" ] || die "Kein Speicher fuer Templates gefunden (Inhalt 'vztmpl')."
pct status "$CTID" >/dev/null 2>&1 && die "Container-ID $CTID ist bereits belegt."

NET0="name=eth0,bridge=$BRIDGE,ip=$CT_IP"
[ -n "${CT_GATEWAY:-}" ] && NET0="$NET0,gw=$CT_GATEWAY"

# --- Template ---
msg "Suche Debian-$DEBIAN_VERSION-Template ..."
pveam update >/dev/null 2>&1 || warn "Template-Liste konnte nicht aktualisiert werden, verwende vorhandene."
TEMPLATE=$(pveam available --section system | awk '{print $2}' \
    | grep -E "^debian-${DEBIAN_VERSION}-standard_.*_amd64\.tar\.(zst|gz|xz)$" | sort -V | tail -n1 || true)
[ -n "$TEMPLATE" ] || die "Kein Debian-$DEBIAN_VERSION-Template gefunden (pveam available --section system)."
if ! pveam list "$TEMPLATE_STORAGE" | grep -q "/$TEMPLATE"; then
    msg "Lade Template $TEMPLATE nach $TEMPLATE_STORAGE ..."
    pveam download "$TEMPLATE_STORAGE" "$TEMPLATE" >/dev/null
fi

# --- Container anlegen und starten ---
msg "Erstelle LXC $CTID ($CT_HOSTNAME): $CORES Kerne, $MEMORY_MB MB RAM, $DISK_GB GB auf $CT_STORAGE, Netz $NET0 ..."
pct create "$CTID" "$TEMPLATE_STORAGE:vztmpl/$TEMPLATE" \
    --hostname "$CT_HOSTNAME" \
    --description "Enpal MQTT Bridge - https://github.com/BlackOrca/EnpalMqttBridge" \
    --ostype debian \
    --unprivileged 1 \
    --features nesting=1 \
    --cores "$CORES" \
    --memory "$MEMORY_MB" \
    --swap "$SWAP_MB" \
    --rootfs "$CT_STORAGE:$DISK_GB" \
    --net0 "$NET0" \
    --timezone host \
    --onboot 1 >/dev/null
pct start "$CTID"

msg "Warte auf Netzwerk im Container ..."
for _ in $(seq 1 30); do
    pct exec "$CTID" -- getent hosts github.com >/dev/null 2>&1 && break
    sleep 2
done
pct exec "$CTID" -- getent hosts github.com >/dev/null 2>&1 \
    || die "Container $CTID hat kein Netzwerk/DNS (github.com nicht aufloesbar). Container bleibt zur Fehlersuche bestehen."

# --- Bridge im Container installieren ---
INSTALL_SCRIPT=$(mktemp)
trap 'rm -f "$INSTALL_SCRIPT"' EXIT
if [ -n "${ENPAL_BRIDGE_INSTALL_SCRIPT:-}" ]; then
    cp "$ENPAL_BRIDGE_INSTALL_SCRIPT" "$INSTALL_SCRIPT"
else
    curl -fsSL "$REPO_RAW/lxc/install.sh" -o "$INSTALL_SCRIPT" || die "Konnte install.sh nicht laden."
fi
pct push "$CTID" "$INSTALL_SCRIPT" /root/enpal-install.sh

msg "Installiere Bridge im Container ..."
pct exec "$CTID" -- env \
    ENPAL_BRIDGE_NONINTERACTIVE=1 \
    ${ENPAL_BRIDGE_RELEASE_URL:+"ENPAL_BRIDGE_RELEASE_URL=$ENPAL_BRIDGE_RELEASE_URL"} \
    "ENPAL_URL=$ENPAL_URL" \
    "MQTT_HOST=$MQTT_HOST" \
    "MQTT_PORT=$MQTT_PORT" \
    "MQTT_USERNAME=${MQTT_USERNAME:-}" \
    "MQTT_PASSWORD=${MQTT_PASSWORD:-}" \
    bash /root/enpal-install.sh \
    || die "Installation im Container $CTID fehlgeschlagen. Container bleibt zur Fehlersuche bestehen (pct enter $CTID)."
pct exec "$CTID" -- rm -f /root/enpal-install.sh

CT_ADDR=$(pct exec "$CTID" -- hostname -I 2>/dev/null | awk '{print $1}' || true)
cat <<EOF

  Fertig! Enpal MQTT Bridge laeuft in LXC $CTID ($CT_HOSTNAME${CT_ADDR:+, $CT_ADDR}).

  Logs anzeigen:          pct exec $CTID -- journalctl -u enpal-mqtt-bridge -f
  Konfiguration aendern:  pct exec $CTID -- nano /etc/enpal-mqtt-bridge/bridge.env
                          pct exec $CTID -- systemctl restart enpal-mqtt-bridge
  Auf neueste Version:    pct exec $CTID -- enpal-bridge-update

EOF
