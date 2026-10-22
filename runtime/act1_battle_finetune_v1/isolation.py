"""Guarded battle contexts and the explicit guard-scope checks (integration of ``battle_guard``).

Every crl Context this package builds is guarded. That covers fine-tuning online fights, validation rounds,
the original-contract reproduction, robustness, heldout, the disposable benchmark replay, fidelity resets,
and the vocab-only contexts of the full-run stages (which play no isolated battle). The flow is:

1. the ORIGINAL Context constructor runs unchanged and starts the original worker; that worker plays no game;
2. :func:`guarded` installs the certified GuardedBattleWorker facade through
   ``battle_guard.install_guarded_worker`` (pinned certificate sha256) before any reset or gameplay; the
   original startup-only worker is closed with ``quit`` and must exit 0;
3. :func:`require` is the runtime gate before every rollout, validation round, save and load.

``crl.checkpoint.provenance_problems`` ignores arbitrary new provenance keys. So the guard identity is
NOT added to provenance (``ctx.provenance()`` stays canonically identical, which install checks). It is a
separate record, :data:`RECORD_KEY`, checked explicitly here on every transfer, load, save and launch
against the pinned identity and the narrow reviewed :data:`SCOPE`. ``allow_mismatch`` is never used. The
original hello fields keep naming the original assemblies, so the old worker is never impersonated.
"""
from __future__ import annotations

import contextlib
import json
import platform
from pathlib import Path

from . import battle_guard as bg
from . import pins

# Pins of the guard build that root reviews and deploys (BATTLE_GUARD_RESULT.json integration_pins).
CERTIFICATE_SHA256 = "aed708b66bdd06f1f28668f9560b938f38c539bd62fc1fd728b9e61a18541bee"
FACADE_SHA256 = "556878cadf9461b30695eabecdeb82da10c8f2ad0a05ae4773638619a5dba3e4"
BATTLE_GUARD_PY_SHA256 = "6b6d2793b90e44fe5d0647851b4971bef7dd0e235b1cd673a01470fc763b4a21"
RECORD_KEY = "act1ft_battle_guard"
RECORD_SCHEMA = "act1ft/battle_guard_record/v1"
SCOPE_SCHEMA = "act1ft/battle_guard_scope/v1"

# The narrow reviewed scope certificate: what the guard may change and what it must not.
SCOPE = {
    "schema": SCOPE_SCHEMA,
    "guard_certificate_sha256": CERTIFICATE_SHA256,
    "facade_assembly_sha256": FACADE_SHA256,
    "battle_guard_module_sha256": BATTLE_GUARD_PY_SHA256,
    "guard_source_sha256": bg.REVIEWED_GUARD_SHA256,
    "reviewed_guard_certificate_sha256": bg.REVIEWED_GUARD_CERTIFICATE_SHA256,
    "reflection_contract_sha256": bg.contract_sha256(),
    "original_dependencies_sha256": dict(sorted(bg.ORIGINAL_DEPENDENCY_SHA256.items())),
    "refusal_contract": bg.REFUSAL_CONTRACT,
    "guarded_commands": ["step", "step_many"],
    "effect": "a chosen legal option matching reviewed class A/B is refused BEFORE native execution (no engine call, "
              "no RNG, no step counter); the session closes unsupported with reason "
              f"{bg.REFUSAL_REASON}; every other request is delegated to the original Session.Step unchanged",
    "unchanged": ["rules", "rng", "legal masks", "chosen actions", "observations", "features", "roster", "engine",
                  "original hello fields", "ctx.provenance()"],
    "episode_rule": "a refused episode is unsupported: the whole episode is never stored (zero replay prefix) "
                    "and is counted separately as guard_refusals",
    "applies_to": ["isolated battle contexts of every act1ft stage", "vocab-only contexts of full-run stages"],
    "full_run_battles": "played inside the owned FullRunWorker with its own byte-identical AutoplayLoopGuard copy",
}


class GuardScopeError(RuntimeError):
    pass


def scope_sha256() -> str:
    return pins.sha256_bytes(json.dumps(SCOPE, sort_keys=True, separators=(",", ":")).encode())


def static_identity() -> dict:
    """The identity every guard record must carry (compared field by field)."""
    return {"certificate_sha256": CERTIFICATE_SHA256, "facade_assembly_sha256": FACADE_SHA256,
            "battle_guard_module_sha256": BATTLE_GUARD_PY_SHA256, "guard_source_sha256": bg.REVIEWED_GUARD_SHA256,
            "reflection_contract_sha256": bg.contract_sha256(), "scope_sha256": scope_sha256()}


def verify_static(certificate_path: Path | str = bg.CERTIFICATE, build_out: Path | str | None = None) -> dict:
    """Launch check (no process): the deployed certificate, facade bytes and battle_guard.py are the pinned ones."""
    got = pins.sha256_file(bg.__file__)
    if got != BATTLE_GUARD_PY_SHA256:
        raise GuardScopeError(f"battle_guard.py sha256 {got} != pinned {BATTLE_GUARD_PY_SHA256}")
    cert = bg.verify_certificate(certificate_path, CERTIFICATE_SHA256, build_out)
    if cert["facade_assembly_sha256"] != FACADE_SHA256:
        raise GuardScopeError(f"certified facade {cert['facade_assembly_sha256']} != pinned {FACADE_SHA256}")
    return {**static_identity(), "certificate_path": cert["_path"], "build_out": cert["_build_out"],
            "build_files": cert["build_files"]}


def check_record(rec, *, what: str) -> dict:
    """An explicit guard record (checkpoint, manifest) must name exactly the pinned guard and scope."""
    if not isinstance(rec, dict) or rec.get("schema") != RECORD_SCHEMA:
        raise GuardScopeError(f"{what}: no {RECORD_SCHEMA} guard record (unguarded or foreign artifact)")
    want = static_identity()
    bad = sorted(k for k, v in want.items() if rec.get(k) != v)
    if bad or rec.get("scope") != SCOPE:
        raise GuardScopeError(f"{what}: guard record differs from the pinned guard/scope: {bad or ['scope']}")
    return rec


def require(ctx) -> dict:
    """Runtime gate: ``ctx`` runs the live certified facade with the pinned identity. Returns its record."""
    ev = bg.require_guarded(ctx)
    for k, want in (("certificate_sha256", CERTIFICATE_SHA256), ("facade_assembly_sha256", FACADE_SHA256),
                    ("battle_guard_module_sha256", BATTLE_GUARD_PY_SHA256)):
        if ev.get(k) != want:
            raise GuardScopeError(f"installed guard {k} {ev.get(k)} != pinned {want}")
    return record(ctx)


def record(ctx) -> dict:
    ev = ctx.battle_guard
    return {"schema": RECORD_SCHEMA, **static_identity(), "scope": SCOPE,
            "installed_utc": ev["installed_utc"], "guarded_pid": ev["guarded_pid"],
            "guard_hello_digest": pins.sha256_bytes(json.dumps(ev["guard_hello"], sort_keys=True).encode()),
            "original_hello_digest": ev["original_hello_digest"],
            "provenance_digest_unchanged": ev["provenance_digest_unchanged"],
            "replaced_worker": ev["replaced_worker"]}


def guarded(factory, *, rpc_timeout_s: float = bg.DEFAULT_RPC_TIMEOUT_S, install=None):
    """``factory()`` builds an ORIGINAL Context (its constructor unchanged); the facade is installed before
    any reset. On any failure the context (and whichever worker it holds) is closed and the error re-raised."""
    ctx = factory()
    try:
        (install or bg.install_guarded_worker)(ctx, certificate_path=bg.CERTIFICATE,
                                                certificate_sha256=CERTIFICATE_SHA256, rpc_timeout_s=rpc_timeout_s)
        require(ctx)
    except BaseException:
        ctx.close()
        raise
    return ctx


def close(ctx) -> dict | None:
    """Close a context; returns the guarded worker's close record (quit/exit code) when there is one."""
    ctx.close()
    rec = getattr(ctx.worker, "close_record", None)
    return dict(rec) if isinstance(rec, dict) else None


def is_refusal(ep) -> bool:
    return bg.is_guard_refusal(getattr(ep, "reason", None))


def failure_evidence(e: BaseException) -> dict | None:
    """The bounded-RPC timeout evidence (exact request, stderr tail, close record) when ``e`` carries it."""
    ev = getattr(e, "evidence", None)
    return ev if isinstance(ev, dict) else None


# ---------------------------------------------------------------------------------------------- scoped bans

@contextlib.contextmanager
def optimizer_prohibited(reason: str):
    """Within the block, constructing any ``torch.optim.Optimizer`` raises. The original ``__init__`` is
    restored in ``finally``, so later learning in the same interpreter is never poisoned."""
    import torch
    original = torch.optim.Optimizer.__init__
    if getattr(original, "_act1ft_prohibited", False):
        raise RuntimeError("optimizer ban already active (nested scopes are refused)")

    def _reject(*a, **k):
        raise RuntimeError(f"{reason}: optimizer construction is prohibited")

    _reject._act1ft_prohibited = True
    torch.optim.Optimizer.__init__ = _reject
    try:
        yield
    finally:
        torch.optim.Optimizer.__init__ = original


# ---------------------------------------------------------------------------------------------- platform

class NativeRefused(RuntimeError):
    pass


def require_native_host(stage: str, *, system: str | None = None) -> None:
    """Native production stages run on the Windows host only; this Mac keeps synthetic tests and builds."""
    system = system or platform.system()
    if system != "Windows":
        raise NativeRefused(f"native production stage {stage!r} refuses to run on {system}: Windows host only, "
                            "after ROOT's prior-closure + tree-idle gate and root GO")
