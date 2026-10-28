"""Check public examples, documented layouts, and absence of private dependencies."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from research.semantic_card_v1.examples.inspect_card import build_example
from research.semantic_card_v1.reference import render_reference


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "examples" / "inspect_card.py"


class PublicExampleTests(unittest.TestCase):
    def test_saved_bash_output_is_reproducible(self):
        expected = json.loads((ROOT / "examples" / "expected-output.json").read_text(encoding="utf-8"))
        actual = build_example("Bash", numeric={"current_cost": 2.0, "damage": 8.0, "hits": 1.0},
                               in_hand=True, playable=True)
        self.assertEqual(actual, expected)

    def test_reference_covers_all_511_named_positions(self):
        reference = render_reference()
        self.assertEqual((ROOT / "docs" / "feature-reference.md").read_text(encoding="utf-8"), reference)
        rows = [line for line in reference.splitlines() if line.startswith("| ") and " | `" in line]
        self.assertEqual(len(rows), 511)
        self.assertIn("| 4 | `damage` | slog + known |", reference)
        self.assertIn("| 59 | `type:Attack` | one-hot |", reference)
        self.assertIn("| 15 | `frame_summary` | slog (also flags) |", reference)

    def test_missing_preview_never_falls_back_to_printed_damage(self):
        result = build_example("Bash")
        current = {entry["name"]: entry["value"] for entry in result["current_card"]["nonzero_features"]}
        self.assertNotIn("damage", current)
        self.assertNotIn("damage_known", current)
        self.assertNotIn("current_cost_known", current)
        self.assertEqual(current["upgrade_level_known"], 1)
        self.assertEqual(result["program"][0]["mechanics"]["amount"], 8)

    def test_cli_works_from_an_isolated_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            isolated = Path(directory)
            module = isolated / "research" / "semantic_card_v1"
            shutil.copytree(ROOT, module, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
            env = dict(os.environ)
            env.pop("PYTHONPATH", None)
            process = subprocess.run(
                [sys.executable, "-I", str(module / "examples" / "inspect_card.py"),
                 "--card", "TrueGrit", "--upgraded", "--json"],
                cwd=isolated, env=env, capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(process.returncode, 0, process.stderr)
            data = json.loads(process.stdout)
            self.assertEqual(data["program"][-1]["mechanics"]["selection"], "chosen")

    def test_bad_cli_inputs_are_clear_errors(self):
        for arguments in (("--card", "UnknownCard"), ("--card", "Splash"),
                          ("--card", "MadScience"), ("--damage", "nan")):
            with self.subTest(arguments=arguments):
                process = subprocess.run([sys.executable, "-I", str(SCRIPT), *arguments],
                                         capture_output=True, text=True, timeout=30)
                self.assertEqual(process.returncode, 2)
                self.assertIn("error:", process.stderr)
                self.assertNotIn("Traceback", process.stderr)

    def test_packaged_snapshot_hashes_match(self):
        manifest = json.loads((ROOT / "SOURCE.json").read_text(encoding="utf-8"))
        for name, hashes in manifest["files"].items():
            self.assertEqual(hashlib.sha256((ROOT / name).read_bytes()).hexdigest(),
                             hashes["packaged_sha256"], name)
        for name in ("schema.py", "source_metadata.json"):
            self.assertEqual(manifest["files"][name]["source_sha256"],
                             manifest["files"][name]["packaged_sha256"])


if __name__ == "__main__":
    unittest.main()
