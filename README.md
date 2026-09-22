# ChangeMyVoice

API für **Singing Voice Conversion**: Eine Gesangsaufnahme behält ihre Melodie,
Phrasierung und Darbietung, übernimmt aber das Timbre einer gespeicherten
Referenzstimme.

```text
quelle.wav ──┐
             ├──► Seed-VC ──► ergebnis.wav
referenz.wav ┘
```

Die fachliche und technische Grundlage steht in [init.md](init.md), die
verbindlichen Vorgaben für die Arbeit am Code in [CLAUDE.md](CLAUDE.md).

## Aufbau

Es sind zwei Dienste, weil die Hardware es erzwingt: Die Inferenz braucht
Metal/MPS und muss deshalb auf den Mac; der von außen erreichbare Teil gehört
auf Proxmox, wo Container ohnehin betrieben werden.

```text
Clients ──► Gateway (Docker, Proxmox)
               │  eigener Schlüssel  →  Schlüssel der API
               ▼
           ChangeMyVoice.Api (launchd, Apple Silicon)
               ▼
           mlx-vc ──► Seed-VC ──► MPS
```

Die Architektur folgt dem Hexagonal-Muster. Die Abhängigkeitsrichtung ist nicht
nur vereinbart, sondern wird in `ChangeMyVoice.Domain.Tests` geprüft — ein
Infrastrukturpaket in der Domäne lässt den Build scheitern.

```text
src/
├── ChangeMyVoice.Domain/               Entitäten, Regeln — ohne jede Abhängigkeit
├── ChangeMyVoice.Application/          Anwendungsfälle und Ports
├── ChangeMyVoice.Adapters.Persistence/ SQLite
├── ChangeMyVoice.Adapters.Storage/     Dateisystem
├── ChangeMyVoice.Adapters.Inference/   ffmpeg, ffprobe, Python-Prozess
├── ChangeMyVoice.Api/                  HTTP, Absicherung, Hintergrunddienste
└── ChangeMyVoice.Gateway/              Weiterleitung, im Container
```

## Endpunkte

Alle unter `/api/v1` und nur mit gültigem `X-Api-Key`.

| Methode | Pfad | Zweck |
| --- | --- | --- |
| `POST` | `/voices` | Referenzstimme senden |
| `GET` | `/voices` | Alle Referenzstimmen anzeigen |
| `GET` | `/voices/{id}` | Einzelne Referenzstimme abrufen |
| `DELETE` | `/voices/{id}` | Referenzstimme löschen |
| `POST` | `/jobs` | Stimme zur Änderung senden |
| `GET` | `/jobs` | Alle Aufträge anzeigen (seitenweise, filterbar) |
| `GET` | `/jobs/{id}` | Status abfragen |
| `GET` | `/jobs/{id}/result` | Ergebnis herunterladen |
| `DELETE` | `/jobs/{id}` | Auftrag abbrechen |
| `GET` | `/health/live` | Lebenszeichen (ohne Schlüssel) |
| `GET` | `/health/ready` | Bereitschaft von Modell und Werkzeugen |

Die vollständige Beschreibung liegt unter `/swagger` — auch über das Gateway,
und dort bewusst ohne Schlüssel erreichbar: Ein Browser kann keinen eigenen
Kopf mitschicken, und mit Schlüsselpflicht wäre die Oberfläche unbenutzbar. Sie
zeigt nur die Beschreibung der Endpunkte; die Aufrufe daraus brauchen weiterhin
einen gültigen Schlüssel, der in der Oberfläche unter „Authorize" hinterlegt
wird. Der eingecheckte Stand liegt in
[docs/openapi/v1.json](docs/openapi/v1.json). Ein Test vergleicht beides: Wer
einen Endpunkt ändert, ohne den Vertrag nachzuziehen, bekommt einen roten Build.

Ein typischer Ablauf:

```bash
KEY=...
# 1. Referenzstimme anlegen
curl -X POST http://mac:5080/api/v1/voices -H "X-Api-Key: $KEY" \
     -F "label=Anna" -F "file=@stimme.mp3"

# 2. Auftrag senden
curl -X POST http://mac:5080/api/v1/jobs -H "X-Api-Key: $KEY" \
     -F "voiceId=<id>" -F "source=@gesang.wav"

# 3. Status abfragen, bis COMPLETED
curl http://mac:5080/api/v1/jobs/<jobId> -H "X-Api-Key: $KEY"

# 4. Ergebnis holen
curl http://mac:5080/api/v1/jobs/<jobId>/result -H "X-Api-Key: $KEY" -o ergebnis.wav
```

## Audio-Formate

Angenommen werden **WAV, MP3, FLAC, M4A/AAC und OGG/Opus** — Clients müssen kein
WAV liefern. Jede Datei wird vor dem Anlegen eines Auftrags geprüft (Codec,
Dauer, Kanäle, Abtastrate); eine untaugliche Datei kostet damit weder einen
Platz in der Warteschlange noch Modell-Ladezeit.

Umgerechnet wird **genau einmal**, und zwar direkt auf die Rate, die der
Modelllauf tatsächlich verwendet:

| Pfad | Zielrate | angefordert mit |
| --- | --- | --- |
| ohne F0-Konditionierung (Sprache) | 22 050 Hz | Voreinstellung |
| mit F0-Konditionierung (Gesang) | 44 100 Hz | `f0Condition=true` |

Das ist kein Zufallswert: `seed_vc_infer.py` lädt seine Eingaben mit
`librosa.load(pfad, sr=…)` und `mono=True`, rechnet also selbst um. Würde die API
auf eine andere Rate normalisieren, liefe jede Datei zweimal durch eine
Umrechnung. Referenzstimmen werden als Mono-PCM-WAV mit 44,1 kHz abgelegt — der
höheren der beiden Raten, damit eine einzige Datei beide Pfade bedient.

Referenzen über 25 Sekunden werden gekürzt und das auch gemeldet: Seed-VC
verwendet mit `ref_audio[: sr * 25]` ohnehin nur diesen Anfang.

## Aufräumen

```text
<DataRoot>/
├── changemyvoice.db
├── .lock                            Einzelinstanz-Sperre
├── voices/<id>/reference.wav        wird NIE automatisch entfernt
├── jobs/<id>/{source,reference,output}.wav
└── tmp/                             Uploads in Arbeit
```

Ein Aufräumlauf entfernt alle 15 Minuten abgelaufene Aufträge, hängengebliebene
Läufe, Verzeichnisse ohne zugehörigen Datensatz und Reste aus abgebrochenen
Uploads. **Referenzstimmen sind davon ausgenommen** — im Code hart auf `jobs/`
und `tmp/` beschränkt und durch Tests abgesichert, auf beiden Ebenen: Der
Anwendungsfall darf sie nicht auswählen, und der Dateisystem-Adapter weist einen
Löschversuch außerhalb von `jobs/` ab, selbst wenn er ihn bekäme.

**Nach einem Absturz** greift die Wiederherstellung beim Start, nicht ein
Abschlusscode beim Beenden — der läuft nach einem `SIGKILL` nicht mehr. Jeder
Auftrag trägt die Kennung des Dienstlaufs und die Prozesskennung der Inferenz.
Beim Start gilt daher:

- noch als laufend verzeichnet, aber aus einem fremden Dienstlauf → als
  unterbrochen beendet, Dateien entfernt, ein zurückgebliebener Python-Prozess
  gezielt beendet;
- wartend → erneut eingereiht, die Eingabedateien liegen vollständig vor;
- danach ein vollständiger Aufräumlauf für alles Verwaiste.

Ein unterbrochener Auftrag wird bewusst **nicht** automatisch wiederholt: Ein
Auftrag, der den Dienst mitgerissen hat, etwa durch Speichermangel, würde sonst
bei jedem Start erneut zuschlagen.

## Absicherung

- **Zugangsschlüssel** im Kopf `X-Api-Key`, verglichen wird nur der SHA-256-Wert
  in gleichbleibender Zeit. Der Klartext steht in keiner Konfigurationsdatei.
- **Freigabeliste** für Quelladressen (die des Gateways) → sonst `403`.
- Kestrel bindet an eine konkrete Adresse, nicht an `0.0.0.0`.
- Ohne hinterlegten Schlüssel startet der Dienst gar nicht erst.

Ein Schlüssel-Streuwert lässt sich so erzeugen:

```bash
printf '%s' 'mein-schluessel' | shasum -a 256 | cut -d' ' -f1
```

## Einrichten

```bash
# 1. Python-Umgebung und Modell prüfen bzw. bereitstellen
./scripts/setup-inference.sh
./scripts/setup-inference.sh --with-f0   # zusätzlich der Gesangspfad

# 2. Bauen und testen
dotnet build
dotnet test --filter "Category!=RealInference"

# 3. Starten
dotnet run --project src/ChangeMyVoice.Api
```

Als Dienst auf dem Mac:

```bash
dotnet publish src/ChangeMyVoice.Api -c Release -r osx-arm64 \
    --self-contained false -o ~/Applications/ChangeMyVoice
cp deploy/launchd/com.b-velop.changemyvoice.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.b-velop.changemyvoice.plist
```

### Betrieb über Tailscale

Der Mac und der Gateway-Container sind Knoten desselben Tailnets; dazwischen
liegt kein offener Port im lokalen Netz.

```text
Client ──► voice.idsrv.info
             │  (Reverse Proxy im LAN)
             ▼
           CT "voice" auf Proxmox        Tailscale-Knoten
             │  Client-Schlüssel  →  Schlüssel der API
             ▼  über das Tailnet
           mac-mini-von-marcel:5080      Tailscale-Knoten
```

Drei Punkte hängen zusammen und müssen zueinander passen:

1. **Die API bindet an ihre Tailscale-Adresse**, nicht an `127.0.0.1` und nicht
   an `0.0.0.0`. Damit ist sie im lokalen Netz unsichtbar und ausschließlich
   über das Tailnet erreichbar. Die Adresse steht in der launchd-Einheit und
   lässt sich mit `tailscale ip -4` ermitteln.
2. **Die Freigabeliste enthält die Tailscale-Adresse des Containers** — eine
   einzelne Adresse, nicht der ganze Tailnet-Bereich. Sonst könnte jedes Gerät
   im Tailnet die API direkt ansprechen und das Gateway umgehen, was die
   Vorgabe „Aufträge nur über das Gateway" gerade aushebeln würde.
3. **Das Gateway spricht den MagicDNS-Namen an**, nicht die IP-Adresse. Der
   Name bleibt gültig, falls der Knoten je eine andere Adresse bekommt.

Einrichten des Containers:

```bash
# im CT, nach der Docker-Installation
tailscale up                 # Knoten anmelden
tailscale ip -4              # diese Adresse in die Freigabeliste des Macs

mkdir -p /opt/changemyvoice-gateway && cd /opt/changemyvoice-gateway
curl -fsSLO https://raw.githubusercontent.com/Marcel-B/ChangeMyVoice/main/deploy/gateway/compose.yaml
curl -fsSL  https://raw.githubusercontent.com/Marcel-B/ChangeMyVoice/main/deploy/gateway/.env.beispiel -o .env
# .env ausfüllen, dann:
docker compose pull && docker compose up -d
```

Ein Anmelden an der Registry ist nicht nötig, das Abbild ist öffentlich
abrufbar. In einem LXC-Container braucht Docker `nesting=1` in der
Container-Konfiguration.

Das Gateway ist zustandslos — es haelt weder Auftraege noch Dateien; die
Ergebnisse liegen alle auf dem Mac. Ein Neustart des Containers verliert daher
nichts, und er kommt mit wenig aus: 256 bis 512 MB Arbeitsspeicher und eine
vCPU genuegen. Auch grosse Uploads aendern daran nichts, weil sie
durchgereicht und nicht zwischengespeichert werden.

Das Laufzeitabbild ist ein *chiseled*-Abbild ohne Paketverwaltung und ohne
Kommandozeile. Das haelt es klein und verkleinert die Angriffsflaeche.

Das Gateway auf Proxmox:

```bash
cd deploy/gateway
cp .env.beispiel .env   # Adressen und Schlüssel eintragen
docker compose pull && docker compose up -d
```

## Tests

```bash
dotnet test --filter "Category!=RealInference"
```

Jedes Projekt hat sein Testprojekt. Der Python-Adapter wird nie mit dem echten
Modell getestet, die Audio-Adapter dagegen gegen **echte** ffmpeg-Dateien — die
Zusicherung, dass am Ende genau mono und genau die Zielrate herauskommt, lässt
sich nur an einer tatsächlich umgewandelten Datei nachmessen.

## Bekannte Punkte

- **Modell-Ladezeit pro Auftrag.** `mlx_vc.backend.run_backend` startet intern
  selbst einen Unterprozess, und `seed_vc_infer.py` hält keine Modelle vor. Ein
  dauerhaft geladener Arbeiter, wie ihn init.md §22 vorschlägt, ist damit ohne
  Eingriff in mlx-vc nicht erreichbar. Die Ladezeit wird gemessen und
  protokolliert, damit ein späterer Umbau begründet entschieden werden kann;
  hinter `IVoiceConversionEngine` ist er austauschbar, ohne dass Domäne,
  Anwendungsfälle oder API sich ändern.
- **Der Gesangspfad ist nicht voreingestellt.** `f0_condition` ist standardmäßig
  aus. Erst damit läuft die Konvertierung bei 44,1 kHz mit F0-Konditionierung,
  und erst dann wird die Tonhöhe sauber übertragen — für *Singing* Voice
  Conversion ist das der eigentlich gemeinte Weg. Der Pfad ist eingerichtet und
  erprobt (`./scripts/setup-inference.sh --with-f0`), kostet aber spürbar mehr
  Zeit: bei knapp 15 Sekunden Gesang rund 60 Sekunden gegenüber 40 Sekunden im
  Sprachpfad. Ob er die Voreinstellung werden soll, ist eine Abwägung zwischen
  Qualität und Durchsatz; angefordert wird er bis dahin je Auftrag mit
  `f0Condition=true`.
- **LaunchAgent braucht eine angemeldete Sitzung.** Metal ist im sessionlosen
  Systemkontext nicht verlässlich nutzbar. Für einen dauerhaft erreichbaren
  Dienst sind automatische Anmeldung und `sudo pmset -a sleep 0 disablesleep 1`
  Teil der Einrichtung.
- **Netzweg Proxmox → Mac.** Die Freigabeliste muss die Adresse treffen, mit der
  der Container tatsächlich ankommt — bei Bridge-Netzwerk die des Proxmox-Knotens,
  nicht die des Containers.
