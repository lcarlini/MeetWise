"""Builds the accent bank: native US English sentences (CMU ARCTIC) re-voiced in the user's own timbre with Seed-VC (offline, GPU), then
WavLM layer-6 features of those utterances. At run time the user's live frames are matched against this bank.

usage: python build_bank.py --ref me.wav [--ref more.wav] [--count 300] [--speakers auto|bdl,rms] [--out bank.pt] [--steps 30]
       python build_bank.py --native-only ...   (bank of the native speakers' own voices, for comparison)
"""
from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path

import numpy as np
import soundfile as sf
import torch

sys.path.insert(0, str(Path(__file__).parent))
from mwaccent import BANKS, DATA, REPOS  # noqa: E402
from mwaccent import knn  # noqa: E402
from mwaccent.metrics import load  # noqa: E402

MALE, FEMALE = ["bdl", "rms"], ["slt", "clb"]


def median_f0(wav16: np.ndarray) -> float:
    import librosa
    f0, voiced, _ = librosa.pyin(wav16, fmin=60, fmax=400, sr=16000)
    f0 = f0[voiced & ~np.isnan(f0)]
    return float(np.median(f0)) if len(f0) else 150.0


def arctic_files(speakers: list[str], count: int) -> list[Path]:
    per = max(1, count // len(speakers))
    out = []
    for s in speakers:
        wavs = sorted((DATA / "arctic" / f"cmu_us_{s}_arctic" / "wav").glob("arctic_a*.wav"))
        out += wavs[:per]
    return out


def seedvc(sources: list[Path], ref: Path, out_dir: Path, steps: int) -> list[Path]:
    sys.path.insert(0, str(REPOS / "seed-vc"))
    cwd = os.getcwd()
    os.chdir(REPOS / "seed-vc")  # its config paths are relative
    try:
        from seed_vc_wrapper import SeedVCWrapper
        vc = SeedVCWrapper()
        out_dir.mkdir(parents=True, exist_ok=True)
        done = []
        t0 = time.time()
        for i, src in enumerate(sources):
            dst = out_dir / src.name
            if not dst.exists():
                y = vc.convert_voice(str(src), str(ref), diffusion_steps=steps, inference_cfg_rate=0.7, f0_condition=False,
                                     auto_f0_adjust=False, stream_output=False)
                sf.write(dst, np.asarray(y, dtype=np.float32), 22050)
            done.append(dst)
            if i % 25 == 0:
                print(f"  seed-vc {i + 1}/{len(sources)} ({time.time() - t0:.0f} s)", flush=True)
        return done
    finally:
        os.chdir(cwd)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ref", action="append", required=True)
    ap.add_argument("--count", type=int, default=300)
    ap.add_argument("--speakers", default="auto")
    ap.add_argument("--out", default=str(BANKS / "user.pt"))
    ap.add_argument("--steps", type=int, default=30)
    ap.add_argument("--native-only", action="store_true")
    a = ap.parse_args()

    refs = [load(p) for p in a.ref]
    ref_audio = np.concatenate(refs)
    f0 = median_f0(ref_audio)
    speakers = (MALE if f0 < 165 else FEMALE) if a.speakers == "auto" else a.speakers.split(",")
    print(f"reference {len(ref_audio) / 16000:.1f} s, median F0 {f0:.0f} Hz -> native speakers {speakers}")

    sources = arctic_files(speakers, a.count)
    if a.native_only:
        wavs = sources
    else:
        ref_path = BANKS / "reference.wav"
        ref_path.parent.mkdir(parents=True, exist_ok=True)
        sf.write(ref_path, ref_audio, 16000)
        wavs = seedvc(sources, ref_path, BANKS / "revoiced" / "_".join(speakers), a.steps)

    wavlm, gen = knn.load_models()
    feats = []
    for w in wavs:
        feats.append(knn.features(wavlm, load(w)).half().cpu())
    f = torch.cat(feats)
    bank = knn.Bank(f, f, {"speakers": speakers, "count": len(wavs), "native_only": a.native_only, "ref_f0": f0,
                           "seconds": f.shape[0] * 0.02})
    bank.save(Path(a.out))
    print(f"bank: {f.shape[0]} frames ({f.shape[0] * 0.02 / 60:.1f} min) -> {a.out}")


if __name__ == "__main__":
    main()
