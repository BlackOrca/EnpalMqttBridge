# Build-Stage: nur SDK, kein Playwright-Browser-Overhead nötig
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY EnpalMqttBridge.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release -o /app --no-restore

# Runtime-Stage: offizielles Playwright-.NET-Image, Chromium ist hier
# bereits vorinstalliert (Version muss zur Microsoft.Playwright-NuGet-
# Version im .csproj passen - aktuell 1.62.0).
FROM mcr.microsoft.com/playwright/dotnet:v1.62.0-noble

# Werden von build-and-push.ps1 per --build-arg gesetzt.
ARG VERSION=0.0.0
ARG REVISION=unknown
ARG CREATED=unknown

LABEL org.opencontainers.image.title="Enpal MQTT Bridge" \
      org.opencontainers.image.description="Liest Enpal-Box-Sensordaten per Headless-Browser aus und veroeffentlicht sie per MQTT." \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.source="https://github.com/BlackOrca/EnpalMqttBridge"

WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "EnpalMqttBridge.dll"]
