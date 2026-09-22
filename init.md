# Seed-VC + mlx-vc auf Apple Silicon

## Zweck

Dieses Dokument beschreibt eine **erfolgreich getestete Installation** von `mlx-vc` zusammen mit `Seed-VC` auf einem Apple-Silicon-Mac.

Ziel ist **Singing Voice Conversion (SVC)**:

```text
source.wav
    │
    │ vorhandener Gesang
    │ Melodie / Timing / Performance
    ▼
 Seed-VC
    ▲
    │ Zielstimme / Timbre
    │
reference.wav
    │
    ▼
out.wav
```

Das Verfahren ist **Audio → Audio**.

Es handelt sich nicht um Text-to-Speech. Die vorhandene Gesangsperformance aus `source.wav` soll erhalten bleiben, während das Stimmtimbre anhand von `reference.wav` verändert wird.

Dieses Dokument soll außerdem als technische Grundlage für die Implementierung einer eigenen API dienen.

---

# 1. Getestete Umgebung

Getestet wurde:

- macOS
- Apple Silicon
- Python 3.10 innerhalb der venv
- `uv`
- `mlx-vc`
- `Seed-VC`
- PyTorch mit MPS/Metal
- Seed-VC über das `mlx-vc` Backend

Die letztlich funktionierende Python-Umgebung enthielt unter anderem:

```text
Torch:        2.14.0
HF Hub:       0.28.1
Transformers: 4.46.3
MPS:          True
```

Der komplette Voice-Conversion-Lauf wurde erfolgreich durchgeführt.

---

# 2. Verzeichnisstruktur

Die beiden Repositories liegen nebeneinander:

```text
/Users/<USER>/
├── mlx-vc/
│   ├── .venv/
│   ├── source.wav
│   ├── reference.wav
│   └── out.wav
│
└── seed-vc-ref/
```

Wichtig:

`mlx-vc` erwartet für das Seed-VC-Backend standardmäßig das Seed-VC-Repository unter:

```text
../seed-vc-ref
```

wenn der aktuelle Working Directory `mlx-vc` ist.

---

# 3. uv installieren

Falls `uv` noch nicht vorhanden ist:

```bash
brew install uv
```

Prüfen:

```bash
uv --version
```

---

# 4. mlx-vc herunterladen

```bash
cd ~
git clone https://github.com/feiyuehchen/mlx-vc.git
cd mlx-vc
```

---

# 5. Virtuelle Python-Umgebung erstellen

```bash
uv venv
```

Aktivieren:

```bash
source .venv/bin/activate
```

Danach sollte der Shell-Prompt normalerweise anzeigen, dass `.venv` aktiv ist.

---

# 6. mlx-vc installieren

Innerhalb von:

```text
~/mlx-vc
```

ausführen:

```bash
uv pip install -e ".[all,dev]"
```

---

# 7. Seed-VC herunterladen

Seed-VC wird parallel zum `mlx-vc`-Verzeichnis abgelegt.

Ausgehend von:

```text
~/mlx-vc
```

ausführen:

```bash
cd ..
git clone https://github.com/Plachtaa/seed-vc.git seed-vc-ref
cd mlx-vc
```

Danach:

```text
~/mlx-vc
~/seed-vc-ref
```

---

# 8. PyTorch installieren

In der getesteten Installation musste PyTorch zusätzlich installiert werden:

```bash
uv pip install torch torchaudio
```

Die resultierende getestete Version war:

```text
torch 2.14.0
```

---

# 9. Apple-MPS-Unterstützung testen

Test:

```bash
uv run python -c "import torch; print('PyTorch:', torch.__version__); print('MPS verfügbar:', torch.backends.mps.is_available()); print('MPS eingebaut:', torch.backends.mps.is_built())"
```

Erfolgreiches Ergebnis:

```text
PyTorch: 2.14.0
MPS verfügbar: True
MPS eingebaut: True
```

Beide MPS-Werte sollten `True` sein.

Damit kann PyTorch die Apple-GPU über Metal Performance Shaders verwenden.

---

# 10. Problem mit requirements-mac.txt

Seed-VC besitzt:

```text
../seed-vc-ref/requirements-mac.txt
```

Die Datei enthielt bei der getesteten Version unter anderem:

```text
--extra-index-url https://download.pytorch.org/whl/cu121
torch --pre --extra-index-url https://download.pytorch.org/whl/nightly/cpu
torchvision --pre --extra-index-url https://download.pytorch.org/whl/nightly/cpu
torchaudio --pre --extra-index-url https://download.pytorch.org/whl/nightly/cpu
accelerate
scipy==1.13.1
librosa==0.10.2
huggingface-hub>=0.28.1
munch==4.0.0
einops==0.8.0
descript-audio-codec==1.0.0
gradio==5.23.0
pydub==0.25.1
resemblyzer
jiwer==3.0.3
transformers==4.46.3
FreeSimpleGUI==5.1.1
soundfile==0.12.1
sounddevice==0.5.0
modelscope==1.18.1
funasr==1.1.5
numpy==1.26.4
pyyaml
python-dotenv
hydra-core==1.3.2
```

Direkt:

```bash
uv pip install -r ../seed-vc-ref/requirements-mac.txt
```

funktionierte NICHT.

Fehler:

```text
Expected `--hash`, found `"--pre"`
```

Die ersten vier Zeilen enthalten pip-spezifische Torch-Konfigurationen, die mit `uv` in dieser Form Probleme verursachen.

Außerdem ist die CUDA-Konfiguration für Apple Silicon nicht relevant.

---

# 11. Seed-VC-Abhängigkeiten installieren

Da PyTorch bereits funktionierte, wurden die ersten vier Zeilen übersprungen.

```bash
tail -n +5 ../seed-vc-ref/requirements-mac.txt > /tmp/seed-vc-mac.txt
```

Danach:

```bash
uv pip install -r /tmp/seed-vc-mac.txt
```

Zusätzlich wurde benötigt:

```bash
uv pip install matplotlib
```

Ohne `matplotlib` erschien beim Start:

```text
Warning: BigVGAN patch failed: No module named 'matplotlib'
```

---

# 12. Hugging-Face-Version korrigieren

Nach Installation der Dependencies entstand ein Versionskonflikt.

Installiert war:

```text
huggingface-hub==1.32.0
```

Seed-VC verwendet jedoch:

```text
transformers==4.46.3
```

Diese Transformers-Version verlangt:

```text
huggingface-hub>=0.23.2,<1.0
```

Seed-VC selbst verlangt mindestens:

```text
huggingface-hub>=0.28.1
```

Deshalb wurde explizit folgende Version installiert:

```bash
uv pip install "huggingface-hub==0.28.1"
```

Diese Kombination funktionierte:

```text
huggingface-hub 0.28.1
transformers     4.46.3
```

---

# 13. Wichtig: Danach nicht mehr uv run verwenden

Während der Einrichtung wurde beobachtet, dass:

```bash
uv run ...
```

die Umgebung erneut anhand der `mlx-vc`-Projektdefinition synchronisieren kann.

Dabei erschien beispielsweise:

```text
Uninstalled 6 packages
Installed 6 packages
```

Das kann die manuell hergestellte Seed-VC-Abhängigkeitskombination verändern.

Deshalb wird nach Abschluss der Installation direkt der Python-Interpreter aus der virtuellen Umgebung verwendet:

```text
.venv/bin/python
```

statt:

```text
uv run python
```

---

# 14. Finale Dependency-Prüfung

Aus `~/mlx-vc`:

```bash
.venv/bin/python -c "import torch, munch, matplotlib, librosa, transformers, huggingface_hub; print('Dependencies: OK'); print('Torch:', torch.__version__); print('HF Hub:', huggingface_hub.__version__); print('Transformers:', transformers.__version__); print('MPS:', torch.backends.mps.is_available())"
```

Erfolgreich getestete Ausgabe:

```text
Dependencies: OK
Torch: 2.14.0
HF Hub: 0.28.1
Transformers: 4.46.3
MPS: True
```

Dieser Stand hat anschließend erfolgreich Seed-VC ausgeführt.

---

# 15. Audiodateien vorbereiten

Im Verzeichnis:

```text
~/mlx-vc/
```

werden zwei Dateien abgelegt:

```text
source.wav
reference.wav
```

## source.wav

Enthält den Gesang, der konvertiert werden soll.

Diese Aufnahme liefert unter anderem:

- Melodie
- Timing
- Phrasierung
- Performance
- Gesangsverlauf

## reference.wav

Enthält die Zielstimme.

Sie dient hauptsächlich als Referenz für das gewünschte Stimmtimbre.

Für gute Ergebnisse sollte die Referenz möglichst:

- sauber
- isoliert
- trocken
- ohne Instrumental
- ohne Backing Vocals
- mit wenig oder keinem Reverb
- mit wenig oder keinem Delay

sein.

---

# 16. Voice Conversion starten

Der erfolgreich getestete Aufruf lautet:

```bash
.venv/bin/python -c "from mlx_vc.backend import run_backend; run_backend('seed-vc', source='source.wav', reference='reference.wav', output='out.wav')"
```

Das Ergebnis wird geschrieben nach:

```text
~/mlx-vc/out.wav
```

Dieser Aufruf wurde erfolgreich getestet.

---

# 17. Python-Aufruf für eine API

Der zentrale Python-Aufruf lautet:

```python
from mlx_vc.backend import run_backend

run_backend(
    "seed-vc",
    source="source.wav",
    reference="reference.wav",
    output="out.wav",
)
```

Dies sollte die Basis einer eigenen API bilden.

---

# 18. Wichtig: mlx_vc.generate ist nicht der gewünschte Weg

Folgender CLI-Befehl:

```bash
mlx_vc.generate
```

zeigte:

```text
--text
--ref_audio
```

und erwartet Text als Quelle.

Beispiel:

```text
mlx_vc.generate --model seed-vc --text "Hello" --ref_audio ref.wav
```

Das ist NICHT der gewünschte Workflow.

Für dieses Projekt wird benötigt:

```text
Audio → Audio
```

also:

```text
source.wav + reference.wav → out.wav
```

Dafür wird verwendet:

```python
mlx_vc.backend.run_backend()
```

---

# 19. Vorschlag für eigene REST-API

Eine erste API könnte beispielsweise mit FastAPI implementiert werden.

Konzeptioneller Endpoint:

```text
POST /api/v1/voice-conversion
```

Request:

```text
multipart/form-data
```

mit:

```text
source
reference
```

Optional später:

```text
model
pitch_shift
output_format
quality
```

Beispiel:

```text
POST /api/v1/voice-conversion

source=@vocals.wav
reference=@voice.wav
model=seed-vc
```

Response zunächst:

```json
{
  "job_id": "123456",
  "status": "processing"
}
```

Später:

```text
GET /api/v1/jobs/123456
```

Response:

```json
{
  "job_id": "123456",
  "status": "completed",
  "output": "/api/v1/jobs/123456/audio"
}
```

Audio:

```text
GET /api/v1/jobs/123456/audio
```

---

# 20. Empfohlene Architektur

Seed-VC sollte nicht unmittelbar innerhalb eines HTTP-Request-Handlers initialisiert werden.

Empfohlen:

```text
Client
   │
   ▼
FastAPI
   │
   ▼
Job Queue
   │
   ▼
Voice Conversion Worker
   │
   ▼
mlx-vc
   │
   ▼
Seed-VC
   │
   ▼
Apple MPS
```

Grund:

Machine-Learning-Inferenz kann lange dauern und erhebliche Ressourcen beanspruchen.

HTTP-Verbindungen sollten deshalb nicht unnötig lange blockieren.

---

# 21. Temporäre Dateien

Für jeden Job sollte ein eigenes Verzeichnis verwendet werden:

```text
jobs/
└── <UUID>/
    ├── source.wav
    ├── reference.wav
    ├── output.wav
    └── metadata.json
```

Beispiel:

```text
jobs/
└── e97df26c/
    ├── source.wav
    ├── reference.wav
    ├── output.wav
    └── metadata.json
```

Nach einer konfigurierbaren Zeit können abgeschlossene Jobs gelöscht werden.

---

# 22. Modell nicht für jeden Request neu laden

Für eine produktive API sollte untersucht werden, ob das Seed-VC-Modell dauerhaft im Worker gehalten werden kann.

Ungünstig:

```text
Request
→ Python starten
→ Modelle laden
→ konvertieren
→ Prozess beenden

Request
→ Python starten
→ Modelle wieder laden
→ ...
```

Besser:

```text
Worker startet
      │
      ▼
Modelle laden
      │
      ▼
Modell bleibt im Speicher
      │
      ├── Job 1
      ├── Job 2
      ├── Job 3
      └── ...
```

Das reduziert insbesondere die Model-Load-Zeit.

---

# 23. Concurrency auf Apple Silicon

Mehrere parallele Seed-VC-Inferenzen sollten nicht automatisch gestartet werden.

MPS verwendet gemeinsame GPU-/Unified-Memory-Ressourcen.

Für eine erste Version empfiehlt sich:

```text
max_concurrent_inference = 1
```

Die API darf mehrere Jobs annehmen, die tatsächliche GPU-Verarbeitung erfolgt jedoch zunächst seriell:

```text
Job 1 ──► Seed-VC
Job 2 ──► wartet
Job 3 ──► wartet
```

Später kann getestet werden, ob der jeweilige Mac mehrere parallele Jobs sinnvoll verkraftet.

---

# 24. Fehlerbehandlung

Die API sollte mindestens folgende Fehler unterscheiden:

```text
INVALID_AUDIO
UNSUPPORTED_FORMAT
REFERENCE_TOO_SHORT
MODEL_LOAD_FAILED
MODEL_DOWNLOAD_FAILED
INFERENCE_FAILED
OUT_OF_MEMORY
MPS_ERROR
OUTPUT_NOT_CREATED
TIMEOUT
```

Ein Fehler sollte nicht nur als HTTP 500 zurückgegeben werden.

Beispiel:

```json
{
  "job_id": "123456",
  "status": "failed",
  "error": {
    "code": "INFERENCE_FAILED",
    "message": "Seed-VC inference failed."
  }
}
```

Interne Tracebacks sollten geloggt, aber nicht ungefiltert an externe Clients ausgeliefert werden.

---

# 25. Audio normalisieren

Vor der Inferenz sollte die API Audio validieren.

Zu prüfen:

```text
Datei lesbar?
Audio vorhanden?
Dauer > 0?
Kanäle gültig?
Sample Rate gültig?
```

Optional kann das Eingangsmaterial intern in ein definiertes WAV-Format konvertiert werden.

Beispielsweise:

```text
PCM WAV
mono
44.1 kHz
```

Dabei sollte allerdings geprüft werden, welche Vorverarbeitung Seed-VC selbst bereits durchführt, damit Audio nicht unnötig mehrfach resampled wird.

---

# 26. ffmpeg

Für eine spätere API ist `ffmpeg` sinnvoll, damit Clients nicht zwingend WAV liefern müssen.

Beispielsweise:

```text
MP3
FLAC
M4A
AAC
WAV
```

können serverseitig normalisiert werden.

Pipeline:

```text
Upload
  │
  ▼
ffmpeg
  │
  ▼
normalized.wav
  │
  ▼
Seed-VC
```

---

# 27. Referenzstimmen verwalten

Später kann die API gespeicherte Stimmen unterstützen.

Beispielsweise:

```text
voices/
├── alice/
│   └── reference.wav
├── bob/
│   └── reference.wav
└── singer-01/
    └── reference.wav
```

API:

```text
POST /api/v1/voices
GET  /api/v1/voices
GET  /api/v1/voices/{id}
DELETE /api/v1/voices/{id}
```

Danach wäre eine Conversion möglich über:

```json
{
  "voice_id": "singer-01"
}
```

statt jedes Mal `reference.wav` hochzuladen.

Für gespeicherte oder fremde Stimmen sollten Einwilligung, Nutzungsrechte und Missbrauchsschutz berücksichtigt werden.

---

# 28. Mögliche spätere Pipeline für Musik

Für Musikproduktion könnte die komplette Pipeline später so aussehen:

```text
Song
 │
 ▼
Stem Separation
 │
 ├── Drums
 ├── Bass
 ├── Other
 └── Vocals
       │
       ▼
    Seed-VC
       │
       ▼
Converted Vocals
       │
       ▼
Mix mit Original-Stems
       │
       ▼
Final Song
```

Damit könnte eine Anwendung aus einem vollständigen Song automatisch:

1. Vocal-Stem extrahieren
2. Stimme konvertieren
3. neue Vocal-Spur erzeugen
4. Song wieder zusammensetzen

---

# 29. API-MVP

Für Version 1 sollte die Implementierung bewusst klein bleiben.

## Endpoint

```text
POST /convert
```

Input:

```text
source.wav
reference.wav
```

Processing:

```python
run_backend(
    "seed-vc",
    source=source_path,
    reference=reference_path,
    output=output_path,
)
```

Output:

```text
output.wav
```

Erst wenn das stabil funktioniert, sollten Job Queue, Voice Library, Stem Separation usw. ergänzt werden.

---

# 30. Hinweise für eine Coding-KI

Wenn eine KI auf Basis dieses Dokuments die API implementieren soll, gelten folgende Anforderungen.

## Primärziel

Implementiere zunächst eine lokale API für:

```text
Audio source + voice reference
                ↓
             Seed-VC
                ↓
          converted audio
```

## Verwende

```python
from mlx_vc.backend import run_backend
```

und:

```python
run_backend(
    "seed-vc",
    source=source_path,
    reference=reference_path,
    output=output_path,
)
```

## Nicht verwenden

Nicht den Text-to-Speech-CLI-Weg:

```text
mlx_vc.generate --text ...
```

für Singing Voice Conversion verwenden.

## Python Interpreter

Die funktionierende Umgebung befindet sich unter:

```text
~/mlx-vc/.venv/bin/python
```

Die API sollte innerhalb dieser Environment ausgeführt werden.

Während der getesteten Einrichtung führte `uv run` zu einer erneuten Synchronisierung von Dependencies. Deshalb sollte die funktionierende Umgebung nicht automatisch verändert oder aktualisiert werden.

## Dependency-Versionen

Bekannter funktionierender Stand:

```text
torch==2.14.0
huggingface-hub==0.28.1
transformers==4.46.3
```

MPS:

```text
torch.backends.mps.is_available() == True
```

## Keine automatischen Updates

Die Coding-KI darf NICHT eigenmächtig:

```text
pip install -U
uv sync
uv lock --upgrade
pip install transformers -U
pip install huggingface-hub -U
```

ausführen.

Die Umgebung ist bereits funktionierend und soll reproduzierbar bleiben.

---

# 31. Reproduzierbarkeit

Sobald die API funktioniert, sollte der aktuelle Dependency-Stand eingefroren werden.

Beispielsweise:

```bash
.venv/bin/pip freeze > requirements-working.txt
```

Alternativ sollte später ein eigener sauberer `pyproject.toml`/Lockfile-Stand für die API erstellt werden.

Wichtig ist:

**Der funktionierende Zustand sollte zuerst konserviert werden, bevor Dependencies modernisiert werden.**

---

# 32. Bekannte Besonderheit

Das ursprüngliche Seed-VC-GitHub-Repository ist archiviert.

Das bedeutet nicht, dass das Modell nicht funktioniert.

Der in diesem Dokument beschriebene Aufbau wurde erfolgreich ausgeführt.

Es bedeutet jedoch, dass zukünftige Änderungen an:

- Python
- PyTorch
- Transformers
- Hugging Face Hub
- macOS
- MPS

zu Inkompatibilitäten führen können, die vom ursprünglichen Seed-VC-Projekt nicht mehr behoben werden.

Deshalb sind Dependency-Pinning und reproduzierbare Environments besonders wichtig.

---

# 33. Erfolgreich getesteter Endzustand

Folgende Prüfung funktionierte:

```bash
.venv/bin/python -c "import torch, munch, matplotlib, librosa, transformers, huggingface_hub; print('Dependencies: OK'); print('Torch:', torch.__version__); print('HF Hub:', huggingface_hub.__version__); print('Transformers:', transformers.__version__); print('MPS:', torch.backends.mps.is_available())"
```

Ausgabe:

```text
Dependencies: OK
Torch: 2.14.0
HF Hub: 0.28.1
Transformers: 4.46.3
MPS: True
```

Anschließend wurde erfolgreich ausgeführt:

```bash
.venv/bin/python -c "from mlx_vc.backend import run_backend; run_backend('seed-vc', source='source.wav', reference='reference.wav', output='out.wav')"
```

Das Voice-Conversion-Verfahren lief erfolgreich durch.

Damit ist dieser Stand die **bekannte funktionierende Baseline** für die weitere API-Entwicklung.

---

# 34. Nächster Entwicklungsschritt

Als nächstes sollte eine minimale FastAPI-Anwendung erstellt werden:

```text
POST /convert
```

mit zwei Uploads:

```text
source
reference
```

Die API speichert beide Dateien temporär, ruft Seed-VC auf und liefert anschließend das erzeugte WAV zurück.

Erst nachdem dieser Minimalfall zuverlässig funktioniert, sollte die Architektur um folgende Komponenten erweitert werden:

```text
Job Queue
Voice Library
Model Cache
Stem Separation
Progress Reporting
Web UI
Authentication
Persistent Storage
```

Die wichtigste Regel für die nächste Entwicklungsphase lautet:

> Die bestehende, erfolgreich getestete Seed-VC-Umgebung zunächst nicht verändern. Die API wird um die funktionierende Inferenz herum gebaut – nicht umgekehrt.