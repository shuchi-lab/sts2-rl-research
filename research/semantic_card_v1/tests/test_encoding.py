"""Run from the repository root with unittest discovery; no game installation needed."""

import copy
import json
import math
from pathlib import Path
import unittest

import numpy as np

from research.semantic_card_v1.catalog import Catalog, UnsupportedSemantics
from research.semantic_card_v1.encoding import encode_current_card
from research.semantic_card_v1.schema import CARD_DENSE_FIELDS, SEMANTIC_DIMS


ROOT = Path(__file__).resolve().parents[1]


class CurrentCardTests(unittest.TestCase):
    def encode(self, **kwargs):
        return encode_current_card(card_type="Attack", rarity="Basic", target="AnyEnemy", **kwargs)

    def test_known_zero_and_unavailable_are_different(self):
        absent = self.encode()
        known = self.encode(numeric={"damage": 0})
        self.assertEqual(absent.shape, (84,))
        self.assertEqual(absent.dtype, np.float32)
        self.assertEqual(tuple(absent[4:6]), (0, 0))
        self.assertEqual(tuple(known[4:6]), (0, 1))
        np.testing.assert_array_equal(self.encode(numeric={"damage": None}), absent)

    def test_exact_positions_and_signed_transform(self):
        row = self.encode(numeric={"current_cost": 1, "damage": 6, "bonus_damage": -2},
                          flags={"in_hand": True})
        self.assertAlmostEqual(float(row[0]), math.log(2), places=6)
        self.assertEqual(row[1], 1)
        self.assertAlmostEqual(float(row[4]), math.log(7), places=6)
        self.assertAlmostEqual(float(row[14]), -math.log(3), places=6)
        self.assertEqual(row[35], 1)
        self.assertEqual(row[59], 1)
        self.assertEqual(row[65], 1)
        self.assertEqual(row[76], 1)

    def test_invalid_values_fail_instead_of_becoming_unknown(self):
        for numeric in ({"damge": 6}, {"damage": float("nan")}, {"damage": float("inf")},
                        {"damage": True}, {"damage": "six"}):
            with self.subTest(numeric=numeric), self.assertRaises(ValueError):
                self.encode(numeric=numeric)
        for flags in ({"in_hnad": True}, {"in_hand": "false"}, {"playable": 2}):
            with self.subTest(flags=flags), self.assertRaises(ValueError):
                self.encode(flags=flags)
        with self.assertRaises(ValueError):
            encode_current_card(card_type="NotACardType", rarity="Basic", target="Self")


class CatalogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalog = Catalog()

    def program(self, name, upgraded=False):
        descriptor = self.catalog.card(self.catalog.by_name[name]["engine_id"], upgraded)
        return self.catalog.descriptors[descriptor].steps

    def test_schema_widths_and_all_admitted_variants(self):
        self.assertEqual(SEMANTIC_DIMS, dict(card=84, step=278, term=83, preview=15,
                                           continuation=20, selection=31))
        self.assertEqual(self.catalog.describe()["cards"], 207)
        for row in self.catalog.cards.values():
            if row["name"] == "Splash":
                continue
            for upgraded in (False, True):
                descriptor = self.catalog.card(row["engine_id"], upgraded)
                arrays = self.catalog.descriptor_arrays([descriptor])
                self.assertEqual(arrays["semantic_steps"].shape[-1], 278)
                self.assertEqual(arrays["semantic_terms"].shape[-1], 83)

    def test_bash_preserves_effect_order_and_upgrade_amounts(self):
        base = self.program("Bash")
        upgraded = self.program("Bash", True)
        self.assertEqual([step["op"] for step in base], ["damage", "status"])
        self.assertEqual(base[1]["property"], "vulnerable")
        self.assertEqual([step["amount"] for step in base], [8, 2])
        self.assertEqual([step["amount"] for step in upgraded], [10, 3])

    def test_true_grit_upgrade_changes_selection(self):
        self.assertEqual(self.program("TrueGrit")[-1]["selection"], "random")
        self.assertEqual(self.program("TrueGrit", True)[-1]["selection"], "chosen")

    def test_body_slam_retains_state_reference(self):
        terms = self.program("BodySlam")[0]["terms"]
        self.assertIn({"op": "state", "ref": "block", "coefficient": 1}, terms)

    def test_card_ids_do_not_change_encoded_mechanics(self):
        metadata = json.loads((ROOT / "source_metadata.json").read_text())
        altered = copy.deepcopy(metadata)
        for row in altered["cards"]:
            row["engine_id"] += 100000
        other = Catalog(altered)
        for name in ("Bash", "TrueGrit", "BodySlam", "FiendFire"):
            a = self.catalog.card(self.catalog.by_name[name]["engine_id"], True)
            b = other.card(other.by_name[name]["engine_id"], True)
            left, right = self.catalog.descriptor_arrays([a]), other.descriptor_arrays([b])
            for key in left:
                np.testing.assert_array_equal(left[key], right[key])

    def test_unsupported_cards_are_visible_errors(self):
        with self.assertRaises(UnsupportedSemantics):
            self.catalog.card(123456789)
        with self.assertRaises(UnsupportedSemantics):
            self.catalog.card(self.catalog.by_name["Splash"]["engine_id"])


if __name__ == "__main__":
    unittest.main()
