"""Identity vocabularies (cards, relics, potions, enemies, effects, intents, selection kinds)
and the static per-card-definition feature table, all derived from the frozen catalog and the
worker's own enum lists. Index 0 is padding in every vocabulary; an id missing from a
vocabulary is an error, never mapped to a shared "unknown" row.

Two vocabularies exist, chosen by the environment's effect scope:

* ``crl.vocab/v2`` (first-audited-v1, exploratory): the retained 214-card roster, catalog enemies.
* ``crl.vocab/v3-three-act`` (three-act-v1): every card definition in the ENGINE's card table
  (checked identical, id and entry, to the frozen catalog: 596), every enemy definition in the
  engine's enemy table (the catalog's 106 plus the five engine-only ids, each name-checked
  against :data:`ENGINE_ONLY_ENEMIES`), and a wider static card table (star cost, Osty/Shiv/
  Minion tags, rarity). Foreign and generated cards therefore have their own identity rows.
* ``crl.vocab/v3-three-act-214`` (three-act-214-v1): the v3 enemies and v3 static card columns,
  but card identities are exactly the retained 214-card roster (sorted engine ids, the same ids
  and dense indices as ``crl.vocab/v2``): 215 model rows with row 0 padding, no unknown row. A
  card outside the roster is an :class:`~crl.features.UnsupportedState`, never dropped.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

from . import paths

VOCAB_SCHEMA = "crl.vocab/v2"
VOCAB_SCHEMA_V3 = "crl.vocab/v3-three-act"
VOCAB_SCHEMA_V3_214 = "crl.vocab/v3-three-act-214"
V3_SCHEMAS = (VOCAB_SCHEMA_V3, VOCAB_SCHEMA_V3_214)  # both select the crl.features/v3 encoder
ROSTER_SIZE = 214
# What the 214-card scope promises about where cards come from (recorded in the vocabulary
# and in Context provenance; see audits/claude_three_act_214_notes.md for the engine audit).
ACQUISITION_RESTRICTIONS_214 = {
    "card_identities": "exactly the 214 retained roster entries (sorted engine ids); row 0 padding, no unknown row",
    "starting_deck": "derived templates drop non-roster Deck instances; Context rejects any template that still has one",
    "combat_generation": "engine rules unchanged; a non-roster card anywhere in an observation is UnsupportedState "
                         "(episode status 'unsupported'), never dropped",
    "potions": "intentional potion use withheld by the worker; automatic potions unchanged",
    "macro_acquisition": "outside combat scope; a macro policy must decline non-roster cards",
}

# Card-definition booleans copied verbatim from the catalog as 0/1 features.
CARD_DEF_BOOLS = (
    "Ethereal", "Exhaust", "Unplayable", "Retain", "Innate", "InnateWhenUpgraded", "HasEnergyCostX",
    "StrikeTag", "DefendTag", "TurnEndInHand", "Upgradable", "Sly", "RetainWhenUpgraded",
    "ExhaustRemovedWhenUpgraded", "EtherealRemovedWhenUpgraded", "Eternal", "GainsBlock",
)
CARD_DEF_NUMS = ("Cost", "BaseDamage", "BaseBlock", "UpgradeDamage", "UpgradeBlock", "UpgradeCost")
# v3 additions: foreign-character resources and tags the full catalog carries.
CARD_DEF_NUMS_V3 = CARD_DEF_NUMS + ("StarCost",)
CARD_DEF_BOOLS_V3 = CARD_DEF_BOOLS + (
    "HasStarCostX", "OstyAttack", "ShivTag", "MinionTag", "MultiplayerOnly", "CanBeGeneratedInCombat",
    "CanBeGeneratedByModifiers",
)

# Enemy definitions the engine can create that catalog.json does not list (Generated/Enemies.g.cs
# and KE constants). Spawned mid-fight (Battle Friend upgrades, the Test Subject) or event-only.
ENGINE_ONLY_ENEMIES = {
    10003: "FakeMerchantMonster", 10004: "BattleFriendV1", 10005: "BattleFriendV2",
    10006: "BattleFriendV3", 10007: "TestSubject",
}
# Reviewed additions to the build08 engine's enemy table (engine/whole_game_build08): three
# Decimillipede segment names its combat code never creates, and the Mysterious Knight event's
# own identity (the frozen engine reported that fight as a Flail Knight). No retained template
# reaches them (audits/engine_sync_20261003_v1). They are kept OUTSIDE the v3 vocabularies, so
# the v3 vocabulary documents and digests are the same on both engine lines, and a creature with
# one of these ids is an UnsupportedState ("not in catalog"), never mapped to another row.
BUILD08_ENEMIES_OUTSIDE_VOCAB = {
    107: "DecimillipedeSegmentBack", 108: "DecimillipedeSegmentFront", 109: "DecimillipedeSegmentMiddle",
    110: "MysteriousKnight",
}


def slog(x):
    return np.sign(x) * np.log1p(np.abs(x))


@dataclass
class Vocab:
    card_ids: list[int]
    relic_ids: list[int]
    potion_ids: list[int]
    enemy_ids: list[int]
    buff_names: list[str]
    intent_types: list[str]
    selection_kinds: list[str]
    card_types: list[str]
    card_targets: list[str]
    card_static: np.ndarray  # [n_cards + 1, F] (row 0 = padding)
    card_static_names: list[str]
    roster_card_ids: set[int]
    catalog_sha256: str
    schema: str = VOCAB_SCHEMA
    # v3 only: provenance of identities the catalog does not carry (engine-only enemies).
    extra: dict = field(default_factory=dict)
    card_index: dict[int, int] = field(init=False)
    relic_index: dict[int, int] = field(init=False)
    potion_index: dict[int, int] = field(init=False)
    enemy_index: dict[int, int] = field(init=False)

    def __post_init__(self) -> None:
        self.card_index = {c: i + 1 for i, c in enumerate(self.card_ids)}
        self.relic_index = {c: i + 1 for i, c in enumerate(self.relic_ids)}
        self.potion_index = {c: i + 1 for i, c in enumerate(self.potion_ids)}
        self.enemy_index = {c: i + 1 for i, c in enumerate(self.enemy_ids)}
        self.buff_index = {n: i for i, n in enumerate(self.buff_names)}

    @property
    def sizes(self) -> dict[str, int]:
        return {
            "cards": len(self.card_ids) + 1,
            "relics": len(self.relic_ids) + 1,
            "potions": len(self.potion_ids) + 1,
            "enemies": len(self.enemy_ids) + 1,
            "buffs": len(self.buff_names) + 1,  # +1: row 0 padding, buff b -> b + 1
            "intents": len(self.intent_types) + 1,
            "selection_kinds": len(self.selection_kinds) + 1,
        }

    def describe(self) -> dict:
        d = {
            "schema": self.schema,
            "catalog_sha256": self.catalog_sha256,
            "card_ids": self.card_ids,
            "relic_ids": self.relic_ids,
            "potion_ids": self.potion_ids,
            "enemy_ids": self.enemy_ids,
            "buff_names": self.buff_names,
            "intent_types": self.intent_types,
            "selection_kinds": self.selection_kinds,
            "card_types": self.card_types,
            "card_targets": self.card_targets,
            "card_static_names": self.card_static_names,
            "card_static_sha256": paths.sha256_bytes(np.ascontiguousarray(self.card_static).tobytes()),
        }
        if self.schema != VOCAB_SCHEMA:  # v2 documents stay byte-identical (same digest)
            d["extra"] = self.extra
        d["digest"] = paths.digest(d)
        return d


def build_vocab(hello: dict, catalog: dict | None = None, scope: str = "first-audited-v1") -> Vocab:
    """The vocabulary for an environment effect scope (see the module docstring)."""
    if scope == "three-act-v1":
        return build_vocab_v3(hello, catalog)
    if scope == "three-act-214-v1":
        return build_vocab_v3_214(hello, catalog)
    if scope not in ("first-audited-v1", "exploratory"):
        raise ValueError(f"unknown effect scope {scope}")
    catalog = catalog if catalog is not None else paths.read_json(paths.CATALOG)
    roster = {r["entry_id"] for r in paths.read_json(paths.ROSTER)["rows"]}
    cards = _roster_cards(catalog, roster)
    types = sorted({c["Type"] for c in cards})
    targets = sorted({c["Target"] for c in cards})
    names = [f"def_{n}" for n in CARD_DEF_NUMS] + [f"def_{b}" for b in CARD_DEF_BOOLS]
    names += [f"type_{t}" for t in types] + [f"target_{t}" for t in targets] + ["in_roster"]
    table = np.zeros((len(cards) + 1, len(names)), dtype=np.float32)
    for i, c in enumerate(cards, start=1):
        row = [float(slog(max(c[n], -1))) for n in CARD_DEF_NUMS]
        row += [float(bool(c[b])) for b in CARD_DEF_BOOLS]
        row += [float(c["Type"] == t) for t in types] + [float(c["Target"] == t) for t in targets]
        row += [float(c["Entry"] in roster)]
        table[i] = row
    _check_codebook(hello["buff_ids"])
    return Vocab(
        card_ids=[c["Id"] for c in cards],
        relic_ids=sorted(r["Id"] for r in catalog["relics"]),
        potion_ids=sorted(p["Id"] for p in catalog["potions"]),
        enemy_ids=sorted(e["Id"] for e in catalog["enemies"]),
        buff_names=list(hello["buff_ids"]),
        intent_types=list(hello["intent_types"]),
        selection_kinds=list(hello["selection_kinds"]),
        card_types=types,
        card_targets=targets,
        card_static=table,
        card_static_names=names,
        roster_card_ids={c["Id"] for c in cards if c["Entry"] in roster},
        catalog_sha256=paths.sha256_file(paths.CATALOG),
    )


def _roster_cards(catalog: dict, entries: set[str]) -> list[dict]:
    """The roster's catalog rows sorted by engine id; every entry must resolve exactly once."""
    cards = sorted((c for c in catalog["cards"] if c["Entry"] in entries), key=lambda c: c["Id"])
    if len(cards) != len(entries) or len(entries) != ROSTER_SIZE:
        raise ValueError("the retained 214-card roster must resolve exactly in the frozen catalog")
    return cards


def _v3_engine_identities(hello: dict, catalog: dict) -> tuple[dict[int, str], dict[int, str]]:
    """Check the worker's engine card/enemy tables against the catalog (v3 contract)."""
    engine_cards = [tuple(x) for x in hello["engine_cards"]]
    catalog_cards = sorted((c["Id"], c["Entry"]) for c in catalog["cards"])
    if engine_cards != catalog_cards:
        raise ValueError("engine card table differs from the frozen catalog (id/entry); rebuild or re-audit")
    engine_enemies = {i: n for i, n in hello["engine_enemies"]}
    outside = {i: n for i, n in engine_enemies.items() if i in BUILD08_ENEMIES_OUTSIDE_VOCAB}
    if outside and (outside != BUILD08_ENEMIES_OUTSIDE_VOCAB or hello.get("engine_line") != paths.BUILD08_ENGINE_VERSION):
        raise ValueError(f"engine enemies {outside} are only reviewed for the build08 engine line, "
                         f"not {hello.get('engine_line')!r}")
    engine_enemies = {i: n for i, n in engine_enemies.items() if i not in outside}
    catalog_enemies = {e["Id"]: e["Name"] for e in catalog["enemies"]}
    for i, n in catalog_enemies.items():
        if engine_enemies.get(i) != n:
            raise ValueError(f"catalog enemy {i} {n} differs from the engine table ({engine_enemies.get(i)})")
    only = {i: n for i, n in engine_enemies.items() if i not in catalog_enemies}
    if only != ENGINE_ONLY_ENEMIES:
        raise ValueError(f"engine-only enemies changed: {only}; review before extending the vocabulary")
    return engine_enemies, only


def _v3_card_table(rows: list[dict], columns_from: list[dict]) -> tuple[np.ndarray, list[str], list, list, list]:
    """v3 static card table for ``rows``; one-hot columns enumerate ``columns_from`` (the full
    catalog), so every v3 vocabulary shares one column layout."""
    types = sorted({c["Type"] for c in columns_from})
    targets = sorted({c["Target"] for c in columns_from})
    rarities = sorted({c["Rarity"] for c in columns_from})
    names = [f"def_{n}" for n in CARD_DEF_NUMS_V3] + [f"def_{b}" for b in CARD_DEF_BOOLS_V3]
    names += [f"type_{t}" for t in types] + [f"target_{t}" for t in targets] + [f"rarity_{r}" for r in rarities]
    table = np.zeros((len(rows) + 1, len(names)), dtype=np.float32)
    for i, c in enumerate(rows, start=1):
        row = [float(slog(max(c[n], -1))) for n in CARD_DEF_NUMS_V3]
        row += [float(bool(c[b])) for b in CARD_DEF_BOOLS_V3]
        row += [float(c["Type"] == t) for t in types] + [float(c["Target"] == t) for t in targets]
        row += [float(c["Rarity"] == r) for r in rarities]
        table[i] = row
    return table, names, types, targets, rarities


def build_vocab_v3(hello: dict, catalog: dict | None = None) -> Vocab:
    """Full engine identities for the three-act scope (no roster filtering)."""
    catalog = catalog if catalog is not None else paths.read_json(paths.CATALOG)
    engine_enemies, only = _v3_engine_identities(hello, catalog)
    cards = sorted(catalog["cards"], key=lambda c: c["Id"])
    roster: set[int] = set()
    if paths.ROSTER.exists():
        entries = {r["entry_id"] for r in paths.read_json(paths.ROSTER)["rows"]}
        roster = {c["Id"] for c in cards if c["Entry"] in entries}
    table, names, types, targets, rarities = _v3_card_table(cards, cards)
    _check_codebook(hello["buff_ids"])
    return Vocab(
        card_ids=[c["Id"] for c in cards],
        relic_ids=sorted(r["Id"] for r in catalog["relics"]),
        potion_ids=sorted(p["Id"] for p in catalog["potions"]),
        enemy_ids=sorted(engine_enemies),
        buff_names=list(hello["buff_ids"]),
        intent_types=list(hello["intent_types"]),
        selection_kinds=list(hello["selection_kinds"]),
        card_types=types,
        card_targets=targets,
        card_static=table,
        card_static_names=names,
        roster_card_ids=roster,
        catalog_sha256=paths.sha256_file(paths.CATALOG),
        schema=VOCAB_SCHEMA_V3,
        extra={
            "card_source": "engine card table == catalog (id, entry)",
            "engine_only_enemies": {str(k): v for k, v in sorted(only.items())},
            "first_roster_cards": len(roster),
            "card_rarities": rarities,
        },
    )


def build_vocab_v3_214(hello: dict, catalog: dict | None = None) -> Vocab:
    """v3 enemies and static columns over exactly the 214 retained card identities."""
    catalog = catalog if catalog is not None else paths.read_json(paths.CATALOG)
    engine_enemies, only = _v3_engine_identities(hello, catalog)
    roster_doc = paths.read_json(paths.ROSTER)
    entries = [r["entry_id"] for r in roster_doc["rows"]]
    if len(set(entries)) != len(entries):
        raise ValueError("the retained roster lists a card identity twice")
    cards = _roster_cards(catalog, set(entries))
    table, names, types, targets, rarities = _v3_card_table(cards, catalog["cards"])
    _check_codebook(hello["buff_ids"])
    return Vocab(
        card_ids=[c["Id"] for c in cards],
        relic_ids=sorted(r["Id"] for r in catalog["relics"]),
        potion_ids=sorted(p["Id"] for p in catalog["potions"]),
        enemy_ids=sorted(engine_enemies),
        buff_names=list(hello["buff_ids"]),
        intent_types=list(hello["intent_types"]),
        selection_kinds=list(hello["selection_kinds"]),
        card_types=types,
        card_targets=targets,
        card_static=table,
        card_static_names=names,
        roster_card_ids={c["Id"] for c in cards},
        catalog_sha256=paths.sha256_file(paths.CATALOG),
        schema=VOCAB_SCHEMA_V3_214,
        extra={
            "card_source": "retained roster entries resolved in the catalog; engine card table == catalog (id, entry)",
            "roster_sha256": paths.sha256_file(paths.ROSTER),
            "roster_digest": paths.digest(roster_doc),
            "roster_cards": len(cards),
            "roster_card_entries": [c["Entry"] for c in cards],
            "acquisition_restrictions": ACQUISITION_RESTRICTIONS_214,
            "engine_only_enemies": {str(k): v for k, v in sorted(only.items())},
            "card_rarities": rarities,
        },
    )


def _check_codebook(buff_ids: list[str]) -> None:
    """The worker's BuffId enum must equal the reviewed 253-entry codebook."""
    schema = paths.read_json(paths.STATUS_SCHEMA)
    if list(buff_ids) != list(schema["codebook"]):
        raise ValueError(
            f"worker BuffId enum ({len(buff_ids)}) differs from the proposed codebook ({schema['K']})"
        )
