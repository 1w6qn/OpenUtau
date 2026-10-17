"""Exercise the persistent frame server: seek, sequential windows, pacing.

The server speaks one JSON line per command; a read reply is a JSON line followed
by exactly samples*2 bytes of int16 PCM. This probe proves three properties:
sequential windows concatenate to the batch render, seek is sample exact, and
paced reads keep up with the wall clock.
"""
from __future__ import annotations

import json
import struct
import subprocess
import sys
import time
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
POWERSHELL = Path(r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
RUNNER = ROOT / "plugins/vocaloid-bridge/V4/run-stream-server.ps1"
CONFIG = ROOT / "plugins/vocaloid-bridge/vocaloid4.example.json"


def read_wav(path):
    raw = Path(path).read_bytes()
    pos, fmt, data = 12, None, None
    while pos < len(raw) - 8:
        tag = raw[pos:pos + 4]
        size = struct.unpack_from("<I", raw, pos + 4)[0]
        body = raw[pos + 8:pos + 8 + size]
        if tag == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", body, 0)
        elif tag == b"data":
            data = body
        pos += 8 + size + (size & 1)
    tagf, channels, rate, _, _, bits = fmt
    if tagf == 3 and bits == 32:
        return rate, np.frombuffer(data, dtype="<f4").astype(np.float64)
    if tagf == 1 and bits == 16:
        return rate, np.frombuffer(data, dtype="<i2").astype(np.float64) / 32768.0
    raise ValueError("unsupported wav format")


def to_pcm16(samples):
    clamped = np.clip(samples, -1.0, 1.0)
    return np.round(clamped * 32767).astype(np.int16)


class Server:
    def __init__(self, request):
        self.process = subprocess.Popen(
            [str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(RUNNER),
             "-Config", str(CONFIG), "-Request", str(request)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.ready = json.loads(self.process.stdout.readline())

    def command(self, payload, pcm_bytes=0):
        self.process.stdin.write((json.dumps(payload) + "\n").encode("utf-8"))
        self.process.stdin.flush()
        reply = json.loads(self.process.stdout.readline())
        if pcm_bytes:
            data = self.process.stdout.read(pcm_bytes)
            if len(data) != pcm_bytes:
                raise RuntimeError("short PCM read: " + str(len(data)))
            return reply, data
        return reply, None

    def close(self):
        try:
            self.command({"op": "close"})
        finally:
            self.process.stdin.close()
            self.process.wait(timeout=30)


def main():
    request = ROOT / "tmp/wav/req-pbs.json"
    reference_rate, reference = read_wav(ROOT / "tmp/wav/full-float.wav")
    server = Server(request)
    print("READY", json.dumps(server.ready))
    budget = server.ready["budget_ms"]
    frame = server.ready["frame_size"]

    # 1) sequential windows must equal the batch render byte for byte
    reply, _ = server.command({"op": "seek", "frame": 0})
    print("SEEK", json.dumps(reply))
    pcm = b""
    for _ in range(5):
        reply, data = server.command({"op": "read", "frames": 172, "pace": False}, pcm_bytes=172 * frame * 2)
        pcm += data
    got = np.frombuffer(pcm, dtype="<i2")
    want = to_pcm16(reference[:len(got)])
    print("SEQUENTIAL identical:", bool(np.array_equal(got, want)), "samples", len(got))

    # 2) seek must reproduce the same slice as the batch render
    reply, _ = server.command({"op": "seek", "frame": 516})
    print("SEEK", json.dumps(reply))
    reply, data = server.command({"op": "read", "frames": 172, "pace": False}, pcm_bytes=172 * frame * 2)
    got = np.frombuffer(data, dtype="<i2")
    want = to_pcm16(reference[516 * frame:516 * frame + len(got)])
    print("SEEK identical:", bool(np.array_equal(got, want)))

    # 3) paced windows must keep up with wall clock
    server.command({"op": "seek", "frame": 0})
    started = time.monotonic()
    for _ in range(5):
        reply, _ = server.command({"op": "read", "frames": 172, "pace": True}, pcm_bytes=172 * frame * 2)
    elapsed = time.monotonic() - started
    audio = 5 * 172 * frame / reference_rate
    print("PACED wall %.3fs audio %.3fs ratio %.3f" % (elapsed, audio, elapsed / audio))
    server.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
