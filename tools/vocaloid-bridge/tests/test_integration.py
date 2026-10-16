"""Registration patch acceptance against an actual OpenUtau source checkout."""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]
SOURCE = Path(os.environ.get("OPENUTAU_SOURCE", REPO if (REPO / "OpenUtau.Core").is_dir() else REPO / "tmp/vocaloid-research/OpenUtau"))
FILES = (
    "OpenUtau.Core/Ustx/USinger.cs",
    "OpenUtau.Core/Classic/ClassicSingerLoader.cs",
    "OpenUtau.Core/Render/Renderers.cs",
    "OpenUtau/Controls/NotesCanvas.cs",
    "OpenUtau/Views/MainWindow.axaml",
    "OpenUtau/Views/MainWindow.axaml.cs",
    "OpenUtau/OpenUtau.csproj",
)


@unittest.skipUnless((SOURCE / ".git").exists(), "Set OPENUTAU_SOURCE to a real OpenUtau git checkout")
class IntegrationAcceptance(unittest.TestCase):
    def setUp(self):
        scratch = REPO / "tmp/vocaloid-integration-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.tmp = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.tmp.cleanup)
        self.target = Path(self.tmp.name)
        for relative in FILES:
            original = subprocess.check_output(["git", "-C", str(SOURCE), "show", f"HEAD:{relative}"])
            # Exercise BOM and CRLF preservation on real source content.
            original = b"\xef\xbb\xbf" + original.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
            path = self.target / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(original)

    def snapshot(self):
        return {
            p.relative_to(self.target).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in self.target.rglob("*") if p.is_file()
        }

    def invoke(self, apply=False):
        command = [sys.executable, str(ROOT / "integrate.py"), "--target", str(self.target)]
        if apply:
            command.append("--apply")
        return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", env={**os.environ, "PYTHONIOENCODING": "utf-8"})

    def test_dry_run_and_idempotent_registration(self):
        before = self.snapshot()
        result = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.snapshot(), before, "dry-run must leave every file untouched")
        result = self.invoke(apply=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        singer = (self.target / FILES[0]).read_text(encoding="utf-8-sig")
        loader = (self.target / FILES[1]).read_text(encoding="utf-8-sig")
        renderer = (self.target / FILES[2]).read_text(encoding="utf-8-sig")
        self.assertEqual(singer.count('"vocaloid"'), 2)
        self.assertIn("Vocaloid = 0x7", singer)
        self.assertIn("new Core.Vocaloid.VocaloidSinger(v)", loader)
        self.assertIn("return vocaloidRenderers;", renderer)
        self.assertIn("new Vocaloid.VocaloidRenderer()", renderer)
        self.assertIn("VocaloidPitchDrawing.Draw", (self.target / FILES[3]).read_text(encoding="utf-8-sig"))
        self.assertTrue((self.target / "OpenUtau/Controls/VocaloidPitchDrawing.cs").exists())
        self.assertIn("OnMenuImportVocaloid", (self.target / FILES[4]).read_text(encoding="utf-8-sig"))
        self.assertTrue((self.target / "tools/vocaloid-bridge/V5/run-native-host.ps1").exists())
        for relative in FILES:
            data = (self.target / relative).read_bytes()
            self.assertTrue(data.startswith(b"\xef\xbb\xbf"))
            self.assertNotIn(b"\n", data.replace(b"\r\n", b""))
        after = self.snapshot()
        self.assertEqual(self.invoke(apply=True).returncode, 0)
        self.assertEqual(self.snapshot(), after, "applying twice must not duplicate registrations")

    def test_unknown_version_refuses_all_changes(self):
        path = self.target / "OpenUtau.Core/Render/Renderers.cs"
        path.write_bytes(path.read_bytes().replace(b"VOICEVOX", b"UPSTREAM_RENAMED_ENGINE"))
        before = self.snapshot()
        result = self.invoke(apply=True)
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.snapshot(), before, "failed validation must not partially patch source")

    def test_existing_payload_is_not_overwritten(self):
        path = self.target / "OpenUtau.Core/Vocaloid/Contracts.cs"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("// user's own edited bridge\n", encoding="utf-8")
        before = self.snapshot()
        result = self.invoke(apply=True)
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.snapshot(), before)

    def test_existing_ui_payload_is_not_overwritten(self):
        path = self.target / "OpenUtau/Controls/VocaloidPitchDrawing.cs"
        path.write_text("// user's own UI helper\n", encoding="utf-8")
        before = self.snapshot()
        result = self.invoke(apply=True)
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.snapshot(), before)


if __name__ == "__main__":
    unittest.main(verbosity=2)
