"""Boss-expansion v1 contracts: tiered reward context, curriculum schedule and recorded demonstrations.

Everything here is opt-in; the legacy trainer, Context and checkpoints are unchanged.

* :class:`ExpansionContext` is a :class:`crl.env.Context` whose provenance ``reward`` entry is
  ``data_preparation.reward_v2.TieredReward(loss_lambda).describe()``. The engine still
  produces the legacy ``crl.reward/preheal`` episode metrics (kept under
  ``legacy_engine_metrics``); :meth:`ExpansionContext.relabel` replaces the terminal training
  reward of every win/loss episode with the tiered value BEFORE replay insertion, and
  :meth:`ExpansionContext.tiered_reward` scores validation/evaluation episodes with the same
  function. Because the provenance reward digest differs, a legacy checkpoint refuses to load
  into an ExpansionContext and a new checkpoint refuses to load into a legacy Context (and into
  an ExpansionContext with another lambda or another reward_v2 implementation).
* :func:`load_expansion_inputs` pins the prepared dataset (index, templates, splits, curriculum)
  by hash and checks the schedule against template metadata and the source-run split.
* :class:`DemoCache` is a disposable indexed zlib record cache of the 50,000 recorded episodes
  (one sequential pass over the finalized shards, each shard and store manifest hash-checked),
  so schedule-ordered admission is a seek + decompress instead of rescanning shards.
* :func:`demo_episode` turns one recorded episode into a replayable :class:`crl.env.Episode`
  with tiered terminal reward, checking it against the running build/environment.

Recorded episodes are a SELECTED distribution (boss wins and elite outcomes were deliberately
collected); replay weights correct only to the store's actual outcome shares, never to natural
game outcome probabilities.
"""

from __future__ import annotations

import gzip
import io
import json
import math
import os
import shutil
import zlib
from collections import Counter
from dataclasses import asdict, dataclass
from pathlib import Path

from . import paths
from .env import Context, EnvConfig, Episode, Transition
from .reward import episode_metrics
from .three_act import ROOM_CLASS, template_hash

from data_preparation.reward_v2 import REWARD_ID, TieredReward

DATASET_ROOT = paths.DATA / "boss_expansion_v1"
FROZEN_214_BUNDLE = paths.DATA / "frozen_three_act_214_inputs"
EXPERIMENT_SPEC = paths.ROOT / "experiments" / "curricula" / "boss-expansion-v1.json"
CACHE_SCHEMA = "crl.expansion_demo_cache/v1"
REWARD_CONTRACT_SCHEMA = "crl.expansion_reward_contract/v1"
EXPANSION_SCOPE = "three-act-214-v1"
PHASE1_EPISODES, PHASE1_ROOMS = 10_000, {"normal": 9000, "elite": 1000}
PHASE2_EPISODES = 40_000
WARMUP_ROOMS = frozenset(PHASE1_ROOMS)
STORABLE = ("win", "loss", "truncated")


def production_env_config() -> EnvConfig:
    """The baseline environment (legacy preheal metrics = the tiered HP boundary)."""
    return EnvConfig(max_steps=2000, max_turns=100, rng_mode="training", reward_variant="preheal",
                     purity_check=False, effect_scope=EXPANSION_SCOPE)


# ---------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------

def require_input_bundle():
    """The frozen 214-card selection, after checking STS2_RL_INPUT_BUNDLE was set before import."""
    if paths.INPUT_BUNDLE is None or paths.INPUT_BUNDLE != FROZEN_214_BUNDLE.resolve():
        raise RuntimeError(f"STS2_RL_INPUT_BUNDLE must point to {FROZEN_214_BUNDLE} before crl is imported "
                           f"(got {paths.INPUT_BUNDLE})")
    from .three_act_214 import load_three_act_214_inputs
    sel = load_three_act_214_inputs(paths.INPUT_BUNDLE)
    if paths.sha256_file(paths.CATALOG) != sel.audit["output_sha256"]["catalog.json"]:
        raise ValueError("active catalog differs from the frozen 214-card inputs")
    return sel


def template_class(t: dict) -> tuple[int, str]:
    """(act 1..3, room normal/elite/boss/event) exactly as the curriculum classified it."""
    s = t["source"]
    elite = bool(s.get("is_elite_room"))
    if elite != bool(t["engine_input"].get("IsEliteRoom")):
        raise ValueError(f"{t['template_id']}: source/engine elite flags disagree")
    room = "elite" if elite else ROOM_CLASS[s["room_type"]]
    return s["act_index"] + 1, room


def _read_gz_jsonl(path: Path) -> list[dict]:
    with gzip.open(path, "rt", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def _check_sha(path: Path, expected: str, what: str) -> str:
    got = paths.sha256_file(path)
    if got != expected:
        raise ValueError(f"{what} {path} changed: sha256 {got}, expected {expected}")
    return got


@dataclass
class ExpansionInputs:
    root: Path
    index: dict
    splits: dict
    curriculum: dict
    templates: dict[str, dict]  # all 596 boss-swap TRAIN template variants
    classes: dict[str, tuple[int, str]]
    order: list[dict]  # 50,000 schedule rows in saved order
    hashes: dict

    def schedule(self, phase1_limit: int = 0, phase2_limit: int = 0) -> tuple[list[dict], list[dict]]:
        """(phase 1 rows, phase 2 rows) in saved order; a positive limit takes that prefix (smoke only)."""
        p1 = [r for r in self.order if r["phase"] == "warmup"]
        p2 = [r for r in self.order if r["phase"] == "full"]
        return (p1[:phase1_limit] if phase1_limit > 0 else p1), (p2[:phase2_limit] if phase2_limit > 0 else p2)


def load_expansion_inputs(root: Path = DATASET_ROOT, selection=None) -> ExpansionInputs:
    """Pinned dataset inputs; every check failure is an exception."""
    root = Path(root)
    index = paths.read_json(root / "dataset_index.json")
    if index.get("schema") != "crl.dataset_index/v1" or index.get("episodes") != 50_000:
        raise ValueError("unexpected dataset index")
    hashes = {"dataset_index.json": paths.sha256_file(root / "dataset_index.json")}
    for key, rel in (("inputs_sha256", index["inputs"]), ("splits_sha256", index["splits"]),
                     ("curriculum_sha256", index["curriculum"]), ("training_order_sha256", index["training_order"])):
        hashes[rel] = _check_sha(root / rel, index[key], key)
    curriculum = paths.read_json(root / index["curriculum"])
    if curriculum.get("order_sha256") != index["training_order_sha256"]:
        raise ValueError("curriculum config and dataset index disagree on the training order")
    if curriculum.get("reward_module") != "data_preparation.reward_v2.TieredReward":
        raise ValueError("curriculum reward module changed")
    want = TieredReward(index["reward"]["loss_lambda"]).describe()
    if index["reward"] != want:
        raise ValueError("stored dataset reward labels were made by another reward_v2 contract/implementation")
    if EXPERIMENT_SPEC.exists():
        hashes["experiments/curricula/boss-expansion-v1.json"] = paths.sha256_file(EXPERIMENT_SPEC)

    templates: dict[str, dict] = {}
    for t in _read_gz_jsonl(root / index["inputs"]):
        if t["template_id"] in templates or template_hash(t["engine_input"]) != t["template_sha256"]:
            raise ValueError(f"duplicate or changed template {t['template_id']}")
        templates[t["template_id"]] = t
    classes = {tid: template_class(t) for tid, t in templates.items()}

    splits = paths.read_json(root / index["splits"])
    train_runs = set(splits["train"]["source_runs"])
    held_out = set(splits["val"]["template_ids"]) | set(splits["test"]["template_ids"])
    held_runs = set(splits["val"]["source_runs"]) | set(splits["test"]["source_runs"])
    if train_runs & held_runs:
        raise ValueError("train and held-out source runs overlap")
    for name in ("val", "test"):
        hashes[splits[name]["templates"]] = _check_sha(root / splits[name]["templates"],
                                                       splits[name]["templates_sha256"], f"{name} templates")
    if selection is not None:
        for name in ("val", "test"):
            ev = _read_gz_jsonl(root / splits[name]["templates"])
            # Same records in the same order as the frozen selection (the baseline's validation
            # order); splits.json lists the same IDs in another order.
            if ev != selection.templates_for(name) or sorted(t["template_id"] for t in ev) != sorted(splits[name]["template_ids"]):
                raise ValueError(f"{name} evaluation inputs differ from the frozen 214-card {name} split")

    order = _read_gz_jsonl(root / index["training_order"])
    if [r["training_episode"] for r in order] != list(range(1, len(order) + 1)) or len(order) != 50_000:
        raise ValueError("training order is not episodes 1..50000")
    if len({r["episode_id"] for r in order}) != len(order):
        raise ValueError("training order repeats a recorded episode")
    for r in order:
        tid = r["template_id"]
        if tid not in templates or tid in held_out:
            raise ValueError(f"schedule row {r['training_episode']}: template {tid} missing or held out")
        if templates[tid]["source"]["run_id"] not in train_runs:
            raise ValueError(f"schedule row {r['training_episode']}: source run is not a TRAIN run")
        if classes[tid] != (r["act"], r["room_type"]):
            raise ValueError(f"schedule row {r['training_episode']}: class {classes[tid]} != order "
                             f"({r['act']}, {r['room_type']})")
        if r["phase"] != ("warmup" if r["training_episode"] <= PHASE1_EPISODES else "full"):
            raise ValueError(f"schedule row {r['training_episode']}: phase {r['phase']}")
    p1 = Counter(r["room_type"] for r in order[:PHASE1_EPISODES])
    if dict(p1) != PHASE1_ROOMS:
        raise ValueError(f"warm-up rooms {dict(p1)} != {PHASE1_ROOMS}")
    if dict(Counter(r["room_type"] for r in order[PHASE1_EPISODES:])) != curriculum["phase_2"]["room_counts"]:
        raise ValueError("phase-2 rooms differ from the curriculum config")
    return ExpansionInputs(root, index, splits, curriculum, templates, classes, order, hashes)


# ---------------------------------------------------------------------------
# Context with the tiered reward
# ---------------------------------------------------------------------------

class ExpansionContext(Context):
    """Context reporting and training on ``TieredReward(loss_lambda)``; legacy metrics kept separately."""

    def __init__(self, templates, cfg: EnvConfig | None = None, *, loss_lambda: float = 1.0,
                 classes: dict[str, tuple[int, str]] | None = None, catalog: dict | None = None):
        templates = list(templates)
        cfg = cfg or production_env_config()
        if cfg.reward_variant != "preheal":
            raise ValueError("the tiered HP ratio uses the preheal terminal boundary; reward_variant must be preheal")
        self.tiered = TieredReward(loss_lambda)
        self.template_class = {t["template_id"]: (classes or {}).get(t["template_id"]) or template_class(t)
                               for t in templates}
        super().__init__(templates, cfg, catalog)

    @property
    def loss_lambda(self) -> float:
        return self.tiered.loss_lambda

    def reward_contract(self) -> dict:
        return {
            "schema": REWARD_CONTRACT_SCHEMA,
            "training_reward": REWARD_ID,
            "loss_lambda": float(self.tiered.loss_lambda),
            "x": "pre-postcombat-heal terminal HP / combat-entry HP, unclipped",
            "relabel": "every terminal win/loss transition relabelled before learner replay insertion; "
                       "truncation stays 0 and bootstrapped",
            "validation_score": "mean TieredReward over validation episodes (same function)",
            "legacy_metrics": "crl.reward/preheal reward_preheal and HP metrics, reported separately, not trained on",
        }

    def provenance(self) -> dict:
        out = super().provenance()
        out["legacy_engine_metrics"] = out["reward"]
        out["reward"] = self.tiered.describe()
        out["reward_contract"] = self.reward_contract()
        return out

    def classify(self, template_id: str) -> tuple[int, str]:
        cls = self.template_class.get(template_id)
        if cls is None:
            raise ValueError(f"no act/room class for template {template_id}")
        return cls

    def tiered_reward(self, ep: Episode) -> float | None:
        """Tiered terminal value of a finished episode (0 for truncation, None for failed episodes)."""
        if ep.status == "truncated":
            return 0.0
        if ep.status not in ("win", "loss"):
            return None
        act, room = self.classify(ep.template_id)
        m = ep.metrics
        return self.tiered.terminal_reward(ep.status, act=act, room_type=room, entry_hp=m.entry_hp,
                                           hp_terminal=m.hp_terminal, max_hp_terminal=m.max_hp_terminal)

    def relabel(self, ep: Episode) -> float | None:
        """Replace the legacy terminal reward of ``ep``'s transitions with the tiered reward."""
        r = self.tiered_reward(ep)
        if r is None:
            return None
        trans = ep.transitions
        for i, t in enumerate(trans):
            last_terminal = i == len(trans) - 1 and ep.status in ("win", "loss")
            if t.done != last_terminal:
                raise ValueError(f"{ep.template_id}/{ep.env_seed}: transition {i} done={t.done}")
            if not last_terminal and t.reward != 0.0:
                raise ValueError(f"{ep.template_id}/{ep.env_seed}: non-terminal reward {t.reward}")
        if trans and ep.status in ("win", "loss"):
            trans[-1].reward = r
        return r


def load_expansion_checkpoint(path: Path, ctx: ExpansionContext, *, device: str = "cpu"):
    """Strict load of a tiered-reward checkpoint into ``ctx`` (exact reward, lambda, build, vocab)."""
    from .checkpoint import load
    if not isinstance(ctx, ExpansionContext):
        raise TypeError("tiered checkpoints load only into an ExpansionContext")
    model, blob = load(Path(path), ctx, device=device)
    contract = blob["meta"].get("reward_contract")
    if contract != ctx.reward_contract():
        raise ValueError(f"{path}: checkpoint reward contract {contract} != running {ctx.reward_contract()}")
    return model, blob


def checkpoint_reward(path: Path) -> dict:
    """The provenance reward entry of a checkpoint (to choose the legacy or expansion context)."""
    import torch
    return torch.load(Path(path), map_location="cpu", weights_only=False)["provenance"]["reward"]


# ---------------------------------------------------------------------------
# Summaries carrying both objectives
# ---------------------------------------------------------------------------

def summarize_expansion(eps: list[Episode], ctx: ExpansionContext, selection=None, seed: int = 0) -> dict:
    """Legacy summary (win rate, reward_preheal, HP, errors, truncations) plus the tiered objective."""
    from collections import defaultdict
    from .evaluate import summarize
    from .grouped_stats import summarize_groups
    from .stats import describe
    out = summarize(eps, seed)
    vals = [ctx.tiered_reward(e) for e in eps]
    scored = [(e, v) for e, v in zip(eps, vals) if v is not None]
    out["tiered_reward"] = {
        **describe([v for _, v in scored], seed, [e.template_id for e, _ in scored]),
        "reward_id": REWARD_ID, "loss_lambda": float(ctx.loss_lambda),
        "excluded_failed_episodes": len(eps) - len(scored),
    }
    by = defaultdict(list)
    for e, v in scored:
        act, room = ctx.classify(e.template_id)
        by[("room_type", room)].append(v)
        by[("act_room_type", f"{act}/{room}")].append(v)
    out["tiered_groups"] = {}
    for (cat, key), v in sorted(by.items()):
        out["tiered_groups"].setdefault(cat, {})[key] = {"episodes": len(v), "tiered_mean": float(sum(v) / len(v))}
    if selection is not None:
        out["groups"] = summarize_groups(eps, selection)
    return out


# ---------------------------------------------------------------------------
# Demonstration record cache
# ---------------------------------------------------------------------------

class DemoCache:
    """``records.bin`` (concatenated zlib-compressed episode JSON) + ``index.json`` (id -> offset, length)."""

    def __init__(self, cache_dir: Path):
        self.dir = Path(cache_dir)
        self.manifest = paths.read_json(self.dir / "manifest.json")
        if self.manifest.get("schema") != CACHE_SCHEMA:
            raise ValueError(f"{cache_dir}: not a {CACHE_SCHEMA} cache")
        self.index: dict[str, list[int]] = paths.read_json(self.dir / "index.json")
        self._f = open(self.dir / "records.bin", "rb")

    def __contains__(self, episode_id: str) -> bool:
        return episode_id in self.index

    def get(self, episode_id: str) -> dict:
        off, n = self.index[episode_id]
        self._f.seek(off)
        return json.loads(zlib.decompress(self._f.read(n)))

    def close(self) -> None:
        self._f.close()

    @staticmethod
    def expected_manifest(inputs: ExpansionInputs) -> dict:
        return {"schema": CACHE_SCHEMA, "dataset_id": inputs.index["dataset_id"],
                "dataset_index_sha256": inputs.hashes["dataset_index.json"],
                "stores": [[s["path"], s["manifest_sha256"], s["episodes"]] for s in inputs.index["stores"]]}

    @classmethod
    def open_or_build(cls, cache_dir: Path, inputs: ExpansionInputs, log=print) -> "DemoCache":
        cache_dir = Path(cache_dir)
        want = cls.expected_manifest(inputs)
        if (cache_dir / "manifest.json").exists():
            have = paths.read_json(cache_dir / "manifest.json")
            if {k: have.get(k) for k in want} != want:
                raise ValueError(f"{cache_dir} was built from other dataset inputs; use a new --cache-dir")
            if paths.sha256_file(cache_dir / "records.bin") != have["records_sha256"]:
                raise ValueError(f"{cache_dir}/records.bin changed; delete the disposable cache and rebuild")
            log(f"demo cache: reusing {cache_dir} ({have['episodes']} episodes)")
            return cls(cache_dir)
        if cache_dir.exists() and any(cache_dir.iterdir()):
            raise FileExistsError(f"{cache_dir} exists without a cache manifest")
        build_demo_cache(cache_dir, inputs, log=log)
        return cls(cache_dir)


def build_demo_cache(cache_dir: Path, inputs: ExpansionInputs, log=print) -> dict:
    """One pass over every finalized shard (hash-checked); the prepared dataset is only read."""
    import hashlib
    tmp = cache_dir.with_name(cache_dir.name + ".building")
    if tmp.exists():
        shutil.rmtree(tmp)
    tmp.mkdir(parents=True)
    index: dict[str, list[int]] = {}
    h = hashlib.sha256()
    off = 0
    with open(tmp / "records.bin", "wb") as out:
        for k, store in enumerate(inputs.index["stores"]):
            sdir = inputs.root / store["path"]
            _check_sha(sdir / "manifest.json", store["manifest_sha256"], "store manifest")
            manifest = paths.read_json(sdir / "manifest.json")
            n_store = 0
            for shard in manifest["shards"]:
                blob = (sdir / shard["filename"]).read_bytes()
                if hashlib.sha256(blob).hexdigest() != shard["sha256"]:
                    raise ValueError(f"shard {sdir / shard['filename']} hash mismatch")
                n_shard = 0
                with gzip.GzipFile(fileobj=io.BytesIO(blob)) as g:
                    for line in g:
                        if not line.strip():
                            continue
                        ep = json.loads(line)
                        eid = ep["episode_id"]
                        if eid in index:
                            raise ValueError(f"duplicate recorded episode {eid}")
                        rec = zlib.compress(json.dumps(ep, separators=(",", ":"), ensure_ascii=False).encode(), 6)
                        out.write(rec)
                        h.update(rec)
                        index[eid] = [off, len(rec)]
                        off += len(rec)
                        n_shard += 1
                if n_shard != shard["episode_count"]:
                    raise ValueError(f"{sdir / shard['filename']}: {n_shard} episodes, manifest {shard['episode_count']}")
                n_store += n_shard
            if n_store != store["episodes"]:
                raise ValueError(f"{sdir}: {n_store} episodes, index says {store['episodes']}")
            if k % 50 == 0:
                log(f"demo cache: store {k + 1}/{len(inputs.index['stores'])}, {len(index)} episodes")
    if len(index) != inputs.index["episodes"]:
        raise ValueError(f"cache holds {len(index)} episodes, index says {inputs.index['episodes']}")
    missing = [r["episode_id"] for r in inputs.order if r["episode_id"] not in index]
    if missing:
        raise ValueError(f"{len(missing)} scheduled episodes are missing from the stores, e.g. {missing[:3]}")
    paths.write_json(tmp / "index.json", index, indent=None)
    manifest = {**DemoCache.expected_manifest(inputs), "episodes": len(index), "records_bytes": off,
                "records_sha256": h.hexdigest(), "note": "disposable derived cache; the prepared dataset is unchanged"}
    paths.write_json(tmp / "manifest.json", manifest)
    if cache_dir.exists():
        cache_dir.rmdir()  # empty (checked by the caller)
    os.replace(tmp, cache_dir)
    log(f"demo cache: built {cache_dir} ({len(index)} episodes, {off / 2 ** 20:.1f} MiB)")
    return manifest


# ---------------------------------------------------------------------------
# Recorded episode -> replayable Episode
# ---------------------------------------------------------------------------

class DemoMismatch(ValueError):
    """A recorded episode does not mean what the running environment means."""


def demo_episode(rec: dict, row: dict, ctx: ExpansionContext, *, template_sha256: str,
                 build_digest: str) -> tuple[Episode, float]:
    """(Episode with encoded transitions and tiered terminal reward, tiered value) for one schedule row."""
    eid = rec.get("episode_id")
    where = f"recorded episode {eid} (schedule {row['training_episode']})"
    for k_rec, k_row in (("episode_id", "episode_id"), ("template_id", "template_id"),
                         ("env_seed", "env_seed"), ("status", "outcome"), ("component", "component")):
        if rec.get(k_rec) != row[k_row]:
            raise DemoMismatch(f"{where}: {k_rec} {rec.get(k_rec)!r} != schedule {row[k_row]!r}")
    if rec["status"] not in STORABLE:
        raise DemoMismatch(f"{where}: status {rec['status']!r} is not trainable")
    if rec.get("env_config") != asdict(ctx.cfg):
        raise DemoMismatch(f"{where}: env_config {rec.get('env_config')} != running {asdict(ctx.cfg)}")
    if rec.get("provenance_digest") != build_digest:
        raise DemoMismatch(f"{where}: recorded worker build digest differs from the running build")
    if rec.get("template_sha256") != template_sha256:
        raise DemoMismatch(f"{where}: template hash differs")
    act, room = ctx.classify(rec["template_id"])
    if (act, room) != (row["act"], row["room_type"]):
        raise DemoMismatch(f"{where}: class mismatch")

    status, entry_hp = rec["status"], rec["entry_hp"]
    if status == "truncated":
        value = 0.0
    else:
        value = ctx.tiered.terminal_reward(status, act=act, room_type=room, entry_hp=entry_hp,
                                           hp_terminal=rec["hp_terminal"], max_hp_terminal=rec.get("max_hp_terminal"))
        comp = rec["reward_components"]
        stored = comp["reward_base"] - ctx.tiered.loss_lambda * comp["loss_coefficient"]
        if not math.isclose(value, stored, rel_tol=1e-9, abs_tol=1e-12):
            raise DemoMismatch(f"{where}: recomputed tiered reward {value} != stored components {stored}")

    decisions = rec.get("decisions") or []
    obs = [ctx.adapter.encode(d["obs_raw"], entry_hp) for d in decisions]
    trans = []
    for i, (d, o) in enumerate(zip(decisions, obs)):
        if len(d["legal"]) != o.n_options or not 0 <= d["chosen"] < o.n_options:
            raise DemoMismatch(f"{where}: decision {i} action/legal options inconsistent with the encoder")
        last = i + 1 == len(decisions)
        if not last:
            nxt, done, dt = obs[i + 1], False, max(0, decisions[i + 1]["turn"] - d["turn"])
        else:
            done = status in ("win", "loss")
            dt = max(0, rec.get("terminal_turn", 0) - d["turn"])
            nxt = None if done else ctx.adapter.encode(rec["terminal_obs_raw"], entry_hp)
        trans.append(Transition(obs=o, action=int(d["chosen"]), reward=value if (last and done) else 0.0,
                                next_obs=nxt, done=done, delta_turn=dt))
    term = rec.get("terminal_obs_raw") or {}
    relics = [x[0] for x in term.get("relics", [])]
    metrics = episode_metrics(ctx.reward, status, entry_hp, rec["entry_max_hp"], rec["hp_terminal"],
                              rec["max_hp_terminal"], relics)
    ep = Episode(template_id=rec["template_id"], env_seed=rec["env_seed"], status=status, reason="recorded",
                 error=None, metrics=metrics, steps=rec["steps"], turns=rec["turns"], transitions=trans)
    return ep, value
