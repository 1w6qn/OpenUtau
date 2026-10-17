"""Emit a vocaloid-bridge render request from a VSQX phrase.

The request carries what the engine actually needs: notes in seconds and an
absolute MIDI pitch curve. Pitch is derived with the VOCALOID model
(cents = P * PBS * 100 / 8192) or with an alternative model, so the effect of a
conversion mistake on the rendered WAV can be measured.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import effect_align as ea


def tempo_map(source):
    # effect_align keeps resolution; tempo is the first BPM of the project.
    resolution = source.get("resolution") or 480
    bpm = source.get("tempos")[0][1] if source.get("tempos") else 120
    return lambda ticks: ticks * 60.0 / (bpm * resolution)


def build(source_path, part_index=0, note_count=10, model="pbs", fixed_cents_per_unit=None):
    source = ea.load_vsqx(source_path)
    part = source["parts"][part_index]
    notes = part["notes"][:note_count]
    ticks_to_seconds = tempo_map(source)
    start = notes[0]["t"]
    end = notes[-1]["t"] + notes[-1]["dur"]
    grid = np.arange(start, end, 5, dtype=float)
    seconds = ticks_to_seconds(grid - start)
    p = np.array([x for x, _ in part["curves"]["P"]], dtype=float)
    pv = np.array([x for _, x in part["curves"]["P"]], dtype=float)
    s = np.array([x for x, _ in part["curves"]["S"]], dtype=float) if part["curves"].get("S") else None
    sv = np.array([x for _, x in part["curves"]["S"]], dtype=float) if s is not None else None
    values = np.interp(grid, p, pv)
    if model == "pbs":
        sensitivity = np.interp(grid, s, sv) if s is not None else np.full(len(grid), 24.0)
        cents = values * sensitivity * 100.0 / 8192.0
    elif model == "fixed":
        cents = values * float(fixed_cents_per_unit if fixed_cents_per_unit is not None else 0.1832)
    else:
        raise ValueError("model must be pbs or fixed")
    tone = np.zeros(len(grid))
    for note in notes:
        mask = (grid >= note["t"]) & (grid < note["t"] + note["dur"])
        tone[mask] = note["tone"]
    # The V4 writer rejects bends beyond 24 semitones; clamp and report them so a
    # long request still renders (the reference WAV then differs at those points).
    raw = cents / 100.0
    limited = np.clip(raw, -24.0, 24.0)
    clamped = int(np.sum(np.abs(raw - limited) > 1e-9))
    pitch = tone + limited
    if clamped:
        print("CLAMPED " + str(clamped) + " pitch points to +/-24 semitones", file=sys.stderr)
    return {
        "VoiceId": "",
        "Notes": [{"StartSeconds": round(ticks_to_seconds(n["t"] - start), 6),
                   "EndSeconds": round(ticks_to_seconds(n["t"] + n["dur"] - start), 6),
                   "Tone": n["tone"], "Lyric": n["lyric"]} for n in notes],
        "Pitch": {"TimesSeconds": [round(x, 6) for x in seconds],
                  "Values": [round(float(x), 6) for x in pitch]},
    }


def build_from_ustx(target_path, part_index=0, note_count=10):
    data = ea.load_ustx(target_path)
    part = data["voice_parts"][part_index]
    notes = part["notes"][:note_count]
    resolution = data.get("resolution") or 480
    bpm = data.get("bpm") or 120.0
    ticks_to_seconds = lambda ticks: ticks * 60.0 / (bpm * resolution)
    start = notes[0]["position"]
    end = notes[-1]["position"] + notes[-1]["duration"]
    grid = np.arange(start, end, 5, dtype=float)
    curve = [c for c in (part.get("curves") or []) if c.get("abbr") == "pitd"]
    if curve:
        xs = np.array(curve[0]["xs"], dtype=float)
        ys = np.array(curve[0]["ys"], dtype=float)
        pitd = np.interp(grid, xs, ys)
    else:
        pitd = np.zeros(len(grid))
    tone = np.zeros(len(grid))
    for note in notes:
        mask = (grid >= note["position"]) & (grid < note["position"] + note["duration"])
        tone[mask] = note["tone"]
    return {
        "VoiceId": "",
        "Notes": [{"StartSeconds": round(ticks_to_seconds(n["position"] - start), 6),
                   "EndSeconds": round(ticks_to_seconds(n["position"] + n["duration"] - start), 6),
                   "Tone": n["tone"], "Lyric": n["lyric"]} for n in notes],
        "Pitch": {"TimesSeconds": [round(x, 6) for x in ticks_to_seconds(grid - start)],
                  "Values": [round(float(t + p / 100.0), 6) for t, p in zip(tone, pitd)]},
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description="VSQX phrase to bridge render request")
    parser.add_argument("--source", type=Path, default=None)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--part", type=int, default=0)
    parser.add_argument("--notes", type=int, default=10)
    parser.add_argument("--model", choices=("pbs", "fixed"), default="pbs")
    parser.add_argument("--fixed-cents-per-unit", type=float, default=None)
    parser.add_argument("--target", type=Path, default=None,
                        help="emit from an OpenUtau .ustx instead of the VSQX (uses tone + PITD)")
    args = parser.parse_args(argv)
    if not args.target and not args.source:
        parser.error("pass --source (VSQX) or --target (USTX)")
    if args.target:
        request = build_from_ustx(args.target, args.part, args.notes)
    else:
        request = build(args.source, args.part, args.notes, args.model, args.fixed_cents_per_unit)
    args.out.write_text(json.dumps(request, ensure_ascii=False, indent=2), encoding="utf-8")
    span = request["Notes"][-1]["EndSeconds"]
    print("WROTE", args.out, "notes", len(request["Notes"]), "pitch points", len(request["Pitch"]["Values"]),
          "seconds", round(span, 3))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
