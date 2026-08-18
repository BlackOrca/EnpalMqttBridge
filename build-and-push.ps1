<#
Baut das Docker-Image fuer linux/amd64 + linux/arm64 (z.B. Raspberry Pi 3
64-bit) mit einer um 1 erhoehten Patch-Version (aus VERSION), taggt es als
Version und als "latest" und laedt beide Tags direkt zu ghcr.io hoch.
#>

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $scriptDir "VERSION"
$imageName = "ghcr.io/blackorca/enpal-mqtt-bridge"
$platforms = "linux/amd64,linux/arm64"

# --- Version einlesen und Patch hochzaehlen ---
$currentVersion = (Get-Content $versionFile -Raw).Trim()
if ($currentVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "VERSION-Datei enthaelt keine gueltige SemVer-Version (Major.Minor.Patch): '$currentVersion'"
}
$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3] + 1
$newVersion = "$major.$minor.$patch"

# --- Revision (Git-Commit) und Build-Zeitpunkt fuer die Image-Labels ---
$revision = git rev-parse --short HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($revision)) {
    $revision = "unknown"
}
$created = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

# --- Buildx-Builder mit Multi-Platform-Push-Unterstuetzung sicherstellen ---
# Der Standard-"docker"-Treiber kann kein Multi-Platform-Manifest in einem
# Durchgang erzeugen/pushen ("Multi-platform build is not supported for the
# docker driver") - dafuer wird einmalig ein "docker-container"-Builder
# angelegt und danach wiederverwendet.
$builderName = "enpal-multiarch"
docker buildx inspect $builderName 2>$null 1>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Erstelle Buildx-Builder '$builderName' (docker-container-Treiber) ..."
    docker buildx create --name $builderName --driver docker-container --bootstrap
    if ($LASTEXITCODE -ne 0) {
        throw "Konnte Buildx-Builder '$builderName' nicht erstellen (Exit-Code $LASTEXITCODE)"
    }
}

Write-Host "Baue und pushe $imageName Version $newVersion fuer $platforms (Revision $revision) ..."

# Multi-Plattform-Images koennen nicht lokal geladen werden - buildx baut
# und pusht sie direkt als ein gemeinsames Manifest pro Tag zu ghcr.io.
docker buildx build `
    --builder $builderName `
    --platform $platforms `
    --build-arg "VERSION=$newVersion" `
    --build-arg "REVISION=$revision" `
    --build-arg "CREATED=$created" `
    -t "${imageName}:$newVersion" `
    -t "${imageName}:latest" `
    --push `
    $scriptDir
if ($LASTEXITCODE -ne 0) {
    throw "docker buildx build/push fehlgeschlagen (Exit-Code $LASTEXITCODE) - VERSION bleibt auf $currentVersion, einfach erneut versuchen."
}

# Build+Push erfolgreich -> Version jetzt erst persistieren.
Set-Content -Path $versionFile -Value $newVersion -NoNewline -Encoding utf8

Write-Host "Fertig: ${imageName}:$newVersion ($platforms) wurde gebaut und hochgeladen."
