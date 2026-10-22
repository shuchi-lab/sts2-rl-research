"""Portable frozen worker locations and byte identity checks; build/deployment omitted."""

from __future__ import annotations

import argparse

import hashlib

import json

import os

import shutil

import subprocess

import sys

from datetime import datetime, timezone

from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

TRIAL = ROOT / "experiments" / "trials" / "run-strategy-e-fixed-v1"

FROZEN_DEPS = Path(os.environ.get("RS1_FROZEN_DEPENDENCIES", TRIAL / "frozen_e_dependencies")).resolve()

DEPENDENCY_DIR = ROOT.parent / "simulator/bin/worker_build08"

DEPENDENCY_MANIFEST = ROOT.parent / "simulator/provenance/worker_build08_manifest.json"

FROZEN_E_CHECKPOINT = Path(os.environ.get("RS1_FROZEN_E", FROZEN_DEPS / "E_best.pt")).resolve()

FROZEN_E_SOURCE = ("experiments/trials/battle-width-af-v1/archives/20261005T095814640230Z/closed_runs/"
                   "bw1-win-gpu-E-s2026101101/checkpoints/best.pt")

FROZEN_E_SHA256 = "9f66f1fc531a3a2ba565019ae4597bc234a420943b5a7b2f943b4b4a6202598a"

EXPECTED_DEPENDENCY_SHA256 = {
    "Sts2Emulator.dll": "11d614a31ea796f5631cf163d876db56b5129c38b9a5e98e7ee7f1632f6b2b9b",
    "CombatWorker.dll": "ab408a807b1837c5da2e17b6785ca7ce18e49a78452271538bf33a1cbbe7cfb9",
    "RlCombatWorker.dll": "b49a635d51cfc68c5e0997342e96ee2a21fb2076e5f406c150194198ff018369",
    "System.IO.Hashing.dll": "fbe8c2fcb4adc01da5c3c8ed0d727903b8fda1e6b84da3c447c9794615f30664",
}

EXPECTED_WORKER_BUILD_DIGEST = "aad649aff28c01f512ddcd4b86678d467d26beafc2bc0d08cd7703a2ebf47d37"

WORKER_SOURCE = ROOT / "worker" / "FullRunWorker"

WORKER_PROJECT = WORKER_SOURCE / "FullRunWorker.csproj"

BUILD_DIR = ROOT.parent / "simulator/bin"

WORKER_DLL = BUILD_DIR / "FullRunWorker" / "FullRunWorker.dll"

BUILD_MANIFEST = ROOT.parent / "simulator/provenance/FullRunWorker_manifest.json"

BUILD_SCHEMA = "rs1/bridge_build_manifest/v1"

DOTNET = Path(os.environ["RS1_DOTNET"])

def sha256_file(path: Path | str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def canonical_digest(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

def dotnet_env() -> dict[str, str]:
    env = {k: v for k, v in os.environ.items() if not k.startswith(("DOTNET_", "COMPlus_", "MSBUILD"))}
    env.update(DOTNET_ROOT=str(DOTNET.parent), DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
               DOTNET_NOLOGO="1", DOTNET_gcServer="0", MSBUILDDISABLENODEREUSE="1")
    return env

def verify_dependencies(directory: Path = DEPENDENCY_DIR) -> dict:
    """The staged assemblies must be exactly the bytes E's provenance names."""
    got = {}
    for name, want in EXPECTED_DEPENDENCY_SHA256.items():
        p = Path(directory) / name
        if not p.is_file():
            raise FileNotFoundError(f"staged dependency missing: {p}")
        got[name] = sha256_file(p)
        if got[name] != want:
            raise ValueError(f"{p}: sha256 {got[name]} is not the frozen {want}")
    return got

def worker_build_digest(manifest_path: Path = DEPENDENCY_MANIFEST) -> str:
    """crl.worker.build_digest's owned-manifest formula, on the staged manifest (no crl import)."""
    m = json.loads(Path(manifest_path).read_text(encoding="utf-8"))
    return canonical_digest({"schema": m["schema"], "engine_version": m["engine_version"], "output": m["output"]})
