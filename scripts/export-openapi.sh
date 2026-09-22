#!/usr/bin/env bash
# Erzeugt die OpenAPI-Beschreibung der API und schreibt sie an den angegebenen Ort.
#
#   ./scripts/export-openapi.sh docs/openapi/v1.json
#
# Die Beschreibung wird aus der laufenden Anwendung geholt, damit sie genau das
# abbildet, was die Endpunkte tatsaechlich anbieten. Der Dienst startet dafuer
# kurz mit einem eigenen Datenverzeichnis und wird danach wieder beendet.

set -euo pipefail

ZIEL="${1:-docs/openapi/v1.json}"
WURZEL="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${OPENAPI_PORT:-5099}"
TEMP="$(mktemp -d)"

aufraeumen() {
    if [[ -n "${DIENST_PID:-}" ]]; then
        kill "$DIENST_PID" 2>/dev/null || true
        wait "$DIENST_PID" 2>/dev/null || true
    fi
    rm -rf "$TEMP"
}
trap aufraeumen EXIT

cd "$WURZEL"

# Die Pflichtangaben muessen gesetzt sein, sonst bricht die Anwendung beim Start
# ab. Verwendet werden sie hier nicht — es geht nur um die Beschreibung.
ASPNETCORE_ENVIRONMENT=Export \
ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
Storage__DataRoot="$TEMP" \
Persistence__DatabasePath="$TEMP/export.db" \
Inference__PythonExecutable="/usr/bin/false" \
Inference__ScriptPath="/usr/bin/false" \
Inference__WorkingDirectory="$TEMP" \
Security__Clients__0__Name="export" \
Security__Clients__0__KeySha256="0000000000000000000000000000000000000000000000000000000000000000" \
    dotnet run --project src/ChangeMyVoice.Api --configuration Release --no-build --no-launch-profile \
    > "$TEMP/dienst.log" 2>&1 &
DIENST_PID=$!

for _ in $(seq 1 60); do
    if curl -fsS "http://127.0.0.1:${PORT}/health/live" >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

if ! curl -fsS "http://127.0.0.1:${PORT}/health/live" >/dev/null 2>&1; then
    echo "Der Dienst ist nicht gestartet:" >&2
    tail -n 40 "$TEMP/dienst.log" >&2
    exit 1
fi

mkdir -p "$(dirname "$ZIEL")"

# Eingerueckt und mit sortierten Schluesseln, damit Aenderungen im Diff
# nachvollziehbar bleiben statt als eine lange Zeile zu erscheinen.
curl -fsS "http://127.0.0.1:${PORT}/openapi/v1.json" \
    | python3 -c 'import json,sys; json.dump(json.load(sys.stdin), sys.stdout, indent=2, ensure_ascii=False, sort_keys=True); print()' \
    > "$ZIEL"

echo "Beschreibung geschrieben: $ZIEL"
