"""Outcome-stratified replay.

Only ``win``, ``loss`` and ``truncated`` episodes are stored; their transitions are grouped
by that outcome into the strata ``win``, ``loss`` and ``other`` (= truncated; bootstrapped,
never terminal). ``error``, ``unsupported`` and ``terminal_other`` episodes are rejected and
counted in ``rejected`` -- they have no defined terminal value, so storing them would teach
"the fight ended, worth 0". An accepted episode whose transitions are malformed (terminal flag
in the wrong place, missing next state, action outside the legal options) raises
:class:`InvalidEpisode`.

A batch of ``n`` rows is drawn stratum-first. The target stratum probabilities ``p`` are: in
``balanced`` mode, win and loss each ``(1 - q_other) / 2`` (``q_other`` = natural transition
share of ``other``), in ``natural`` mode ``p = q``. Integer row counts come from systematic
randomized rounding of ``n * p`` (``E[counts_k] = n * p_k`` exactly, ``|counts_k - n p_k| < 1``),
so an even balanced win/loss batch is always exactly half/half, an odd one gives the extra
row to win or loss with probability 1/2, and a rare natural stratum is not starved by
deterministic rounding. Rows within a stratum are uniform with replacement.

Importance correction (``is_correction="natural"``, default) targets the natural
transition-level mixture ``sum_k q_k E_k[loss]``, where ``q`` is the natural transition share
per stratum (measured from the store, or passed as ``natural_shares`` when the store is the
persisted balanced prefill set). Weights use the ACTUAL integer counts of the batch:

* ``n >= #eligible strata``: every eligible stratum is given at least one row (taken from the
  largest stratum), and ``w_k = q_k / (counts_k / n)``. Then ``mean(w * loss)`` is an exactly
  unbiased estimate of the natural loss for any realised counts, and ``sum(w) == n``.
* tiny batches (``n`` smaller than the number of eligible strata, e.g. ``n = 1``): rows are a
  randomized categorical draw and ``w_k = q_k / p_k`` (Horvitz-Thompson), unbiased in
  expectation; ``sum(w)`` then only equals ``n`` in expectation.

Weights are not clipped or normalized per source, so mixing a prefill batch of ``n_pre`` rows
with an online batch of ``n_on`` rows under ``mean(w * loss)`` gives the prefill exactly the
``n_pre / (n_pre + n_on)`` share of the weight (except for the tiny-batch case above). With
``is_correction="none"`` all weights are 1 and the learner trains on the sampled mixture
``p`` itself, which biases Q toward the over-sampled outcome.

The correction only fixes outcome MARGINALS: within a stratum the stored trajectories are what
was collected (outcome-conditional, finite sample). It cannot recreate trajectories that were
never stored -- e.g. the wins dropped from the balanced prefill set -- and it does not by
itself guarantee lower variance than natural sampling (the effective sample size is logged).
"""

from __future__ import annotations

import gzip
import pickle
from collections import Counter
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .env import Episode, Transition

STRATA = ("win", "loss", "other")
STATUS_STRATUM = {"win": "win", "loss": "loss", "truncated": "other"}
REJECTED_STATUSES = ("error", "unsupported", "terminal_other")


class InvalidEpisode(ValueError):
    """An accepted-status episode whose transitions cannot be trained on."""


def validate_episode(ep: Episode) -> None:
    """Raise :class:`InvalidEpisode` unless ``ep`` is a well-formed win/loss/truncated episode."""
    if ep.status not in STATUS_STRATUM:
        raise InvalidEpisode(f"status {ep.status!r} is not storable")
    last = len(ep.transitions) - 1
    for i, t in enumerate(ep.transitions):
        where = f"{ep.template_id} seed {ep.env_seed} ({ep.status}) transition {i}"
        if t.obs is None or t.obs.n_options < 1:
            raise InvalidEpisode(f"{where}: state has no legal options")
        if not 0 <= t.action < t.obs.n_options:
            raise InvalidEpisode(f"{where}: action {t.action} outside {t.obs.n_options} options")
        adm = getattr(t.obs, "opt_admissible", None)
        if adm is not None and not adm[t.action]:
            raise InvalidEpisode(f"{where}: action {t.action} is legal but policy-inadmissible")
        nxt = getattr(t.next_obs, "opt_admissible", None) if t.next_obs is not None else None
        if nxt is not None and not nxt.any():
            raise InvalidEpisode(f"{where}: next state has no admissible option to bootstrap from")
        terminal_here = i == last and ep.status in ("win", "loss")
        if t.done != terminal_here:
            raise InvalidEpisode(f"{where}: done={t.done}, expected {terminal_here}")
        if t.done:
            if t.next_obs is not None:
                raise InvalidEpisode(f"{where}: terminal transition carries a next state")
        elif t.next_obs is None or t.next_obs.n_options < 1:
            raise InvalidEpisode(f"{where}: bootstrapped transition has no next state with legal options")
        if not np.isfinite(t.reward):
            raise InvalidEpisode(f"{where}: non-finite reward {t.reward}")


def systematic_round(m: np.ndarray, n: int, rng: np.random.Generator) -> np.ndarray:
    """Integer counts summing to ``n`` with ``E[c_k] = m_k`` and ``floor(m_k) <= c_k <= ceil(m_k)``
    (``m`` sums to ``n``). Systematic sampling: one uniform offset, remainder units at unit spacing."""
    m = np.round(np.asarray(m, dtype=np.float64), 9)
    c = np.floor(m).astype(np.int64)
    r = int(n - c.sum())
    if r > 0:
        frac = np.clip(m - c, 0.0, 1.0)
        frac *= r / frac.sum()
        idx = np.searchsorted(np.cumsum(frac), rng.random() + np.arange(r), side="right")
        np.add.at(c, np.minimum(idx, len(c) - 1), 1)
    return c


class _Ring:
    def __init__(self) -> None:
        self.items: list[Transition] = []
        self.head = 0

    def __len__(self) -> int:
        return len(self.items) - self.head

    def extend(self, xs: list[Transition]) -> None:
        self.items.extend(xs)

    def popleft(self, n: int) -> None:
        self.head += n
        if self.head > 4096 and self.head * 2 > len(self.items):
            self.items = self.items[self.head:]
            self.head = 0

    def get(self, i: int) -> Transition:
        return self.items[self.head + i]


@dataclass
class Batch:
    transitions: list[Transition]
    weights: np.ndarray
    strata: np.ndarray  # index into STRATA per transition
    source: np.ndarray | None = None  # 0 = prefill, 1 = online (set by dqn.merge_batches)

    def counts(self) -> dict[str, int]:
        return {s: int((self.strata == i).sum()) for i, s in enumerate(STRATA)}


class StratifiedReplay:
    def __init__(self, capacity: int, mode: str = "balanced", is_correction: str = "natural",
                 natural_shares: dict[str, float] | None = None):
        """``natural_shares``: the natural transition share per stratum when the store itself is
        not a natural sample (the persisted balanced prefill set); else measured from the store."""
        if mode not in ("balanced", "natural"):
            raise ValueError("mode must be balanced or natural")
        if is_correction not in ("natural", "none"):
            raise ValueError("is_correction must be natural or none")
        self.capacity, self.mode, self.is_correction = capacity, mode, is_correction
        self.rings = {s: _Ring() for s in STRATA}
        self.order: list[tuple[str, int]] = []  # FIFO of (stratum, n transitions) per episode
        self.order_head = 0
        self.episodes_added = {s: 0 for s in STRATA}
        self.rejected: Counter = Counter()  # status (or "empty") -> episodes not stored
        self.natural_shares = natural_shares

    def __len__(self) -> int:
        return sum(len(r) for r in self.rings.values())

    def sizes(self) -> dict[str, int]:
        return {s: len(r) for s, r in self.rings.items()}

    def add_episode(self, ep: Episode) -> bool:
        """Store a win/loss/truncated episode; return False (and count it) for anything else."""
        if ep.status not in STATUS_STRATUM:
            self.rejected[ep.status] += 1
            return False
        if not ep.transitions:
            self.rejected["empty"] += 1
            return False
        validate_episode(ep)
        s = STATUS_STRATUM[ep.status]
        self.rings[s].extend(ep.transitions)
        self.order.append((s, len(ep.transitions)))
        self.episodes_added[s] += 1
        while len(self) > self.capacity and self.order_head < len(self.order):
            os_, n = self.order[self.order_head]
            self.rings[os_].popleft(n)
            self.order_head += 1
        return True

    def probabilities(self) -> tuple[np.ndarray, np.ndarray]:
        """(sampling p_k, natural q_k) over STRATA; both zero for empty strata."""
        n = np.array([len(self.rings[s]) for s in STRATA], dtype=np.float64)
        if self.natural_shares is not None:
            q = np.array([self.natural_shares.get(s, 0.0) if n[i] > 0 else 0.0 for i, s in enumerate(STRATA)])
            q = q / max(q.sum(), 1e-12)
        else:
            q = n / max(n.sum(), 1.0)
        if self.mode == "natural":
            return q.copy(), q
        p = np.zeros(3)
        p[2] = q[2]
        wl = [i for i in (0, 1) if n[i] > 0]
        for i in wl:
            p[i] = (1.0 - q[2]) / len(wl)
        if p.sum() == 0:
            return q.copy(), q
        return p / p.sum(), q

    def allocate(self, n: int, rng: np.random.Generator) -> tuple[np.ndarray, np.ndarray]:
        """(integer rows per stratum, per-row weight per stratum) for a batch of ``n``."""
        p, q = self.probabilities()
        sizes = np.array([len(self.rings[s]) for s in STRATA])
        eligible = (p > 0) & (sizes > 0)
        if n < 1 or not eligible.any():
            raise ValueError(f"cannot sample {n} rows from replay with sizes {self.sizes()}")
        counts = systematic_round(n * np.where(eligible, p, 0.0) / p[eligible].sum(), n, rng)
        if self.is_correction == "none":
            return counts, np.ones(3)
        if n >= eligible.sum():
            # Exact stratified estimator: every eligible stratum present, weights from actual counts.
            for k in np.flatnonzero(eligible & (counts == 0)):
                largest = np.flatnonzero(counts == counts.max())  # ties broken at random
                counts[int(rng.choice(largest))] -= 1
                counts[k] += 1
            w = np.where(counts > 0, q * n / np.maximum(counts, 1), 0.0)
        else:
            w = np.where(eligible, q / np.where(eligible, p, 1.0), 0.0)  # Horvitz-Thompson
        return counts, w

    def sample(self, n: int, rng: np.random.Generator) -> Batch:
        counts, wk = self.allocate(n, rng)
        trans, strata, w = [], [], []
        for k, s in enumerate(STRATA):
            if counts[k] == 0:
                continue
            ring = self.rings[s]
            idx = rng.integers(len(ring), size=int(counts[k]))
            trans.extend(ring.get(int(i)) for i in idx)
            strata.extend([k] * int(counts[k]))
            w.extend([wk[k]] * int(counts[k]))
        return Batch(trans, np.asarray(w, dtype=np.float32), np.asarray(strata, dtype=np.int64))


def save_episodes(path: Path, episodes: list[Episode]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(path, "wb", compresslevel=3) as f:
        pickle.dump(episodes, f, protocol=pickle.HIGHEST_PROTOCOL)


def load_episodes(path: Path) -> list[Episode]:
    with gzip.open(path, "rb") as f:
        return pickle.load(f)
