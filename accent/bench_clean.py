"""Phase-1 quality bench: noise removal, background-voice removal (voice isolation) and echo cancellation, scored with DNSMOS P.835,
Whisper word error rate against the known text, and SI-SDR against the clean voice. Everything runs offline through the exact live chain
(MeetWise.App.exe --process).

usage: python bench_clean.py --exe path\\to\\MeetWise.App.exe [--quick] [--out report.md]
"""
from __future__ import annotations

import argparse
import csv
import random
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path

import numpy as np
import soundfile as sf

sys.path.insert(0, str(Path(__file__).parent))
from mwaccent import DATA  # noqa: E402
from mwaccent import metrics as M  # noqa: E402

SR = 16000
NOISES = ["dog", "chirping_birds", "rooster", "keyboard_typing", "mouse_click", "vacuum_cleaner", "crying_baby", "door_wood_knock",
          "clock_tick", "siren", "washing_machine", "car_horn"]
TARGETS = ["rms", "slt"]          # one male, one female US speaker
INTERFERERS = {"rms": "bdl", "slt": "clb"}


def arctic(speaker: str):
    root = DATA / "arctic" / f"cmu_us_{speaker}_arctic"
    texts = {}
    for line in (root / "etc" / "txt.done.data").read_text().splitlines():
        line = line.strip()[1:-1].strip()
        uid, text = line.split(" ", 1)
        texts[uid] = text.strip().strip('"')
    return root / "wav", texts


def utterance(speaker: str, ids: list[str]):
    wav_dir, texts = arctic(speaker)
    parts, words = [], []
    for i in ids:
        parts.append(M.load(wav_dir / f"{i}.wav"))
        parts.append(np.zeros(SR // 4, np.float32))
        words.append(texts[i])
    x = np.concatenate(parts)
    return x / (np.abs(x).max() + 1e-9) * 0.5, " ".join(words)


def esc50(category: str, rng: random.Random) -> np.ndarray:
    root = DATA / "esc50" / "ESC-50-master"
    rows = [r for r in csv.DictReader(open(root / "meta" / "esc50.csv")) if r["category"] == category]
    clips = [M.load(root / "audio" / r["filename"]) for r in rng.sample(rows, min(4, len(rows)))]
    return np.concatenate(clips)


def fit(noise: np.ndarray, n: int) -> np.ndarray:
    reps = int(np.ceil(n / max(1, len(noise))))
    return np.tile(noise, reps)[:n]


def mix(clean: np.ndarray, noise: np.ndarray, snr_db: float) -> np.ndarray:
    noise = fit(noise, len(clean))
    pc = np.mean(clean ** 2) + 1e-12
    pn = np.mean(noise ** 2) + 1e-12
    return clean + noise * np.sqrt(pc / (pn * 10 ** (snr_db / 10)))


def room_echo(far: np.ndarray, delay_ms=60, decay_ms=80, gain=0.7, seed=1) -> np.ndarray:
    rng = np.random.default_rng(seed)
    n = int(SR * 0.25)
    t = np.arange(n) / SR
    h = rng.standard_normal(n) * np.exp(-t / (decay_ms / 1000 / 6.9))
    h[: int(SR * delay_ms / 1000)] = 0
    h[int(SR * delay_ms / 1000)] = 1.0
    h = h / np.sqrt(np.sum(h ** 2)) * gain
    return np.convolve(far, h)[: len(far)].astype(np.float32)


class Runner:
    def __init__(self, exe: str):
        self.exe = exe
        self.tmp = Path(tempfile.mkdtemp(prefix="mwbench-"))
        self.n = 0

    def process(self, x: np.ndarray, *args: str, far: np.ndarray | None = None) -> np.ndarray:
        self.n += 1
        i, o = self.tmp / f"in{self.n}.wav", self.tmp / f"out{self.n}.wav"
        sf.write(i, x, SR, subtype="FLOAT")
        cmd = [self.exe, "--process", str(i), str(o), *args]
        if far is not None:
            fp = self.tmp / f"far{self.n}.wav"
            sf.write(fp, far, SR, subtype="FLOAT")
            cmd += ["--far", str(fp)]
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        if r.returncode != 0:
            raise RuntimeError(r.stderr or r.stdout)
        y = M.load(o)
        return M.align(y, x)[: len(x)]


def score(y: np.ndarray, clean: np.ndarray, text: str, dns: M.DNSMOS) -> dict:
    d = dns.score(y)
    hyp = M.transcribe(y)
    return {**d, "WER": M.wer(text, hyp), "SI-SDR": M.si_sdr(y, clean)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True)
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--out", default="QUALITY-REPORT.md")
    a = ap.parse_args()
    rng = random.Random(7)
    dns = M.DNSMOS()
    run = Runner(a.exe)
    snrs = [0, 5] if a.quick else [0, 5, 10]
    noises = NOISES[:5] if a.quick else NOISES
    systems = {"raw": None, "Light": ["--noise", "Light"], "Balanced": ["--noise", "Balanced"], "Max": ["--noise", "Max"]}
    res = defaultdict(list)   # (section, system) -> list of score dicts
    clean_scores = []

    for spk in TARGETS:
        ids = [f"arctic_a{n:04d}" for n in rng.sample(range(100, 500), 2)]
        clean, text = utterance(spk, ids)
        clean_scores.append(score(clean, clean, text, dns))

        # 1. noise
        for cat in noises:
            noise = esc50(cat, rng)
            for snr in snrs:
                noisy = mix(clean, noise, snr)
                for name, args in systems.items():
                    y = noisy if args is None else run.process(noisy, *args, "--noagc")
                    res[("noise", name)].append(score(y, clean, text, dns))
            print(f"  noise {spk} {cat} done", flush=True)

        # 2. background voices: a nearby person talking at 0 and 5 dB below/at the user's level
        other, _ = utterance(INTERFERERS[spk], [f"arctic_b{n:04d}" for n in rng.sample(range(100, 500), 3)])
        enrol, _ = utterance(spk, [f"arctic_a{n:04d}" for n in rng.sample(range(500, 590), 3)])
        ep = run.tmp / f"enrol_{spk}.wav"
        sf.write(ep, enrol, SR, subtype="FLOAT")
        for sir in [0, 5]:
            noisy = mix(clean, other, sir)
            for name, args in {"raw": None, "Max": ["--noise", "Max"], "Voice isolation + Max": ["--noise", "Max", "--voicelock", "--voice", str(ep)]}.items():
                y = noisy if args is None else run.process(noisy, *args, "--noagc")
                res[("voices", name)].append(score(y, clean, text, dns))
        print(f"  voices {spk} done", flush=True)

        # 3. echo: the other side's voice comes back through the speakers; the user talks over it half of the time
        far, _ = utterance(INTERFERERS[spk], [f"arctic_b{n:04d}" for n in rng.sample(range(100, 500), 4)])
        far = fit(far, len(clean) * 2)
        near = np.concatenate([np.zeros(len(clean), np.float32), clean])
        mic = near + room_echo(far)
        for name, args in {"raw": None, "Echo + Max": ["--noise", "Max"]}.items():
            y = mic if args is None else run.process(mic, *args, "--noagc", far=far)
            first = slice(SR, len(clean))           # far end only: how much echo is left
            erle = 10 * np.log10(np.mean(mic[first] ** 2) / (np.mean(y[first] ** 2) + 1e-12))
            s = score(y[len(clean):], clean, text, dns)  # double talk: is the user's voice intact
            s["ERLE"] = erle
            res[("echo", name)].append(s)
        print(f"  echo {spk} done", flush=True)

    lines = ["# MeetWise quality report (phase 1)", "",
             "Offline, through the exact live chain (`MeetWise.App.exe --process`, 48 kHz, 10 ms blocks). Clean speech: CMU ARCTIC US speakers "
             "(rms, slt) with known text. Noise: ESC-50. Higher DNSMOS and SI-SDR are better; lower WER is better.", ""]
    cs = {k: np.mean([c[k] for c in clean_scores]) for k in clean_scores[0]}
    lines.append(f"Clean speech itself (the ceiling): DNSMOS OVRL {cs['OVRL']:.2f}, SIG {cs['SIG']:.2f}, BAK {cs['BAK']:.2f}, WER {cs['WER'] * 100:.1f} %")
    lines.append("")
    for section, title, cols in [("noise", f"Noise ({len(noises)} ESC-50 categories × SNR {snrs} dB)", ["OVRL", "SIG", "BAK", "P808", "WER", "SI-SDR"]),
                                 ("voices", "Background voices (a nearby person at SIR 0 and 5 dB)", ["OVRL", "SIG", "BAK", "WER", "SI-SDR"]),
                                 ("echo", "Echo (far end through speakers, then double talk)", ["ERLE", "OVRL", "SIG", "BAK", "WER", "SI-SDR"])]:
        lines += [f"## {title}", "", "| System | " + " | ".join(cols) + " |", "|---" * (len(cols) + 1) + "|"]
        for (sec, name), items in res.items():
            if sec != section:
                continue
            vals = []
            for c in cols:
                v = np.mean([i[c] for i in items])
                vals.append(f"{v * 100:.1f} %" if c == "WER" else f"{v:.2f}")
            lines.append(f"| {name} | " + " | ".join(vals) + " |")
        lines.append("")
    Path(a.out).write_text("\n".join(lines), encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
