#!/usr/bin/env python3
"""Inspect the float WAV and note-aligned F0 from the OpenUtau V3 acceptance render."""
import argparse
import json
import math
import struct
from pathlib import Path

import numpy as np


def read_float_wav(path: Path):
    raw = path.read_bytes()
    if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError("output is not RIFF/WAVE")
    offset = 12
    fmt = None
    data = None
    while offset + 8 <= len(raw):
        kind = raw[offset : offset + 4]
        size = struct.unpack_from("<I", raw, offset + 4)[0]
        payload = raw[offset + 8 : offset + 8 + size]
        if len(payload) != size:
            raise ValueError("truncated WAV chunk")
        if kind == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", payload)
        elif kind == b"data":
            data = payload
            break
        offset += 8 + size + (size & 1)
    if fmt is None or data is None:
        raise ValueError("WAV is missing fmt or data chunk")
    format_code, channels, sample_rate, _, block_align, bits = fmt
    if format_code != 3 or channels != 1 or bits != 32 or block_align != 4:
        raise ValueError(f"expected mono IEEE float32 WAV, got {fmt}")
    samples = np.frombuffer(data, dtype="<f4").astype(np.float64)
    return sample_rate, samples


def estimate_f0(samples, sample_rate, center_seconds, expected_midi):
    size = 8192
    center = int(round(center_seconds * sample_rate))
    start = center - size // 2
    if start < 0 or start + size > len(samples):
        return None
    frame = samples[start : start + size].copy()
    frame -= frame.mean()
    frame *= np.hanning(size)
    if np.max(np.abs(frame)) < 1e-7:
        return None
    corr = np.fft.irfft(np.abs(np.fft.rfft(frame, n=size * 2)) ** 2)[:size]
    if corr[0] <= 0:
        return None
    # Search within +/- 3 semitones of the expected note to avoid locking onto
    # a strong harmonic while still reporting a measured, sample-derived pitch.
    expected_hz = 440.0 * (2.0 ** ((expected_midi - 69) / 12.0))
    low_lag = max(2, int(sample_rate / (expected_hz * 2 ** (3 / 12))))
    high_lag = min(size // 2, int(sample_rate / (expected_hz * 2 ** (-3 / 12))))
    values = corr[low_lag : high_lag + 1]
    if values.size == 0:
        return None
    lag = low_lag + int(np.argmax(values))
    if 1 <= lag < size - 1:
        left, mid, right = corr[lag - 1], corr[lag], corr[lag + 1]
        denom = left - 2 * mid + right
        if denom:
            lag += 0.5 * (left - right) / denom
    return 69.0 + 12.0 * math.log2(sample_rate / lag / 440.0)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("wav", type=Path)
    parser.add_argument("phrases", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    sample_rate, samples = read_float_wav(args.wav)
    peak = float(np.max(np.abs(samples))) if samples.size else 0.0
    threshold = max(0.001, peak * 0.03)
    audible = np.flatnonzero(np.abs(samples) >= threshold)
    if audible.size == 0:
        raise ValueError("OpenUtau output is silent")
    phrases = json.loads(args.phrases.read_text(encoding="utf-8"))
    expected = [
        {"lyric": "你", "midi": 60, "onset_seconds": 1.0, "center_seconds": 1.3},
        {"lyric": "好", "midi": 62, "onset_seconds": 1.6, "center_seconds": 1.9},
        {"lyric": "啊", "midi": 64, "onset_seconds": 2.2, "center_seconds": 2.5},
    ]
    measured = []
    for note in expected:
        f0_midi = estimate_f0(samples, sample_rate, note["center_seconds"], note["midi"])
        measured.append({**note, "measured_midi": f0_midi,
                         "pitch_error_semitones": None if f0_midi is None else f0_midi - note["midi"]})
    phone_rows = []
    for phrase in phrases:
        for phone in phrase.get("phones", []):
            phone_rows.append({"phoneme": phone.get("phoneme"), "start_ms": phone.get("start_ms"),
                               "end_ms": phone.get("end_ms")})
    report = {
        "status": "openutau-v3-pipeline-render-analyzed",
        "project": "vocaloid3-demo.ustx",
        "voice_id": "4:0",
        "component_id": "BETDB8W6KWZPYEB9",
        "sample_rate": sample_rate,
        "sample_count": int(samples.size),
        "duration_seconds": samples.size / sample_rate,
        "peak": peak,
        "first_audible_seconds": float(audible[0] / sample_rate),
        "last_audible_seconds": float(audible[-1] / sample_rate),
        "expected_notes": measured,
        "phonemes": phone_rows,
        "phrase_count": len(phrases),
        "wav_path": str(args.wav.resolve()),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
