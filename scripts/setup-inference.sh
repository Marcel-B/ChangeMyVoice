#!/usr/bin/env bash
# Prueft die Python-Umgebung und stellt das Modell bereit.
#
#   ./scripts/setup-inference.sh            # pruefen, Modell bei Bedarf holen
#   ./scripts/setup-inference.sh --check    # nur pruefen, nichts holen
#   ./scripts/setup-inference.sh --with-f0  # zusaetzlich den Gesangspfad einrichten
#
# WICHTIG: Dieses Skript veraendert die bestehende Umgebung nicht. init.md §30
# untersagt ausdruecklich `pip install -U`, `uv sync` und `uv lock --upgrade`.
# Abweichende Paketversionen werden gemeldet, aber nicht "korrigiert" — die
# Kombination aus torch 2.14.0, huggingface-hub 0.28.1 und transformers 4.46.3
# ist erprobt, und ein gut gemeintes Update macht sie kaputt.

set -euo pipefail

MLX_VC="${MLX_VC_PATH:-$HOME/mlx-vc}"
SEED_VC="${SEED_VC_PATH:-$HOME/seed-vc-ref}"
PYTHON="$MLX_VC/.venv/bin/python"
WURZEL="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WRAPPER="$WURZEL/scripts/changemyvoice_infer.py"

NUR_PRUEFEN=0
MIT_F0=0
for arg in "$@"; do
    case "$arg" in
        --check) NUR_PRUEFEN=1 ;;
        --with-f0) MIT_F0=1 ;;
        *) echo "Unbekannte Option: $arg" >&2; exit 2 ;;
    esac
done

FEHLER=0
ok()      { printf '  \033[32mOK\033[0m      %s\n' "$1"; }
warnung() { printf '  \033[33mHINWEIS\033[0m %s\n' "$1"; }
fehler()  { printf '  \033[31mFEHLT\033[0m   %s\n' "$1"; FEHLER=1; }

echo "== 1. Werkzeuge und Verzeichnisse =="

[[ -x "$PYTHON" ]] && ok "Python-Interpreter: $PYTHON" \
    || fehler "Python-Interpreter fehlt: $PYTHON (siehe init.md §5)"
[[ -d "$SEED_VC" ]] && ok "Seed-VC: $SEED_VC" \
    || fehler "Seed-VC fehlt: $SEED_VC (siehe init.md §7)"
[[ -f "$WRAPPER" ]] && ok "Aufrufwrapper: $WRAPPER" \
    || fehler "Aufrufwrapper fehlt: $WRAPPER"
command -v ffmpeg  >/dev/null && ok "ffmpeg"  || fehler "ffmpeg fehlt (brew install ffmpeg)"
command -v ffprobe >/dev/null && ok "ffprobe" || fehler "ffprobe fehlt (brew install ffmpeg)"

if [[ $FEHLER -ne 0 ]]; then
    echo
    echo "Die Grundlage stimmt noch nicht. Die Einrichtung ist in init.md beschrieben." >&2
    exit 1
fi

echo
echo "== 2. Pakete und Grafikbeschleunigung =="

PRUEFUNG="$("$PYTHON" - <<'PYEOF'
import json
ergebnis = {}
try:
    import torch, transformers, huggingface_hub
    ergebnis["torch"] = torch.__version__
    ergebnis["transformers"] = transformers.__version__
    ergebnis["huggingface_hub"] = huggingface_hub.__version__
    ergebnis["mps"] = bool(torch.backends.mps.is_available())
    import librosa, soundfile, munch, matplotlib  # noqa: F401
    ergebnis["weitere"] = True
except Exception as exc:
    ergebnis["fehler"] = str(exc)
print(json.dumps(ergebnis))
PYEOF
)"

python3 - "$PRUEFUNG" <<'PYEOF'
import json, sys
d = json.loads(sys.argv[1])

if "fehler" in d:
    print(f"  \033[31mFEHLT\033[0m   Abhaengigkeiten: {d['fehler']}")
    sys.exit(1)

# Der in init.md §33 festgehaltene, erfolgreich getestete Stand.
erwartet = {"torch": "2.14.0", "huggingface_hub": "0.28.1", "transformers": "4.46.3"}

for name, soll in erwartet.items():
    ist = d.get(name, "?")
    if ist == soll:
        print(f"  \033[32mOK\033[0m      {name} {ist}")
    else:
        print(f"  \033[33mHINWEIS\033[0m {name} {ist} (erprobt war {soll}) — bewusst nicht angepasst")

if d.get("mps"):
    print("  \033[32mOK\033[0m      Grafikbeschleunigung (MPS) verfuegbar")
else:
    print("  \033[31mFEHLT\033[0m   Grafikbeschleunigung (MPS) nicht verfuegbar")
    sys.exit(1)
PYEOF

echo
echo "== 3. Modelldateien =="

CACHE="${HF_HUB_CACHE:-$SEED_VC/checkpoints/hf_cache}"
echo "  Zwischenspeicher: $CACHE"

modell_vorhanden() {
    [[ -d "$MLX_VC/checkpoints/models--Plachta--Seed-VC" ]] \
        && [[ -d "$MLX_VC/checkpoints/models--funasr--campplus" ]]
}

if modell_vorhanden; then
    ok "Seed-VC-Checkpoint (Sprachpfad, 22,05 kHz)"
else
    warnung "Seed-VC-Checkpoint fehlt noch"

    if [[ $NUR_PRUEFEN -eq 1 ]]; then
        echo "  Nur-Pruefen-Modus: es wird nichts geholt."
    else
        echo "  Es wird ein einmaliger Aufwaermlauf gestartet, der die Dateien holt."
        echo "  Das kann beim ersten Mal einige Minuten dauern."
        ANLAUF="$(mktemp -d)"
        trap 'rm -rf "$ANLAUF"' EXIT
        ffmpeg -hide_banner -v error -y -f lavfi \
            -i "sine=frequency=440:sample_rate=44100:duration=5" "$ANLAUF/quelle.wav"
        cp "$ANLAUF/quelle.wav" "$ANLAUF/referenz.wav"

        ( cd "$MLX_VC" && SEED_VC_PATH="$SEED_VC" HF_HUB_CACHE="$CACHE" \
            "$PYTHON" "$WRAPPER" \
                --source "$ANLAUF/quelle.wav" \
                --reference "$ANLAUF/referenz.wav" \
                --output "$ANLAUF/ergebnis.wav" )

        modell_vorhanden && ok "Seed-VC-Checkpoint geholt" || fehler "Der Aufwaermlauf war erfolglos"
    fi
fi

# Der Gesangspfad braucht ein anderes Checkpoint (44,1 kHz mit
# F0-Konditionierung). Erst damit wird die Tonhoehe sauber uebertragen — ohne ihn
# klingt das Ergebnis eher nach Sprachumwandlung als nach Gesang.
if [[ $MIT_F0 -eq 1 ]]; then
    echo
    echo "== 4. Gesangspfad (f0_condition) =="
    echo "  Es wird ein Aufwaermlauf mit F0-Konditionierung gestartet."

    ANLAUF_F0="$(mktemp -d)"
    trap 'rm -rf "$ANLAUF_F0"' EXIT
    ffmpeg -hide_banner -v error -y -f lavfi \
        -i "sine=frequency=440:sample_rate=44100:duration=5" "$ANLAUF_F0/quelle.wav"
    cp "$ANLAUF_F0/quelle.wav" "$ANLAUF_F0/referenz.wav"

    ( cd "$MLX_VC" && SEED_VC_PATH="$SEED_VC" HF_HUB_CACHE="$CACHE" \
        "$PYTHON" "$WRAPPER" \
            --source "$ANLAUF_F0/quelle.wav" \
            --reference "$ANLAUF_F0/referenz.wav" \
            --output "$ANLAUF_F0/ergebnis.wav" \
            --f0-condition ) && ok "Gesangspfad einsatzbereit" \
        || fehler "Der Gesangspfad liess sich nicht einrichten"
fi

echo
if [[ $FEHLER -eq 0 ]]; then
    echo "Die Umgebung ist einsatzbereit."
    if [[ $MIT_F0 -eq 0 ]]; then
        echo "Hinweis: Fuer den eigentlichen Gesangspfad zusaetzlich --with-f0 ausfuehren."
    fi
else
    echo "Es sind offene Punkte verblieben." >&2
    exit 1
fi
