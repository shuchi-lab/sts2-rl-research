"""Frozen battle policy E (Task 1): strict original-context load, inference only, per-fight state.

* :func:`original_context` builds the ordinary E battle context -- a
  ``potion_quality_training_v1.reward.BossFreePotionContext`` over the unchanged production
  environment (``turn100_env_config(100)``: max_steps 2000, max_turns 99, three-act-214-v1) -- which
  starts the ACTUAL prebuilt RlCombatWorker and reads its hello, the frozen catalog/roster/vocabulary
  and the boss-free reward implementation bytes. Nothing is fabricated from the checkpoint.
* :func:`load_frozen_e` refuses ``None``, calls ``battle_width_scan_v1.checkpoint.load`` with
  ``expected_configuration="E"`` (never ``allow_mismatch``), and requires
  ``compatibility_problems == []``, 597,365 parameters, 214 cards, eval mode and no gradients.
* :class:`FightState` owns one fight's actual entry context, ``PotionEnvState`` (round tracker) and
  ``BossFreeLedger``; :meth:`FrozenBattlePolicy.account` is the unchanged ``crl.potion_v1.account_step``
  on complete Session-shaped replies.

The battle reward/bill is E's own input feature; it is never added to a strategy target.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
import torch

from . import build
from .bootstrap import bootstrap
from .contracts import ROOMS, BattleChoice

EXPECTED = {"configuration": "E", "parameters": 597_365, "cards": 214, "selected_round": 14, "episodes": 35_000,
            "feature_schema": "crl.features/v3-potion-v2"}
POTION_LAMBDA = 0.5
LOSS_PROFILE = "boss-act-v2"
ALLOWED_TURNS = 100


class BattleContractError(RuntimeError):
    """A frozen-E contract violation (nonfinite admissible Q, inadmissible greedy choice...): fatal --
    the owner stops new admissions; never an unsupported-run classification."""


class UnsupportedBattleState(RuntimeError):
    """Frozen E cannot encode this battle state (foreign card, no admissible option, encoder limit...)."""

    def __init__(self, kind: str, message: str):
        super().__init__(f"{kind}: {message}")
        self.kind = kind


def original_context():
    """E's original compatible battle context, against the actual prebuilt worker (no templates needed)."""
    bootstrap()
    from crl import paths
    from crl.expansion_turn100_v1 import turn100_env_config
    from potion_quality_training_v1.reward import BossFreePotionContext

    staged = build.verify_dependencies()
    running = {name: paths.sha256_file(paths.WORKER_DLL.parent / name) for name in build.EXPECTED_DEPENDENCY_SHA256}
    if running != staged:
        raise ValueError(f"the original worker at {paths.WORKER_DLL.parent} is not the staged frozen build: {running}")
    if build.worker_build_digest(paths.BUILD08_WORKER_BUILD_MANIFEST) != build.EXPECTED_WORKER_BUILD_DIGEST:
        raise ValueError("original worker build manifest digest differs from E's recorded worker_build_digest")
    return BossFreePotionContext([], turn100_env_config(ALLOWED_TURNS), potion_lambda=POTION_LAMBDA,
                                 allowed_turns=ALLOWED_TURNS, loss_profile=LOSS_PROFILE)


def battle_option_id(o: dict) -> str:
    """Typed identity of a legal battle option (kind:action:target[:slot])."""
    tid = f"{o['kind']}:{o['action']}:{o['target']}"
    if o["kind"] == "use_potion":
        tid += f":{o['potion_slot']}"
    return tid


@dataclass
class FightState:
    fight_id: str
    act_ordinal: int
    room: str
    entry_hp: int
    pstate: object  # crl.potion_v1.PotionEnvState with a BossFreeLedger
    decisions: int = 0
    encoded: set = field(default_factory=set)
    last_obs: object = None
    last_q: np.ndarray | None = None
    unsupported: str | None = None
    closed: bool = False


def load_frozen_e(checkpoint: Path, original_context, device: str = "cpu") -> "FrozenBattlePolicy":
    if original_context is None:
        raise ValueError("frozen E needs its original compatible battle context; None would skip the strict check")
    bootstrap()
    from battle_width_scan_v1.checkpoint import load

    checkpoint = Path(checkpoint)
    digest = build.sha256_file(checkpoint)
    if digest != build.FROZEN_E_SHA256:
        raise ValueError(f"{checkpoint}: sha256 {digest} is not the selected Windows E best.pt {build.FROZEN_E_SHA256}")
    model, blob = load(checkpoint, original_context, device=device, expected_configuration="E")
    if blob.get("compatibility_problems") != []:
        raise ValueError(f"incompatible with the original context: {blob.get('compatibility_problems')}")
    meta = blob.get("meta") or {}
    ident = dict(blob["model_identity"])
    ident.update(checkpoint_sha256=digest, selected_round=meta.get("validation_round"), episodes=meta.get("episodes"),
                 selected_by=meta.get("selected_by"), source=str(build.FROZEN_E_SOURCE))
    if ident["parameters"] != EXPECTED["parameters"] or ident["selected_round"] != EXPECTED["selected_round"] \
            or ident["episodes"] != EXPECTED["episodes"]:
        raise ValueError(f"not the selected E: {ident}")
    if len(original_context.vocab.card_ids) != EXPECTED["cards"]:
        raise ValueError("vocabulary is not the 214-card roster")
    if original_context.adapter.describe()["schema"] != EXPECTED["feature_schema"]:
        raise ValueError("feature schema is not crl.features/v3-potion-v2")
    return FrozenBattlePolicy(original_context, model, blob, ident)


class FrozenBattlePolicy:
    def __init__(self, ctx, model, blob: dict, identity: dict):
        self.ctx, self.model, self.blob, self.identity = ctx, model, blob, identity
        self.model.eval()
        for p in self.model.parameters():
            p.requires_grad_(False)
        self._frozen_ids = {id(p) for p in self.model.parameters()}
        self.device = next(self.model.parameters()).device
        self.forward_calls = 0
        self.forward_rows = 0

    # ------------------------------------------------------------------ freeze guards
    def train(self, mode: bool = True):
        raise RuntimeError("frozen E has no training mode")

    def trainable_parameters_guard(self, params):
        """Refuse any parameter list that contains an E tensor (for every optimizer the process builds)."""
        params = list(params)
        if any(id(p) in self._frozen_ids for p in params):
            raise ValueError("a frozen E parameter was handed to an optimizer")
        return params

    def provenance(self) -> dict:
        p = self.ctx.provenance()
        return {"identity": self.identity, "original_context_provenance": p,
                "original_worker_hello": {k: v for k, v in self.ctx.worker.hello.items()
                                          if k in ("protocol", "observation_schema", "engine_assembly_sha256",
                                                   "cds_worker_assembly_sha256", "rl_worker_assembly_sha256",
                                                   "engine_assembly_mvid", "engine_line", "terminal_boundary",
                                                   "seed_derivation", "runtime")},
                "compatibility_problems": self.blob["compatibility_problems"]}

    # ------------------------------------------------------------------ per fight
    def begin_fight(self, fight_id: str, act_ordinal: int, room: str, entry_hp: int) -> FightState:
        from crl.potion_v1 import PotionEnvState
        from potion_quality_training_v1.reward import BossFreeLedger
        if act_ordinal not in (1, 2, 3):
            raise ValueError(f"act ordinal {act_ordinal} outside 1..3")
        if room not in ROOMS:
            raise ValueError(f"room {room!r} is not one of {ROOMS}")
        ps = PotionEnvState(self.ctx.potion.potion_lambda, act_ordinal, room)
        ps.ledger = BossFreeLedger(self.ctx.potion.potion_lambda)  # BossFreePotionContext.episode_state
        return FightState(fight_id, act_ordinal, room, int(entry_hp), ps)

    def encode(self, raw_reply: dict, fight: FightState):
        """E's observation of one decision, through unchanged ``decode_potion`` (exactly once per decision)."""
        from crl.potion_v1 import decode_potion
        key = raw_reply.get("decision")
        if key in fight.encoded:
            raise RuntimeError(f"{fight.fight_id}: decision {key} encoded more than once (encode exactly once)")
        fight.encoded.add(key)
        try:
            st = decode_potion(self.ctx, raw_reply, fight.pstate)
        except (KeyError, TypeError, IndexError) as e:
            fight.unsupported = "malformed_battle_reply"
            raise UnsupportedBattleState("malformed_battle_reply", f"{type(e).__name__}: {e}") from e
        if st.status != "running" or st.obs is None:
            kind = fight.pstate.unsupported_kind or st.status
            fight.unsupported = kind
            raise UnsupportedBattleState(kind, st.error or f"status {st.status}")
        fight.decisions += 1
        fight.last_obs = st.obs
        return st.obs

    def choose(self, items: list[tuple[dict, FightState]]) -> list[BattleChoice]:
        out = self.choose_safe(items)
        for c in out:
            if isinstance(c, Exception):
                raise c
        return out

    @torch.inference_mode()
    def choose_safe(self, items: list[tuple[dict, FightState]]) -> list[BattleChoice | UnsupportedBattleState]:
        """Greedy masked E choices, one result per item: a BattleChoice, the item's UnsupportedBattleState
        (E cannot encode it), or its BattleContractError (nonfinite admissible Q: fatal for admissions)."""
        results: list = [None] * len(items)
        ok, obs = [], []
        for k, (r, f) in enumerate(items):
            try:
                obs.append(self.encode(r, f))
                ok.append(k)
            except UnsupportedBattleState as e:
                results[k] = e
            except Exception as e:  # e.g. a decision encoded twice: that item's contract violation only
                results[k] = BattleContractError(f"{f.fight_id}: encode failed: {type(e).__name__}: {e}")
        if not ok:
            return results
        try:
            rows = list(self._forward(obs))
        except Exception:  # isolate the failing item(s): each already-encoded observation alone
            rows = []
            for ob in obs:
                try:
                    rows.append(self._forward([ob])[0])
                except Exception as e:
                    rows.append(e)
        for k, ob, qrow in zip(ok, obs, rows):
            r, f = items[k]
            try:
                if isinstance(qrow, Exception):
                    raise BattleContractError(f"{f.fight_id}: frozen-E forward failed: {type(qrow).__name__}: {qrow}")
                results[k] = self._greedy(r, f, ob, qrow)
            except (UnsupportedBattleState, BattleContractError) as e:
                results[k] = e
            except Exception as e:
                results[k] = BattleContractError(f"{f.fight_id}: {type(e).__name__}: {e}")
        return results

    def _forward(self, obs: list) -> torch.Tensor:
        from crl.features import collate
        q = self.model(collate(obs, device=self.device)).float().cpu()
        self.forward_calls += 1
        self.forward_rows += len(obs)
        return q

    @staticmethod
    def admissible_q(ob, qrow: torch.Tensor) -> tuple[torch.Tensor, torch.Tensor]:
        """(Q over the decision's options, admissible mask). Raises UnsupportedBattleState with no admissible
        option and BattleContractError when ANY admissible Q is NaN/Inf (masked options may be -inf)."""
        n = ob.n_options
        qr = qrow[:n]
        adm = getattr(ob, "opt_admissible", None)
        allowed = torch.ones(n, dtype=torch.bool) if adm is None else torch.as_tensor(np.asarray(adm[:n], bool))
        if not allowed.any():
            raise UnsupportedBattleState("no_admissible_option", "no admissible option")
        if not bool(torch.isfinite(qr[allowed]).all()):
            raise BattleContractError(f"nonfinite admissible frozen-E Q {qr.tolist()}")
        return qr, allowed

    def _greedy(self, r: dict, f: FightState, ob, qrow: torch.Tensor) -> BattleChoice:
        qr, allowed = self.admissible_q(ob, qrow)
        masked = torch.where(allowed, qr, torch.full_like(qr, -float("inf")))
        choice = int(torch.argmax(masked).item())
        if not allowed[choice]:
            raise BattleContractError(f"{f.fight_id}: greedy E chose an inadmissible option")
        f.last_q = qr.numpy().copy()
        return BattleChoice(battle_option_id(r["obs"]["legal"][choice]), choice, int(r["decision"]))

    def account(self, fight: FightState, previous_reply: dict, ordinal: int, next_reply: dict) -> None:
        """Unchanged ``crl.potion_v1.account_step`` for one executed ordinal against the SUBSEQUENT complete
        Session-shaped reply: the next decision of the same fight, or -- when that step closed the fight --
        the original terminal reply the bridge's pinned writer produced on the retained combat state."""
        from crl.potion_v1 import account_step
        if next_reply is None or "status" not in next_reply:
            raise BattleContractError(f"{fight.fight_id}: account needs the subsequent Session-shaped reply")
        account_step(self.ctx, fight.pstate, previous_reply, ordinal, next_reply)

    def close(self) -> None:
        self.ctx.close()
