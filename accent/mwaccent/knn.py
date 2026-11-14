"""kNN accent softening: WavLM layer-6 features of the live voice are replaced (partly) by their nearest neighbours in a bank of frames that
carry native US pronunciation in the user's own voice, then vocoded with the prematched HiFi-GAN from kNN-VC.

Streaming: audio arrives in hops (default 160 ms at 16 kHz). WavLM sees a window of past context plus the new hop plus nothing from the future;
the last `lookahead` frames of each window are held back until the next hop provides their right context. The vocoder runs over the new frames
with some frames of left context, so consecutive chunks join without a seam.
"""
from __future__ import annotations

import json
import math
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F

from . import WEIGHTS, REPOS

SR = 16000
HOP = 320  # samples per WavLM frame (20 ms)
LAYER = 6


def load_models(device: str = "cuda"):
    from wavlm.WavLM import WavLM, WavLMConfig
    from hifigan.models import Generator as HiFiGAN
    from hifigan.utils import AttrDict

    ckpt = torch.load(WEIGHTS / "WavLM-Large.pt", map_location="cpu", weights_only=False)
    wavlm = WavLM(WavLMConfig(ckpt["cfg"]))
    wavlm.load_state_dict(ckpt["model"])
    wavlm = wavlm.to(device).eval()

    with open(REPOS / "knn-vc" / "hifigan" / "config_v1_wavlm.json") as f:
        h = AttrDict(json.load(f))
    gen = HiFiGAN(h).to(device)
    gen.load_state_dict(torch.load(WEIGHTS / "prematch_g_02500000.pt", map_location=device, weights_only=False)["generator"])
    gen.eval()
    gen.remove_weight_norm()
    if device == "cuda":
        wavlm = wavlm.half()
        gen = gen.half()
    return wavlm, gen


@torch.inference_mode()
def features(wavlm, wav: np.ndarray | torch.Tensor, device: str = "cuda") -> torch.Tensor:
    """(T,) float 16 kHz -> (frames, 1024) layer-6 features."""
    x = torch.as_tensor(wav, dtype=torch.float32, device=device)[None]
    if next(wavlm.parameters()).dtype == torch.float16:
        x = x.half()
    return wavlm.extract_features(x, output_layer=LAYER, ret_layer_results=False)[0].squeeze(0)


def cosine_topk(query: torch.Tensor, bank: torch.Tensor, bank_norm: torch.Tensor, k: int) -> torch.Tensor:
    q = F.normalize(query.float(), dim=-1)
    sims = q @ bank_norm.T  # (n, N)
    idx = sims.topk(k, dim=-1).indices
    return bank[idx].float().mean(dim=1)


@torch.inference_mode()
def vocode(gen, feats: torch.Tensor) -> torch.Tensor:
    dtype = next(gen.parameters()).dtype
    return gen(feats[None].to(dtype)).squeeze().float()


@dataclass
class Bank:
    feats: torch.Tensor      # (N, 1024) fp16 on device
    norm: torch.Tensor       # normalised copy for cosine search
    info: dict

    @staticmethod
    def load(path: Path, device: str = "cuda") -> "Bank":
        d = torch.load(path, map_location="cpu", weights_only=False)
        f = d["feats"].to(device).half()
        return Bank(f, F.normalize(f.float(), dim=-1).half(), d.get("info", {}))

    def save(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        torch.save({"feats": self.feats.half().cpu(), "info": self.info}, path)


def convert_offline(wavlm, gen, bank: Bank, wav: np.ndarray, strength: float = 1.0, k: int = 4, device: str = "cuda") -> np.ndarray:
    """Whole-utterance conversion (reference for the streaming version)."""
    q = features(wavlm, wav, device)
    m = cosine_topk(q, bank.feats, bank.norm.float(), k)
    out = (1 - strength) * q.float() + strength * m
    y = vocode(gen, out).cpu().numpy()
    return match_loudness(y, wav)


def match_loudness(y: np.ndarray, ref: np.ndarray) -> np.ndarray:
    ry = math.sqrt(float(np.mean(y ** 2)) + 1e-12)
    rr = math.sqrt(float(np.mean(ref ** 2)) + 1e-12)
    return (y * (rr / ry)).astype(np.float32)


class StreamingConverter:
    """Hop-by-hop conversion with fixed latency. Feed 16 kHz float blocks of any size; get converted audio of the same total length back,
    delayed by `latency_samples`."""

    def __init__(self, wavlm, gen, bank: Bank, strength: float = 0.8, k: int = 4, hop_ms: int = 160, context_ms: int = 1000,
                 lookahead_frames: int = 3, voc_context_frames: int = 12, device: str = "cuda"):
        self.wavlm, self.gen, self.bank = wavlm, gen, bank
        self.strength, self.k, self.device = strength, k, device
        self.hop = hop_ms * SR // 1000
        assert self.hop % HOP == 0
        self.ctx = context_ms * SR // 1000
        self.la = lookahead_frames
        self.vctx = voc_context_frames
        self.buf = np.zeros(0, np.float32)        # unprocessed input
        self.hist = np.zeros(self.ctx, np.float32)  # past input for WavLM context
        self.feat_hist: torch.Tensor | None = None  # converted features already emitted (for vocoder context)
        self.pending_out = np.zeros(0, np.float32)
        self.total_in = 0
        self.total_out = 0

    @property
    def latency_samples(self) -> int:
        return self.hop + self.la * HOP

    def process(self, block: np.ndarray) -> np.ndarray:
        self.buf = np.concatenate([self.buf, block.astype(np.float32)])
        self.total_in += len(block)
        out = []
        while len(self.buf) >= self.hop:
            hop, self.buf = self.buf[: self.hop], self.buf[self.hop:]
            out.append(self._step(hop))
        y = np.concatenate([self.pending_out] + out) if out else self.pending_out
        # emit exactly as many samples as came in minus the fixed latency (silence first)
        want = max(0, self.total_in - self.latency_samples) - self.total_out
        if want <= 0:
            self.pending_out = y
            return np.zeros(0, np.float32)
        if len(y) < want:  # start-up: pad with silence so timing stays fixed
            y = np.concatenate([np.zeros(want - len(y), np.float32), y])
        emit, self.pending_out = y[:want], y[want:]
        self.total_out += len(emit)
        return emit

    @torch.inference_mode()
    def _step(self, hop: np.ndarray) -> np.ndarray:
        window = np.concatenate([self.hist, hop])
        self.hist = window[-self.ctx:]
        q = features(self.wavlm, window, self.device)  # frames for the whole window (no future context at its end)
        n_new = self.hop // HOP
        # frames that are now "settled": the window end minus the look-ahead
        end = q.shape[0] - self.la
        sel = q[end - n_new:end].float()
        m = cosine_topk(sel, self.bank.feats, self.bank.norm.float(), self.k)
        conv = (1 - self.strength) * sel + self.strength * m
        ctx = self.feat_hist if self.feat_hist is not None else conv[:0]
        voc_in = torch.cat([ctx, conv], dim=0)
        wav = vocode(self.gen, voc_in).cpu().numpy()
        new = wav[len(ctx) * HOP:]
        self.feat_hist = voc_in[-self.vctx:]
        # the vocoder saw the previous frames as left context, so the new chunk continues the waveform without a seam
        return new.astype(np.float32)
