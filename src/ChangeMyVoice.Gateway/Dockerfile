# syntax=docker/dockerfile:1

# Gebaut wird aus dem Wurzelverzeichnis des Repositorys, damit die zentralen
# Build-Dateien (Directory.Build.props, Directory.Packages.props) mitkommen:
#   docker build -f src/ChangeMyVoice.Gateway/Dockerfile -t changemyvoice-gateway .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Erst die Projektdateien kopieren: Solange sich an den Abhaengigkeiten nichts
# aendert, bleibt die Wiederherstellung im Zwischenspeicher.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ChangeMyVoice.Gateway/ChangeMyVoice.Gateway.csproj src/ChangeMyVoice.Gateway/
RUN dotnet restore src/ChangeMyVoice.Gateway/ChangeMyVoice.Gateway.csproj

COPY src/ChangeMyVoice.Gateway/ src/ChangeMyVoice.Gateway/
RUN dotnet publish src/ChangeMyVoice.Gateway/ChangeMyVoice.Gateway.csproj \
        -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Das Microsoft-Abbild bringt einen Benutzer ohne Sonderrechte bereits mit.
USER $APP_UID

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Der Test laeuft ueber die Anwendung selbst, weil das Laufzeitabbild weder
# curl noch wget mitbringt.
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD ["dotnet", "ChangeMyVoice.Gateway.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "ChangeMyVoice.Gateway.dll"]
