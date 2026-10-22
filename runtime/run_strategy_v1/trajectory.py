"""Whole-run strategy trajectories and n-decision gamma-1 returns (Task 6; n = 5 or 10 by learning profile).

A run's strategic transitions are buffered until its terminal classification is known. Only a
NATURAL completion (death, legitimate event nonvictory, run victory) yields replay rows; an
unsupported / error / engineering-limit run yields none at all -- no partial prefix and no
bootstrap from an undefined continuation. Windows never cross a terminal, and never mix runs.
"""
from __future__ import annotations

import math

from .contracts import ContractError, NStepTransition, RunOutcome, StrategyTransition


class RunTrajectory:
    def __init__(self, run_id: str, n_step: int = 5, gamma: float = 1.0):
        if n_step < 1:
            raise ValueError("n_step >= 1")
        self.run_id, self.n_step, self.gamma = run_id, n_step, gamma
        self.transitions: list[StrategyTransition] = []
        self.finished = False

    def append(self, t: StrategyTransition) -> None:
        if self.finished:
            raise ContractError(f"{self.run_id}: append after finish")
        if getattr(t.obs, "run_id", self.run_id) != self.run_id:
            raise ContractError(f"transition from run {t.obs.run_id} appended to {self.run_id}")
        if t.next_obs is not None and getattr(t.next_obs, "run_id", self.run_id) != self.run_id:
            raise ContractError("next observation from another run")
        if self.transitions:
            last = self.transitions[-1]
            if last.terminal:
                raise ContractError(f"{self.run_id}: transition after a terminal")
            if last.next_obs is not t.obs:
                raise ContractError(f"{self.run_id}: discontinuous trajectory (next_obs is not the next decision)")
        if t.terminal != (t.next_obs is None):
            raise ContractError("terminal iff next_obs is None")
        if not math.isfinite(t.reward):
            raise ContractError("nonfinite transition reward")
        self.transitions.append(t)

    def finish(self, outcome: RunOutcome) -> list[NStepTransition]:
        if self.finished:
            raise ContractError(f"{self.run_id}: finished twice")
        self.finished = True
        if outcome.run_id != self.run_id:
            raise ContractError("outcome of another run")
        if not outcome.natural:
            return []  # whole trajectory excluded from TD; raw evidence is kept elsewhere
        T = self.transitions
        if not T:
            return []
        if not T[-1].terminal:
            raise ContractError(f"{self.run_id}: natural outcome but the last transition is not terminal")
        rows = []
        for i in range(len(T)):
            ret, disc, k, terminal = 0.0, 1.0, 0, False
            while k < self.n_step and i + k < len(T):
                ret += disc * T[i + k].reward
                disc *= self.gamma
                terminal = T[i + k].terminal
                k += 1
                if terminal:
                    break
            nxt = None if terminal else T[i + k - 1].next_obs
            rows.append(NStepTransition(T[i].obs, T[i].candidate_id, ret, nxt, terminal, k, self.run_id, disc))
        return rows
