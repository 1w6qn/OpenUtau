#!/usr/bin/env python3
"""Integrate the Vocaloid bridge into a matching OpenUtau source checkout.

Dry-run is the default. Only exact, versioned source anchors are changed.
"""
from __future__ import annotations

import argparse
import difflib
import sys
from pathlib import Path


PATCHES = {
    "OpenUtau.Core/Ustx/USinger.cs": [
        ("[Flags] public enum USingerType { Classic = 0x1, Enunu = 0x2, Vogen = 0x4, DiffSinger = 0x5, Voicevox = 0x6 }",
         "[Flags] public enum USingerType { Classic = 0x1, Enunu = 0x2, Vogen = 0x4, DiffSinger = 0x5, Voicevox = 0x6, Vocaloid = 0x7 }"),
        ('            {USingerType.Voicevox, "voicevox"},',
         '            {USingerType.Voicevox, "voicevox"},\n            {USingerType.Vocaloid, "vocaloid"},'),
        ('            {"voicevox", USingerType.Voicevox},',
         '            {"voicevox", USingerType.Voicevox},\n            {"vocaloid", USingerType.Vocaloid},'),
    ],
    "OpenUtau.Core/Classic/ClassicSingerLoader.cs": [
        ("                case USingerType.Voicevox:\n                    return new Core.Voicevox.VoicevoxSinger(v) as USinger;",
         "                case USingerType.Voicevox:\n                    return new Core.Voicevox.VoicevoxSinger(v) as USinger;\n                case USingerType.Vocaloid:\n                    return new Core.Vocaloid.VocaloidSinger(v);"),
    ],
    "OpenUtau.Core/Render/Renderers.cs": [
        ('        public const string VOICEVOX = "VOICEVOX";',
         '        public const string VOICEVOX = "VOICEVOX";\n        public const string VOCALOID = "VOCALOID-BRIDGE";'),
        ('        static readonly string[] voicevoxRenderers = new[] { VOICEVOX };',
         '        static readonly string[] voicevoxRenderers = new[] { VOICEVOX };\n        static readonly string[] vocaloidRenderers = new[] { VOCALOID };'),
        ('                case USingerType.Voicevox:\n                    return voicevoxRenderers;',
         '                case USingerType.Voicevox:\n                    return voicevoxRenderers;\n                case USingerType.Vocaloid:\n                    return vocaloidRenderers;'),
        ('            } else if (renderer == VOICEVOX) {\n                return new Voicevox.VoicevoxRenderer();',
         '            } else if (renderer == VOICEVOX) {\n                return new Voicevox.VoicevoxRenderer();\n            } else if (renderer == VOCALOID) {\n                return new Vocaloid.VocaloidRenderer();'),
    ],
    "OpenUtau/Controls/NotesCanvas.cs": [
        ("                    int pitchStart = phrase.position - phrase.leading - Part.position;",
         "                    if (VocaloidPitchDrawing.Draw(context, viewModel, phrase, Part.position, pen, leftTick, rightTick)) {\n                        continue;\n                    }\n                    int pitchStart = phrase.position - phrase.leading - Part.position;"),
    ],
    "OpenUtau/Views/MainWindow.axaml": [
        ('            <MenuItem Header="{DynamicResource menu.file.importaudio}" Click="OnMenuImportAudio"/>',
         '            <MenuItem Header="{DynamicResource menu.file.importaudio}" Click="OnMenuImportAudio"/>\n            <MenuItem Header="导入 VOCALOID 声库…" Click="OnMenuImportVocaloid"/>'),
    ],
    "OpenUtau/Views/MainWindow.axaml.cs": [
        ('        async void OnMenuImportAudio(object sender, RoutedEventArgs args) {',
         '        async void OnMenuImportVocaloid(object sender, RoutedEventArgs args) {\n            await VocaloidImportDialog.ShowForImportAsync(this);\n        }\n\n        async void OnMenuImportAudio(object sender, RoutedEventArgs args) {'),
    ],
    "OpenUtau/OpenUtau.csproj": [
        ('  <ItemGroup>\n    <AvaloniaResource Include="Assets\\**" />\n  </ItemGroup>',
         '  <ItemGroup>\n    <AvaloniaResource Include="Assets\\**" />\n  </ItemGroup>\n  <ItemGroup>\n    <None Include="..\\tools\\vocaloid-bridge\\V5\\*.cs;..\\tools\\vocaloid-bridge\\V5\\*.ps1" Link="VocaloidBridge\\V5\\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />\n  </ItemGroup>'),
    ],
}


def read_text(path: Path) -> tuple[str, str, str]:
    raw = path.read_bytes()
    bom = "\ufeff" if raw.startswith(b"\xef\xbb\xbf") else ""
    text = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else "\n"
    return bom, newline, text


def encode_text(path: Path, bom: str, text: str) -> bytes:
    return (bom + text).encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", type=Path, required=True, help="OpenUtau repository root")
    parser.add_argument("--apply", action="store_true", help="Write the validated integration")
    args = parser.parse_args()
    target = args.target.resolve()
    plugin = Path(__file__).resolve().parent
    replacements: list[tuple[Path, bytes, bytes]] = []
    errors: list[str] = []

    # Validate every anchor and every payload before making any changes.
    for rel, patches in PATCHES.items():
        path = target / rel
        if not path.is_file():
            errors.append(f"missing required source file: {path}")
            continue
        bom, newline, original = read_text(path)
        updated = original
        for old, new in patches:
            old = old.replace("\n", newline)
            new = new.replace("\n", newline)
            new_count = updated.count(new)
            old_without_applied = updated.replace(new, "") if new_count == 1 else updated
            old_count = old_without_applied.count(old)
            if new_count == 1 and old_count == 0:
                continue  # already applied
            elif old_count == 1:
                updated = updated.replace(old, new, 1)
            else:
                errors.append(f"unknown or ambiguous source version at {path}: expected one exact anchor (found old={old_count}, patched={new_count})")
        replacements.append((path, encode_text(path, bom, original), encode_text(path, bom, updated)))

    destination = target / "OpenUtau.Core" / "Vocaloid"
    payloads = [(p, destination / p.name) for p in (
        sorted((plugin / "Core").glob("*.cs")) + sorted((plugin / "OpenUtau").glob("*.cs")))]
    payloads += [(p, target / "OpenUtau" / "Controls" / p.name) for p in sorted((plugin / "UI").glob("*.cs"))]
    payloads += [(p, target / "tools" / "vocaloid-bridge" / "V5" / p.name)
                 for p in sorted((plugin / "V5").iterdir()) if p.suffix in (".cs", ".ps1")]
    if not payloads:
        errors.append(f"no bridge source payloads found under {plugin}")
    for source, dest in payloads:
        if not source.is_file():
            errors.append(f"invalid payload: {source}")
        elif dest.exists() and dest.read_bytes() != source.read_bytes():
            errors.append(f"existing destination would be overwritten: {dest}")
    if errors:
        print("Integration refused:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 2

    if not args.apply:
        for path, before, after in replacements:
            old = before.decode("utf-8-sig").splitlines(keepends=True)
            new = after.decode("utf-8-sig").splitlines(keepends=True)
            diff = difflib.unified_diff(old, new, fromfile=str(path), tofile=str(path) + " (integrated)")
            sys.stdout.writelines(diff)
        print(f"Would copy {len(payloads)} C# source file(s) into Core/Vocaloid and UI/Controls")
        print("Dry run only. Pass --apply to write these exact changes.")
        return 0

    for path, before, after in replacements:
        if before != after:
            # Re-read to avoid replacing content changed after validation.
            if path.read_bytes() != before:
                print(f"Integration refused: target changed during validation: {path}", file=sys.stderr)
                return 2
    for source, dest in payloads:
        content = source.read_bytes()
        if dest.exists() and dest.read_bytes() != content:
            print(f"Integration refused: existing destination would be overwritten: {dest}", file=sys.stderr)
            return 2
    for path, before, after in replacements:
        if before != after:
            path.write_bytes(after)
    for source, dest in payloads:
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(source.read_bytes())
    print(f"Integrated Vocaloid bridge into {target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
