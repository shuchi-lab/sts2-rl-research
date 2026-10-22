"""Strategy observation encoding (Task 3): variable-size public rows -> compact arrays -> padded batches.

:func:`encode_strategy` turns one :class:`~run_strategy_v1.contracts.PublicRunObservation` into a
:class:`StrategyObs`. Candidate ids stay outside the tensors. Collections are never truncated:
every deck card, relic, potion slot, map node/edge and candidate is a row, and padding is masked.
An act map's topology (node types, coordinates, edges, backward-DAG summaries) is interned once
per run and act: only the per-decision flags (visited/current/option/reachable) are new arrays.
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass

import numpy as np
import torch

from .contracts import DECISION_FLOW, PHASES, ContractError, PublicRunObservation, UnsupportedStrategyState
from .map_features import FEATURE_NAMES as DAG_FEATURES
from .map_features import NODE_TYPES, public_path_features, reachable_from
from .metadata import (CARD_TYPE_NONE, FIGHT_OUTCOMES, KNOWN_CARD_TYPES, KNOWN_ENCHANTMENTS, KNOWN_TINKER_RIDERS,
                       MAX_ENCOUNTER, ROOMS, SELECTION_PURPOSES, TINKER, PublicMetadata)

# v3 (decision flow v2): the held potion a full-belt choice replaces/uses (cand_potion2) beside the incoming one
# (cand_potion); reward-screen decision context; no reopen flag (a skip is final).
ENCODING_SCHEMA = "rs1/strategy_features/v3"
# One public card-row feature set, shared by permanent deck cards, offered cards and bundle members.
DECK_FEATS = ("upgraded", "enchant_amount", "tinker_type", "tinker_rider", "served")
BUNDLE_SIZE = 3  # engine invariant (two bundles of exactly three cards), enforced, never clipped
# every per-candidate array of a StrategyObs (row j = candidate j)
CAND_FIELDS = ("cand_kind", "cand_card", "cand_ench", "cand_card_feat", "cand_relic", "cand_relic2", "cand_potion",
               "cand_potion2", "cand_node", "cand_deck", "cand_event", "cand_bundle", "cand_bundle_ench", "cand_bundle_feat", "cand_num")


def card_row(m: PublicMetadata, c: dict, served: int = 0) -> tuple[int, int, list[float]]:
    """(card index, enchantment id, DECK_FEATS) of one displayed card instance. A modifier outside the
    known engine enums is an explicit unsupported state (never clipped onto a known id)."""
    ench, ttype, rider = int(c["enchantment"]), int(c["tinker_type"]), int(c["tinker_rider"])
    if not 0 <= ench < KNOWN_ENCHANTMENTS:
        raise UnsupportedStrategyState("unknown_displayed_modifier", f"card {c['def_id']} enchantment {ench}")
    if not 0 <= ttype < KNOWN_CARD_TYPES:
        raise UnsupportedStrategyState("unknown_displayed_modifier", f"card {c['def_id']} tinker_type {ttype}")
    if not 0 <= rider < KNOWN_TINKER_RIDERS:
        raise UnsupportedStrategyState("unknown_displayed_modifier", f"card {c['def_id']} tinker_rider {rider}")
    return m.card(c["def_id"]), ench, [float(c["upgraded"]), c["enchant_amount"] / 5.0, ttype / TINKER,
                                        rider / TINKER, served / 5.0]


# a plain named card (event grant): unupgraded, no enchantment, TinkerType None, no rider
PLAIN_CARD_FEATS = [0.0, 0.0, CARD_TYPE_NONE / TINKER, 0.0, 0.0]
CAND_NUM = ("gold", "cost", "hp_cost", "max_hp_cost", "gold_cost", "starts_fight", "slot", "page", "option",
            "heal", "row", "col", "tool_big", "tool_small", "x", "y", "grants",
            "upgraded", "gold_gain", "card_type", "tinker_rider", "chosen_card_type", "dish", "removes_card")
NODE_STATIC = len(NODE_TYPES) + 2 + len(DAG_FEATURES) - 1 + 2  # type one-hot, row, col, DAG (minus reachable), fur, quest
NODE_DYN = 4  # visited, current, option, reachable_from_current
SCREEN = 45


def slog(x: float) -> float:
    return float(np.sign(x) * np.log1p(abs(x)))


@dataclass(eq=False)
class MapStatic:
    key: str
    node_static: np.ndarray  # [M, NODE_STATIC] float16
    edges: np.ndarray  # [E, 2] int16
    coord_index: dict


@dataclass(eq=False)
class StrategyObs:
    run_id: str
    decision_id: str
    phase: str
    candidate_ids: tuple
    glob: np.ndarray
    screen: np.ndarray
    screen_ids: np.ndarray  # event idx, ancient idx, purpose idx, boss encounter idx
    deck_card: np.ndarray
    deck_ench: np.ndarray
    deck_feat: np.ndarray
    relic_idx: np.ndarray
    relic_feat: np.ndarray
    potion_idx: np.ndarray
    map: MapStatic
    node_dyn: np.ndarray
    cand_kind: np.ndarray
    cand_card: np.ndarray
    cand_ench: np.ndarray
    cand_card_feat: np.ndarray  # [C, DECK_FEATS] the offered card's own public row
    cand_relic: np.ndarray
    cand_relic2: np.ndarray  # the relic a trade gives away (Relic Trader / Ranwid)
    cand_potion: np.ndarray  # the candidate's potion: the INCOMING one on a full-belt reward choice
    cand_potion2: np.ndarray  # the HELD potion that choice replaces or drinks (0 otherwise)
    cand_node: np.ndarray
    cand_deck: np.ndarray
    cand_event: np.ndarray
    cand_bundle: np.ndarray  # [C, 3] member card indices (0 = not a bundle)
    cand_bundle_ench: np.ndarray  # [C, 3]
    cand_bundle_feat: np.ndarray  # [C, 3, DECK_FEATS]
    cand_num: np.ndarray

    @property
    def n_candidates(self) -> int:
        return len(self.candidate_ids)

    def index(self, candidate_id: str) -> int:
        try:
            return self.candidate_ids.index(candidate_id)
        except ValueError:
            raise ContractError(f"{self.decision_id}: {candidate_id} not among the encoded candidates") from None


class StrategyEncoder:
    """Stateful only through the per-run/act map topology intern table (bounded)."""

    def __init__(self, metadata: PublicMetadata, max_maps: int = 4096):
        self.m = metadata
        self.maps: dict[str, MapStatic] = {}
        self.max_maps = max_maps

    def describe(self) -> dict:
        return {"schema": ENCODING_SCHEMA, "decision_flow": DECISION_FLOW, "deck_feats": list(DECK_FEATS), "candidate_numeric": list(CAND_NUM),
                "node_static": NODE_STATIC, "node_dynamic": NODE_DYN, "screen": SCREEN, "dag_features": list(DAG_FEATURES),
                "metadata": self.m.describe()["digest"]}

    def _map(self, obs: PublicRunObservation) -> MapStatic:
        nodes, edges = obs.map_nodes, obs.map_edges
        h = hashlib.sha256(repr([(n["col"], n["row"], n["node_type"], n["fur_coat"], n["quest"]) for n in nodes]
                                + list(edges)).encode()).hexdigest()
        key = f"{obs.run_id}|{obs.map_meta.get('act_ordinal')}|{h}"
        hit = self.maps.get(key)
        if hit is not None:
            return hit
        dag = public_path_features(list(nodes), list(edges), -1)
        rows = []
        for i, n in enumerate(nodes):
            t = int(n["node_type"])
            if t not in NODE_TYPES:
                raise ContractError(f"unknown map node type {t}")
            rows.append([float(t == k) for k in NODE_TYPES] + [n["row"] / 16.0, n["col"] / 7.0]
                        + dag[i][:-1] + [float(n["fur_coat"]), float(n["quest"])])
        ms = MapStatic(key, np.asarray(rows, dtype=np.float16).reshape(len(nodes), NODE_STATIC),
                       np.asarray(edges, dtype=np.int16).reshape(-1, 2),
                       {(n["col"], n["row"]): i for i, n in enumerate(nodes)})
        if len(self.maps) >= self.max_maps:
            self.maps.pop(next(iter(self.maps)))
        self.maps[key] = ms
        return ms

    def encode(self, obs: PublicRunObservation) -> StrategyObs:
        m = self.m
        s = obs.scalars
        if obs.phase not in PHASES:
            raise ContractError(f"phase {obs.phase}")
        hp, mhp = s["hp"], max(1, s["max_hp"])
        region = obs.map_meta.get("act_region", s["act_region"])
        held = sum(p["potion_id"] != 0 for p in obs.potions)
        wins = np.zeros((3, len(ROOMS)), np.float32)
        outcomes = np.zeros(len(FIGHT_OUTCOMES), np.float32)
        for f in obs.history.get("fights", []):
            if f["outcome"] == "victory":
                wins[f["act_ordinal"] - 1, ROOMS.index(f["room"])] += 1
            outcomes[FIGHT_OUTCOMES.index(f["outcome"])] += 1
        glob = [hp / mhp, hp / 100.0, mhp / 100.0, s["gold"] / 300.0, s["floor"] / 50.0]
        glob += [float(s["act_ordinal"] == a) for a in (1, 2, 3)] + [float(region == r) for r in (1, 2, 3, 4)]
        glob += [s["ascension"] / 10.0, s["potion_capacity"] / 6.0, held / 6.0, len(obs.deck) / 40.0,
                 len(obs.relics) / 20.0, s["fights_won"] / 30.0, s.get("strategic_decisions", 0) / 1000.0,
                 len(obs.history.get("visited", [])) / 50.0]
        glob += [float(obs.phase == p) for p in PHASES]
        glob += list((wins / 5.0).ravel()) + list(outcomes / 30.0)
        glob = np.asarray(glob, dtype=np.float32)

        rows = [card_row(m, c, c.get("served", 0)) for c in obs.deck]
        deck_card = np.asarray([x[0] for x in rows], dtype=np.int16)
        deck_ench = np.asarray([x[1] for x in rows], dtype=np.int8)
        deck_feat = np.asarray([x[2] for x in rows], dtype=np.float16).reshape(-1, len(DECK_FEATS))
        inst_row = {c["instance_id"]: i for i, c in enumerate(obs.deck)}
        relic_idx = np.asarray([m.relic(r["def_id"]) for r in obs.relics], dtype=np.int16)
        relic_feat = np.asarray([[slog(r["counter"]) / 3.0, float(r["used_up"]), float(r["wax"])] for r in obs.relics],
                                dtype=np.float16).reshape(-1, 3)
        potion_idx = np.asarray([m.potion(p["potion_id"]) for p in obs.potions], dtype=np.int16)

        ms = self._map(obs)
        cur = int(obs.map_meta.get("current", -1))
        reach = reachable_from(len(obs.map_nodes), obs.map_edges, cur)
        node_dyn = np.asarray([[float(n["visited"]), float(n["current"]), float(n["option"] >= 0), float(i in reach)]
                               for i, n in enumerate(obs.map_nodes)], dtype=np.float16).reshape(-1, NODE_DYN)

        screen, ids = self._screen(obs)
        C = len(obs.candidates)
        k_ = np.zeros(C, np.int16)
        card = np.zeros(C, np.int16)
        ench = np.zeros(C, np.int8)
        cfeat = np.zeros((C, len(DECK_FEATS)), np.float32)
        relic = np.zeros(C, np.int16)
        relic2 = np.zeros(C, np.int16)
        potion = np.zeros(C, np.int16)
        potion2 = np.zeros(C, np.int16)
        node = np.full(C, -1, np.int16)
        deck = np.full(C, -1, np.int16)
        event = np.zeros(C, np.int16)
        bundle = np.zeros((C, BUNDLE_SIZE), np.int16)
        bench = np.zeros((C, BUNDLE_SIZE), np.int8)
        bfeat = np.zeros((C, BUNDLE_SIZE, len(DECK_FEATS)), np.float32)
        num = np.zeros((C, len(CAND_NUM)), np.float32)
        for j, c in enumerate(obs.candidates):
            p = c.public
            k_[j] = m.kind(c.kind)
            if "card" in p:
                if "instance_id" in p:  # a deck instance (smith/removal/selection): its actual deck row
                    if p["instance_id"] not in inst_row:
                        raise ContractError(f"{c.id}: instance {p['instance_id']} not in the deck")
                    i = inst_row[p["instance_id"]]
                    card[j], ench[j], cfeat[j] = deck_card[i], deck_ench[i], deck_feat[i]
                else:
                    card[j], ench[j], cfeat[j] = card_row(m, p["card"])
                num[j, CAND_NUM.index("upgraded")] = float(p["card"]["upgraded"])
            if "cards" in p:
                if len(p["cards"]) != BUNDLE_SIZE:
                    raise ContractError(f"{c.id}: a bundle of {len(p['cards'])} cards (engine bundles hold exactly 3)")
                for q, cc in enumerate(p["cards"]):
                    bundle[j, q], bench[j, q], bfeat[j, q] = card_row(m, cc)
            if "relic_id" in p:
                relic[j] = m.relic(p["relic_id"])
            if "gives_relic_id" in p:
                relic2[j] = m.relic(p["gives_relic_id"])
            if "potion_id" in p:
                potion[j] = m.potion(p["potion_id"])
            if "held_potion_id" in p:
                if not 0 <= int(p.get("slot", -1)) < len(obs.potions) \
                        or obs.potions[int(p["slot"])]["potion_id"] != p["held_potion_id"]:
                    raise ContractError(f"{c.id}: held potion {p['held_potion_id']} is not in belt slot {p.get('slot')}")
                potion2[j] = m.potion(p["held_potion_id"])
            if c.kind == "map_node":
                node[j] = ms.coord_index[(p["col"], p["row"])]
            if "instance_id" in p:
                if p["instance_id"] not in inst_row:
                    raise ContractError(f"{c.id}: instance {p['instance_id']} not in the deck")
                deck[j] = inst_row[p["instance_id"]]
            if "event_id" in p:
                event[j] = m.event(p["event_id"])
            if p.get("grants_cards") and not card[j]:
                card[j], cfeat[j] = m.card(p["grants_cards"][0]), PLAIN_CARD_FEATS
            for name, scale in (("gold", 300.0), ("cost", 300.0), ("hp_cost", 20.0), ("max_hp_cost", 20.0),
                                ("gold_cost", 300.0), ("slot", 6.0), ("page", 5.0), ("option", 4.0), ("heal", 50.0),
                                ("row", 16.0), ("col", 7.0), ("x", 10.0), ("y", 10.0), ("gold_gain", 300.0),
                                ("card_type", 8.0), ("tinker_rider", 8.0), ("chosen_card_type", 8.0), ("dish", 8.0)):
                if name in p:
                    num[j, CAND_NUM.index(name)] = float(p[name]) / scale
            num[j, CAND_NUM.index("starts_fight")] = float(bool(p.get("starts_fight")))
            num[j, CAND_NUM.index("tool_big")] = float(p.get("tool") == "big")
            num[j, CAND_NUM.index("tool_small")] = float(p.get("tool") == "small")
            num[j, CAND_NUM.index("grants")] = len(p.get("grants_cards") or []) / 3.0
            num[j, CAND_NUM.index("removes_card")] = float(bool(p.get("removes_card")))
        if not (np.isfinite(num).all() and np.isfinite(glob).all() and np.isfinite(cfeat).all()
                and np.isfinite(bfeat).all() and np.isfinite(deck_feat).all()):
            raise ContractError(f"{obs.decision_id}: nonfinite strategy features")
        return StrategyObs(obs.run_id, obs.decision_id, obs.phase, tuple(c.id for c in obs.candidates), glob, screen, ids,
                           deck_card, deck_ench, deck_feat, relic_idx, relic_feat, potion_idx, ms, node_dyn,
                           k_, card, ench, cfeat.astype(np.float16), relic, relic2, potion, potion2, node, deck, event,
                           bundle, bench, bfeat.astype(np.float16), num.astype(np.float16))

    def _screen(self, obs: PublicRunObservation) -> tuple[np.ndarray, np.ndarray]:
        sc = obs.screen or {}
        v = np.zeros(SCREEN, np.float32)
        ev = anc = purpose = 0
        boss = int(obs.map_meta.get("boss_encounter_id", 0))
        if not 0 <= boss < MAX_ENCOUNTER:
            raise ContractError(f"boss encounter id {boss} outside the embedding")
        ph = obs.phase
        if ph == "RelicReward":
            v[0] = float(sc.get("can_leave", True))
            v[1] = sc.get("card_offer_rows", 0) / 3.0
            v[2:5] = [float(sc.get("context") == c) for c in ("ancient", "event", "room")]
            v[39] = sc.get("card_removals_owed", 0) / 3.0
            v[40] = float(sc.get("decision") == "potion_full_belt")
            v[41] = float(sc.get("decision") == "special_card")
            v[42] = sc.get("potion_rows", 0) / 3.0
        elif ph == "CardReward":
            v[5] = float(sc.get("reroll_available", False))
            v[6] = float(sc.get("fourth_card") is not None)
            v[7] = float(sc.get("returns_to_reward_screen", False))
            v[43] = sc.get("remaining_card_offers", 0) / 3.0
        elif ph == "Shop":
            v[8] = sc.get("removal_cost", 0) / 150.0
            v[9] = float(sc.get("removal_used", False))
        elif ph == "Rest":
            mask = int(sc.get("options_taken_mask", 0))
            v[10:19] = [float(mask >> b & 1) for b in range(9)]
            v[19] = float(sc.get("result_pending", False))
        elif ph == "Event":
            ev = self.m.event(sc["event_id"])
            v[20] = sc.get("page", 0) / 5.0
        elif ph == "Ancient":
            anc = self.m.ancient(sc["ancient"])
            v[21] = float(sc.get("awaiting_proceed", False))
        elif ph == "TransformSelect":
            mode = sc.get("mode")
            v[22:26] = [float(mode == x) for x in ("grid", "smith", "deck_selection", "continue")]
            v[26] = sc.get("remaining", 0) / 3.0
            v[27] = sc.get("picks_left", 0) / 3.0
            v[28] = float(sc.get("optional", False))
            v[29] = sc.get("follow_up_hp_loss", 0) / 20.0
            v[30] = sc.get("follow_up_gold_loss", 0) / 300.0
            v[31] = sc.get("follow_up_max_hp_gain", 0) / 10.0
            v[32] = float(sc.get("shop_removal", False))
            v[33] = float(sc.get("follow_up_card", 0) != 0)
            purpose = int(sc.get("purpose_id", 0))
            if not 0 <= purpose < SELECTION_PURPOSES:
                raise ContractError(f"selection purpose {purpose}")
            purpose += 1
        elif ph == "Treasure":
            v[34] = float(sc.get("opened", False))
        elif ph == "CrystalSphere":
            cells = sc.get("cells", [])
            v[35] = sc.get("divinations", 0) / 6.0
            v[36] = sc.get("revealed_items", 0) / 15.0
            v[37] = sum(c["hidden"] for c in cells) / max(1, len(cells))
        elif ph == "BundleSelect":
            v[38] = float(sc.get("selected", -1) >= 0)
        return v, np.asarray([ev, anc, purpose, boss], dtype=np.int16)


def encode_strategy(obs: PublicRunObservation, metadata: PublicMetadata) -> StrategyObs:
    """Stateless convenience (no topology interning across calls)."""
    return StrategyEncoder(metadata).encode(obs)


def _pad(arrs, width=None, dtype=np.float32, fill=0):
    B = len(arrs)
    L = max((len(a) for a in arrs), default=0)
    L = max(L, 1)
    shape = (B, L) if width is None else (B, L, *(width if isinstance(width, tuple) else (width,)))
    out = np.full(shape, fill, dtype=dtype)
    mask = np.zeros((B, L), dtype=bool)
    for i, a in enumerate(arrs):
        n = len(a)
        if n:
            out[i, :n] = a
            mask[i, :n] = True
    return out, mask


def collate_strategy(batch: list[StrategyObs], device="cpu") -> dict[str, torch.Tensor]:
    if not batch:
        raise ValueError("empty strategy batch")
    if any(o.n_candidates == 0 for o in batch):
        raise ContractError("a strategy observation with zero candidates")
    t = lambda x, dt=None: torch.as_tensor(x, dtype=dt).to(device)  # noqa: E731
    out = {"glob": t(np.stack([o.glob for o in batch]), torch.float32),
           "screen": t(np.stack([o.screen for o in batch]), torch.float32),
           "screen_ids": t(np.stack([o.screen_ids for o in batch]).astype(np.int64))}
    dc, dm = _pad([o.deck_card for o in batch], dtype=np.int64)
    de, _ = _pad([o.deck_ench for o in batch], dtype=np.int64)
    df, _ = _pad([o.deck_feat for o in batch], width=len(DECK_FEATS))
    out.update(deck_card=t(dc), deck_ench=t(de), deck_feat=t(df), deck_mask=t(dm))
    ri, rm = _pad([o.relic_idx for o in batch], dtype=np.int64)
    rf, _ = _pad([o.relic_feat for o in batch], width=3)
    out.update(relic_idx=t(ri), relic_feat=t(rf), relic_mask=t(rm))
    pi, pm = _pad([o.potion_idx for o in batch], dtype=np.int64)
    out.update(potion_idx=t(pi), potion_mask=t(pm))
    # map: flattened node lists with per-sample offsets
    offs, nodes, dyn, edges, seg = [], [], [], [], []
    base = 0
    for b, o in enumerate(batch):
        M = len(o.map.node_static)
        offs.append(base)
        nodes.append(o.map.node_static.astype(np.float32))
        dyn.append(o.node_dyn.astype(np.float32))
        if len(o.map.edges):
            edges.append(o.map.edges.astype(np.int64) + base)
        seg.append(np.full(M, b, dtype=np.int64))
        base += M
    out["node_feat"] = t(np.concatenate([np.concatenate(nodes), np.concatenate(dyn)], axis=1), torch.float32)
    out["node_seg"] = t(np.concatenate(seg))
    out["edges"] = t(np.concatenate(edges) if edges else np.zeros((0, 2), np.int64))
    out["node_offset"] = t(np.asarray(offs, dtype=np.int64))
    ck, cmask = _pad([o.cand_kind for o in batch], dtype=np.int64)
    out["cand_mask"] = t(cmask)
    out["cand_kind"] = t(ck)
    for name in ("cand_card", "cand_ench", "cand_relic", "cand_relic2", "cand_potion", "cand_potion2", "cand_event"):
        a, _ = _pad([getattr(o, name) for o in batch], dtype=np.int64)
        out[name] = t(a)
    cn, _ = _pad([o.cand_node for o in batch], dtype=np.int64, fill=-1)
    cd, _ = _pad([o.cand_deck for o in batch], dtype=np.int64, fill=-1)
    cf, _ = _pad([o.cand_card_feat for o in batch], width=len(DECK_FEATS))
    cb, _ = _pad([o.cand_bundle for o in batch], width=BUNDLE_SIZE, dtype=np.int64)
    cbe, _ = _pad([o.cand_bundle_ench for o in batch], width=BUNDLE_SIZE, dtype=np.int64)
    cbf, _ = _pad([o.cand_bundle_feat for o in batch], width=(BUNDLE_SIZE, len(DECK_FEATS)))
    cu, _ = _pad([o.cand_num for o in batch], width=len(CAND_NUM))
    # global node index for map candidates (-1 stays -1)
    gn = np.where(cn >= 0, cn + np.asarray(offs)[:, None], -1)
    out.update(cand_node=t(gn), cand_deck=t(cd), cand_card_feat=t(cf, torch.float32), cand_bundle=t(cb),
               cand_bundle_ench=t(cbe), cand_bundle_feat=t(cbf, torch.float32), cand_num=t(cu, torch.float32))
    return out
