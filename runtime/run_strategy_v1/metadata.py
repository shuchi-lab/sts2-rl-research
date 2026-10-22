"""Static PUBLIC metadata for the strategy network (Task 3): card / relic / potion definitions from the
frozen catalog, plus the categorical vocabularies the projection uses. No recorded choice, label or
E tensor enters it. Every displayed identity must be in the catalog; an unknown id fails closed."""
from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .contracts import CANDIDATE_KINDS, PHASES, ContractError
from .map_features import NODE_TYPES

ANCIENTS = ("NEOW", "DARV", "OROBAS", "PAEL", "TEZCATARA", "NONUPEIPE", "TANX", "VAKUU")
EVENT_IDS = tuple(range(-1, 60))  # EventResultPending (-1) .. The Architect (59)
SELECTION_PURPOSES = 12  # DeckSelection enum 0..11
ROOMS = ("normal", "event", "elite", "boss")
FIGHT_OUTCOMES = ("victory", "death", "event_escape")
CARD_TYPES = ("Attack", "Curse", "Power", "Quest", "Skill", "Status", "None")
MAX_ENCOUNTER = 128
MAX_ENCHANT = 32  # embedding capacity; only the known values below are ever encoded
# Known values of the build08 engine enums displayed on a card instance (Sts2Emulator/Core/Card.cs):
# Enchantment None..Glam = 0..21, CardType Attack..None = 0..6 (None = 6 on an ordinary card),
# TinkerRider None..Improvement = 0..9. Anything else is an explicit unsupported modifier.
KNOWN_ENCHANTMENTS = 22
CARD_TYPE_NONE = 6
KNOWN_CARD_TYPES = 7
KNOWN_TINKER_RIDERS = 10
TINKER = 8
CARD_BOOLS = ("Ethereal", "Exhaust", "Unplayable", "Retain", "Innate", "InnateWhenUpgraded", "HasEnergyCostX",
              "HasStarCostX", "MultiplayerOnly", "CanBeGeneratedByModifiers", "CanBeGeneratedInCombat", "StrikeTag",
              "DefendTag", "TurnEndInHand", "ShivTag", "MinionTag", "OstyAttack")


def _onehot(value, options) -> list[float]:
    return [float(value == o) for o in options]


@dataclass
class PublicMetadata:
    catalog_sha256: str
    card_ids: list[int]
    relic_ids: list[int]
    potion_ids: list[int]
    card_static: np.ndarray  # [n_cards + 1, F] (row 0 padding)
    relic_static: np.ndarray
    potion_static: np.ndarray
    roster: frozenset
    card_index: dict = field(init=False)
    relic_index: dict = field(init=False)
    potion_index: dict = field(init=False)
    kind_index: dict = field(init=False)
    phase_index: dict = field(init=False)
    event_index: dict = field(init=False)
    ancient_index: dict = field(init=False)

    def __post_init__(self):
        self.card_index = {c: i + 1 for i, c in enumerate(self.card_ids)}
        self.relic_index = {c: i + 1 for i, c in enumerate(self.relic_ids)}
        self.potion_index = {c: i + 1 for i, c in enumerate(self.potion_ids)}
        self.kind_index = {k: i + 1 for i, k in enumerate(CANDIDATE_KINDS)}
        self.phase_index = {p: i for i, p in enumerate(PHASES)}
        self.event_index = {e: i + 1 for i, e in enumerate(EVENT_IDS)}
        self.ancient_index = {a: i + 1 for i, a in enumerate(ANCIENTS)}

    def card(self, def_id: int) -> int:
        try:
            return self.card_index[abs(int(def_id))]
        except KeyError:
            raise ContractError(f"card {def_id} is not in the public catalog") from None

    def relic(self, relic_id: int) -> int:
        try:
            return self.relic_index[int(relic_id)]
        except KeyError:
            raise ContractError(f"relic {relic_id} is not in the public catalog") from None

    def potion(self, potion_id: int) -> int:
        if potion_id == 0:
            return 0
        try:
            return self.potion_index[int(potion_id)]
        except KeyError:
            raise ContractError(f"potion {potion_id} is not in the public catalog") from None

    def kind(self, k: str) -> int:
        try:
            return self.kind_index[k]
        except KeyError:
            raise ContractError(f"unknown candidate kind {k}") from None

    def event(self, e: int) -> int:
        try:
            return self.event_index[int(e)]
        except KeyError:
            raise ContractError(f"unknown event id {e}") from None

    def ancient(self, a: str) -> int:
        try:
            return self.ancient_index[a]
        except KeyError:
            raise ContractError(f"unknown ancient {a}") from None

    @property
    def sizes(self) -> dict:
        return {"cards": len(self.card_ids), "relics": len(self.relic_ids), "potions": len(self.potion_ids),
                "kinds": len(CANDIDATE_KINDS), "phases": len(PHASES), "events": len(EVENT_IDS),
                "ancients": len(ANCIENTS), "card_static": self.card_static.shape[1],
                "relic_static": self.relic_static.shape[1], "potion_static": self.potion_static.shape[1],
                "node_types": len(NODE_TYPES)}

    def describe(self) -> dict:
        d = {"schema": "rs1/public_metadata/v1", "catalog_sha256": self.catalog_sha256, "sizes": self.sizes,
             "roster_cards": len(self.roster),
             "source": "frozen catalog definitions + rs1 categorical vocabularies; no recorded choices"}
        d["digest"] = hashlib.sha256(json.dumps(d, sort_keys=True).encode()
                                     + self.card_static.tobytes() + self.relic_static.tobytes()
                                     + self.potion_static.tobytes()).hexdigest()
        return d


def load_public_metadata(catalog: Path, roster_card_ids=()) -> PublicMetadata:
    raw = Path(catalog).read_bytes()
    cat = json.loads(raw)
    roster = frozenset(int(x) for x in roster_card_ids)
    cards = sorted(cat["cards"], key=lambda c: c["Id"])
    rarities = sorted({c["Rarity"] for c in cards})
    targets = sorted({c["Target"] for c in cards})
    rows = [np.zeros(0)]
    for c in cards:
        cost = c["Cost"]
        row = [cost / 3.0 if cost >= 0 else -1.0, c["BaseDamage"] / 20.0, c["BaseBlock"] / 20.0,
               c["UpgradeDamage"] / 10.0, c["UpgradeBlock"] / 10.0, c["UpgradeCost"] / 3.0]
        row += _onehot(c["Type"], CARD_TYPES) + _onehot(c["Rarity"], rarities) + _onehot(c["Target"], targets)
        row += [float(bool(c.get(k, False))) for k in CARD_BOOLS] + [float(c["Id"] in roster)]
        rows.append(np.asarray(row, dtype=np.float32))
    width = len(rows[1])
    rows[0] = np.zeros(width, dtype=np.float32)
    card_static = np.stack(rows)

    relics = sorted(cat["relics"], key=lambda r: r["Id"])
    rr = sorted({r["Rarity"] for r in relics})
    rel_rows = [np.zeros(len(rr) + 4, dtype=np.float32)]
    for r in relics:
        rel_rows.append(np.asarray(_onehot(r["Rarity"], rr) + [float(r["HasUponPickupEffect"]), float(r["SpawnsPets"]),
                                                                  float(r["IsAllowedInShops"]), float(r["StopsAfterAct3Chest"])],
                                   dtype=np.float32))
    potions = sorted(cat["potions"], key=lambda p: p["Id"])
    pr = sorted({p["Rarity"] for p in potions})
    pu = sorted({p["Usage"] for p in potions})
    pot_rows = [np.zeros(len(pr) + len(pu) + 2, dtype=np.float32)]
    for p in potions:
        pot_rows.append(np.asarray(_onehot(p["Rarity"], pr) + _onehot(p["Usage"], pu)
                                   + [float(p["TargetsEnemy"]), float(p["CanBeGeneratedInCombat"])], dtype=np.float32))
    return PublicMetadata(hashlib.sha256(raw).hexdigest(), [c["Id"] for c in cards], [r["Id"] for r in relics],
                          [p["Id"] for p in potions], card_static, np.stack(rel_rows), np.stack(pot_rows), roster)
