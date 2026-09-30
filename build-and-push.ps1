<#
Baut das Docker-Image fuer linux/amd64 + linux/arm64 (z.B. Raspberry Pi 3
64-bit) mit einer um 1 erhoehten Patch-Version (aus VERSION), taggt es als
Version und als "latest" und laedt beide Tags direkt zu ghcr.io hoch.

Zusaetzlich werden self-contained Tarballs (linux-x64/linux-arm64) fuer die
LXC-Installation (lxc/install.sh) gebaut und als GitHub-Release
"v<Version>" veroeffentlicht - install.sh laedt immer das neueste Release.
Voraussetzung: gh CLI angemeldet und der aktuelle Commit bereits gepusht.
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

# Das GitHub-Release wird auf genau diesen Commit getaggt - der muss dafuer
# schon auf GitHub liegen. Vor dem Bauen pruefen, statt erst nach dem
# Docker-Push zu scheitern.
$commit = git rev-parse HEAD
git fetch --quiet origin
$remoteBranches = git branch -r --contains $commit
if ([string]::IsNullOrWhiteSpace($remoteBranches)) {
    throw "Commit $revision ist noch nicht auf GitHub (git push) - das Release braucht ihn als Tag-Ziel."
}

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

# --- LXC-Tarballs bauen (vor dem Docker-Push, damit ein Fehler hier nichts
# halb veroeffentlicht) ---
$distDir = Join-Path $scriptDir "dist"
if (Test-Path $distDir) {
    Remove-Item -Recurse -Force $distDir
}
Write-Host "Baue LXC-Tarballs Version $newVersion ..."
docker buildx build `
    --builder $builderName `
    --target lxc-tarballs `
    --build-arg "VERSION=$newVersion" `
    --output "type=local,dest=$distDir" `
    $scriptDir
if ($LASTEXITCODE -ne 0) {
    throw "Bau der LXC-Tarballs fehlgeschlagen (Exit-Code $LASTEXITCODE) - VERSION bleibt auf $currentVersion."
}
$tarballs = Get-ChildItem $distDir -Filter "enpal-mqtt-bridge-*.tar.gz" | ForEach-Object { $_.FullName }

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

# --- GitHub-Release mit den LXC-Tarballs ---
Write-Host "Erstelle GitHub-Release v$newVersion mit LXC-Tarballs ..."
gh release create "v$newVersion" @tarballs `
    --target $commit `
    --title "v$newVersion" `
    --notes "Docker: ``${imageName}:$newVersion`` - LXC: siehe README (lxc/create-lxc.sh bzw. lxc/install.sh)."
if ($LASTEXITCODE -ne 0) {
    throw "GitHub-Release fehlgeschlagen (Exit-Code $LASTEXITCODE) - Docker-Image ist bereits veroeffentlicht. Manuell nachholen: gh release create v$newVersion $($tarballs -join ' ') --target $commit"
}

Write-Host "Fertig: GitHub-Release v$newVersion veroeffentlicht."
