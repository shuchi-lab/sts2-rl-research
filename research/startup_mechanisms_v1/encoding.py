"""Adapted v1 calculation over normalized public observations, with no simulator.

Cards carry already-resolved semantic programs and instance flags. This module
does not infer legality, resolve game effects, or forecast future cards. See
docs/feature-reference.md for the input boundary and integration requirements.
"""
from dataclasses import dataclass
from numbers import Integral, Real

import numpy as np

from .schema import BASE_CARD_WIDTH, CORES, EXHAUST_THRESHOLD, NUMERIC_GLOBAL_SLICE

PILES = ("hand", "draw", "discard", "exhaust")
CARD_TYPES = ("Attack", "Skill", "Power", "Status", "Curse", "Quest")


@dataclass(frozen=True)
class MechanismEncoding:
    card_roles: np.ndarray
    global_raw: np.ndarray
    global_encoded: np.ndarray


def _validate(cards, buffs, legal_hand_indices):
    for card in cards:
        if card.get("pile") not in PILES or card.get("type") not in CARD_TYPES:
            raise ValueError("Each card needs an observed pile and current card type")
        if not isinstance(card.get("name"), str) or not card["name"]:
            raise ValueError("A canonical card lookup name is required")
        if not isinstance(card.get("rarity"), str):
            raise ValueError("Card rarity is required for supply filters")
        if not isinstance(card.get("tags"), (list, tuple)) or not all(isinstance(t, str) for t in card["tags"]):
            raise ValueError("Card tags must be a sequence of strings")
        flags = card.get("flags", {})
        for key in ("exhaust", "ethereal", "eternal"):
            value = flags.get(key)
            if not isinstance(value, (Real, np.bool_)) or value not in (0, 1):
                raise ValueError(f"Explicit current {key} flag must be boolean or 0/1")
        if not isinstance(card.get("steps"), (list, tuple)) or not all(
                isinstance(s, dict) and isinstance(s.get("op"), str) for s in card["steps"]):
            raise ValueError("An already-resolved, reviewed semantic step program is required")
    hand_size = sum(c["pile"] == "hand" for c in cards)
    for i in legal_hand_indices:
        if isinstance(i, (bool, np.bool_)) or not isinstance(i, Integral) or not 0 <= i < hand_size:
            raise ValueError("Legal hand indices must identify current hand cards")
    normalized = {}
    for name, value in buffs.items():
        if not isinstance(name, str) or isinstance(value, (bool, np.bool_)) or not isinstance(value, Real) or not np.isfinite(value):
            raise ValueError("Observed buff names and finite numeric amounts are required")
        name = name.removesuffix("Power")
        normalized[name] = normalized.get(name, 0) + float(value)
        if not np.isfinite(normalized[name]) or abs(normalized[name]) > np.finfo(np.float32).max:
            raise ValueError("Buff total cannot be represented as float32")
    return normalized


def _basic(card, buffs):
    steps, flags = card["steps"], card["flags"]
    out = np.zeros((4, 4), np.float32)
    for m, core in enumerate(CORES):
        out[m, 0] = card["name"] == core
    out[0, 1] = any(s["op"] in ("lose_hp", "damage") and s.get("target") == "self" for s in steps)
    strength = any(s["op"] == "damage" and s.get("powered", False)
                   and s.get("target") in ("enemy", "all_enemies", "random_enemy") for s in steps)
    out[0, 2] = out[1, 2] = strength
    self_exhaust = (flags["exhaust"] or flags["ethereal"]
                    or (buffs.get("Corruption", 0) > 0 and card["type"] == "Skill"))
    others_exhaust = any(s["op"] == "exhaust" or (s.get("destination") == "exhaust"
                        and s["op"] in ("move", "modify", "autoplay")) for s in steps)
    out[2, 1] = others_exhaust or (self_exhaust and not flags["eternal"])
    out[2, 2] = card["name"] == "AshenStrike"
    out[3, 1] = any(s["op"] == "draw" for s in steps)
    out[3, 2] = "Strike" in card["tags"]
    return out


def _matches(card, filter_name):
    return {"any": True, "attack": card["type"] == "Attack",
            "skill": card["type"] == "Skill", "power": card["type"] == "Power",
            "attack_or_power": card["type"] in ("Attack", "Power"),
            "rare": card["rarity"] == "Rare", "strike": "Strike" in card["tags"]}.get(filter_name, False)


def encode(cards, buffs, legal_hand_indices) -> MechanismEncoding:
    """Return N×16 card roles and raw/transformed 24-value combat state.

    ``cards`` contains only owned cards in visible hand/draw/discard/exhaust
    piles, in caller order. Hand indices follow the hand subset of that order.
    ``buffs`` is the complete visible player-buff mapping; an absent buff is zero.
    ``legal_hand_indices`` comes from admissible play-card options, not card costs.
    Role flags express capabilities, including conditional effects, not outcomes.
    """
    cards, legal_hand_indices = list(cards), list(legal_hand_indices)
    buffs = _validate(cards, buffs, legal_hand_indices)
    basic = np.asarray([_basic(c, buffs) for c in cards], np.float32).reshape(-1, 4, 4)
    roles = basic.copy()
    for i, card in enumerate(cards):
        for step in card["steps"]:
            destination = step.get("destination", "")
            if step["op"] not in ("move", "clone") or destination not in ("hand", "draw_top", "draw_bottom", "draw_random"):
                continue
            for j, candidate in enumerate(cards):
                if candidate["pile"] != step.get("source") or (i == j and card["pile"] == "hand"):
                    continue
                if not _matches(candidate, step.get("filter", "any")):
                    continue
                roles[i, :3, 3] = np.maximum(roles[i, :3, 3], basic[j, :3, :3].any(axis=1))
                fuel = basic[j, 3, 2] if destination.startswith("draw") else bool(basic[j, 3, 0] or basic[j, 3, 1])
                roles[i, 3, 3] = max(roles[i, 3, 3], fuel)

    raw = np.zeros(24, np.float32)
    active = (buffs.get("Rupture", 0) > 0, buffs.get("DemonForm", 0) > 0,
              False, buffs.get("Hellraiser", 0) > 0)
    hand = [i for i, c in enumerate(cards) if c["pile"] == "hand"]
    exhaust_count = sum(c["pile"] == "exhaust" for c in cards)
    legal = set(legal_hand_indices)
    for m in range(4):
        enabled = bool(active[m] or roles[:, m, 0].any())
        online = enabled and exhaust_count >= EXHAUST_THRESHOLD if m == 2 else active[m]
        held = bool(roles[hand, m, 2].any())
        playable = any(h in legal and roles[node, m, 2] for h, node in enumerate(hand))
        raw[4*m:4*m+4] = (enabled, online, held, playable)
    count = lambda pile, strike=False: sum(c["pile"] == pile and (not strike or "Strike" in c["tags"]) for c in cards)
    raw[16:] = (buffs.get("Strength", 0), buffs.get("Rupture", 0), buffs.get("DemonForm", 0),
                exhaust_count, count("draw"), count("draw", True), count("discard", True),
                buffs.get("NoDraw", 0) <= 0)
    encoded = raw.copy()
    values = raw[NUMERIC_GLOBAL_SLICE].astype(np.float64)
    encoded[NUMERIC_GLOBAL_SLICE] = np.sign(values) * np.log1p(np.abs(values))
    return MechanismEncoding(roles.reshape(-1, 16), raw, encoded)


def append_features(base_cards, base_global, mechanisms: MechanismEncoding):
    """Append to already-encoded base arrays without modifying them.

    Card width is exactly 84 -> 100; global indices are base width + 0..23.
    Apply this once. Never pass an already-extended or unencoded base array.
    """
    cards, glob = np.asarray(base_cards, np.float32), np.asarray(base_global, np.float32)
    if cards.shape != (len(mechanisms.card_roles), BASE_CARD_WIDTH) or glob.ndim != 1:
        raise ValueError("Expected N×84 base card rows and a one-dimensional base global vector")
    if not np.isfinite(cards).all() or not np.isfinite(glob).all():
        raise ValueError("Base features must be finite")
    return np.concatenate((cards, mechanisms.card_roles), axis=1), np.concatenate((glob, mechanisms.global_encoded))
