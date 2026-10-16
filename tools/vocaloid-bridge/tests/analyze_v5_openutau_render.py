#!/usr/bin/env python3
"""Check real PCM, placement, and non-integer MIDI pitch in the three-bank V5 demo."""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze_v3_openutau_render import read_float_wav, estimate_f0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("wav", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    rate, samples = read_float_wav(args.wav)
    if rate != 44100 or samples.size == 0 or not np.isfinite(samples).all():
        raise ValueError("invalid OpenUtau PCM output")
    peak = float(np.max(np.abs(samples)))
    if peak < 0.0001:
        raise ValueError("silent OpenUtau output")
    audible = np.flatnonzero(np.abs(samples) >= max(0.001, peak * 0.03))
    if not audible.size or not 0.75 < audible[0] / rate < 1.25:
        raise ValueError("first phrase is not aligned near the 1-second note onset")
    if not 9.5 <= len(samples) / rate <= 10.5:
        raise ValueError("project output duration does not match the three-phrase demo")
    voices = []
    for name, component, start, offset in [
        ("Luo_Tianyi_Ning", "BD79E492NWWK3DDF", 1.0, 0.0),
        ("Luo_Tianyi_Wan", "BL8CEAM5N4XN3LFK", 4.0, 0.5),
        ("Luo_Tianyi_Meng", "BP8CDDH5M7XN2PED", 7.0, -0.5),
    ]:
        notes = []
        for center, midi in [(0.3, 60), (0.9, 62), (1.6, 64)]:
            expected = midi + offset
            measured = estimate_f0(samples, rate, start + center, expected)
            if measured is None or abs(measured - expected) > 0.4:
                raise ValueError(f"{name}: pitch at {start + center}s is {measured}, expected {expected}")
            notes.append({"center_seconds": start + center, "expected_midi": expected,
                          "measured_midi": measured, "error_semitones": measured - expected})
        voices.append({"name": name, "voice_id": "v5:" + component, "pitch_checks": notes})
    report = {"status": "real-openutau-v5-three-bank-render-verified", "sample_rate": rate,
              "sample_count": int(samples.size), "duration_seconds": samples.size / rate,
              "peak": peak, "first_audible_seconds": float(audible[0] / rate),
              "last_audible_seconds": float(audible[-1] / rate), "voices": voices,
              "wav_path": str(args.wav.resolve())}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
