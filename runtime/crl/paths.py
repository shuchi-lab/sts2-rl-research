"""Paths, hashing and small JSON helpers shared by every combat_rl module (stdlib only)."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT.parent
INPUT_BUNDLE = ROOT / "data" / "frozen_three_act_214_inputs"
TEMPLATES = INPUT_BUNDLE / "templates.jsonl.gz"  # No templates are used by full-run inference.
CATALOG = INPUT_BUNDLE / "catalog.json"
ROSTER = INPUT_BUNDLE / "roster.json"
STATUS_SCHEMA = INPUT_BUNDLE / "status_schema.json"
COHORT_MANIFEST = INPUT_BUNDLE / "manifest.json"
CDS_BUILD = PROJECT / "simulator/bin/worker_build08"
CDS_BUILD_MANIFEST = PROJECT / "simulator/provenance/worker_build08_manifest.json"
BUILD = PROJECT / "simulator" / "bin"
ENGINE_LINE_BUILD08 = "build08"
ENGINE_LINE_FROZEN = "frozen-cds"
ENGINE_LINES = (ENGINE_LINE_BUILD08, ENGINE_LINE_FROZEN)
ENGINE_LINE = ENGINE_LINE_BUILD08
BUILD08_ENGINE_VERSION = "sts2emulator-whole-game-build08-v0.111.0+crl-training-overlay/v1+p2-juggernaut-await-kill"
OWNED_ENGINE_ROOT = PROJECT / "simulator/source/engine/whole_game_build08"
OWNED_ENGINE_SRC = OWNED_ENGINE_ROOT / "Sts2Emulator"
OWNED_ENGINE_SOURCE_MANIFEST = OWNED_ENGINE_ROOT / "SOURCE.json"
COLLECTOR_WORKER_SRC = PROJECT / "simulator/source/worker/CollectorWorker"
BUILD08_WORKER_PROJECT = PROJECT / "simulator/source/worker/RlWorkerBuild08/RlWorker.csproj"
BUILD08_WORKER_SRC = BUILD08_WORKER_PROJECT.parent
BUILD08_WORKER_DLL = BUILD / "worker_build08/RlCombatWorker.dll"
BUILD08_WORKER_BUILD_MANIFEST = PROJECT / "simulator/provenance/worker_build08_manifest.json"
FROZEN_WORKER_PROJECT, FROZEN_WORKER_SRC = BUILD08_WORKER_PROJECT, BUILD08_WORKER_SRC
FROZEN_WORKER_BUILD_MANIFEST = BUILD08_WORKER_BUILD_MANIFEST
WORKER_PROJECT, WORKER_SRC = BUILD08_WORKER_PROJECT, BUILD08_WORKER_SRC
WORKER_DLL, WORKER_BUILD_MANIFEST = BUILD08_WORKER_DLL, BUILD08_WORKER_BUILD_MANIFEST
DATA, RUNS = ROOT / "data", PROJECT / "runs"
DOTNET = Path(os.environ["RS1_DOTNET"])
DOTNET_ROOT = DOTNET.parent

def worker_env() -> dict[str, str]:
    env = {k: v for k, v in os.environ.items() if not k.startswith(("DOTNET_", "COMPlus_", "MSBUILD"))}
    env.update(
        DOTNET_ROOT=str(DOTNET_ROOT),
        DOTNET_CLI_TELEMETRY_OPTOUT="1",
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
        DOTNET_NOLOGO="1",
        DOTNET_gcServer="0",
        MSBUILDDISABLENODEREUSE="1",
    )
    return env


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path | str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def canonical(obj: Any) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def digest(obj: Any) -> str:
    return sha256_bytes(canonical(obj))


def derive_seed(base: int, tag: str, index: int) -> int:
    """Stable 63-bit seed: sha256("crl/seed/v1|tag|base|index")."""
    d = hashlib.sha256(f"crl/seed/v1|{tag}|{base}|{index}".encode()).digest()
    return int.from_bytes(d[:8], "little") & ((1 << 63) - 1)


def write_json(path: Path, obj: Any, indent: int | None = 1) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(obj, f, indent=indent, ensure_ascii=False)
            f.write("\n")
        os.replace(tmp, path)
    except BaseException:
        if os.path.exists(tmp):
            os.unlink(tmp)
        raise


def read_json(path: Path | str) -> Any:
    with open(path, encoding="utf-8") as f:
        return json.load(f)
