"""Objective measures used by the bench: speaker similarity, accent probability, ASR transcripts and WER, DNSMOS, SI-SDR, ERLE."""
from __future__ import annotations

import functools
import os
import re

import numpy as np
import torch

from . import DATA, HOME

SR = 16000
_SB = HOME / "speechbrain"


def to16k(wav: np.ndarray, sr: int) -> np.ndarray:
    if sr == SR:
        return wav.astype(np.float32)
    import librosa
    return librosa.resample(wav.astype(np.float32), orig_sr=sr, target_sr=SR)


def load(path, sr: int = SR) -> np.ndarray:
    import soundfile as sf
    x, r = sf.read(str(path), dtype="float32", always_2d=True)
    return to16k(x.mean(axis=1), r) if sr == SR else x.mean(axis=1)


@functools.lru_cache(None)
def _speaker_model():
    from speechbrain.inference.speaker import EncoderClassifier
    from speechbrain.utils.fetching import LocalStrategy
    return EncoderClassifier.from_hparams("speechbrain/spkrec-ecapa-voxceleb", savedir=str(_SB / "ecapa"), run_opts={"device": "cuda"},
                                          local_strategy=LocalStrategy.COPY)


@torch.inference_mode()
def speaker_embedding(wav16: np.ndarray) -> torch.Tensor:
    return _speaker_model().encode_batch(torch.from_numpy(wav16)[None].cuda()).squeeze().float()


def speaker_similarity(a16: np.ndarray, b16: np.ndarray) -> float:
    return float(torch.nn.functional.cosine_similarity(speaker_embedding(a16), speaker_embedding(b16), dim=0))


@functools.lru_cache(None)
def _accent_model():
    from speechbrain.inference.classifiers import EncoderClassifier
    from speechbrain.utils.fetching import LocalStrategy
    return EncoderClassifier.from_hparams("Jzuluaga/accent-id-commonaccent_ecapa", savedir=str(_SB / "accent"), run_opts={"device": "cuda"},
                                          local_strategy=LocalStrategy.COPY)


@torch.inference_mode()
def accent_probs(wav16: np.ndarray) -> dict[str, float]:
    m = _accent_model()
    out_prob, _, _, _ = m.classify_batch(torch.from_numpy(wav16)[None].cuda())
    p = torch.softmax(out_prob.squeeze().float(), dim=-1) if out_prob.squeeze().min() < 0 else out_prob.squeeze().float()
    labels = m.hparams.label_encoder.decode_ndim(torch.arange(p.shape[-1]))
    return {str(l): float(v) for l, v in zip(labels, p.cpu())}


@functools.lru_cache(None)
def _asr():
    from transformers import pipeline
    return pipeline("automatic-speech-recognition", model="openai/whisper-large-v3-turbo", torch_dtype=torch.float16, device="cuda")


def transcribe(wav16: np.ndarray) -> str:
    r = _asr()({"raw": wav16, "sampling_rate": SR}, generate_kwargs={"language": "en", "task": "transcribe"})
    return r["text"].strip()


def normalize_text(t: str) -> str:
    t = t.lower().replace("-", " ")
    t = re.sub(r"[^a-z0-9' ]", " ", t)
    return " ".join(t.split())


def wer(ref: str, hyp: str) -> float:
    import jiwer
    r, h = normalize_text(ref), normalize_text(hyp)
    if not r:
        return 0.0 if not h else 1.0
    return float(jiwer.wer(r, h))


class DNSMOS:
    """Microsoft DNSMOS P.835 (SIG, BAK, OVRL) and P.808, ONNX models from the DNS Challenge repository."""
    INPUT = 144160  # 9.01 s at 16 kHz

    def __init__(self):
        import onnxruntime as ort
        d = DATA / "dnsmos"
        self.p835 = ort.InferenceSession(str(d / "sig_bak_ovr.onnx"), providers=["CPUExecutionProvider"])
        self.p808 = ort.InferenceSession(str(d / "model_v8.onnx"), providers=["CPUExecutionProvider"])

    @staticmethod
    def _poly(sig, bak, ovr):
        # personalized=False polynomial fit from the official dnsmos_local.py
        p_ovr = np.poly1d([-0.06766283, 1.11546468, 0.04602535])
        p_sig = np.poly1d([-0.08397278, 1.22083953, 0.0052439])
        p_bak = np.poly1d([-0.13166888, 1.60915514, -0.39604546])
        return p_sig(sig), p_bak(bak), p_ovr(ovr)

    def _mel(self, audio):
        import librosa
        mel = librosa.feature.melspectrogram(y=audio, sr=SR, n_fft=321, hop_length=160, n_mels=120)
        return ((librosa.power_to_db(mel, ref=np.max) + 40) / 40).T

    def score(self, wav16: np.ndarray) -> dict[str, float]:
        audio = wav16.astype(np.float32)
        while len(audio) < self.INPUT:
            audio = np.concatenate([audio, audio])
        hop = SR
        n = int(np.floor(len(audio) / SR) - 9.01) + 1
        rows = []
        for i in range(max(1, n)):
            seg = audio[i * hop: i * hop + self.INPUT]
            if len(seg) < self.INPUT:
                continue
            x = seg[None]
            s, b, o = self.p835.run(None, {"input_1": x})[0][0]
            p808 = self.p808.run(None, {"input_1": self._mel(seg[:-160])[None].astype(np.float32)})[0][0][0]
            rows.append((*self._poly(s, b, o), p808))
        r = np.mean(rows, axis=0)
        return {"SIG": float(r[0]), "BAK": float(r[1]), "OVRL": float(r[2]), "P808": float(r[3])}


def highpass80(x: np.ndarray, sr: int = SR) -> np.ndarray:
    """The same 80 Hz 2nd-order Butterworth high-pass the microphone chain applies first. SI-SDR must compare against the reference
    filtered the same way: the filter's phase shift around a male voice's 100 Hz fundamental otherwise counts as distortion."""
    from scipy.signal import butter, lfilter
    b, a = butter(2, 80, "highpass", fs=sr)
    return lfilter(b, a, x).astype(np.float32)


def si_sdr(est: np.ndarray, ref: np.ndarray, hpf: bool = True) -> float:
    if hpf:
        ref = highpass80(ref)
    n = min(len(est), len(ref))
    est, ref = est[:n].astype(np.float64), ref[:n].astype(np.float64)
    est = est - est.mean(); ref = ref - ref.mean()
    alpha = np.dot(est, ref) / (np.dot(ref, ref) + 1e-12)
    t = alpha * ref
    return float(10 * np.log10((np.sum(t ** 2) + 1e-12) / (np.sum((est - t) ** 2) + 1e-12)))


def align(est: np.ndarray, ref: np.ndarray, max_lag: int = 8000) -> np.ndarray:
    """Shift `est` so it lines up with `ref` (processing adds look-ahead delay). FFT cross-correlation over +-max_lag samples."""
    n = min(len(est), len(ref))
    a, b = est[:n].astype(np.float64), ref[:n].astype(np.float64)
    size = 1 << int(np.ceil(np.log2(2 * n)))
    xc = np.fft.irfft(np.fft.rfft(a, size) * np.conj(np.fft.rfft(b, size)), size)
    lags = np.concatenate([np.arange(0, max_lag + 1), np.arange(-max_lag, 0)])
    vals = np.concatenate([xc[: max_lag + 1], xc[-max_lag:]])
    lag = int(lags[np.argmax(vals)])
    if lag >= 0:
        return est[lag:]
    return np.concatenate([np.zeros(-lag, np.float32), est]).astype(np.float32)
