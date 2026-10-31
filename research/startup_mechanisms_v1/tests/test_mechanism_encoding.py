"""Behavioral contract checks for the public, standalone v1 encoder."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT.parent))
from startup_mechanisms_v1.encoding import append_features, encode
from startup_mechanisms_v1.schema import CARD_FIELDS, GLOBAL_FIELDS


def card(name="DefendIronclad", pile="hand", steps=(), card_type="Skill", tags=(), **flags):
    return dict(name=name, pile=pile, type=card_type, rarity="Basic", tags=list(tags),
                steps=list(steps), flags=dict(exhaust=False, ethereal=False, eternal=False) | flags)


def attack(name="StrikeIronclad", pile="hand", tags=("Strike",)):
    return card(name, pile, [dict(op="damage", target="enemy", powered=True)], "Attack", tags)


class MechanismTests(unittest.TestCase):
    def test_role_order_and_overlap(self):
        cards = [card("Rupture", card_type="Power"),
                 card("Bloodletting", steps=[dict(op="lose_hp", target="self")]),
                 attack("SwordBoomerang", tags=()),
                 attack("AshenStrike"),
                 card("BurningPact", steps=[dict(op="exhaust"), dict(op="draw")])]
        r = encode(cards, {}, [0, 1, 2, 3, 4])
        self.assertEqual(r.card_roles.shape, (5, 16))
        np.testing.assert_array_equal(r.card_roles[0, :4], [1, 0, 0, 0])
        np.testing.assert_array_equal(r.card_roles[1, :4], [0, 1, 0, 0])
        np.testing.assert_array_equal(r.card_roles[2, :8], [0, 0, 1, 0, 0, 0, 1, 0])
        np.testing.assert_array_equal(r.card_roles[3, 8:12], [1, 0, 1, 0])
        self.assertEqual(r.card_roles[4, 9], 1)
        self.assertEqual(r.card_roles[4, 13], 1)
        self.assertEqual(len(CARD_FIELDS), 16)
        self.assertEqual(len(GLOBAL_FIELDS), 24)

    def test_core_ownership_active_power_and_legal_hand_are_distinct(self):
        cards = [card("Rupture", "discard", card_type="Power"), attack()]
        r = encode(cards, {}, [])
        np.testing.assert_array_equal(r.global_raw[:4], [1, 0, 1, 0])
        r = encode([attack()], {"RupturePower": 2}, [0])
        np.testing.assert_array_equal(r.global_raw[:4], [1, 1, 1, 1])
        self.assertEqual(r.global_raw[17], 2)

    def test_supply_uses_visible_candidates_and_distinguishes_hand_from_draw(self):
        route = dict(op="move", source="discard", destination="draw_top", filter="any")
        headbutt = card("Headbutt", steps=[route])
        r = encode([headbutt, attack(pile="discard")], {}, [])
        np.testing.assert_array_equal(r.card_roles[0, [3, 7, 11, 15]], [1, 1, 0, 1])
        route["destination"] = "hand"
        r = encode([headbutt, attack(pile="discard")], {}, [])
        self.assertEqual(r.card_roles[0, 15], 0)
        r = encode([headbutt], {}, [])
        self.assertEqual(r.card_roles[0, 3], 0)
        route["filter"] = "skill"
        r = encode([headbutt, attack(pile="discard")], {}, [])
        self.assertEqual(r.card_roles[0, 3], 0)

    def test_supplier_cannot_supply_itself_from_hand(self):
        supplier = attack()
        supplier["steps"].append(dict(op="clone", source="hand", destination="draw_top"))
        self.assertEqual(encode([supplier], {}, []).card_roles[0, 15], 0)
        self.assertEqual(encode([supplier, attack()], {}, []).card_roles[0, 15], 1)

    def test_exhaust_threshold_uses_current_pile_and_requires_core(self):
        core = attack("AshenStrike")
        exhausted = [card(pile="exhaust") for _ in range(3)]
        self.assertEqual(encode([core, *exhausted[:2]], {}, []).global_raw[9], 0)
        self.assertEqual(encode([core, *exhausted], {}, []).global_raw[9], 1)
        self.assertEqual(encode(exhausted, {}, []).global_raw[9], 0)
        self.assertEqual(encode([core, *exhausted[:2]], {}, []).global_raw[19], 2)

    def test_instance_flags_corruption_and_eternal(self):
        cards = [card(), card(eternal=True), card(ethereal=True),
                 card(eternal=True, steps=[dict(op="exhaust")])]
        np.testing.assert_array_equal(encode(cards, {"Corruption": 1}, []).card_roles[:, 9], [1, 0, 1, 1])

    def test_current_totals_signed_log_and_no_draw(self):
        cards = [attack(pile="draw"), card(pile="draw"), attack(pile="discard")]
        r = encode(cards, {"Strength": -2, "Rupture": 2, "DemonForm": 3, "NoDraw": 1}, [])
        np.testing.assert_array_equal(r.global_raw[16:], [-2, 2, 3, 0, 2, 1, 1, 0])
        np.testing.assert_allclose(r.global_encoded[16:23], [-np.log(3), np.log(3), np.log(4), 0, np.log(3), np.log(2), np.log(2)], rtol=1e-6)
        self.assertEqual(r.global_encoded[23], 0)
        producer = card(steps=[dict(op="draw")])
        self.assertEqual(encode([producer], {"NoDraw": 1}, []).card_roles[0, 13], 1)
        self.assertEqual(encode([attack()], {}, [0]).global_raw[5], 0)
        self.assertEqual(encode([attack()], {}, [0]).card_roles[0, 5], 0)

    def test_append_preserves_inputs_and_uses_84_to_100_layout(self):
        cards = [attack()]
        saved = copy.deepcopy(cards)
        r = encode(cards, {}, [0])
        base_card = np.ones((1, 84), np.float32)
        base_global = np.arange(4, dtype=np.float32)
        c, g = append_features(base_card, base_global, r)
        self.assertEqual(c.shape, (1, 100))
        self.assertEqual(g.shape, (28,))
        np.testing.assert_array_equal(c[:, 84:], r.card_roles)
        np.testing.assert_array_equal(g[4:], r.global_encoded)
        np.testing.assert_array_equal(base_card, np.ones((1, 84)))
        np.testing.assert_array_equal(base_global, np.arange(4))
        self.assertEqual(cards, saved)
        self.assertEqual(c.dtype, np.float32)

    def test_invalid_inputs_fail_and_empty_shapes_are_explicit(self):
        for cards, buffs, legal in [([card()], {}, [1]), ([card()], {}, [-1]),
                ([card()], {}, [True]), ([card()], {"Strength": float("nan")}, []),
                ([card(pile="offer")], {}, []), ([card(exhaust="false")], {}, [])]:
            with self.subTest(cards=cards, buffs=buffs, legal=legal):
                with self.assertRaises(ValueError):
                    encode(cards, buffs, legal)
        r = encode([], {}, [])
        self.assertEqual(r.card_roles.shape, (0, 16))
        self.assertEqual(r.global_encoded.shape, (24,))
        with self.assertRaises(ValueError): append_features(np.zeros((0, 100)), np.zeros(2), r)
        with self.assertRaises(ValueError): append_features(np.zeros((0, 84)), np.array([np.nan]), r)


class ExampleTests(unittest.TestCase):
    def test_example_matches_saved_output_in_isolated_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "startup_mechanisms_v1"
            shutil.copytree(ROOT, package, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
            env = dict(os.environ)
            env.pop("PYTHONPATH", None)
            result = subprocess.run([sys.executable, "-I", str(package / "examples/inspect_features.py"), "--json"],
                                    cwd=directory, env=env, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            expected = json.loads((ROOT / "examples/expected-output.json").read_text())
            self.assertEqual(json.loads(result.stdout), expected)


if __name__ == "__main__":
    unittest.main()
