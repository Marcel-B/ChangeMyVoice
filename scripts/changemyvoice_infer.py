#!/usr/bin/env python3
"""Singing Voice Conversion mit Seed-VC, im eigenen Prozess.

Dieses Skript veraendert die bestehende Python-Umgebung nicht. Es wird mit dem
Interpreter der eingerichteten venv gestartet und schreibt genau eine JSON-Zeile
auf die Standardausgabe:

    {"status": "ok", "modelLoadMs": 41230, "inferenceMs": 8120}
    {"status": "error", "code": "OUT_OF_MEMORY", "message": "..."}

Die Zuordnung zu den Fehlercodes aus init.md Paragraph 24 passiert hier, weil
Python die Ausnahmen kennt. Der .NET-Adapter hat zusaetzlich eine grobe
Ersatzzuordnung ueber die Fehlerausgabe, falls dieses Skript gar nicht erst
bis zur Ausgabe kommt.

Frueher rief das Skript mlx_vc.backend.run_backend auf. Dessen Seed-VC-Backend
laedt im Gesangspfad zwar das F0-Modell, reicht den Tonhoehenverlauf aber nie an
den Length Regulator weiter (f0=None); das Modell bekommt dann nur seine
F0-Maske und muss die Tonhoehe aus dem Inhalt erraten. Deshalb laeuft die
Inferenz jetzt hier, nach dem Vorbild von Seed-VCs eigener inference.py: mit
Tonhoehenverlauf, Halbtonverschiebung und Tonlagenabgleich. Der Rest (Laden,
Stueckelung, Vocoder in inference_mode, fp32 fuer soundfile) folgt dem
mlx-vc-Skript, das auf dem Mac erprobt ist. Benutzt werden weiterhin dessen
venv und Seed-VCs Checkpoints; neu geladen wird nichts.
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


def build_parser():
    parser = argparse.ArgumentParser(description="Singing Voice Conversion ueber Seed-VC.")
    parser.add_argument("--source", required=True)
    parser.add_argument("--reference", required=True)
    parser.add_argument("--output", required=True)
    # Nur noch aus Kompatibilitaet zum Aufruf des Dienstes; es gibt nur Seed-VC.
    parser.add_argument("--backend", default="seed-vc")
    parser.add_argument("--diffusion-steps", type=int, default=50)
    parser.add_argument("--inference-cfg-rate", type=float, default=0.7)
    parser.add_argument("--length-adjust", type=float, default=1.0)
    parser.add_argument("--f0-condition", action="store_true")
    parser.add_argument("--semi-tone-shift", type=int, default=0)
    parser.add_argument("--auto-f0-adjust", action="store_true")
    parser.add_argument("--no-fp16", action="store_true")
    return parser


def semitone_factor(n_semitones):
    """Der Faktor, um den eine Frequenz fuer n Halbtoene waechst."""
    return 2 ** (n_semitones / 12)


def patch_bigvgan():
    """Gibt BigVGAN._from_pretrained die Parameter zurueck, die neuere
    huggingface_hub-Versionen nicht mehr uebergeben (aus mlx-vc uebernommen)."""
    try:
        from modules.bigvgan import bigvgan

        original = bigvgan.BigVGAN._from_pretrained.__func__

        @classmethod
        def patched(cls, *, proxies=None, resume_download=False, **kwargs):
            return original(cls, proxies=proxies, resume_download=resume_download, **kwargs)

        bigvgan.BigVGAN._from_pretrained = patched
    except Exception as exc:  # pragma: no cover - haengt an der Umgebung
        print("Warnung: BigVGAN-Anpassung gescheitert: %s" % exc, file=sys.stderr)


def seed_vc_path():
    """SEED_VC_PATH setzt der Dienst; sonst wie mlx-vc neben dessen Checkout."""
    path = os.environ.get("SEED_VC_PATH")
    if not path:
        import mlx_vc

        path = os.path.join(os.path.dirname(mlx_vc.__file__), "..", "..", "seed-vc-ref")
    return os.path.abspath(path)


def load(args):
    """Laedt Seed-VC und liefert die Modellteile samt Geraet."""
    root = seed_vc_path()
    if not os.path.isdir(root):
        raise FileNotFoundError("Seed-VC fehlt unter %s (checkpoint)" % root)

    sys.path.insert(0, root)
    os.environ.setdefault("HF_HUB_CACHE", os.path.join(root, "checkpoints", "hf_cache"))
    patch_bigvgan()

    from types import SimpleNamespace

    # inference.py legt beim Import sein Geraet fest (MPS auf dem Mac) und
    # laedt im Gesangspfad zusaetzlich RMVPE als F0-Extraktor.
    import inference

    parts = inference.load_models(SimpleNamespace(
        checkpoint=None, config=None, f0_condition=args.f0_condition, fp16=not args.no_fp16))
    return inference, parts


def convert(args, inference, parts):
    import librosa
    import numpy as np
    import soundfile
    import torch
    import torchaudio

    model, semantic_fn, f0_fn, vocoder_fn, campplus_model, mel_fn, _ = parts
    device = inference.device
    f0_condition = args.f0_condition
    fp16 = not args.no_fp16

    sr = 44100 if f0_condition else 22050
    hop_length = 512 if f0_condition else 256
    max_context_window = sr // hop_length * 30
    overlap_frame_len = 16
    overlap_wave_len = overlap_frame_len * hop_length

    source_audio = librosa.load(args.source, sr=sr)[0]
    # Seed-VC verwendet von der Referenz ohnehin nur die ersten 25 Sekunden.
    ref_audio = librosa.load(args.reference, sr=sr)[0][: sr * 25]

    source = torch.tensor(source_audio).unsqueeze(0).float().to(device)
    ref = torch.tensor(ref_audio).unsqueeze(0).float().to(device)

    with torch.inference_mode():
        source_16k = torchaudio.functional.resample(source, sr, 16000)
        ref_16k = torchaudio.functional.resample(ref, sr, 16000)

        # Whisper sieht hoechstens 30 Sekunden; laengeres Material in
        # ueberlappenden Stuecken.
        if source_16k.size(-1) <= 16000 * 30:
            s_source = semantic_fn(source_16k)
        else:
            overlap = 5
            chunks = []
            buffer = None
            position = 0
            while position < source_16k.size(-1):
                if buffer is None:
                    chunk = source_16k[:, position:position + 16000 * 30]
                else:
                    chunk = torch.cat(
                        [buffer, source_16k[:, position:position + 16000 * (30 - overlap)]], dim=-1)
                s = semantic_fn(chunk)
                chunks.append(s if position == 0 else s[:, 50 * overlap:])
                buffer = chunk[:, -16000 * overlap:]
                position += 30 * 16000 if position == 0 else chunk.size(-1) - 16000 * overlap
            s_source = torch.cat(chunks, dim=1)

        s_ref = semantic_fn(ref_16k)
        source_mel = mel_fn(source.float())
        ref_mel = mel_fn(ref.float())

        fbank = torchaudio.compliance.kaldi.fbank(
            ref_16k, num_mel_bins=80, dither=0, sample_frequency=16000)
        style = campplus_model((fbank - fbank.mean(dim=0, keepdim=True)).unsqueeze(0))

        # Der Tonhoehenverlauf, den mlx-vc nie weitergab. Verschoben wird nur
        # der stimmhafte Teil (F0 > 1); stumme Stellen bleiben 0.
        f0_source = None
        f0_ref = None
        if f0_condition:
            f0_source = torch.from_numpy(f0_fn(source_16k[0], thred=0.03)).to(device)[None]
            f0_ref = torch.from_numpy(f0_fn(ref_16k[0], thred=0.03)).to(device)[None]
            voiced = f0_source > 1
            shifted = f0_source.clone()
            if args.auto_f0_adjust and voiced.any() and (f0_ref > 1).any():
                log_source = torch.log(f0_source[voiced] + 1e-5)
                log_ref = torch.log(f0_ref[f0_ref > 1] + 1e-5)
                shifted[voiced] = torch.exp(log_source - torch.median(log_source) + torch.median(log_ref))
            if args.semi_tone_shift != 0:
                shifted[voiced] = shifted[voiced] * semitone_factor(args.semi_tone_shift)
            f0_source = shifted

        target_lengths = torch.LongTensor([int(source_mel.size(2) * args.length_adjust)]).to(device)
        ref_lengths = torch.LongTensor([ref_mel.size(2)]).to(device)
        cond = model.length_regulator(s_source, ylens=target_lengths, n_quantizers=3, f0=f0_source)[0]
        prompt = model.length_regulator(s_ref, ylens=ref_lengths, n_quantizers=3, f0=f0_ref)[0]

        max_source_window = max_context_window - ref_mel.size(2)
        processed = 0
        waves = []
        previous = None
        while processed < cond.size(1):
            chunk = cond[:, processed:processed + max_source_window]
            last = processed + max_source_window >= cond.size(1)
            condition = torch.cat([prompt, chunk], dim=1)
            with torch.autocast(device_type=device.type,
                                dtype=torch.float16 if fp16 else torch.float32):
                target = model.cfm.inference(
                    condition, torch.LongTensor([condition.size(1)]).to(device),
                    ref_mel, style, None, args.diffusion_steps,
                    inference_cfg_rate=args.inference_cfg_rate)
                target = target[:, :, ref_mel.size(-1):]
                # soundfile schreibt kein fp16.
                wave = vocoder_fn(target.float()).float().squeeze()[None, :]

            if previous is None:
                if last:
                    waves.append(wave[0].cpu().numpy())
                    break
                waves.append(wave[0, :-overlap_wave_len].cpu().numpy())
            elif last:
                waves.append(inference.crossfade(
                    previous.cpu().numpy(), wave[0].cpu().numpy(), overlap_wave_len))
                break
            else:
                waves.append(inference.crossfade(
                    previous.cpu().numpy(), wave[0, :-overlap_wave_len].cpu().numpy(),
                    overlap_wave_len))
            previous = wave[0, -overlap_wave_len:]
            processed += target.size(2) - overlap_frame_len

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    soundfile.write(args.output, np.concatenate(waves), sr)


def main():
    args = build_parser().parse_args()

    for label, path in (("source", args.source), ("reference", args.reference)):
        if not os.path.isfile(path):
            fail(INVALID_AUDIO, "Die Datei fuer %s fehlt: %s" % (label, path))

    if args.backend != "seed-vc":
        fail(MODEL_LOAD_FAILED, "Unbekanntes Backend: %s" % args.backend)

    if not args.f0_condition and (args.semi_tone_shift != 0 or args.auto_f0_adjust):
        fail(INFERENCE_FAILED, "Tonhoehenverschiebung gibt es nur mit --f0-condition.")

    started = time.perf_counter()

    try:
        inference, parts = load(args)
    except Exception as exc:
        traceback.print_exc(file=sys.stderr)
        code = classify(exc)
        fail(code if code in (MODEL_DOWNLOAD_FAILED, OUT_OF_MEMORY, MPS_ERROR) else MODEL_LOAD_FAILED, exc)
        return

    model_loaded = time.perf_counter()

    try:
        convert(args, inference, parts)
    except Exception as exc:
        # Der vollstaendige Traceback gehoert ins Log des Dienstes, nicht in die
        # Antwort an den Aufrufer (init.md Paragraph 24).
        traceback.print_exc(file=sys.stderr)
        fail(classify(exc), exc)
        return

    finished = time.perf_counter()

    if not os.path.isfile(args.output) or os.path.getsize(args.output) < 100:
        fail(OUTPUT_NOT_CREATED, "Es entstand keine verwertbare Ausgabedatei.")

    emit({
        "status": "ok",
        "modelLoadMs": int((model_loaded - started) * 1000),
        "inferenceMs": int((finished - model_loaded) * 1000),
    })


if __name__ == "__main__":
    main()
