"""Protect lethality target mapping, unknown results, and the public example contract."""

import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

import numpy as np

from research.current_hand_lethality_v1.encoding import encode, feature_arrays


ROOT = Path(__file__).resolve().parents[1]


def observation():
    """Enemy 0 is killable; targeting enemy 1 must not inherit that hint."""
    return {
        "enemies": [{"hp": 17}, {"hp": 50}, {"hp": 0}],
        "legal": [
            {"kind": "play_card", "hand_index": 0, "target": 0},
            {"kind": "play_card", "hand_index": 1, "target": 1},
            {"kind": "play_card", "hand_index": 2, "target": -1},
            {"kind": "end_turn", "target": -1},
        ],
        "current_hand_lethality": {
            "schema": "current-hand-lethality/v1",
            "enemies": [{"Found": True, "Complete": True},
                        {"Found": False, "Complete": False},
                        {"Found": False, "Complete": True}],
            "actions": [
                {"Option": 0, "Hand": 0, "Target": 0, "Immediate": [False, False, False],
                 "Found": [True, False, False], "ImmediateKnown": True, "Complete": True},
                {"Option": 1, "Hand": 1, "Target": 1, "Immediate": [True, False, False],
                 "Found": [True, False, False], "ImmediateKnown": True, "Complete": False},
                {"Option": 2, "Hand": 2, "Target": -1, "Immediate": [False, False, False],
                 "Found": [True, False, False], "ImmediateKnown": True, "Complete": False},
            ],
        },
    }


class EncodingTests(unittest.TestCase):
    def test_selected_target_and_untargeted_setup_have_distinct_meanings(self):
        enemies, actions = feature_arrays(observation())
        np.testing.assert_array_equal(enemies, [[1, 1], [0, 0], [0, 0]])
        np.testing.assert_array_equal(actions,
                                      [[0, 1, 1, 1], [0, 0, 1, 0], [0, 1, 1, 0], [0, 0, 0, 0]])
        self.assertEqual(enemies.dtype, np.float32)
        self.assertEqual(actions.dtype, np.float32)

    def test_untargeted_action_ignores_dead_enemies(self):
        raw = observation()
        row = raw["current_hand_lethality"]["actions"][2]
        row["Found"] = row["Immediate"] = [False, False, True]
        _, actions = feature_arrays(raw)
        np.testing.assert_array_equal(actions[2], [0, 0, 1, 0])

    def test_missing_action_result_is_unknown(self):
        raw = observation()
        raw["current_hand_lethality"]["actions"] = []
        _, actions = feature_arrays(raw)
        self.assertFalse(actions.any())

    def test_complete_negative_is_distinct_from_unknown(self):
        raw = observation()
        raw["current_hand_lethality"]["enemies"][0]["Found"] = False
        enemies, _ = feature_arrays(raw)
        np.testing.assert_array_equal(enemies[:2], [[0, 1], [0, 0]])

    def test_mask_preserves_current_hp_and_inputs_and_appends_flags(self):
        raw = observation()
        before = copy.deepcopy(raw)
        enemy = np.arange(90, dtype=np.float32).reshape(3, 30)
        action = np.arange(24, dtype=np.float32).reshape(4, 6)
        enemy_before, action_before = enemy.copy(), action.copy()
        out_e, out_a = encode(raw, enemy, action)
        self.assertEqual(out_e.shape, (3, 32))
        self.assertEqual(out_a.shape, (4, 10))
        np.testing.assert_array_equal(out_e[:, 1:3], 0)
        np.testing.assert_array_equal(out_e[:, 0], enemy[:, 0])
        np.testing.assert_array_equal(out_e[:, 3:30], enemy[:, 3:])
        np.testing.assert_array_equal(out_a[:, :6], action)
        np.testing.assert_array_equal(out_e[:, 30:], [[1, 1], [0, 0], [0, 0]])
        np.testing.assert_array_equal(enemy, enemy_before)
        np.testing.assert_array_equal(action, action_before)
        self.assertEqual(raw, before)
        self.assertFalse(np.shares_memory(enemy, out_e))
        self.assertFalse(np.shares_memory(action, out_a))

    def test_baseline_needs_no_judge_output_but_has_same_width_and_mask(self):
        raw = observation()
        del raw["current_hand_lethality"]
        enemies, actions = encode(raw, np.ones((3, 30)), np.ones((4, 6)),
                                  profile="masked_baseline")
        self.assertEqual(enemies.shape, (3, 32))
        self.assertEqual(actions.shape, (4, 10))
        np.testing.assert_array_equal(enemies[:, 1:3], 0)
        self.assertFalse(enemies[:, 30:].any())
        self.assertFalse(actions[:, 6:].any())
        self.assertEqual(enemies.dtype, np.float32)

    def test_wrong_schema_alignment_and_non_boolean_flags_fail(self):
        for mutation in ("schema", "target", "duplicate", "width", "flag", "option_type", "missing"):
            with self.subTest(mutation=mutation):
                raw = observation()
                data = raw["current_hand_lethality"]
                if mutation == "schema": data["schema"] = "future/v2"
                elif mutation == "target": data["actions"][0]["Target"] = 1
                elif mutation == "duplicate": data["actions"].append(data["actions"][0])
                elif mutation == "width": data["actions"][0]["Found"] = [True]
                elif mutation == "flag": data["enemies"][0]["Found"] = "false"
                elif mutation == "option_type": data["actions"][0]["Option"] = True
                else: del raw["current_hand_lethality"]
                with self.assertRaises(ValueError): feature_arrays(raw)

    def test_bad_profile_shape_and_nonfinite_inputs_fail(self):
        for profile, enemy, action in (
            ("typo", np.zeros((3, 30)), np.zeros((4, 6))),
            ("masked_lethality", np.zeros((3, 32)), np.zeros((4, 6))),
            ("masked_lethality", np.zeros((3, 30)), np.zeros((3, 6))),
            ("masked_lethality", np.full((3, 30), np.nan), np.zeros((4, 6))),
        ):
            with self.subTest(profile=profile, shape=enemy.shape):
                with self.assertRaises(ValueError): encode(observation(), enemy, action, profile=profile)

    def test_empty_observation_has_two_dimensional_outputs(self):
        raw = {"enemies": [], "legal": [], "current_hand_lethality": {
            "schema": "current-hand-lethality/v1", "enemies": [], "actions": []}}
        enemies, actions = encode(raw, np.zeros((0, 30)), np.zeros((0, 6)))
        self.assertEqual(enemies.shape, (0, 32))
        self.assertEqual(actions.shape, (0, 10))


class ExampleTests(unittest.TestCase):
    def test_native_bash_strike_fixture_and_recorded_paths(self):
        fixture = json.loads((ROOT / "examples/bash_strike.json").read_text())
        enemies, actions = encode(fixture["observation"], fixture["base_enemy_features"],
                                  fixture["base_action_features"])
        np.testing.assert_array_equal(enemies[:, 30:], [[1, 1]])
        np.testing.assert_array_equal(actions[:, 6:], [[0, 1, 1, 1], [0, 0, 1, 1], [0, 0, 0, 0]])
        forward, reverse = fixture["native_paths"]
        self.assertEqual(forward["steps"][-1]["status"], "win")
        self.assertEqual(forward["steps"][-1]["enemy_hp"], [])
        self.assertEqual(reverse["steps"][-1]["status"], "running")
        self.assertEqual(reverse["steps"][-1]["enemy_hp"], [1])

    def test_example_runs_in_isolation_without_private_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            module = Path(directory) / "research/current_hand_lethality_v1"
            shutil.copytree(ROOT, module, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
            env = dict(os.environ)
            env.pop("PYTHONPATH", None)
            for profile, expected in (("masked_lethality", [0, 1, 1, 1]),
                                      ("masked_baseline", [0, 0, 0, 0])):
                process = subprocess.run(
                    [sys.executable, "-I", str(module / "examples/inspect_features.py"),
                     "--profile", profile, "--json"],
                    cwd=directory, env=env, text=True, capture_output=True, timeout=30)
                self.assertEqual(process.returncode, 0, process.stderr)
                data = json.loads(process.stdout)
                self.assertEqual(data["actions"][0]["added_values"], expected)
                self.assertEqual(data["masked_enemy_columns"], [1, 2])

    def test_provenance_matches_packaged_files(self):
        source = json.loads((ROOT / "SOURCE.json").read_text())
        for name, digest in source["packaged_sha256"].items():
            self.assertEqual(hashlib.sha256((ROOT / name).read_bytes()).hexdigest(), digest, name)


if __name__ == "__main__":
    unittest.main()
