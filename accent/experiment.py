"""Measures accent softening on the user's own recordings: speaker similarity to the original, US-accent probability, and transcript agreement.

usage: python experiment.py --bank native.pt [--bank user.pt] --clip a.wav [--clip b.wav] [--strengths 0,0.3,0.5,0.7,1] [--out dir] [--stream]
"""
from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

import numpy as np
import soundfile as sf

sys.path.insert(0, str(Path(__file__).parent))
from mwaccent import knn, metrics  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bank", action="append", required=True)
    ap.add_argument("--clip", action="append", required=True)
    ap.add_argument("--strengths", default="0,0.3,0.5,0.7,1")
    ap.add_argument("--out", default=None)
    ap.add_argument("--stream", action="store_true", help="use the streaming converter (what the microphone runs)")
    a = ap.parse_args()

    wavlm, gen = knn.load_models()
    strengths = [float(s) for s in a.strengths.split(",")]
    out_dir = Path(a.out) if a.out else None
    rows = []
    for clip_path in a.clip:
        x = metrics.load(clip_path)
        ref_text = metrics.transcribe(x)
        ref_us = metrics.accent_probs(x).get("us", 0.0)
        rows.append((Path(clip_path).stem, "original", 1.0, 1.0, ref_us, 0.0, ref_text, 0.0))
        for bank_path in a.bank:
            bank = knn.Bank.load(Path(bank_path))
            for s in strengths:
                t0 = time.time()
                if a.stream:
                    conv = knn.StreamingConverter(wavlm, gen, bank, strength=s)
                    y = np.concatenate([conv.process(x[i:i + 160]) for i in range(0, len(x), 160)] + [conv.process(np.zeros(conv.latency_samples, np.float32))])
                    y = y[conv.latency_samples:][: len(x)]
                    y = knn.match_loudness(y, x)
                else:
                    y = knn.convert_offline(wavlm, gen, bank, x, strength=s)
                rtf = (time.time() - t0) / (len(x) / 16000)
                sim = metrics.speaker_similarity(x, y)
                us = metrics.accent_probs(y).get("us", 0.0)
                text = metrics.transcribe(y)
                w = metrics.wer(ref_text, text)
                rows.append((Path(clip_path).stem, Path(bank_path).stem, s, sim, us, w, text, rtf))
                if out_dir:
                    out_dir.mkdir(parents=True, exist_ok=True)
                    sf.write(out_dir / f"{Path(clip_path).stem}_{Path(bank_path).stem}_{int(s * 100)}.wav", y, 16000)

    print(f"{'clip':<16}{'bank':<10}{'str':>5}{'spk sim':>9}{'P(us)':>8}{'WER':>7}{'RTF':>7}  transcript")
    for c, b, s, sim, us, w, text, rtf in rows:
        print(f"{c:<16}{b:<10}{s:>5.1f}{sim:>9.3f}{us:>8.3f}{w:>7.2f}{rtf:>7.3f}  {text[:70]}")


if __name__ == "__main__":
    main()
