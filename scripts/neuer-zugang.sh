#!/usr/bin/env bash
# Erzeugt einen Zugangsschluessel fuer eine Anwendung.
#
#   ./scripts/neuer-zugang.sh verwaltungs-ui
#
# Ausgegeben werden der Schluessel im Klartext -- einmalig, er wird nirgends
# gespeichert -- und der Eintrag fuer clients.json, der nur den Streuwert
# enthaelt. Aus dem Streuwert laesst sich der Schluessel nicht zurueckrechnen;
# geht er verloren, wird ein neuer erzeugt.

set -euo pipefail

NAME="${1:-}"
LIMIT="${2:-60}"

if [[ -z "$NAME" ]]; then
    echo "Aufruf: $0 <name-der-anwendung> [anfragen-pro-minute]" >&2
    exit 2
fi

SCHLUESSEL="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"

if command -v sha256sum >/dev/null 2>&1; then
    STREUWERT="$(printf '%s' "$SCHLUESSEL" | sha256sum | cut -d' ' -f1)"
else
    STREUWERT="$(printf '%s' "$SCHLUESSEL" | shasum -a 256 | cut -d' ' -f1)"
fi

cat <<AUSGABE

Zugang fuer "$NAME"

  Schluessel (nur jetzt sichtbar, an die Anwendung geben):

    $SCHLUESSEL

  Eintrag fuer deploy/gateway/config/clients.json:

    {
      "Name": "$NAME",
      "KeySha256": "$STREUWERT",
      "RequestsPerMinute": $LIMIT
    }

  Die Datei wird im laufenden Betrieb neu eingelesen; ein Neustart des
  Containers ist nicht noetig.

AUSGABE
