# Eine externe Oberfläche anbinden

Wie eine Verwaltungsoberfläche Zugriff auf das Gateway bekommt.

## Der Aufbau, der hier vorausgesetzt wird

```text
Browser ──► UI-Backend ──► voice.idsrv.info ──► Mac-API
              │
              └─ hier liegt der Zugangsschlüssel
```

Die Oberfläche hat einen Server-Teil, der das Gateway aufruft. Das ist wichtig,
und zwar aus zwei Gründen:

**Der Schlüssel bleibt geheim.** Ruft eine reine Browser-Anwendung das Gateway
direkt auf, steht der Schlüssel im ausgelieferten JavaScript — jeder Besucher
kann ihn auslesen und damit selbst Aufträge starten, die Rechenzeit auf dem Mac
verbrauchen. Im Server-Teil erreicht er den Browser nie.

**Es braucht kein CORS.** Der Aufruf passiert von Server zu Server, nicht aus
dem Browser heraus. Die Herkunftsprüfung, an der eine Browser-Anwendung ohne
weitere Konfiguration scheitern würde, kommt gar nicht erst ins Spiel.

Soll die Oberfläche das Gateway doch direkt aus dem Browser ansprechen, ist
beides nachzuholen: eine Freigabe der Herkunft im Gateway und ein anderer
Umgang mit dem Schlüssel — etwa kurzlebige, serverseitig ausgegebene Marken.
Beides ist derzeit nicht eingebaut.

## 1. Zugang anlegen

Auf dem Entwicklungsrechner:

```bash
./scripts/neuer-zugang.sh verwaltungs-ui 120
```

Das gibt den Schlüssel einmalig aus und dazu den Eintrag für die
Konfigurationsdatei. Der Schlüssel wird nirgends gespeichert; aus dem Streuwert
lässt er sich nicht zurückrechnen. Geht er verloren, wird ein neuer erzeugt.

## 2. Eintragen

Auf dem Container-Host, in `/opt/changemyvoice-gateway/config/clients.json`.
Der Dateiname lautet **`clients.json`** mit s, und der Rahmen mit `Gateway` und
`Clients` gehört dazu — ein einzelnes Objekt allein wird nicht erkannt, und der
Dienst startet dann nicht:

```json
{
  "Gateway": {
    "Clients": [
      { "Name": "verwaltungs-ui", "KeySha256": "…", "RequestsPerMinute": 120 },
      { "Name": "studio-app",     "KeySha256": "…", "RequestsPerMinute": 60 }
    ]
  }
}
```

Das Verzeichnis `config/` muss existieren, weil die compose-Datei es einhängt —
leer sein darf es. Ein erster Aufrufer kommt bereits aus den
Umgebungsvariablen, damit das Gateway auch ohne diese Datei startet.

Änderungen werden im laufenden Betrieb übernommen.

Ein eigener Eintrag je Anwendung lohnt sich: Nur so lässt sich ein einzelner
Zugang entziehen, ohne die anderen zu stören, und nur so zeigen die Protokolle,
wer welchen Auftrag ausgelöst hat. Das Ratenlimit gilt je Anwendung.

## 3. Aus der Oberfläche aufrufen

Der Schlüssel gehört in den Kopf `X-Api-Key`, gesetzt im Server-Teil:

```csharp
builder.Services.AddHttpClient("changemyvoice", client =>
{
    client.BaseAddress = new Uri("https://voice.idsrv.info");
    client.DefaultRequestHeaders.Add(
        "X-Api-Key", builder.Configuration["ChangeMyVoice:ApiKey"]);
    // Die Konvertierung selbst laeuft im Hintergrund; die Aufrufe hier sind
    // kurz. Lediglich Upload und Download koennen dauern.
    client.Timeout = TimeSpan.FromMinutes(5);
});
```

## Der Ablauf einer Oberfläche

| Zweck | Aufruf |
| --- | --- |
| Stimmen anzeigen | `GET /api/v1/voices` |
| Stimme anlegen | `POST /api/v1/voices` (multipart: `label`, `file`) |
| Stimme löschen | `DELETE /api/v1/voices/{id}` |
| Übersicht der Aufträge | `GET /api/v1/jobs?limit=50&offset=0` |
| Nur laufende zeigen | `GET /api/v1/jobs?status=RUNNING` |
| Auftrag starten | `POST /api/v1/jobs` (multipart: `source`, `voiceId`) |
| Fortschritt | `GET /api/v1/jobs/{id}` |
| Ergebnis | `GET /api/v1/jobs/{id}/result` |
| Abbrechen | `DELETE /api/v1/jobs/{id}` |

Für die Übersicht liefert `GET /api/v1/jobs` neben `items` auch `total`, womit
sich eine Blätterung anzeigen lässt. Die jüngsten Aufträge stehen zuoberst.

**Zum Fortschritt:** Es gibt keine Benachrichtigung, die Oberfläche fragt den
Zustand ab. Ein Abstand von fünf bis zehn Sekunden reicht, häufigeres Fragen
bringt nichts und zählt gegen das Ratenlimit.

Die Antwort auf `POST /api/v1/jobs` enthält `estimatedDurationSeconds`. Zeig den
Wert an — die Läufe dauern lange, und ohne Angabe wirkt der Dienst hängen
geblieben. Gemessen auf dem eingesetzten Mac:

| Material | Rechenzeit |
| --- | --- |
| 15 s | gut 1 Minute |
| 3 Minuten | rund 25 Minuten |
| 5 Minuten | rund 55 Minuten |
| 7 Minuten (Maximum) | rund 95 Minuten |

Der Aufwand wächst überproportional. Länger als sieben Minuten wird deshalb
sofort abgelehnt, mit einer Meldung, die die geschätzte Rechenzeit nennt —
besser als ein Abbruch nach anderthalb Stunden, bei dem nichts übrig bliebe.

Mit `diffusionSteps=25` statt der voreingestellten 50 halbiert sich die
Rechenzeit ungefähr, bei etwas geringerer Qualität.

**Zur Abtastrate:** Das Ergebnis kommt mit 48 kHz, passend zu üblichen
Projekteinstellungen. Braucht dein Projekt etwas anderes, setz
`outputSampleRate` beim Anlegen des Auftrags — etwa `44100` für die
unveränderte Ausgabe des Modells.

**Zum Ergebnis:** Es bleibt nach dem ersten Abruf noch eine Stunde liegen,
insgesamt höchstens 24 Stunden. Die Oberfläche sollte es also herunterladen und
selbst ablegen, wenn es dauerhaft verfügbar sein soll. `resultSha256` aus der
Statusantwort erlaubt zu prüfen, ob der Transfer vollständig war.

## Fehler, mit denen zu rechnen ist

Alle Fehler kommen als `application/problem+json` mit einem Feld `code`, das
sich auswerten lässt:

| Lage | Antwort | `code` |
| --- | --- | --- |
| Schlüssel fehlt oder falsch | 401 | — |
| Datei unbrauchbar | 400 | `INVALID_AUDIO` |
| Format nicht unterstützt | 400 | `UNSUPPORTED_FORMAT` |
| Referenz zu kurz | 400 | `REFERENCE_TOO_SHORT` |
| Bezeichnung vergeben | 409 | `DUPLICATE_VOICE_LABEL` |
| Stimme noch in Verwendung | 409 | `VOICE_IN_USE` |
| Ergebnis noch nicht fertig | 409 | `RESULT_NOT_READY` |
| Ergebnis schon aufgeräumt | 410 | `RESULT_GONE` |
| Warteschlange voll | 503 | `QUEUE_FULL` |
| Ratenlimit überschritten | 429 | — |
| Mac nicht erreichbar | 503 | `UPSTREAM_UNAVAILABLE` |

Die beiden letzten lohnen eine eigene Behandlung: Bei `429` und `503` ist ein
späterer Versuch sinnvoll, bei `400` dagegen nie — dort stimmt etwas an der
Datei, und ein erneuter Versuch mit derselben ändert nichts.

## Zum Ausprobieren

Die Schnittstellenbeschreibung liegt unter
[https://voice.idsrv.info/swagger](https://voice.idsrv.info/swagger). Sie ist
ohne Schlüssel erreichbar; für die Aufrufe daraus wird er über *Authorize*
hinterlegt.
