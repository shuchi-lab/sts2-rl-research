"""Native potion legality with unchanged selected frozen numeric features.

PotionAdapter.encode is retained here because its monolithic implementation
asserts the simulator's broader action set before constructing the final features.
Only the held-manual-potion completeness condition differs: enemy-targeting
potions have no native use while every enemy is untargetable. Inventory, legal
rows, feature math, masks and all-boss charge semantics are preserved. No training
runtime file or process-global class is modified.
"""
from __future__ import annotations

import dataclasses

import numpy as np

from crl.features import Adapter, UnsupportedState
from crl.potion_v1 import (ACTS, ROOMS, NoAdmissibleOption, PotionAdapter,
                           PotionEnvState, inventory, use_charge)
from crl.vocab import slog
from act1_battle_finetune_v1.reward import AllBossFreeAdapter


class NativePotionAdapter(PotionAdapter):
    def encode(self, obs: dict, entry_hp: int, pctx: PotionEnvState | None = None):
        if pctx is None:
            raise ValueError("crl.features/v3-potion-v2 needs the fight's PotionEnvState")
        if obs.get("allow_potions") is not True or "round_number" not in obs:
            raise UnsupportedState("observation is not from a potion-mode reset (allow_potions)")
        held, cap = inventory(obs)
        sel = obs["selection"]
        self._sel_kind = sel["kind_name"] if sel else None
        self._blocked: list[dict] = []
        ob = Adapter.encode(self, obs, entry_hp)
        v = self.v
        pots = obs["potions"]
        ctr = dict(zip(self.hello_counters, obs["player"]["counters"]))
        if cap != ctr["max_potion_slots"]:
            raise UnsupportedState(f"potion capacity {cap} != MaxPotionSlots counter {ctr['max_potion_slots']}")
        enemies_targetable = [ev["targetable"] for ev in obs["view"]["enemies"]]
        legal = obs["legal"]
        O = len(legal)
        opt_pot = np.zeros(O, dtype=np.int32)
        opt_slot = np.full(O, -1, dtype=np.int32)
        used_slots = set()
        for i, o in enumerate(legal):
            if o["kind"] != "use_potion":
                continue
            slot, pid = o.get("potion_slot"), o.get("potion_id")
            if not isinstance(slot, int) or not 0 <= slot < cap or pid == 0 or pots[slot] != pid:
                raise UnsupportedState(f"potion option slot {slot} id {pid} does not match usable inventory {pots}")
            if o["action"] != ob.n_hand + 1 + slot:
                raise UnsupportedState(f"potion option action {o['action']} != hand {ob.n_hand} + 1 + slot {slot}")
            d = self.potion_defs[pid]
            if pid in self.automatic_ids:
                raise UnsupportedState(f"automatic potion {d['name']} offered as a manual use")
            if pid in self.unmodelled_ids:
                raise UnsupportedState(f"potion {d['name']} has no in-combat engine effect; a use would only "
                                       "empty its slot")
            t = o["target"]
            if d["targets_enemy"]:
                if t < 0 and any(enemies_targetable):
                    raise UnsupportedState(f"targeted potion {d['name']} without a target")
                if t >= 0 and not enemies_targetable[t]:
                    raise UnsupportedState(f"potion target {t} is not targetable")
            elif t >= 0:
                raise UnsupportedState(f"untargeted potion {d['name']} with target {t}")
            opt_pot[i] = v.potion_index[pid]
            opt_slot[i] = slot
            used_slots.add(slot)
        admissible = np.ones(O, dtype=bool)
        if self._blocked:
            for b in self._blocked:
                admissible[b["o"]] = False
            if not admissible.any():
                raise NoAdmissibleOption(
                    f"every option of the {sel['kind_name']} screen offers an outside-roster card and no legal skip "
                    f"exists: {[(b['def_id'], b['entry']) for b in self._blocked]}")
            pctx.foreign_offers.append({
                "decision": None, "raw_turn": obs["turn"], "selection_kind": sel["kind_name"],
                "source_card_def_id": sel["source_card_def_id"], "source_potion_id": sel["source_potion_id"],
                "candidates": [{"o": o["o"], "kind": o["kind"], "action": o["action"],
                                "def_id": int(o["card"][0]) if o["kind"] == "select" else None,
                                "admissible": bool(admissible[o["o"]])} for o in legal],
                "excluded": self._blocked, "skip_available": any(o["kind"] == "skip_selection" for o in legal),
                "chosen": None})
        if sel is None:
            # Native UsePotion requires a live target for enemy-targeting potions.
            # Revival may leave none; the potion is still held and fully encoded.
            # The simulator's target=-1 fallback is not a native legal action.
            manual = {s for s, p in enumerate(pots)
                      if p != 0 and p not in self.automatic_ids
                      and (not self.potion_defs[p]["targets_enemy"] or any(enemies_targetable))}
            if manual != used_slots:
                raise UnsupportedState(f"held manual potions in slots {sorted(manual)} but legal uses for "
                                       f"{sorted(used_slots)}")
            auto_held = sum(p in self.automatic_ids for p in pots if p)
            if obs["excluded_potion_options"] < auto_held:
                raise UnsupportedState("an automatic potion is neither legal nor reported as excluded")
        elif used_slots:
            raise UnsupportedState("potion use offered during a selection screen")
        src = sel["source_potion_id"] if sel else 0
        if src and src not in v.potion_index:
            raise UnsupportedState(f"selection source potion {src} not in vocabulary")

        relics = {r for r, _ in obs["relics"]}
        frond, sozu = self.frond_relic_id in relics, self.sozu_relic_id in relics
        act, room = pctx.act, pctx.room
        final = act == 3 and room == "boss"
        lam = pctx.ledger.potion_lambda
        now = use_charge(lam, held=held, capacity=cap, act=act, room=room, frond=frond, sozu=sozu)[0] if held else 0.0
        rnd, lag, extra = pctx.round_context(obs)
        g = [float(act == a) for a in ACTS] + [float(room == r) for r in ROOMS]
        g += [float(frond and not sozu), float(cap > 0 and held == cap), float(slog(cap - held)),
              float(final), lam, float(pctx.ledger.bill), float(slog(pctx.ledger.charged_uses)), now,
              float(slog(rnd)), float(slog(lag)), float(extra)]
        glob = np.concatenate([ob.glob, np.asarray(g, dtype=np.float32)])
        return dataclasses.replace(ob, glob=glob, opt_potion_idx=opt_pot, opt_potion_slot=opt_slot,
                                   sel_source_potion=int(v.potion_index[src]) if src else 0,
                                   opt_admissible=admissible)


class NativeAllBossFreeAdapter(AllBossFreeAdapter, NativePotionAdapter):
    """Keep the selected all-boss charge override above native potion encoding."""
