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
WORKDIR /app
COPY --from=build /app .

ENTRYPOINT ["dotnet", "EnpalMqttBridge.dll"]
