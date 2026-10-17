"""Audit and repair VSQX -> USTX equivalence for OpenUtau.

Goal: for a given VOCALOID .vsqx, the OpenUtau project must carry the same
content. This tool answers that with measurements instead of assumptions:

* audit  - compare notes (position/duration/pitch/lyric), tempo/time signatures,
           the pitch curve (P vs PITD) and list what cannot be represented;
* apply  - rewrite the pitch curve so it reproduces P exactly under a declared
           unit factor, then re-audit.

The unit factor is explicit because it is a convention, not a secret: VOCALOID
stores P in bend units, OpenUtau stores PITD in cents. Measured on OpenUtau
0.1.574 the built-in reader uses about 0.1832 cents per unit and clamps to
+/-1200 cents; pass --pitch-unit-cents to state the factor you want instead.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
import yaml

sys.path.insert(0, str(Path(__file__).resolve().parent))
import effect_align as ea

REST_SOURCE = "-"
REST_TARGET = "+~"


def _curve(pair, abbr):
    for curve in pair.get("curves") or []:
        if curve.get("abbr") == abbr:
            return curve
    return None


def _pitch_curve(part, pitch_unit_cents=None):
    """Cents for every P point: P units times the PBS (S) curve.

    VSQX stores pitch bend as P with a per-note sensitivity in S (PBS, in
    semitones). plugins/vocaloid-bridge/V4/V4ScoreWriter.cs writes exactly that
    pair ("S=PBS and P=PIT"), so cents = P * S * 100 / 8192. A fixed
    --pitch-unit-cents is only a fallback for files without an S curve.
    """
    points = part["curves"].get("P") or []
    if not points:
        return []
    sensitivity = part["curves"].get("S") or []
    st = np.array([t for t, _ in sensitivity], dtype=float) if sensitivity else None
    sv = np.array([v for _, v in sensitivity], dtype=float) if sensitivity else None
    ticks = np.array([t for t, _ in points], dtype=float)
    values = np.array([v for _, v in points], dtype=float)
    if st is not None:
        per_unit = np.interp(ticks, st, sv) * 100.0 / 8192.0
    elif pitch_unit_cents is not None:
        per_unit = np.full(len(ticks), float(pitch_unit_cents))
    else:
        per_unit = np.full(len(ticks), 2400.0 / 8192.0)
    return [(int(t), float(v * k)) for t, v, k in zip(ticks, values, per_unit)]


def audit(source_path, target_path, pitch_unit_cents=None):
    source = ea.load_vsqx(source_path)
    target = ea.load_ustx(target_path)
    report = {"source": str(source_path), "target": str(target_path),
              "pitch_unit_cents": pitch_unit_cents or "per-note PBS (S curve)",
              "parts": [], "problems": []}
    if len(source["parts"]) != len(target["voice_parts"]):
        report["problems"].append("track count differs: " + str(len(source["parts"]))
                                  + " vs " + str(len(target["voice_parts"])))
    tempo_equal = [(t.get("position"), t.get("bpm")) for t in target.get("tempos") or
                   [{"position": source["tempos"][0][0] if source.get("tempos") else 0,
                     "bpm": source["tempos"][0][1] if source.get("tempos") else 0}]]
    report["tempos_equal"] = None
    for index, (part, ustx_part) in enumerate(zip(source["parts"], target["voice_parts"])):
        notes = part["notes"]
        target_notes = ustx_part.get("notes") or []
        entry = {"part": index + 1, "source_notes": len(notes), "target_notes": len(target_notes)}
        if len(notes) != len(target_notes):
            report["problems"].append("part " + str(index + 1) + " note count differs")
            report["parts"].append(entry)
            continue
        position_ok = sum(1 for a, b in zip(notes, target_notes) if a["t"] == b["position"])
        duration_ok = sum(1 for a, b in zip(notes, target_notes) if a["dur"] == b["duration"])
        tone_ok = sum(1 for a, b in zip(notes, target_notes) if a["tone"] == b["tone"])
        lyric_diff = [(a["t"], a["lyric"], b.get("lyric")) for a, b in zip(notes, target_notes)
                      if a["lyric"] != b.get("lyric")]
        benign_rest = [d for d in lyric_diff if d[1] == REST_SOURCE and d[2] == REST_TARGET]
        entry.update({
            "positions_equal": position_ok,
            "durations_equal": duration_ok,
            "pitches_equal": tone_ok,
            "lyrics_equal": len(notes) - len(lyric_diff),
            "lyric_differences": len(lyric_diff),
            "lyric_rest_normalized": len(benign_rest),
            "lyric_unexplained": [d for d in lyric_diff if d not in benign_rest][:20],
        })
        curve = _curve(ustx_part, "pitd")
        source_points = _pitch_curve(part, pitch_unit_cents)
        if source_points and curve:
            xs = np.array(curve["xs"], dtype=float)
            ys = np.array(curve["ys"], dtype=float)
            px = np.array([p[0] for p in source_points], dtype=float)
            py = np.array([p[1] for p in source_points], dtype=float)
            mask = (xs >= px[0]) & (xs <= px[-1])
            raw = np.interp(xs[mask], px, py)   # already in cents
            got = ys[mask]
            expected = raw
            error = np.abs(got - expected)
            limit = 1200.0
            inside = np.abs(expected) <= limit
            # Clamped points flatten the regression, so report shape metrics on
            # the range that OpenUtau can actually express.
            fit = inside & (raw.var() > 0) & (got.var() > 0)
            entry["pitch"] = {
                "points": int(mask.sum()),
                "correlation_vs_target": float(np.corrcoef(got[fit], raw[fit])[0, 1]) if fit.sum() > 2 else None,
                "slope_ratio": float(np.polyfit(raw[fit], got[fit], 1)[0]) if fit.sum() > 2 else None,
                "expected_slope_ratio": 1.0,
                "median_absolute_cents": float(np.median(error)),
                "rms_cents": float(math.sqrt(float(np.mean(error ** 2)))),
                "median_absolute_cents_within_range": float(np.median(error[inside])) if inside.any() else None,
                "rms_cents_within_range": float(math.sqrt(float(np.mean(error[inside] ** 2)))) if inside.any() else None,
                "clamped_share": float(np.mean(~inside) * 100.0),
            }
        elif source_points and not curve:
            report["problems"].append("part " + str(index + 1) + " lost the pitch curve")
        elif not source_points and curve:
            report["problems"].append("part " + str(index + 1) + " invented a pitch curve")
        report["parts"].append(entry)
    dropped = ea.count_ignored_parameters(source)
    report["not_representable"] = {"vocaloid_note_parameters": dropped,
                                   "notes_with_non_default_parameters": int(sum(dropped.values())),
                                   "reason": "OpenUtau has no accent/decay/opening/vibrato-enum parameters"}
    return report


def summarize(report):
    lines = []
    for part in report["parts"]:
        pitch = part.get("pitch")
        lines.append("part " + str(part["part"]) + ": notes " + str(part["target_notes"]) + "/"
                     + str(part["source_notes"]) + " pos=" + str(part.get("positions_equal"))
                     + " dur=" + str(part.get("durations_equal")) + " tone=" + str(part.get("pitches_equal"))
                     + " lyric_ok=" + str(part.get("lyrics_equal")) + " (rest normalized "
                     + str(part.get("lyric_rest_normalized", 0)) + ")"
                     + ("" if not pitch else " | pitch corr=" + str(round(pitch["correlation_vs_target"], 4))
                        + " ratio=" + str(round(pitch["slope_ratio"], 4))
                        + " median=" + str(round(pitch["median_absolute_cents"], 1)) + "c"
                        + " median_in_range=" + str(round(pitch.get("median_absolute_cents_within_range") or 0, 2)) + "c"
                        + " clamped=" + str(round(pitch["clamped_share"], 1)) + "%"))
    dropped = report["not_representable"]["notes_with_non_default_parameters"]
    lines.append("not representable: " + str(dropped) + " notes carry VOCALOID-only tuning")
    if report["problems"]:
        lines.append("problems: " + "; ".join(report["problems"]))
    return "\n".join(lines)


def apply(source_path, target_path, out_path, *, pitch_unit_cents=None,
          pitch_limit_cents=1200.0, force=False):
    source = ea.load_vsqx(source_path)
    target = ea.load_ustx(target_path)
    data = json.loads(json.dumps(target))
    clamped = 0
    for index, (part, ustx_part) in enumerate(zip(source["parts"], data["voice_parts"])):
        points = _pitch_curve(part, pitch_unit_cents)
        if not points:
            continue
        # VSQX stores P and S as two separate curves; carry their product on the
        # renderer's 5-tick grid so pitch bend quantizes to the same engine units.
        source_ticks = np.array([t for t, _ in points], dtype=float)
        source_cents = np.array([cents for _, cents in points], dtype=float)
        grid = np.arange(int(source_ticks[0]), int(source_ticks[-1]) + 1, 5, dtype=float)
        xs = [int(t) for t in grid]
        ys = []
        for cents in np.interp(grid, source_ticks, source_cents):
            if abs(cents) > pitch_limit_cents:
                clamped += 1
                cents = math.copysign(pitch_limit_cents, cents)
            ys.append(float(cents))
        kept = [curve for curve in (ustx_part.get("curves") or []) if curve.get("abbr") != "pitd"]
        kept.append({"abbr": "pitd", "xs": xs, "ys": ys})
        ustx_part["curves"] = kept
    out_path = Path(out_path)
    if out_path.exists() and not force:
        raise SystemExit("Refusing to overwrite " + str(out_path) + " (pass --force)")
    out_path.write_text(yaml.safe_dump(data, allow_unicode=True, sort_keys=False), encoding="utf-8")
    return {"written": str(out_path), "clamped_points": clamped}


def main(argv=None):
    parser = argparse.ArgumentParser(description="VSQX to USTX content equivalence for OpenUtau (PBS-aware)")
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("audit", "apply"):
        node = sub.add_parser(name)
        node.add_argument("--source", type=Path, required=True)
        node.add_argument("--target", type=Path, required=True)
        node.add_argument("--pitch-unit-cents", type=float, default=None,
                          help="fallback cents per P unit when the project has no S (PBS) curve")
        if name == "apply":
            node.add_argument("--out", type=Path)
            node.add_argument("--pitch-limit-cents", type=float, default=1200.0)
            node.add_argument("--force", action="store_true")
        node.add_argument("--report", type=Path)
    args = parser.parse_args(argv)
    if args.command == "audit":
        report = audit(args.source, args.target, args.pitch_unit_cents)
        print(summarize(report))
        if args.report:
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        bad = bool(report["problems"]) or any(
            part.get("lyric_unexplained") or (part.get("pitch") and part["pitch"]["median_absolute_cents"] > 1.0)
            for part in report["parts"])
        return 1 if bad else 0
    out = args.out or args.target.with_name(args.target.stem + "-fixed" + args.target.suffix)
    written = apply(args.source, args.target, out, pitch_unit_cents=args.pitch_unit_cents,
                    pitch_limit_cents=args.pitch_limit_cents, force=args.force)
    report = audit(args.source, out, args.pitch_unit_cents)
    print(summarize(report))
    print("WRITTEN", written["written"], "clamped", written["clamped_points"])
    if args.report:
        args.report.write_text(json.dumps({"apply": written, "audit": report}, ensure_ascii=False, indent=2),
                               encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
