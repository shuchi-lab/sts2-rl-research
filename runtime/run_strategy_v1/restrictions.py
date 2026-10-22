"""The versioned acquisition restriction manifest, resolved against frozen E's actual vocabulary (Task 4)."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

MANIFEST = Path(__file__).resolve().parent / "acquisition_restrictions.json"


def load_manifest(path: Path = MANIFEST) -> dict:
    m = json.loads(Path(path).read_text(encoding="utf-8"))
    if m.get("schema") != "rs1/acquisition_restrictions/v1":
        raise ValueError(f"{path}: not an rs1 acquisition restriction manifest")
    return m


def manifest_digest(path: Path = MANIFEST) -> str:
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def build_compatibility(battle, path: Path = MANIFEST) -> dict:
    """The worker ``configure`` payload: E's exact 214 card ids, unmodelled manual potions, the manifest's
    declined options and capacity relics. Every id comes from the original context, not a copy."""
    m = load_manifest(path)
    ctx = battle.ctx
    adapter = ctx.adapter
    for x in m["declined_event_options"]:
        if x["event_id"] == 50 and 110 in ctx.vocab.enemy_index:
            raise ValueError("manifest says the Mysterious Knight (110) is outside E's vocabulary, but it is not")
    unmodelled_manual = sorted(adapter.unmodelled_ids - adapter.automatic_ids)
    if not unmodelled_manual:
        raise ValueError("no unmodelled manual potions resolved")
    return {
        "roster_card_ids": sorted(int(c) for c in ctx.vocab.card_ids),
        "declined_potion_ids": unmodelled_manual,
        "declined_ancient_relic_ids": [x["relic_id"] for x in m["declined_ancient_relics"]],
        "declined_event_options": [{"event_id": x["event_id"], "page": x["page"], "option": x["option"]}
                                   for x in m["declined_event_options"]],
        "capacity_relics": [{"relic_id": x["relic_id"], "slots": x["slots"]} for x in m["capacity_relics"]],
        "neows_bones_dead_end_relic_ids": [x["relic_id"] for x in m["neows_bones_dead_end_relics"]["relics"]],
        "potion_slot_limit": 6,
        "manifest_digest": manifest_digest(path),
    }
