"""Accent Assist worker: real-time accent softening for the MeetWise audio host.

Protocol (TCP, 127.0.0.1 only):
  1. the host connects and sends one JSON line: {"token": "...", "bank": "path.pt", "strength": 0.6}
  2. the worker answers one JSON line: {"ok": true, "rate": 16000, "latency": <samples>} (or {"ok": false, "error": "..."})
  3. then binary frames both ways: uint32 little-endian sample count + that many float32 samples at 16 kHz.
     Every frame the host sends is answered by a frame of the same length: the converted audio, delayed by the fixed latency.

usage: python worker.py --port 0 --token T   (prints "PORT <n>" on stdout once listening)
"""
from __future__ import annotations

import argparse
import json
import socket
import struct
import sys
import time
import traceback
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))


def recv_exact(conn: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("closed")
        buf += chunk
    return bytes(buf)


def serve(conn: socket.socket, token: str, models):
    from mwaccent import knn
    f = conn.makefile("rb")
    hello = json.loads(f.readline().decode("utf-8"))
    if hello.get("token") != token:
        conn.sendall(b'{"ok": false, "error": "bad token"}\n')
        return
    try:
        bank = knn.Bank.load(Path(hello["bank"]))
        conv = knn.StreamingConverter(models[0], models[1], bank, strength=float(hello.get("strength", 0.6)))
        # warm up the GPU kernels so the first real hop is on time
        conv.process(np.zeros(conv.hop * 3, np.float32))
        conv = knn.StreamingConverter(models[0], models[1], bank, strength=float(hello.get("strength", 0.6)))
    except Exception as ex:  # noqa: BLE001
        conn.sendall((json.dumps({"ok": False, "error": str(ex)}) + "\n").encode())
        return
    conn.sendall((json.dumps({"ok": True, "rate": 16000, "latency": conv.latency_samples}) + "\n").encode())
    slow = 0
    while True:
        n = struct.unpack("<I", recv_exact(conn, 4))[0]
        x = np.frombuffer(recv_exact(conn, 4 * n), dtype=np.float32)
        t0 = time.perf_counter()
        y = conv.process(x)
        if len(y) < n:   # start-up: the converter emits nothing until its latency has passed
            y = np.concatenate([np.zeros(n - len(y), np.float32), y])
        elif len(y) > n:
            y = y[-n:]
        if time.perf_counter() - t0 > n / 16000:
            slow += 1
        conn.sendall(struct.pack("<I", n) + y.astype(np.float32).tobytes())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--token", required=True)
    a = ap.parse_args()
    from mwaccent import knn
    models = knn.load_models()
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.bind(("127.0.0.1", a.port))
    srv.listen(1)
    print(f"PORT {srv.getsockname()[1]}", flush=True)
    while True:
        conn, _ = srv.accept()
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        try:
            serve(conn, a.token, models)
        except (ConnectionError, OSError):
            pass
        except Exception:  # noqa: BLE001
            traceback.print_exc()
        finally:
            conn.close()


if __name__ == "__main__":
    main()
