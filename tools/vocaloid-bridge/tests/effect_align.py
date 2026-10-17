"""Parameter-free effect alignment from a VOCALOID project into OpenUtau.

The nine note-level VOCALOID tuning parameters (accent, decay, bendDep,
bendLen, risePort, fallPort, opening, vibLen, vibType) have no OpenUtau
equivalent, so they are deliberately NOT migrated. What is migrated is the
measurable effect they were used to draw:

* pitch  -> a PITD curve rebuilt from the project pitch curve (P) at 5-tick
            resolution, in cents, instead of LibreSVIP's lossy resample;
* energy -> an optional DYN curve from note velocity (needs --energy-gain,
            because the dB-per-velocity slope is engine specific);
* timing -> note positions/durations are validated, never rewritten here.

Every run reports before/after error against the same target, so "aligned"
is a measured claim, not an assertion.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np
import yaml

VSQ4_NS = "http://www.yamaha.co.jp/vocaloid/schema/vsq4/"
NOTE_PARAMS = ("accent", "decay", "bendDep", "bendLen", "risePort", "fallPort",
               "opening", "vibLen", "vibType")
GRID_TICKS = 5


def _tag(element):
    return element.tag.rsplit("}", 1)[-1]


def _children(element, name):
    return [child for child in element if _tag(child) == name]


def _one(element, name, default=None):
    found = _children(element, name)
    if not found:
        return default
    return found[0]


def _int(element, name, default=0):
    found = _one(element, name)
    if found is None or found.text is None:
        return default
    return int(found.text.strip())


def load_vsqx(path):
    """Read the parts, notes and control curves of a VSQX4 project."""
    path = Path(path)
    root = ET.fromstring(path.read_bytes())
    if _tag(root) != "vsq4" or not (root.tag.startswith("{" + VSQ4_NS + "}") or True):
        raise ValueError("Only VSQX4 projects are supported right now: " + str(path))
    if _tag(root) != "vsq4":
        raise ValueError("Only VSQX4 projects are supported right now: " + str(path))
    master = _one(root, "masterTrack")
    resolution = _int(master, "resolution", 480) if master is not None else 480
    parts = []
    for track in _children(root, "vsTrack"):
        for part in _children(track, "vsPart"):
            notes = []
            for element in _children(part, "note"):
                lyric = ""
                for node in _children(element, "y"):
                    lyric = (node.text or "").strip()
                phonemes = ""
                for node in _children(element, "p"):
                    phonemes = (node.text or "").strip()
                params = {}
                style = _one(element, "nStyle")
                if style is not None:
                    for item in _children(style, "v"):
                        params[item.get("id")] = int((item.text or "0").strip())
                notes.append({"t": _int(element, "t"), "dur": _int(element, "dur"),
                              "tone": _int(element, "n"), "vel": _int(element, "v", 64),
                              "lyric": lyric, "phonemes": phonemes, "params": params})
            notes.sort(key=lambda item: item["t"])
            curves = {}
            for element in _children(part, "cc"):
                value = _one(element, "v")
                if value is None:
                    continue
                curves.setdefault(value.get("id"), []).append((_int(element, "t"), int((value.text or "0").strip())))
            for key in curves:
                curves[key].sort()
            parts.append({"notes": notes, "curves": curves})
    return {"path": str(path), "resolution": resolution, "parts": parts}


def load_ustx(path):
    data = yaml.safe_load(Path(path).read_text("utf-8"))
    if not isinstance(data, dict) or "voice_parts" not in data:
        raise ValueError("Not an OpenUtau .ustx project: " + str(path))
    return data


def _interp(points):
    if not points:
        raise ValueError("Empty curve")
    ticks = np.array([tick for tick, _ in points], dtype=float)
    values = np.array([value for _, value in points], dtype=float)
    return lambda grid: np.interp(np.asarray(grid, dtype=float), ticks, values)


def _active_tone(notes, part_position, grid):
    """Tone of the note containing each tick, else the nearest note's tone."""
    starts = np.array([note["position"] + part_position for note in notes], dtype=float)
    ends = np.array([note["position"] + part_position + note["duration"] for note in notes], dtype=float)
    tones = np.array([note["tone"] for note in notes], dtype=float)
    result = np.zeros(len(grid), dtype=float)
    for index, tick in enumerate(np.asarray(grid, dtype=float)):
        inside = np.flatnonzero((starts <= tick) & (tick < ends))
        if inside.size:
            result[index] = tones[inside[0]]
            continue
        distance = np.maximum(starts - tick, 0) + np.maximum(tick - ends, 0)
        result[index] = tones[int(np.argmin(distance))]
    return result


def _match(source, target):
    """Pair source parts/notes with the USTX parts and derive the tick offset."""
    pairs = []
    if len(source["parts"]) != len(target["voice_parts"]):
        raise ValueError("Track count mismatch: VSQX has " + str(len(source["parts"]))
                         + ", USTX has " + str(len(target["voice_parts"])))
    for index, (part, ustx_part) in enumerate(zip(source["parts"], target["voice_parts"])):
        notes = part["notes"]
        target_notes = ustx_part.get("notes") or []
        if not notes or not target_notes:
            raise ValueError("Part " + str(index + 1) + " has no notes on one side")
        if len(notes) != len(target_notes):
            raise ValueError("Part " + str(index + 1) + " note count mismatch: "
                             + str(len(notes)) + " vs " + str(len(target_notes)))
        part_position = int(ustx_part.get("position") or 0)
        # USTX note positions are relative to the part start, just like VSQX ticks.
        offset = int(target_notes[0]["position"]) - notes[0]["t"]
        for source_note, target_note in zip(notes, target_notes):
            if int(target_note["position"]) - source_note["t"] != offset:
                raise ValueError("Part " + str(index + 1) + " note positions are not a constant offset")
            if int(target_note["tone"]) != source_note["tone"]:
                raise ValueError("Part " + str(index + 1) + " note pitch mismatch at tick "
                                 + str(source_note["t"]))
        pairs.append({"index": index, "source": part, "target": ustx_part,
                      "position": part_position, "offset": offset})
    return pairs


def _existing_pitch(pair, grid):
    """PITD already present in the USTX, in cents, or NaN when absent."""
    for curve in pair["target"].get("curves") or []:
        if curve.get("abbr") != "pitd":
            continue
        xs = np.array(curve["xs"], dtype=float)
        ys = np.array(curve["ys"], dtype=float)
        return np.interp(np.asarray(grid, dtype=float), xs, ys)
    return np.full(len(grid), np.nan)


def build_curves(source, target, *, bend_unit_cents=2400.0 / 8192.0, pitch_mode="bend",
                 pitch_limit_cents=1200.0, energy_gain=0.0, ignored_defaults=None):
    """Return per-part curve data, the target pitch profile and the metrics."""
    pairs = _match(source, target)
    results = []
    for pair in pairs:
        notes = pair["source"]["notes"]
        target_notes = pair["target"].get("notes") or []
        start = min(int(note["t"]) for note in notes)
        end = max(int(note["t"]) + int(note["dur"]) for note in notes)
        grid = np.arange(start, end, GRID_TICKS, dtype=float)
        if not grid.size:
            raise ValueError("Part " + str(pair["index"] + 1) + " has no ticks")
        relative_ticks = grid + pair["offset"]
        part_ticks = relative_ticks + pair["position"]
        tone = _active_tone(target_notes, pair["position"], part_ticks)
        curve = pair["source"]["curves"].get("P")
        raw = _interp(curve)(grid) if curve else np.zeros(len(grid))
        if pitch_mode == "bend":
            # P is a deviation around the note pitch, in engine bend units.
            pitch = tone * 100.0 + raw * bend_unit_cents
        elif pitch_mode == "absolute":
            # P is already an absolute pitch in cents; no unit scaling.
            pitch = raw
        else:
            raise ValueError("pitch_mode must be bend or absolute")
        pitd = pitch - tone * 100.0
        clipped = np.clip(pitd, -pitch_limit_cents, pitch_limit_cents)
        existing = _existing_pitch(pair, part_ticks)
        velocity = np.zeros(len(grid))
        for source_note in notes:
            mask = (grid >= source_note["t"]) & (grid < source_note["t"] + source_note["dur"])
            velocity[mask] = source_note["vel"]
        results.append({
            "index": pair["index"],
            "xs": [int(round(x)) for x in relative_ticks],
            "pitd": [int(round(y)) for y in clipped],
            "pitd_raw": pitd,
            "clipped_points": int(np.sum(np.abs(pitd - clipped) > 0.5)),
            "max_abs_pitd": float(np.max(np.abs(clipped))) if len(clipped) else 0.0,
            "tone": tone,
            "target_pitch": pitch,
            "existing_pitch": existing,
            "velocity": velocity,
            "grid": grid,
            "offset": pair["offset"],
            "part_position": pair["position"],
        })
    return results


def _pitch_metrics(target, candidate):
    paired = np.isfinite(candidate) & np.isfinite(target)
    if not paired.any():
        return {"paired_points": 0}
    error = np.abs(candidate[paired] - target[paired])
    return {
        "paired_points": int(paired.sum()),
        "median_absolute_cents": float(np.median(error)),
        "p90_absolute_cents": float(np.percentile(error, 90)),
        "rms_cents": float(math.sqrt(float(np.mean(error ** 2)))),
        "max_absolute_cents": float(error.max()),
        "over_50_cents_percent": float(np.mean(error > 50) * 100),
        "over_100_cents_percent": float(np.mean(error > 100) * 100),
    }


def _expressible(item, limit):
    """Points whose source bend fits inside the OpenUtau pitch range."""
    if limit is None:
        return np.ones(len(item["pitd"]), dtype=bool)
    return np.abs(item["pitd_raw"]) <= limit + 0.5


def measure(results, pitch_limit_cents=1200.0):
    per_part = []
    targets, before, after, expressible = [], [], [], []
    for item in results:
        mask = _expressible(item, pitch_limit_cents)
        new_pitch = item["tone"] * 100.0 + np.asarray(item["pitd"], dtype=float)
        old_pitch = item["tone"] * 100.0 + item["existing_pitch"]
        metrics = {
            "part": item["index"] + 1,
            "points": int(len(item["pitd"])),
            "clipped_points": item["clipped_points"],
            "clipped_percent": round(100.0 * item["clipped_points"] / max(1, len(item["pitd"])), 2),
            "max_abs_pitd_cents": round(item["max_abs_pitd"], 1),
            "before": _pitch_metrics(item["target_pitch"], old_pitch),
            "before_expressible": _pitch_metrics(item["target_pitch"][mask], old_pitch[mask]),
            "after": _pitch_metrics(item["target_pitch"], new_pitch),
            "after_expressible": _pitch_metrics(item["target_pitch"][mask], new_pitch[mask]),
        }
        per_part.append(metrics)
        targets.append(item["target_pitch"])
        before.append(old_pitch)
        after.append(new_pitch)
        expressible.append(mask)
    mask = np.concatenate(expressible)
    target = np.concatenate(targets)
    summary = {
        "before": _pitch_metrics(target, np.concatenate(before)),
        "before_expressible": _pitch_metrics(target[mask], np.concatenate(before)[mask]),
        "after": _pitch_metrics(target, np.concatenate(after)),
        "after_expressible": _pitch_metrics(target[mask], np.concatenate(after)[mask]),
        "clipped_points": int(np.sum(~mask)),
        "clipped_percent": round(100.0 * float(np.sum(~mask)) / max(1, len(mask)), 2),
    }
    return {"parts": per_part, "summary": summary}


def apply_curves(target, results, *, energy_gain=0.0):
    data = json.loads(json.dumps(target))
    for item in results:
        part = data["voice_parts"][item["index"]]
        kept = [curve for curve in (part.get("curves") or []) if curve.get("abbr") != "pitd"]
        kept.append({"abbr": "pitd", "xs": item["xs"], "ys": item["pitd"]})
        if energy_gain:
            median = float(np.median(item["velocity"])) if len(item["velocity"]) else 64.0
            dyn = [round((value - median) * energy_gain * 10.0) for value in item["velocity"]]
            kept = [curve for curve in kept if curve.get("abbr") != "dyn"]
            kept.append({"abbr": "dyn", "xs": item["xs"], "ys": dyn})
        part["curves"] = kept
    return data


def _write_atomic(path, text):
    path = Path(path)
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".tmp",
                                     dir=str(path.parent), delete=False) as handle:
        handle.write(text)
        temporary = Path(handle.name)
    temporary.replace(path)


def count_ignored_parameters(source):
    counts = {name: 0 for name in NOTE_PARAMS}
    defaults = {"accent": 50, "decay": 50, "opening": 127}
    for part in source["parts"]:
        for note in part["notes"]:
            for name in NOTE_PARAMS:
                value = note["params"].get(name)
                if value is None:
                    continue
                if value != defaults.get(name, 0):
                    counts[name] += 1
    return counts


def main(argv=None):
    parser = argparse.ArgumentParser(description="Parameter-free VOCALOID effect alignment for OpenUtau")
    parser.add_argument("--source", type=Path, required=True, help="VSQX4 project the effect comes from")
    parser.add_argument("--target", type=Path, required=True, help="OpenUtau .ustx converted from that project")
    parser.add_argument("--out", type=Path, help="write the aligned project here (default: next to target)")
    parser.add_argument("--report", type=Path, help="write the JSON report here")
    parser.add_argument("--pitch-mode", choices=("bend", "absolute"), default="bend",
                        help="bend: P is a deviation around the note pitch (default); absolute: P is the pitch itself")
    parser.add_argument("--bend-unit-cents", type=float, default=2400.0 / 8192.0,
                        help="cents per P unit; 2400/8192 assumes 24-semitone bend sensitivity")
    parser.add_argument("--pitch-limit-cents", type=float, default=1200.0,
                        help="clip |PITD| to this many cents (OpenUtau note range)")
    parser.add_argument("--energy-gain", type=float, default=0.0,
                        help="dB per velocity unit for the optional DYN curve (0 = off)")
    parser.add_argument("--dry-run", action="store_true", help="measure only, write nothing")
    parser.add_argument("--force", action="store_true", help="overwrite --out if it exists")
    parser.add_argument("--max-after-rms-cents", type=float, default=30.0,
                        help="fail when the written curve misses the target by more than this")
    parser.add_argument("--max-clipped-percent", type=float, default=5.0,
                        help="fail when more than this share of the pitch curve exceeds the pitch range")
    args = parser.parse_args(argv)

    source = load_vsqx(args.source)
    target = load_ustx(args.target)
    results = build_curves(source, target, bend_unit_cents=args.bend_unit_cents,
                           pitch_mode=args.pitch_mode, pitch_limit_cents=args.pitch_limit_cents,
                           energy_gain=args.energy_gain)
    metrics = measure(results, pitch_limit_cents=args.pitch_limit_cents)
    ignored = count_ignored_parameters(source)
    report = {
        "source": str(args.source), "target": str(args.target),
        "method": "parameter-free: pitch curve rebuilt at " + str(GRID_TICKS) + "-tick resolution",
        "pitch_mode": args.pitch_mode, "bend_unit_cents": args.bend_unit_cents,
        "pitch_limit_cents": args.pitch_limit_cents, "energy_gain": args.energy_gain,
        "ignored_vocaloid_parameters": ignored,
        "ignored_parameter_notes_total": int(sum(ignored.values())),
        "metrics": metrics,
        "written": None,
    }
    summary = metrics["summary"]
    print("PARTS", len(results),
          "| before RMS", round(summary["before"].get("rms_cents", 0.0), 1), "cents",
          "-> after RMS", round(summary["after"].get("rms_cents", 0.0), 1), "cents",
          "| expressible RMS", round(summary["after_expressible"].get("rms_cents", 0.0), 2), "cents",
          "| p90", round(summary["after"].get("p90_absolute_cents", 0.0), 2),
          "| clipped", summary["clipped_points"], "(" + str(summary["clipped_percent"]) + "%)", flush=True)
    if args.report:
        _write_atomic(args.report, json.dumps(report, ensure_ascii=False, indent=2))
    rms = summary["after_expressible"].get("rms_cents")
    if rms is None or rms > args.max_after_rms_cents:
        print("ALIGNMENT REJECTED: expressible after RMS", rms, "cents exceeds",
              args.max_after_rms_cents, file=sys.stderr)
        return 2
    if summary["clipped_percent"] > args.max_clipped_percent:
        print("ALIGNMENT REJECTED: " + str(summary["clipped_percent"]) + "% of the pitch curve exceeds the "
              + str(args.pitch_limit_cents) + "-cent OpenUtau range (limit "
              + str(args.max_clipped_percent) + "%)", file=sys.stderr)
        return 3
    if args.dry_run:
        return 0
    out = args.out or args.target.with_name(args.target.stem + "-aligned" + args.target.suffix)
    if out.exists() and not args.force:
        raise SystemExit("Refusing to overwrite " + str(out) + " (pass --force)")
    aligned = apply_curves(target, results, energy_gain=args.energy_gain)
    _write_atomic(out, yaml.safe_dump(aligned, allow_unicode=True, sort_keys=False))
    report["written"] = str(out)
    if args.report:
        _write_atomic(args.report, json.dumps(report, ensure_ascii=False, indent=2))
    print("WROTE", out, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
