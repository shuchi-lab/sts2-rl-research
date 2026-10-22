"""Frozen greedy evaluator core, extracted for the v1.0.0 local release."""

from __future__ import annotations

EVALUATION_TRANSPORT = "private/fifo-unlocked-pipe-write/v1"

import argparse

from collections import Counter

from concurrent.futures import Future

from copy import deepcopy

import gc

import json

import os

from pathlib import Path

import sys

import threading

import time

import traceback

import types

import uuid

import fullrun_core as core

def write(path, value, *, exclusive=False):
    core.write_json(path, value, exclusive=exclusive)

def append(path, row):
    with Path(path).open("a", encoding="utf-8") as f:
        f.write(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n")
        f.flush()
        os.fsync(f.fileno())

class ResourceLog:
    """Observation only (never gates admission): own-tree CPU time and host memory every 60 s."""
    def __init__(self, out):
        import psutil
        self.psutil, self.path, self.last = psutil, Path(out) / "RESOURCE_LOG.jsonl", 0.0

    def tick(self, workers=(), force=False):
        if not force and time.monotonic() - self.last < 60:
            return
        self.last = time.monotonic()
        try:
            me = self.psutil.Process(os.getpid())
            vm = self.psutil.virtual_memory()
            row = {"utc": core.now_iso(), "pid": os.getpid(), "cpu_seconds": sum(me.cpu_times()[:2]),
                   "rss_bytes": me.memory_info().rss, "host_available_bytes": vm.available,
                   "worker_pids": [w.pid for w in workers], "gating": False}
        except Exception as exc:
            row = {"utc": core.now_iso(), "observation_error": f"{type(exc).__name__}: {exc}", "gating": False}
        append(self.path, row)

def evaluation_worker_class(base_class, error_type):
    """Original protocol/reader with private FIFO pipe writes that cannot block its reader lock."""
    class EvaluationRunWorker(base_class):
        def __init__(self, *args, **kwargs):
            self._write_lock = threading.Lock()  # The base constructor calls the overridden submit().
            super().__init__(*args, **kwargs)

        def submit(self, cmd):
            future = Future()
            line = json.dumps(cmd, separators=(",", ":")) + "\n"
            with self._write_lock:
                with self._lock:
                    if self._dead:
                        future.set_exception(error_type(self._dead))
                        return future
                    self._pending.append(future)
                try:
                    self.proc.stdin.write(line)
                    self.proc.stdin.flush()
                except (BrokenPipeError, OSError) as error:
                    with self._lock:
                        pending = future in self._pending
                        if pending:
                            self._pending.remove(future)
                    if pending and not future.done():
                        future.set_exception(error_type(f"worker pipe closed: {error}"))
            return future

    return EvaluationRunWorker

def freeze(model):
    model.eval()
    for p in model.parameters():
        p.requires_grad_(False)
    original = model.train

    def guarded_train(self, mode=True):
        if mode:
            raise RuntimeError("evaluation only: model.train(True) prohibited")
        return original(False)
    model.train = types.MethodType(guarded_train, model)
    return model

def reject_optimizer(*args, **kwargs):
    raise RuntimeError("evaluation only: optimizer construction prohibited")

def install_boss_bill_check(policy, potion_features, expected_width):
    """Every encoded Battle decision: model-declared global width; at ALL Acts' bosses zero bill features
    and zero ledger. A violation raises inside encode -> BattleContractError -> visible run error."""
    from run_strategy_v1.frozen_battle import BattleContractError
    counts = Counter()
    base = type(policy)

    class BossBillChecked(base):
        def encode(self, raw_reply, fight):
            ob = super().encode(raw_reply, fight)
            ledger = fight.pstate.ledger
            problems = core.boss_bill_violations(ob.glob, fight.room, ledger.bill, ledger.charged_uses,
                                                 potion_features, expected_width)
            counts["decisions_checked"] += 1
            if fight.room == "boss":
                counts[f"boss_act{fight.act_ordinal}_decisions_checked"] += 1
            if problems:
                counts["violations"] += 1
                raise BattleContractError(f"{fight.fight_id}: all-boss-free feature contract: {problems}")
            return ob
    policy.__class__ = BossBillChecked
    policy.bill_check_counts = counts
    return policy

class Evaluation:
    def __init__(self, manifest, out):
        self.m, self.out = manifest, Path(out)
        self.workers, self.worker_ownership, self.ctx = [], {}, None
        self.battle_cache, self.strategy_cache = {}, {}
        self.compat = self.worker_limits = None
        try:
            self._initialize()
        except BaseException:
            self.close()
            raise

    def _initialize(self):
        m = self.m
        roots = m["source_roots"]
        if not roots:
            raise ValueError("manifest.source_roots required: [bwc1 overlay, four-arm overlay, four-arm runtime]")
        sys.path[:0] = [str(Path(p)) for p in roots]
        runtime = Path(m.get("runtime_root") or roots[-1])
        os.environ.update(STS2_RL_ENGINE_LINE="build08", STS2_RL_INPUT_BUNDLE=str(runtime / "data/frozen_three_act_214_inputs"),
                          PYTHONDONTWRITEBYTECODE="1", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1",
                          OPENBLAS_NUM_THREADS="1", KMP_BLOCKTIME="0", OMP_WAIT_POLICY="PASSIVE")
        for k, v in m.get("runtime_environment", {}).items():
            if not k.startswith(("STS2_RL_", "RS1_")):
                raise ValueError(f"unexpected runtime environment key {k}")
            os.environ[k] = str(v)
        from run_strategy_v1.bootstrap import bootstrap
        self.bootstrap = bootstrap()  # must precede crl.paths
        import torch
        self.torch = torch
        torch.set_num_threads(1)
        torch.set_num_interop_threads(1)
        torch.set_grad_enabled(False)
        torch.optim.Optimizer.__init__ = reject_optimizer
        from run_strategy_battle62p5_v1 import battle62
        from run_strategy_v1 import build
        from run_strategy_v1.metadata import load_public_metadata
        from run_strategy_v1.features import StrategyEncoder
        from crl import paths
        self.build = build
        self.ctx = battle62.make_context()
        problems = battle62.context_problems(self.ctx)  # all-boss-free family, 214 cards, v3-potion-v2
        if self.ctx.potion.reward_id != core.REWARD_ID or self.ctx.potion.allowed_turns != 100:
            problems.append("context reward id / 100-turn limit differs from bwc1 training")
        if problems:
            raise core.ContractViolation(f"evaluation context refused: {problems}")
        provenance = self.ctx.provenance()
        self.ctx.provenance = lambda: deepcopy(provenance)
        self.metadata = load_public_metadata(paths.CATALOG, self.ctx.vocab.card_ids)
        self.encoder = StrategyEncoder(self.metadata)
        self.res = ResourceLog(self.out)

    # -------------------------------------------------------------------- Choice (frozen)


    # -------------------------------------------------------------------- Battle (strict bwc1)

    # -------------------------------------------------------------------- owned native workers
    def _register(self, worker, index):
        import psutil
        proc = worker.proc
        expected = [str(self.build.DOTNET), str(self.build.WORKER_DLL)]
        ps = psutil.Process(proc.pid)
        if proc.poll() is not None or proc.pid != worker.pid or list(proc.args) != expected or ps.ppid() != os.getpid():
            raise RuntimeError("new native run worker is not this evaluator's own child")
        self.worker_ownership[id(worker)] = {"worker": index, "pid": proc.pid, "parent_pid": os.getpid(),
                                             "create_time": ps.create_time(), "argv": expected}

    def _owned(self, worker):
        import psutil
        owned = self.worker_ownership.get(id(worker))
        proc = worker.proc
        if owned is None or proc.pid != owned["pid"]:
            raise RuntimeError("native cleanup has no original owned process handle")
        if proc.poll() is None:
            ps = psutil.Process(proc.pid)
            if ps.create_time() != owned["create_time"] or ps.ppid() != owned["parent_pid"]:
                raise RuntimeError("native process birth/parent changed; refusing to touch it")
        return owned

    def ensure_workers(self, compat, limits):
        from run_strategy_v1.worker import RunWorker, WorkerError
        if self.workers and (self.worker_limits != limits or self.compat != compat):
            raise core.ContractViolation("worker limits/compatibility differ within one evaluator process")
        if not self.workers:
            self.compat, self.worker_limits = compat, limits
            Private = evaluation_worker_class(RunWorker, WorkerError)
            for i in range(self.m.get("native_workers", 2)):
                self.workers.append(Private(self.build.WORKER_DLL, compat, limits, purity_check=False,
                                            name=f"bwc1-fullrun-{os.getpid()}-{i}", dotnet=self.build.DOTNET))
                self._register(self.workers[-1], i)

    def close_workers(self, reason, *, kill_all=False, failed=()):
        """Bounded quit using only original owned child handles; failed/cutoff workers are killed."""
        closed, errors = [], []
        for worker in list(self.workers):
            try:
                owned = self._owned(worker)
                proc, forced, quit_sent = worker.proc, False, False
                if proc.poll() is None:
                    if kill_all or owned["worker"] in failed:
                        proc.kill()
                        forced = True
                    else:
                        failures = []

                        def send(w=worker):
                            try:
                                with w._write_lock:
                                    w.proc.stdin.write('{"cmd":"quit"}\n')
                                    w.proc.stdin.flush()
                            except BaseException as exc:
                                failures.append(repr(exc))
                        t = threading.Thread(target=send, daemon=True)
                        t.start()
                        t.join(timeout=10)
                        quit_sent = True
                        if t.is_alive() or failures:
                            self._owned(worker)
                            proc.kill()
                            forced = True
                    try:
                        proc.wait(timeout=10)
                    except Exception:
                        self._owned(worker)
                        proc.kill()
                        forced = True
                        proc.wait(timeout=10)
                closed.append({**owned, "reason": reason, "quit_sent": quit_sent, "forced": forced,
                               "actual_os_exit_code": proc.poll(), "utc": core.now_iso()})
            except BaseException as exc:
                errors.append(f"{type(exc).__name__}: {exc}")
        self.workers, self.worker_ownership = [], {}
        if errors:
            raise RuntimeError(f"owned native cleanup failed: {errors}")
        return closed

    # -------------------------------------------------------------------- one cell
    def cell(self, phase, choice, model_entry, block, seeds, run_dir, *, admit_limit=None):
        from run_strategy_v1.dispatcher import Dispatcher
        from run_strategy_v1.restrictions import build_compatibility
        from run_strategy_v1.seeds import seed_string
        cid = core.cell_id(choice["id"], model_entry, block, diagnostic=phase == "diagnostic")
        out = self.out / run_dir / cid
        out.mkdir(parents=True, exist_ok=True)
        records_path, admissions_path, summary_path = out / "records.jsonl", out / "admissions.jsonl", out / "SUMMARY.json"
        records = core.checked_rows(core.parse_jsonl(records_path), seeds)
        admissions = core.checked_rows(core.parse_jsonl(admissions_path), seeds)
        identity = {"schema": core.CELL_SCHEMA, "cell_id": cid, "phase": phase, "block": block, "choice_id": choice["id"],
                    "model_id": model_entry["id"], "host": model_entry["host"], "profile": model_entry["profile"],
                    "stage2_online": model_entry["stage2_online"], "battle_checkpoint": model_entry["path"],
                    "choice_checkpoint": choice["path"], "seed_tag": core.NAMESPACE_TAGS[phase], "admit_limit": admit_limit,
                    "runtime_file_hashes_performed": False}
        if summary_path.exists():
            old = core.read_json(summary_path)
            if any(old.get(k) != identity[k] for k in ("cell_id", "choice_id", "model_id", "battle_checkpoint", "choice_checkpoint")):
                raise core.ContractViolation(f"{cid}: retained cell identity changed")
            if old.get("state") in ("complete", "diagnostic_complete"):
                return old
            if old.get("state") in ("failure", "cutoff"):
                return old  # retained, never retried
        if {r["seed"] for r in admissions} - {r["seed"] for r in records}:
            raise core.ContractViolation(f"{cid}: admitted seed without outcome retained; never replayed")
        battle = self.battle(model_entry)
        strategy, cfg = self.strategy(choice)
        compat = build_compatibility(battle)
        self.ensure_workers(compat, cfg.limits)
        index = {s: j for j, s in enumerate(seeds)}
        done = {r["seed"] for r in records}
        jobs = [(f"{cid}:{index[s]:03d}", s) for s in seeds[:admit_limit] if s not in done]
        counts, returned_error = Counter(), None
        hard_stop, final = core.cell_hard_stop(self.m), core.utc(self.m["final_deadline_utc"]).timestamp()
        start, last, stop_reason, drain_cutoff = time.monotonic(), 0.0, None, False

        def audit(kind, rec):
            counts[f"event:{kind}"] += 1
            if "purpose" in rec and rec["purpose"] != "validation":
                raise RuntimeError("nonvalidation action refused")
            if kind == "admit":
                append(admissions_path, {"utc": core.now_iso(), **rec, "cell_id": cid})
            if kind == "decision" and (rec["info"]["policy"] != "greedy" or rec["info"]["random"]):
                raise RuntimeError("nongreedy Choice action refused")
            if kind == "run" and (rec["replay_rows"] != 0 or rec["namespace"] != "validation"):
                raise RuntimeError("replay or wrong seed namespace refused")
        d = Dispatcher(self.workers, battle, strategy, deadline_ms=5, on_record=audit, n_step=cfg.learner.n_step,
                       gamma=cfg.learner.gamma, worker_timeout_s=900)

        def progress(state):
            write(self.out / "RUN_STATE.json", {"utc": core.now_iso(), "pid": os.getpid(), "state": state, "cell_id": cid,
                  "observed": len(records), "expected": len(seeds), "elapsed_s": time.monotonic() - start,
                  "worker_pids": [w.pid for w in self.workers]})
        progress("running")
        try:
            with records_path.open("a", encoding="utf-8") as f:
                def finished(r):
                    nonlocal returned_error
                    if r.purpose != "validation" or r.rows:
                        raise RuntimeError("run modified replay or purpose")
                    row = {"run_id": r.run_id, "seed": r.seed, "outcome": r.outcome.__dict__, "cell_id": cid,
                           "model_id": model_entry["id"], "choice_id": choice["id"], "block": block,
                           "return": r.reward.total if r.outcome.natural else None,
                           "strategy_decisions": r.strategy_decisions, "battle_decisions": r.battle_decisions,
                           "elapsed_s": time.monotonic() - r.started, "policy": "greedy"}
                    f.write(json.dumps(row, separators=(",", ":"), allow_nan=False) + "\n")
                    f.flush()
                    os.fsync(f.fileno())
                    records.append(row)
                    if r.outcome.kind not in ("natural", "unsupported", "limit"):
                        returned_error = {"run_id": r.run_id, "outcome": row["outcome"]}
                    r.trajectory.transitions.clear()
                    r.fights.clear()
                pending = list(jobs)
                while pending or d.runs:
                    now = time.time()
                    if stop_reason is None:
                        if returned_error is not None or d.fatal or d.dead_workers:
                            stop_reason = "operational_error"
                        elif (self.out / "STOP_NEW_ADMISSIONS").exists():
                            stop_reason = "stop_file"
                        elif now >= hard_stop:
                            stop_reason = "cell_admission_hard_stop"
                    if stop_reason is None:
                        for w in range(len(self.workers)):
                            while pending and w not in d.dead_workers and d.slots_used(w) < self.m.get("slots_per_worker", 16):
                                run_id, seed = pending.pop(0)
                                d.admit(run_id, "validation", seed, seed_string("validation", seed), "validation",
                                        worker=w, policy_seed=index[seed])
                    if not d.runs:
                        break
                    if now >= final:  # bounded drain: never run past the absolute deadline
                        drain_cutoff = True
                        break
                    d.advance()
                    for run in d.finished():
                        d.release(run.run_id)
                        finished(run)
                    if time.monotonic() - last >= 5:
                        progress("draining" if stop_reason else "running")
                        self.res.tick(self.workers)
                        last = time.monotonic()
        finally:
            admissions = core.parse_jsonl(admissions_path)
            no_grad = all(x.grad is None and not x.requires_grad for mdl in (strategy.model, battle.model)
                          for x in mdl.parameters())
            summary = {**identity, **core.summarize_cell(records, admissions, seeds),
                       "stop_reason": stop_reason, "drain_cutoff": drain_cutoff,
                       "fatal": d.fatal or returned_error, "dead_workers": list(d.dead_workers),
                       "worker_failures": d.worker_failures, "audit_counts": dict(counts),
                       "boss_bill_checks": dict(battle.bill_check_counts), "eval_mode": not strategy.model.training and not battle.model.training,
                       "no_gradients": no_grad, "elapsed_s": time.monotonic() - start, "finished_utc": core.now_iso(),
                       "device": "cpu", "torch_threads": 1, "native_workers": len(self.workers),
                       "slots_per_worker": self.m.get("slots_per_worker", 16), "transport": EVALUATION_TRANSPORT,
                       "dispatcher_timing": d.timing_summary()}
            failed = bool(d.fatal or returned_error or d.dead_workers or not no_grad or not summary["eval_mode"])
            summary["state"] = ("failure" if failed else "cutoff" if drain_cutoff else
                                "complete" if summary["complete"] else
                                "diagnostic_complete" if admit_limit and summary["observed"] == admit_limit else "partial")
            write(summary_path, summary)
            progress(summary["state"])
        if summary["state"] in ("failure", "cutoff"):
            summary["closed_workers"] = self.close_workers(summary["state"], kill_all=drain_cutoff,
                                                           failed=tuple(d.dead_workers))
            write(summary_path, summary)
        del d
        gc.collect()
        print(json.dumps({k: summary[k] for k in ("cell_id", "state", "observed", "run_victories", "undefined",
                                                  "errors", "elapsed_s")}), flush=True)
        return summary

    def close(self):
        errors = []
        try:
            if self.workers:
                self.close_workers("batch_exit")
        except BaseException as exc:
            errors.append(f"run_workers: {exc}")
        try:
            if self.ctx is not None:
                self.ctx.close()
        except BaseException as exc:
            errors.append(f"context: {exc}")
        if errors:
            raise RuntimeError(f"evaluation cleanup failed: {errors}")
