<#
Baut das Docker-Image mit einer um 1 erhoehten Patch-Version (aus VERSION),
taggt es als Version und als "latest" und laedt beide Tags zu ghcr.io hoch.
#>

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $scriptDir "VERSION"
$imageName = "ghcr.io/blackorca/enpal-mqtt-bridge"

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

Write-Host "Baue $imageName Version $newVersion (Revision $revision) ..."

docker build `
    --build-arg "VERSION=$newVersion" `
    --build-arg "REVISION=$revision" `
    --build-arg "CREATED=$created" `
    -t "${imageName}:$newVersion" `
    -t "${imageName}:latest" `
    $scriptDir
if ($LASTEXITCODE -ne 0) {
    throw "docker build fehlgeschlagen (Exit-Code $LASTEXITCODE)"
}

# Build erfolgreich -> Version jetzt erst persistieren, damit ein fehlgeschlagener
# Build die VERSION-Datei nicht unnoetig hochzaehlt.
Set-Content -Path $versionFile -Value $newVersion -NoNewline -Encoding utf8

Write-Host "Lade ${imageName}:$newVersion und :latest zu ghcr.io hoch ..."
docker push "${imageName}:$newVersion"
if ($LASTEXITCODE -ne 0) {
    throw "docker push fehlgeschlagen (Exit-Code $LASTEXITCODE) - VERSION steht bereits auf $newVersion, Image ist lokal vorhanden und kann erneut gepusht werden."
}

docker push "${imageName}:latest"
if ($LASTEXITCODE -ne 0) {
    throw "docker push von 'latest' fehlgeschlagen (Exit-Code $LASTEXITCODE)"
}

Write-Host "Fertig: ${imageName}:$newVersion wurde gebaut und hochgeladen."
