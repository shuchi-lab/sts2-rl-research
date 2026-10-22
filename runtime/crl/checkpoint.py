"""Strict model/context compatibility checks for inference."""

from __future__ import annotations

import json

from . import paths

CHECKPOINT_SCHEMA = "crl.checkpoint/v1"

BUILD_KEYS = (
    "worker_protocol", "engine_assembly_sha256", "cds_worker_assembly_sha256", "rl_worker_assembly_sha256",
    "worker_build_digest", "catalog_sha256", "vocab_digest", "feature_schema_digest",
)

DIGEST_KEYS = ("reward", "terminal_boundary")

ENV_INSTRUMENTATION_KEYS = ("purity_check",)

def provenance_problems(then: dict, now: dict, *, env: bool = False, seeds: bool = False) -> list[str]:
    """Names of meaning-bearing provenance entries that differ between ``then`` and ``now``.

    ``env``: also compare every ``env_config`` field except :data:`ENV_INSTRUMENTATION_KEYS`
    (fail closed: a new EnvConfig field is compared until declared instrumentation).
    ``seeds``: also compare the worker's seed derivation."""
    problems = []
    for k in BUILD_KEYS:
        if k not in then or then.get(k) != now.get(k):
            problems.append(k)
    for k in DIGEST_KEYS + (("seed_derivation",) if seeds else ()):
        if k not in then or _json_digest(then.get(k)) != _json_digest(now.get(k)):
            problems.append(k)
    if env:
        a, b = then.get("env_config") or {}, now.get("env_config") or {}
        for k in sorted(set(a) | set(b)):
            if k not in ENV_INSTRUMENTATION_KEYS and (k not in a or k not in b or a[k] != b[k]):
                problems.append(f"env_config.{k}")
    return problems

def _json_digest(x) -> str:
    """Digest of ``x`` as it reads back from JSON (int dict keys become strings, tuples lists),
    so in-memory and persisted provenance compare equal."""
    return paths.digest(json.loads(paths.canonical(x)))

def check_compatible(blob: dict, ctx) -> list[str]:
    problems = provenance_problems(blob["provenance"], ctx.provenance())
    if blob["vocab"]["digest"] != ctx.vocab.describe()["digest"]:
        problems.append("vocab digest")
    return problems
