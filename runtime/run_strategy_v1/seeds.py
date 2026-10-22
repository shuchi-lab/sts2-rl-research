"""Disjoint full-run seed streams (Task 1): train, frozen validation, preflight diagnostic and a reserved
final-test stream that this pilot never runs or selects on. A full-run seed is the independent unit."""
from __future__ import annotations

import hashlib
import json
from dataclasses import asdict, dataclass

from .contracts import SeedManifest

NAMESPACES = ("train", "validation", "diagnostic", "reserved_test")
SEED_SCHEMA = "rs1/seed_manifest/v1"


def derive_seed(base: int, tag: str, index: int) -> int:
    """crl.paths.derive_seed's formula (stable 63-bit), namespaced by ``rs1/<tag>``."""
    d = hashlib.sha256(f"crl/seed/v1|rs1/{tag}|{base}|{index}".encode()).digest()
    return int.from_bytes(d[:8], "little") & ((1 << 63) - 1)


def seed_string(namespace: str, seed: int) -> str:
    """The engine's opaque stored run seed (RunRngSet hashes it). Never a policy input."""
    if namespace not in NAMESPACES:
        raise ValueError(f"unknown seed namespace {namespace}")
    return f"RS1{namespace[0].upper()}{seed:016X}"


def make_seed_manifest(base: int, train_count: int, val_count: int, *, diagnostic_count: int = 12,
                       reserved_test_count: int = 512) -> SeedManifest:
    streams = {}
    seen: dict[int, str] = {}
    for ns, n in (("train", train_count), ("validation", val_count), ("diagnostic", diagnostic_count),
                  ("reserved_test", reserved_test_count)):
        out, i = [], 0
        while len(out) < n:
            s = derive_seed(base, ns, i)
            i += 1
            if s in seen:  # cross-namespace collision: skip deterministically, keep streams disjoint
                continue
            seen[s] = ns
            out.append(s)
        streams[ns] = tuple(out)
    return SeedManifest(base=base, train=streams["train"], validation=streams["validation"],
                        diagnostic=streams["diagnostic"], reserved_test=streams["reserved_test"])


def manifest_record(m: SeedManifest, *, include_reserved_values: bool = False) -> dict:
    d = asdict(m)
    d["schema"] = SEED_SCHEMA
    d["digest"] = m.digest()
    d["seed_string_format"] = "RS1<namespace initial><seed:016X> (RunEngine.Reset string seed)"
    if not include_reserved_values:
        d["reserved_test"] = {"count": len(m.reserved_test),
                              "digest": hashlib.sha256(json.dumps(list(m.reserved_test)).encode()).hexdigest(),
                              "note": "reserved final-test stream: never run, never selected on in this pilot"}
    return d
