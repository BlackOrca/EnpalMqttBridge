# syntax=docker/dockerfile:1
# Build-Stage: laeuft absichtlich immer auf der nativen Plattform des
# Builders (--platform=$BUILDPLATFORM), auch wenn fuer ein anderes Ziel
# (z.B. linux/arm64 fuer Raspberry Pi) gebaut wird - unter QEMU-Emulation
# stuerzt der .NET-SDK-Build sonst ab (AccessViolationException/SIGABRT).
# Die eigentliche Kompilierung erzeugt plattformunabhaengiges MSIL, aber
# Microsoft.Playwright kopiert beim Publish zusaetzlich einen eigenen
# architekturspezifischen Node-basierten "Treiber" (steuert Chromium via
# CDP, getrennt vom Chromium-Browser selbst) - deshalb trotzdem "-r
# linux-$TARGETARCH", damit der zur Zielplattform passende Treiber
# landet statt (mangels RID-Angabe) der des Build-Rechners.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY EnpalMqttBridge.csproj .
COPY Program.cs .
RUN DOTNET_ARCH=$(echo "$TARGETARCH" | sed 's/^amd64$/x64/') && \
    dotnet restore -r "linux-$DOTNET_ARCH" && \
    dotnet publish -c Release -o /app --no-restore -r "linux-$DOTNET_ARCH" --self-contained false

# Tarballs fuer die LXC-/Bare-Metal-Installation per lxc/install.sh:
# self-contained (inkl. .NET-Runtime und Playwright-Treiber), aber ohne
# Chromium - das installiert install.sh passend fuers Zielsystem. Wird
# nur per "--target lxc-tarballs" gebaut (siehe build-and-push.ps1), ein
# normaler Image-Build ueberspringt diese beiden Stages. Unter Linux
# gebaut, damit Ausfuehrungsrechte im Tarball korrekt gesetzt sind.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS lxc-build
ARG VERSION=0.0.0
WORKDIR /src
COPY EnpalMqttBridge.csproj .
COPY Program.cs .
RUN set -e; mkdir /dist; \
    for rid in linux-x64 linux-arm64; do \
        dotnet publish -c Release -r "$rid" --self-contained true -o "/out/$rid"; \
        echo "$VERSION" > "/out/$rid/VERSION"; \
        tar -C "/out/$rid" -czf "/dist/enpal-mqtt-bridge-$rid.tar.gz" .; \
    done

FROM scratch AS lxc-tarballs
COPY --from=lxc-build /dist/ /

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

# Verhindert JIT-Abstuerze auf manchen ARM-Systemen/Kerneln (u.a. Raspberry
# Pi) bzw. unter QEMU-Emulation, bei denen W^X-Speicherschutz fuer den JIT
# nicht sauber unterstuetzt wird.
ENV DOTNET_EnableWriteXorExecute=0

ENTRYPOINT ["dotnet", "EnpalMqttBridge.dll"]
