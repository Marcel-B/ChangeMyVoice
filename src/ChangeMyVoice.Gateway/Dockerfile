# syntax=docker/dockerfile:1

# Gebaut wird aus dem Wurzelverzeichnis des Repositorys, damit die zentralen
# Build-Dateien (Directory.Build.props, Directory.Packages.props) mitkommen:
#   docker build -f src/ChangeMyVoice.Gateway/Dockerfile -t changemyvoice-gateway .

ARG ZIEL_RID=linux-x64

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG ZIEL_RID
WORKDIR /src

# Erst die Projektdateien kopieren: Solange sich an den Abhaengigkeiten nichts
# aendert, bleibt die Wiederherstellung im Zwischenspeicher.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ChangeMyVoice.Gateway/ChangeMyVoice.Gateway.csproj src/ChangeMyVoice.Gateway/
RUN dotnet restore src/ChangeMyVoice.Gateway/ChangeMyVoice.Gateway.csproj \
        --runtime "$ZIEL_RID"

COPY src/ChangeMyVoice.Gateway/ src/ChangeMyVoice.Gateway/

# Die Laufzeit-Kennung wird ausdruecklich gesetzt, damit eine startfaehige
# Programmdatei entsteht. Das schlanke Laufzeitabbild bringt die dotnet-CLI
# nicht mit, weshalb die Anwendung sich selbst starten koennen muss.
RUN dotnet publish src/ChangeMyVoice.Gateway/ChangeMyVoice.Gateway.csproj \
        -c Release -o /app \
        --runtime "$ZIEL_RID" --self-contained false --no-restore

# Das schlanke Abbild enthaelt weder Paketverwaltung noch Kommandozeile. Das
# halbiert die Groesse und verkleinert zugleich die Angriffsflaeche: Was nicht
# da ist, kann auch nicht missbraucht werden.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

# Im Microsoft-Abbild bereits als Benutzer ohne Sonderrechte vorbereitet.
USER $APP_UID

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Der Test laeuft ueber die Anwendung selbst, weil das Laufzeitabbild weder
# curl noch wget noch die dotnet-CLI mitbringt.
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD ["/app/ChangeMyVoice.Gateway", "--healthcheck"]

ENTRYPOINT ["/app/ChangeMyVoice.Gateway"]
