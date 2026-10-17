"""Self-contained acceptance run for the VOCALOID frame server.

Uses the repository fixtures (request.vocaloid4.json + vocaloid4.example.json),
so it needs no song project:

  1. render the reference phrase with the one-shot streaming host;
  2. drive the persistent server through sequential windows and compare;
  3. seek into the phrase and compare against the reference slice;
  4. read paced windows and check the wall clock keeps up.

Prints PASS/FAIL per check and exits non-zero on failure. Requires an installed
VOCALOID 4 engine and bank; nothing is written outside tmp/.
"""
from __future__ import annotations

import json
import struct
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1] if (ROOT.parents[1] / "OpenUtau.slnx").is_file() else ROOT.parents[2]
POWERSHELL = Path(r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
HOST_RUNNER = ROOT / "V4/run-stream-host.ps1"
SERVER_RUNNER = ROOT / "V4/run-stream-server.ps1"
CONFIG = ROOT / "vocaloid4.example.json"
REQUEST = ROOT / "request.vocaloid4.json"
TMP = REPO / "tmp/vocaloid-stream-acceptance"


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
        return rate, __import__("numpy").frombuffer(data, dtype="<f4").astype("float64")
    if tagf == 1 and bits == 16:
        return rate, __import__("numpy").frombuffer(data, dtype="<i2").astype("float64") / 32768.0
    raise ValueError("unsupported wav format")


class Server:
    def __init__(self):
        self.process = subprocess.Popen(
            [str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SERVER_RUNNER),
             "-Config", str(CONFIG), "-Request", str(REQUEST)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.ready = json.loads(self.process.stdout.readline())
        if not self.ready.get("ready"):
            raise RuntimeError("frame server not ready: " + json.dumps(self.ready))

    def command(self, payload, pcm_bytes=0):
        self.process.stdin.write((json.dumps(payload) + "\n").encode("utf-8"))
        self.process.stdin.flush()
        reply = json.loads(self.process.stdout.readline())
        if pcm_bytes:
            data = self.process.stdout.read(pcm_bytes)
            if len(data) != pcm_bytes:
                raise RuntimeError("short PCM reply: " + str(len(data)))
            return reply, data
        return reply, None

    def close(self):
        try:
            self.command({"op": "close"})
        finally:
            self.process.stdin.close()
            self.process.wait(timeout=30)


def pcm16(samples):
    numpy = __import__("numpy")
    return numpy.round(numpy.clip(samples, -1.0, 1.0) * 32767).astype("int16")


def main():
    TMP.mkdir(parents=True, exist_ok=True)
    reference_path = TMP / "reference.wav"
    for stale in (reference_path,):
        stale.unlink(missing_ok=True)
    render = subprocess.run(
        [str(POWERSHELL), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(HOST_RUNNER),
         "-Config", str(CONFIG), "-Request", str(REQUEST), "-Output", str(reference_path),
         "-Seconds", "12"],
        capture_output=True, text=True)
    if render.returncode != 0:
        print("FAIL reference render:\n" + render.stdout[-800:] + render.stderr[-800:])
        return 2
    rate, reference = read_wav(reference_path)
    checks = []
    server = Server()
    frame = server.ready["frame_size"]
    total = server.ready["total_frames"]

    def check(name, ok, detail=""):
        checks.append((name, ok, detail))
        print(("PASS " if ok else "FAIL ") + name + ((" " + detail) if detail else ""))

    try:
        # 1) sequential windows == reference
        server.command({"op": "seek", "frame": 0})
        pcm = b""
        window = 64
        for _ in range(4):
            _, data = server.command({"op": "read", "frames": window, "pace": False},
                                     pcm_bytes=window * frame * 2)
            pcm += data
        got = __import__("numpy").frombuffer(pcm, dtype="<i2")
        want = pcm16(reference[:len(got)])
        check("sequential windows match reference", bool((got == want).all()),
              str(len(got)) + " samples")

        # 2) seek == reference slice
        target = max(1, total // 3)
        server.command({"op": "seek", "frame": target})
        _, data = server.command({"op": "read", "frames": window, "pace": False},
                                 pcm_bytes=window * frame * 2)
        got = __import__("numpy").frombuffer(data, dtype="<i2")
        want = pcm16(reference[target * frame: target * frame + len(got)])
        check("seek matches reference slice", bool((got == want).all()), "frame " + str(target))

        # 3) paced reads keep up
        server.command({"op": "seek", "frame": 0})
        started = time.monotonic()
        windows = 4
        for _ in range(windows):
            server.command({"op": "read", "frames": window, "pace": True}, pcm_bytes=window * frame * 2)
        elapsed = time.monotonic() - started
        audio = windows * window * frame / rate
        ratio = elapsed / audio
        check("paced reads track the wall clock", 0.9 <= ratio <= 1.15,
              "wall %.3fs audio %.3fs ratio %.3f" % (elapsed, audio, ratio))
    finally:
        server.close()

    failed = [name for name, ok, _ in checks if not ok]
    print("\n%d/%d checks passed" % (len(checks) - len(failed), len(checks)))
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
