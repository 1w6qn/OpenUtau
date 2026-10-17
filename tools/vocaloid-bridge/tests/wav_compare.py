"""Compare two WAV files the way an audio null test does.

Used to answer one question: does OpenUtau's render of a VSQX produce the same
WAV as the VOCALOID editor? Byte identity is reported first; when it fails the
tool aligns both signals and reports the residual in dB so "close" and
"different" are distinguishable.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
import wave
from pathlib import Path

import numpy as np


def read_wav(path):
    with wave.open(str(path), "rb") as handle:
        channels = handle.getnchannels()
        width = handle.getsampwidth()
        rate = handle.getframerate()
        frames = handle.getnframes()
        raw = handle.readframes(frames)
    if width == 2:
        data = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    elif width == 4:
        data = np.frombuffer(raw, dtype="<i4").astype(np.float64) / 2147483648.0
    elif width == 3:
        raw8 = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 3)
        values = (raw8[:, 0].astype(np.int32)
                  | (raw8[:, 1].astype(np.int32) << 8)
                  | (raw8[:, 2].astype(np.int32) << 16))
        values = np.where(values >= 1 << 23, values - (1 << 24), values)
        data = values.astype(np.float64) / float(1 << 23)
    else:
        raise ValueError("Unsupported sample width: " + str(width))
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    return {"rate": rate, "frames": frames, "channels": channels,
            "width": width, "samples": data,
            "sha256": hashlib.sha256(path.read_bytes() if False else raw).hexdigest(),
            "path": str(path)}


def _dbfs(value):
    return 20.0 * math.log10(max(value, 1e-12))


def compare(a_path, b_path, max_lag_seconds=0.25):
    a = read_wav(a_path)
    b = read_wav(b_path)
    report = {"a": {"path": a["path"], "rate": a["rate"], "frames": a["frames"],
                    "channels": a["channels"], "pcm_sha256": a["sha256"]},
              "b": {"path": b["path"], "rate": b["rate"], "frames": b["frames"],
                    "channels": b["channels"], "pcm_sha256": b["sha256"]}}
    report["same_format"] = (a["rate"] == b["rate"] and a["channels"] == b["channels"]
                             and a["width"] == b["width"])
    report["byte_identical"] = (Path(a_path).read_bytes() == Path(b_path).read_bytes())
    x, y = a["samples"], b["samples"]
    if len(x) != len(y):
        report["length_difference_frames"] = len(x) - len(y)
    # Align on the shorter prefix by searching lag on 1 kHz envelopes.
    lag = 0
    if a["rate"] == b["rate"] and len(x) > 1000 and len(y) > 1000:
        hop = max(1, a["rate"] // 1000)
        ex = np.abs(x[:len(x) // hop * hop]).reshape(-1, hop).mean(axis=1)
        ey = np.abs(y[:len(y) // hop * hop]).reshape(-1, hop).mean(axis=1)
        limit = int(max_lag_seconds * 1000)
        best = None
        for offset in range(-limit, limit + 1):
            if offset >= 0:
                p, q = ex[offset:], ey[:len(ey) - offset] if offset else ey
            else:
                p, q = ex[:len(ex) + offset], ey[-offset:]
            n = min(len(p), len(q), 4000)
            if n < 100:
                continue
            p, q = p[:n] - p[:n].mean(), q[:n] - q[:n].mean()
            denom = math.sqrt(float((p * p).sum() * (q * q).sum()))
            score = float((p * q).sum() / denom) if denom > 0 else 0.0
            if best is None or score > best[0]:
                best = (score, offset)
        if best:
            lag = best[1]
    report["best_lag_ms"] = lag
    shift = int(round(lag * a["rate"] / 1000.0))
    if shift >= 0:
        p, q = x[shift:], y
    else:
        p, q = x, y[-shift:]
    n = min(len(p), len(q))
    if n <= 0:
        report["verdict"] = "no-overlap"
        return report
    p, q = p[:n], q[:n]
    difference = p - q
    peak = float(max(np.max(np.abs(p)), np.max(np.abs(q)), 1e-9))
    residual_rms = float(math.sqrt(float(np.mean(difference ** 2))))
    report.update({
        "compared_frames": n,
        "peak": peak,
        "difference_max": float(np.max(np.abs(difference))),
        "difference_rms": residual_rms,
        "null_depth_db": _dbfs(residual_rms / peak),
        "correlation": float(np.corrcoef(p, q)[0, 1]) if p.std() > 0 and q.std() > 0 else None,
    })
    if report["byte_identical"]:
        report["verdict"] = "identical"
    elif report["null_depth_db"] < -60.0:
        report["verdict"] = "equivalent"
    else:
        report["verdict"] = "different"
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description="WAV null test for render equality")
    parser.add_argument("a", type=Path)
    parser.add_argument("b", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    report = compare(args.a, args.b)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if report.get("verdict") == "identical" else (1 if report.get("verdict") == "equivalent" else 2)


if __name__ == "__main__":
    raise SystemExit(main())
