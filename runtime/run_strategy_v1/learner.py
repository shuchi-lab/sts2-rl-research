"""Original acting policy; learner, targets and update schedule omitted."""

from __future__ import annotations

import torch

from .config import ExplorationConfig

from .features import StrategyEncoder, StrategyObs, collate_strategy

from .model import NEG

class StrategyNumericError(RuntimeError):
    """A nonfinite Q / target / loss / gradient: the update is refused before any optimizer change."""

class EpsilonSchedule:
    def __init__(self, cfg: ExplorationConfig):
        self.cfg = cfg

    def value(self, resolved_attempts: int) -> float:
        c = self.cfg
        if resolved_attempts >= c.decay_attempts:
            return c.epsilon_end  # then hold
        f = max(0.0, resolved_attempts / c.decay_attempts)
        return c.epsilon_start + f * (c.epsilon_end - c.epsilon_start)

class StrategyPolicy:
    """Acting policy over displayed candidates. ``train``: uniform legal random until the gradient gate
    opens, then epsilon-greedy (epsilon by resolved training attempts); ``validation``: greedy;
    ``diagnostic``: uniform legal random (the nonlearning preflight)."""

    name = "rs1_strategy_policy/v1"

    def __init__(self, encoder: StrategyEncoder, model: torch.nn.Module, device, exploration: ExplorationConfig,
                 gate_open=lambda: False, resolved_attempts=lambda: 0):
        self.encoder, self.model, self.device = encoder, model, torch.device(device)
        self.eps = EpsilonSchedule(exploration)
        self.gate_open, self.resolved_attempts = gate_open, resolved_attempts
        self.forward_calls = 0

    def encode(self, obs):
        return self.encoder.encode(obs)

    @torch.no_grad()
    def q_values(self, sobs: list[StrategyObs]) -> torch.Tensor:
        self.model.eval()
        q = self.model(collate_strategy(sobs, self.device)).float().cpu()
        self.forward_calls += 1
        return q

    def act(self, items):
        out: list = [None] * len(items)
        need = []
        for k, (sobs, obs, rng, purpose) in enumerate(items):
            n = sobs.n_candidates
            if purpose == "diagnostic" or (purpose == "train" and not self.gate_open()):
                out[k] = (sobs.candidate_ids[int(rng.integers(n))], {"policy": "uniform_legal_random", "random": True})
            elif purpose == "train":
                eps = self.eps.value(self.resolved_attempts())
                if rng.random() < eps:
                    out[k] = (sobs.candidate_ids[int(rng.integers(n))],
                              {"policy": "epsilon_greedy", "random": True, "epsilon": eps})
                else:
                    need.append((k, {"policy": "epsilon_greedy", "random": False, "epsilon": eps}))
            elif purpose == "validation":
                need.append((k, {"policy": "greedy", "random": False}))
            else:
                raise ValueError(f"unknown purpose {purpose}")
        if need:
            try:
                q = list(self.q_values([items[k][0] for k, _ in need]))
            except Exception:  # isolate the failing item(s); random draws already made are not repeated
                q = []
                for k, _ in need:
                    try:
                        q.append(self.q_values([items[k][0]])[0])
                    except Exception as e:
                        q.append(e)
            for row, (k, info) in enumerate(need):
                sobs = items[k][0]
                if isinstance(q[row], Exception):
                    out[k] = (StrategyNumericError(f"{sobs.decision_id}: strategy forward failed: "
                                                   f"{type(q[row]).__name__}: {q[row]}"), {})
                    continue
                qr = q[row][:sobs.n_candidates]
                if not bool(torch.isfinite(qr).all()) or bool((qr <= NEG / 2).any()):
                    out[k] = (StrategyNumericError(f"{sobs.decision_id}: nonfinite strategy Q {qr.tolist()}"), {})
                    continue
                j = int(torch.argmax(qr).item())
                top = torch.topk(qr, min(2, len(qr))).values
                info = {**info, "q": float(qr[j]), "q_gap": float(top[0] - top[1]) if len(top) > 1 else None,
                        "ties": int((qr == qr[j]).sum().item())}
                out[k] = (sobs.candidate_ids[j], info)
        return out
