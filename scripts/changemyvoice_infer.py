#!/usr/bin/env python3
"""Duenner Aufrufwrapper um mlx_vc.backend.run_backend.

Dieses Skript veraendert die bestehende Python-Umgebung nicht. Es wird mit dem
Interpreter der eingerichteten venv gestartet und schreibt genau eine JSON-Zeile
auf die Standardausgabe:

    {"status": "ok", "modelLoadMs": 41230, "inferenceMs": 8120}
    {"status": "error", "code": "OUT_OF_MEMORY", "message": "..."}

Die Zuordnung zu den Fehlercodes aus init.md Paragraph 24 passiert hier, weil
Python die Ausnahmen kennt. Der .NET-Adapter hat zusaetzlich eine grobe
Ersatzzuordnung ueber die Fehlerausgabe, falls dieses Skript gar nicht erst
bis zur Ausgabe kommt.
"""

import argparse
import json
import os
import sys
import time
import traceback

# Fehlercodes nach init.md Paragraph 24.
INVALID_AUDIO = "INVALID_AUDIO"
MODEL_LOAD_FAILED = "MODEL_LOAD_FAILED"
MODEL_DOWNLOAD_FAILED = "MODEL_DOWNLOAD_FAILED"
INFERENCE_FAILED = "INFERENCE_FAILED"
OUT_OF_MEMORY = "OUT_OF_MEMORY"
MPS_ERROR = "MPS_ERROR"
OUTPUT_NOT_CREATED = "OUTPUT_NOT_CREATED"


def emit(payload):
    """Schreibt genau eine JSON-Zeile und beendet den Lauf."""
    sys.stdout.write(json.dumps(payload) + "\n")
    sys.stdout.flush()


def fail(code, message):
    emit({"status": "error", "code": code, "message": str(message)[:2000]})
    sys.exit(1)


def classify(exc):
    """Ordnet eine Ausnahme einem Fehlercode zu."""
    name = type(exc).__name__
    text = str(exc).lower()

    if "out of memory" in text or "oom" in text or name == "MemoryError":
        return OUT_OF_MEMORY
    if "mps" in text or "metal" in text:
        return MPS_ERROR
    # Netzwerk- oder Hub-Fehler beim Nachladen der Checkpoints.
    if any(marker in text for marker in ("huggingface", "hf_hub", "connection", "timed out", "404")):
        return MODEL_DOWNLOAD_FAILED
    if any(marker in text for marker in ("checkpoint", "no such file", "state_dict", "config")):
        return MODEL_LOAD_FAILED
    if "output" in text and "not" in text:
        return OUTPUT_NOT_CREATED
    if any(marker in text for marker in ("audio", "soundfile", "librosa", "sample")):
        return INVALID_AUDIO
    return INFERENCE_FAILED


def main():
    parser = argparse.ArgumentParser(description="Singing Voice Conversion ueber Seed-VC.")
    parser.add_argument("--source", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--backend", default="seed-vc")
    parser.add_argument("--diffusion-steps", type=int, default=50)
    parser.add_argument("--inference-cfg-rate", type=float, default=0.7)
    parser.add_argument("--length-adjust", type=float, default=1.0)
    parser.add_argument("--f0-condition", action="store_true")
    parser.add_argument("--no-fp16", action="store_true")
    args = parser.parse_args()

    for label, path in (("source", args.source), ("reference", args.reference)):
        if not os.path.isfile(path):
            fail(INVALID_AUDIO, "Die Datei fuer %s fehlt: %s" % (label, path))

    started = time.perf_counter()

    try:
        from mlx_vc.backend import run_backend
    except Exception as exc:  # pragma: no cover - haengt an der Umgebung
        fail(MODEL_LOAD_FAILED, "mlx_vc liess sich nicht laden: %s" % exc)
        return

    model_loaded = time.perf_counter()

    try:
        run_backend(
            args.backend,
            source=args.source,
            reference=args.reference,
            output=args.output,
            verbose=False,
            diffusion_steps=args.diffusion_steps,
            inference_cfg_rate=args.inference_cfg_rate,
            length_adjust=args.length_adjust,
            f0_condition=args.f0_condition,
            fp16=not args.no_fp16,
        )
    except Exception as exc:
        # Der vollstaendige Traceback gehoert ins Log des Dienstes, nicht in die
        # Antwort an den Aufrufer (init.md Paragraph 24).
        traceback.print_exc(file=sys.stderr)
        fail(classify(exc), exc)
        return

    finished = time.perf_counter()

    # run_backend meldet Erfolg auch dann, wenn die Datei winzig ist; deshalb
    # hier noch einmal selbst nachsehen.
    if not os.path.isfile(args.output) or os.path.getsize(args.output) < 100:
        fail(OUTPUT_NOT_CREATED, "Es entstand keine verwertbare Ausgabedatei.")

    emit({
        "status": "ok",
        "modelLoadMs": int((model_loaded - started) * 1000),
        "inferenceMs": int((finished - model_loaded) * 1000),
    })


if __name__ == "__main__":
    main()
