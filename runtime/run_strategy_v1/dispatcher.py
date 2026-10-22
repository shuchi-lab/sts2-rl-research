"""Batched full-run dispatcher (Task 7).

One central owner routes every decision of every admitted run: combat and combat-owned selections
to frozen E (batched greedy inference), every other decision to the strategy policy. Worker
processes run their runs sequentially inside ``step_many``; real parallelism comes from several
processes whose replies are drained asynchronously (FIFO futures). After at least one reply is
ready the owner waits at most ``deadline_ms`` for more before batching -- it never waits for every
slot. A run never has more than one outstanding request, and every request names its decision id,
so a delayed or dead worker cannot cause a stale or duplicate action.

Fault isolation. Every run ends with exactly one outcome. An unexpected exception while routing,
encoding, choosing or absorbing ONE run ends only that run (``error`` with a ``contract_violation:``
reason, recorded in :attr:`Dispatcher.fatal` so the owner stops new admissions), and an
:class:`~run_strategy_v1.contracts.UnsupportedStrategyState` ends it ``unsupported``; healthy sibling
runs keep draining. An infrastructure failure of a worker (dead process, malformed batch, protocol
desynchronisation, no reply within ``worker_timeout_s``) ends EVERY active run of that worker once --
including ready siblings without an outstanding request -- and its late futures are ignored, so a dead
worker's run can never be resurrected or updated. Other workers continue.

Per run the dispatcher owns: each fight's :class:`FightState` (``begin_fight`` exactly once per new
fight id, ``account`` for every executed ordinal against the subsequent Session-shaped reply), the
per-run :class:`StrategyReward` (battle events between two strategy decisions attach to the
earlier decision's interval; events before the first decision are recorded as unattributed), and
the :class:`RunTrajectory`.
"""
from __future__ import annotations

import time
import traceback
from collections import Counter, defaultdict
from concurrent.futures import FIRST_COMPLETED, Future, wait
from dataclasses import dataclass, field
from typing import Any, Callable

import numpy as np

from .artifacts import sanitize
from .contracts import (BattleEvent, ContractError, PublicRunObservation, RunOutcome, StepReply,
                        StrategyTransition, UnsupportedStrategyState)
from .frozen_battle import BattleContractError, FightState, UnsupportedBattleState
from .reward import StrategyReward
from .trajectory import RunTrajectory
from .worker import RunWorker, WorkerError, battle_request, parse_many, strategy_request


@dataclass
class ActiveRun:
    run_id: str
    worker: int
    namespace: str
    seed: int
    seed_string: str
    purpose: str  # train | validation | diagnostic
    rng: np.random.Generator
    reward: StrategyReward
    trajectory: RunTrajectory
    reply: StepReply | None = None
    outstanding: bool = False
    ready_since: float = 0.0
    fights: dict = field(default_factory=dict)
    last_battle: tuple | None = None  # (fight_id, raw reply, ordinal) awaiting its account
    pending: dict | None = None  # the open strategy decision {sobs, obs, candidate_id, info}
    interval_reward: float = 0.0
    interval_events: list = field(default_factory=list)
    unattributed_reward: float = 0.0
    battle_decisions: int = 0
    strategy_decisions: int = 0
    outcome: RunOutcome | None = None
    rows: list = field(default_factory=list)
    started: float = field(default_factory=time.monotonic)
    admitted_utc: float = field(default_factory=time.time)
    fight_summaries: list = field(default_factory=list)


class Dispatcher:
    def __init__(self, workers: list[RunWorker], battle_policy, strategy_policy, *, deadline_ms: float = 5.0,
                 on_record: Callable[[str, dict], None] | None = None, n_step: int = 5, gamma: float = 1.0,
                 worker_timeout_s: float = 900.0):
        self.workers = workers
        self.battle = battle_policy
        self.strategy = strategy_policy
        self.deadline = deadline_ms / 1000.0
        self.on_record = on_record or (lambda kind, rec: None)
        self.n_step, self.gamma = n_step, gamma
        self.worker_timeout = worker_timeout_s
        self.runs: dict[str, ActiveRun] = {}
        self.futures: dict[Future, tuple[int, list[str], str, float]] = {}
        self.dead_workers: set[int] = set()
        self.worker_failures: dict[int, str] = {}
        self.stats = Counter()
        self.timing = defaultdict(list)
        self.fatal: list[dict] = []  # contract violations: the owner stops new admissions

    # ------------------------------------------------------------------ records
    def _emit(self, kind: str, rec: dict) -> None:
        """Record through the owner's sink; a record it refuses (e.g. a nonfinite number under strict JSON)
        is a contract violation, kept as an explicitly sanitized record rather than lost."""
        try:
            self.on_record(kind, rec)
        except ValueError as e:
            self.fatal.append({"run_id": rec.get("run_id"), "reason": "contract_violation:unserializable_record",
                               "detail": f"{kind}: {e}"})
            self.on_record(kind, {**sanitize(rec), "sanitized_nonfinite": True})

    # ------------------------------------------------------------------ admission
    def slots_used(self, w: int) -> int:
        return sum(1 for r in self.runs.values() if r.worker == w)

    def admit(self, run_id: str, namespace: str, seed: int, seed_string: str, purpose: str, *, worker: int,
              policy_seed: int, ascension: int = 2) -> ActiveRun:
        if run_id in self.runs:
            raise ContractError(f"run {run_id} already admitted")
        if worker in self.dead_workers:
            raise WorkerError(f"worker {worker} is dead")
        run = ActiveRun(run_id, worker, namespace, seed, seed_string, purpose, np.random.default_rng(policy_seed),
                        StrategyReward(run_id), RunTrajectory(run_id, self.n_step, self.gamma))
        slot = self.slots_used(worker)
        self.runs[run_id] = run
        run.outstanding = True
        fut = self.workers[worker].submit({"cmd": "reset", "run_id": run_id, "slot": slot,
                                           "seed": seed_string, "ascension": ascension})
        self.futures[fut] = (worker, [run_id], "reset", time.monotonic())
        self._emit("admit", {"run_id": run_id, "namespace": namespace, "seed": seed, "purpose": purpose,
                             "worker": worker, "worker_pid": self.workers[worker].pid})
        return run

    @property
    def active(self) -> int:
        return len(self.runs)

    # ------------------------------------------------------------------ main tick
    def advance(self, run_ids: list[str] | None = None) -> list[StrategyTransition | RunOutcome]:
        """Act on every ready run, submit, then absorb whichever replies arrive (one tick)."""
        events: list = []
        for w in {r.worker for r in self.runs.values() if r.outcome is None} - self.dead_workers:
            if not getattr(self.workers[w], "alive", True):
                events.extend(self._kill_worker(w, "worker process is no longer alive"))
        ready = [r for r in self.runs.values() if not r.outstanding and r.reply is not None and r.outcome is None
                 and (run_ids is None or r.run_id in run_ids)]
        requests: dict[int, list[tuple[str, dict]]] = defaultdict(list)
        battle_items, strategy_items = [], []
        now = time.monotonic()
        for r in ready:
            self.timing["ready_wait"].append(now - r.ready_since)
            if r.reply.owner == "battle" and r.reply.observation.fight_id in r.fights:
                battle_items.append(r)
            elif r.reply.owner == "strategy" and r.pending is not None:
                strategy_items.append(r)
            else:
                events.extend(self._contract_fail(r, "routing", ContractError(
                    f"running reply owned by {r.reply.owner!r} without its fight/decision state")))
        if battle_items:
            events.extend(self._route_battle(battle_items, requests))
        if strategy_items:
            events.extend(self._route_strategy(strategy_items, requests))
        for w, reqs in requests.items():
            if w in self.dead_workers:
                continue
            for rid, _ in reqs:
                self.runs[rid].outstanding = True
            fut = self.workers[w].submit({"cmd": "step_many", "steps": [q for _, q in reqs]})
            self.futures[fut] = (w, [rid for rid, _ in reqs], "step_many", time.monotonic())
        events.extend(self._drain())
        return events

    def _route_battle(self, items: list[ActiveRun], requests) -> list:
        events: list = []
        t0 = time.monotonic()
        pairs = [(r.reply.observation.raw, r.fights[r.reply.observation.fight_id]) for r in items]
        try:
            choices = self.battle.choose_safe(pairs)
            if len(choices) != len(pairs):
                raise ContractError(f"{len(choices)} battle choices for {len(pairs)} decisions")
        except Exception as e:  # not attributable to one item: every run in this batch, once
            for r in items:
                events.extend(self._contract_fail(r, "battle_batch", e))
            return events
        self.timing["battle_forward"].append(time.monotonic() - t0)
        self.timing["battle_batch"].append(len(pairs))
        for r, (raw, fight), c in zip(items, pairs, choices):
            try:
                if isinstance(c, BattleContractError):
                    self.fatal.append({"run_id": r.run_id, "reason": "battle_contract_violation", "detail": str(c)})
                    requests[r.worker].append((r.run_id, {"cmd": "abandon", "run_id": r.run_id, "kind": "error",
                                                          "reason": "contract_violation:battle_frozen_e",
                                                          "detail": str(c)[:2000]}))
                    continue
                if isinstance(c, UnsupportedBattleState):
                    self.stats[f"abandon:battle_{c.kind}"] += 1
                    requests[r.worker].append((r.run_id, {"cmd": "abandon", "run_id": r.run_id, "kind": "unsupported",
                                                          "reason": f"battle_{c.kind}", "detail": str(c)[:2000]}))
                    continue
                if isinstance(c, Exception):
                    raise c
                r.last_battle = (fight.fight_id, raw, c.ordinal, c.option_id)
                r.battle_decisions += 1
                requests[r.worker].append((r.run_id, battle_request(r.run_id, r.reply.decision_id, c.decision_id,
                                                                    c.ordinal)))
            except Exception as e:
                events.extend(self._contract_fail(r, "battle_route", e))
        return events

    def _route_strategy(self, items: list[ActiveRun], requests) -> list:
        events: list = []
        t0 = time.monotonic()
        try:
            acts = self.strategy.act([(r.pending["sobs"], r.reply.observation, r.rng, r.purpose) for r in items])
            if len(acts) != len(items):
                raise ContractError(f"{len(acts)} strategy choices for {len(items)} decisions")
        except Exception as e:  # not attributable to one item
            for r in items:
                events.extend(self._contract_fail(r, "strategy_batch", e))
            return events
        self.timing["strategy_forward"].append(time.monotonic() - t0)
        self.timing["strategy_batch"].append(len(items))
        for r, (cid, info) in zip(items, acts):
            try:
                if isinstance(cid, Exception):
                    self.fatal.append({"run_id": r.run_id, "reason": "strategy_contract_violation", "detail": str(cid)})
                    requests[r.worker].append((r.run_id, {"cmd": "abandon", "run_id": r.run_id, "kind": "error",
                                                          "reason": "contract_violation:strategy_q",
                                                          "detail": str(cid)[:2000]}))
                    continue
                obs: PublicRunObservation = r.reply.observation
                obs.candidate(cid)  # must be displayed
                r.pending["candidate_id"] = cid
                r.pending["info"] = info
                r.strategy_decisions += 1
                requests[r.worker].append((r.run_id, strategy_request(r.run_id, r.reply.decision_id, cid)))
            except Exception as e:
                events.extend(self._contract_fail(r, "strategy_route", e))
        return events

    def _drain(self) -> list:
        if not self.futures:
            return []
        events: list = []
        done, _ = wait(list(self.futures), timeout=self._wait_budget(), return_when=FIRST_COMPLETED)
        if not done:
            now = time.monotonic()
            for fut, (w, run_ids, kind, t0) in list(self.futures.items()):
                if w not in self.dead_workers and now - t0 >= self.worker_timeout:
                    # Evidence before the kill: which decisions the stuck request carried (unchanged timeout).
                    inflight = ", ".join(f"{rid}@{r.reply.decision_id if (r := self.runs.get(rid)) and r.reply else '-'}"
                                         for rid in run_ids)
                    events.extend(self._kill_worker(w, f"no {kind} reply within {self.worker_timeout:.0f} s; "
                                                       f"in flight: {inflight}", terminate=True))
            return events
        if self.deadline > 0:
            t_end = time.monotonic() + self.deadline
            more, _ = wait([f for f in self.futures if f not in done], timeout=max(0.0, t_end - time.monotonic()),
                           return_when="ALL_COMPLETED")
            done = set(done) | set(more)
        for fut in done:
            w, run_ids, kind, _ = self.futures.pop(fut)
            if w in self.dead_workers:
                fut.exception()  # a late reply/failure of a dead worker: its runs already ended, never resurrected
                self.stats["late_future_ignored"] += 1
                continue
            try:
                raw = fut.result()
                if not raw.get("ok"):
                    raise WorkerError(f"{raw.get('error_type')}: {raw.get('message')}")
                replies = [StepReply.parse(raw)] if kind == "reset" else parse_many(raw)
                if len(replies) != len(run_ids):
                    raise WorkerError(f"{len(replies)} replies for {len(run_ids)} requests")
                for rid, rep in zip(run_ids, replies):
                    if not isinstance(rep, Exception) and rep.run_id != rid:
                        raise WorkerError(f"reply for {rep.run_id} delivered as {rid} (protocol desynchronised)")
            except Exception as e:  # dead worker / malformed batch: every active run on that worker ends once
                events.extend(self._kill_worker(w, f"{type(e).__name__}: {e}"))
                continue
            for rid, rep in zip(run_ids, replies):
                r = self.runs.get(rid)
                if r is None or r.outcome is not None:
                    self.stats["late_reply_ignored"] += 1  # the run already ended (failed locally / released)
                    continue
                if isinstance(rep, Exception):
                    events.extend(self._contract_fail(r, "rejected_request", rep, reason="contract_violation:rejected_request"))
                    continue
                try:
                    events.extend(self._absorb(r, rep))
                except UnsupportedStrategyState as e:
                    r.outstanding = False
                    events.extend(self._fail(rid, "unsupported", f"strategy_{e.kind}", str(e)))
                except Exception as e:
                    events.extend(self._contract_fail(r, "absorb", e))
        return events

    def _wait_budget(self) -> float:
        oldest = min(t0 for (_, _, _, t0) in self.futures.values())
        return max(0.05, self.worker_timeout - (time.monotonic() - oldest))

    def _kill_worker(self, w: int, why: str, *, terminate: bool = False) -> list:
        """Infrastructure failure of worker ``w``: it takes no further requests and EVERY run it hosts ends
        exactly once as ``error/worker_failure`` (outstanding or not); late futures are ignored."""
        events: list = []
        if w in self.dead_workers:
            return events
        self.dead_workers.add(w)
        self.worker_failures[w] = why
        if terminate and hasattr(self.workers[w], "kill"):
            try:
                self.workers[w].kill()
            except Exception:
                pass
        for r in list(self.runs.values()):
            if r.worker == w and r.outcome is None:
                events.extend(self._fail(r.run_id, "error", "worker_failure", f"worker {w}: {why}"))
        return events

    def _contract_fail(self, r: ActiveRun, where: str, e: BaseException, *, reason: str | None = None) -> list:
        """An unexpected per-run exception: that run alone ends (excluded, fatal for new admissions)."""
        if r.outcome is not None:
            return []
        reason = reason or f"contract_violation:dispatcher_{where}"
        detail = f"{type(e).__name__}: {e}\n{traceback.format_exc()[-1500:]}"
        self.fatal.append({"run_id": r.run_id, "reason": reason, "detail": detail[:2000]})
        return self._fail(r.run_id, "error", reason, detail)

    # ------------------------------------------------------------------ reply processing
    def _absorb(self, r: ActiveRun, rep: StepReply) -> list:
        r.outstanding = False
        r.reply = rep
        r.ready_since = time.monotonic()
        events: list = []
        reward = r.reward.consume(rep.battle_events)
        for e in rep.battle_events:
            r.interval_events.append(e)
        if r.pending is None:
            r.unattributed_reward += reward
        else:
            r.interval_reward += reward
        # account the previous battle step against this (subsequent) reply
        if r.last_battle is not None:
            fid, raw, ordinal, option_id = r.last_battle
            fight: FightState = r.fights[fid]
            same = rep.owner == "battle" and rep.observation.fight_id == fid
            if same:
                nxt = rep.observation.raw
            else:
                ev = next((e for e in rep.battle_events if e.fight_id == fid), None)
                if ev is None:
                    if rep.status == "running":
                        raise ContractError(f"{r.run_id}: fight {fid} left without a closing battle event")
                    nxt = None  # the run ended inside the fight (limit/error/abandon): nothing natural to account
                else:
                    nxt = ev.terminal_reply
            if nxt is not None:
                self.battle.account(fight, raw, ordinal, nxt)
            r.last_battle = None
            if not same:
                self._close_fight(r, fight, rep)
        if rep.owner == "battle":
            ob = rep.observation
            if ob.fight_id not in r.fights:
                for f in r.fights.values():
                    if not f.closed:
                        raise ContractError(f"{r.run_id}: new fight {ob.fight_id} while {f.fight_id} is open")
                e = ob.entry
                r.fights[ob.fight_id] = self.battle.begin_fight(ob.fight_id, e["act_ordinal"], e["room"], e["entry_hp"])
                if ob.raw.get("entry_hp") != e["entry_hp"]:
                    raise ContractError(f"{ob.fight_id}: Session entry_hp {ob.raw.get('entry_hp')} != captured {e['entry_hp']}")
        elif rep.owner == "strategy":
            sobs = self.strategy.encode(rep.observation)
            self._close_decision(r, sobs, terminal=False, events=events)
            r.pending = {"sobs": sobs, "obs": rep.observation, "candidate_id": None, "info": None,
                         "raw": rep.raw.get("strategy"), "auto_before": list(rep.auto_actions)}
        if rep.status != "running":
            events.extend(self._finish(r, rep.outcome))
        return events

    def _close_fight(self, r: ActiveRun, fight: FightState, rep: StepReply) -> None:
        fight.closed = True
        ev = next((e for e in rep.battle_events if e.fight_id == fight.fight_id), None)
        rec = {"run_id": r.run_id, "fight_id": fight.fight_id, "act_ordinal": fight.act_ordinal, "room": fight.room,
               "entry_hp": fight.entry_hp, "decisions": fight.decisions,
               "outcome": ev.outcome if ev else None, "hp_terminal": ev.hp_terminal if ev else None,
               "ledger": fight.pstate.ledger.summary(), "foreign_offers": len(fight.pstate.foreign_offers),
               "unsupported": fight.unsupported}
        r.fight_summaries.append(rec)
        self._emit("fight", rec)

    def _close_decision(self, r: ActiveRun, next_sobs, *, terminal: bool, events: list) -> None:
        p = r.pending
        if p is None:
            return
        if p["candidate_id"] is None:
            raise ContractError(f"{r.run_id}: a decision closed before it was answered")
        t = StrategyTransition(p["sobs"], p["candidate_id"], r.interval_reward, None if terminal else next_sobs, terminal)
        r.trajectory.append(t)
        self._emit("decision", {
            "run_id": r.run_id, "purpose": r.purpose, "decision_id": p["obs"].decision_id, "phase": p["obs"].phase,
            "candidates": [c.id for c in p["obs"].candidates], "chosen": p["candidate_id"], "info": p["info"],
            "reward": r.interval_reward, "terminal": terminal, "auto_before": p.get("auto_before"),
            "battle_events": [{k: v for k, v in e.__dict__.items() if k != "terminal_reply"} for e in r.interval_events],
            "observation": p.get("raw")})
        events.append(t)
        r.interval_reward = 0.0
        r.interval_events = []
        r.pending = None

    def _finish(self, r: ActiveRun, outcome: RunOutcome) -> list:
        events: list = []
        if r.outcome is not None:
            return events  # exactly one outcome per run
        try:
            if outcome.natural and r.pending is not None and r.pending["candidate_id"] is not None:
                self._close_decision(r, None, terminal=True, events=events)
            elif r.pending is not None and r.pending["candidate_id"] is None:
                r.pending = None  # a decision displayed but never answered (run ended by the client/limit)
            rows = r.trajectory.finish(outcome)
        except Exception as e:  # an inconsistent trajectory: excluded, never partially admitted
            detail = f"{type(e).__name__}: {e} (engine outcome {outcome.kind}:{outcome.reason})"
            self.fatal.append({"run_id": r.run_id, "reason": "contract_violation:trajectory", "detail": detail})
            outcome = RunOutcome(r.run_id, "error", "contract_violation:trajectory", False, outcome.battles, detail,
                                 outcome.hp, outcome.floor, outcome.act_ordinal)
            events = [e for e in events if not isinstance(e, StrategyTransition)]
            rows = []
        r.rows = rows if r.purpose == "train" else []  # validation/diagnostic runs never feed replay
        r.outcome = outcome
        r.outstanding = False
        self.stats[f"outcome:{outcome.kind}:{outcome.reason}"] += 1
        self._emit("run", {
            "run_id": r.run_id, "purpose": r.purpose, "namespace": r.namespace, "seed": r.seed,
            "outcome": {"kind": outcome.kind, "reason": outcome.reason, "won": outcome.won,
                        "detail": (outcome.detail or "")[:2000], "hp": outcome.hp, "floor": outcome.floor,
                        "act_ordinal": outcome.act_ordinal},
            "return": r.reward.total if outcome.natural else None, "reward_total_observed": r.reward.total,
            "unattributed_reward": r.unattributed_reward, "fights": list(outcome.battles),
            "fight_summaries": r.fight_summaries, "strategy_decisions": r.strategy_decisions,
            "battle_decisions": r.battle_decisions, "replay_rows": len(r.rows),
            "declined_counts": ((r.reply.raw if r.reply else None) or {}).get("outcome", {}).get("declined_counts"),
            "elapsed_s": time.monotonic() - r.started, "admitted_utc": r.admitted_utc, "finished_utc": time.time(),
            "worker": r.worker})
        events.append(outcome)
        return events

    def _fail(self, run_id: str, kind: str, reason: str, detail: str) -> list:
        r = self.runs[run_id]
        if r.outcome is not None:
            return []
        r.outstanding = False
        c = r.reply.counters if r.reply is not None else {}
        out = RunOutcome(run_id, kind, reason, False, (), detail, 0, int(c.get("floor", 0)), int(c.get("act_ordinal", 0)))
        return self._finish(r, out)

    def release(self, run_id: str) -> ActiveRun:
        """Forget a finished run (its worker slot frees); closes it inside the worker."""
        r = self.runs.pop(run_id)
        if r.outcome is None:
            raise ContractError(f"{run_id} released before it finished")
        if r.worker not in self.dead_workers:
            fut = self.workers[r.worker].submit({"cmd": "close", "run_id": run_id})
            fut.add_done_callback(lambda f: f.exception())
        return r

    def finished(self) -> list[ActiveRun]:
        return [r for r in self.runs.values() if r.outcome is not None]

    def timing_summary(self) -> dict:
        out = {}
        for k, v in self.timing.items():
            a = np.asarray(v, dtype=np.float64)
            out[k] = {"n": int(a.size), "mean": float(a.mean()) if a.size else None,
                      "p95": float(np.percentile(a, 95)) if a.size else None, "max": float(a.max()) if a.size else None}
        return out


def drive_to_end(d: Dispatcher, max_ticks: int = 10_000_000) -> list[ActiveRun]:
    """Advance until every admitted run finished; returns them (still registered)."""
    for _ in range(max_ticks):
        if all(r.outcome is not None for r in d.runs.values()):
            return list(d.runs.values())
        d.advance()
    raise RuntimeError("drive_to_end exceeded max_ticks")


class RandomStrategy:
    """Uniform legal random strategy (the pre-gate collection policy); encode is the identity."""
    name = "uniform_legal_random/v1"

    def encode(self, obs: PublicRunObservation):
        return obs

    def act(self, items):
        return [(obs.candidates[int(rng.integers(len(obs.candidates)))].id, {"policy": self.name})
                for _, obs, rng, _ in items]
