#!/usr/bin/env python3
"""Run the released frozen two-agent policy on the retained validation seeds."""
from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import traceback

RELEASE = Path(__file__).resolve().parents[1]


def discover_dotnet(explicit: str | None) -> Path:
    candidates = [explicit, os.environ.get("RS1_DOTNET"), shutil.which("dotnet")]
    if os.environ.get("DOTNET_ROOT"):
        candidates.append(str(Path(os.environ["DOTNET_ROOT"]) / ("dotnet.exe" if os.name == "nt" else "dotnet")))
    candidates.extend(str(p) for p in (Path.home()/".dotnet/dotnet", Path("/usr/local/share/dotnet/dotnet"), Path("/usr/share/dotnet/dotnet")))
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            path = Path(candidate).resolve()
            probe = subprocess.run([str(path), "--list-runtimes"], text=True, capture_output=True, timeout=20)
            if probe.returncode == 0 and any(line.startswith("Microsoft.NETCore.App 9.") for line in probe.stdout.splitlines()):
                return path
            if explicit:
                raise ValueError(f"{path} does not provide the required .NET 9 runtime: {probe.stdout} {probe.stderr}")
    raise FileNotFoundError(".NET 9 runtime not found. Install .NET 9, add dotnet to PATH, or pass --dotnet /absolute/path/to/dotnet.")


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--episodes", type=int, default=1, help="Run first N retained seeds, 1..256 (default: 1).")
    group.add_argument("--all", action="store_true", help="Run all 256 retained validation seeds.")
    parser.add_argument("--output-dir", type=Path, help="New output directory; default: release/runs/<UTC timestamp>.")
    parser.add_argument("--dotnet", help="Path to an existing dotnet executable with .NET 9 runtime.")
    parser.add_argument("--timeout-seconds", type=int, default=7200, help="Gameplay/admission deadline in seconds (default: 7200); does not bound startup checks.")
    parser.add_argument("--check", action="store_true", help="Validate both policies and isolated Battle context; no FullRunWorker launch or gameplay.")
    args = parser.parse_args()
    episodes = 256 if args.all else args.episodes
    if not 1 <= episodes <= 256 or args.timeout_seconds < 30:
        parser.error("--episodes must be 1..256 and --timeout-seconds must be at least 30")
    dotnet = discover_dotnet(args.dotnet)
    output = (args.output_dir or RELEASE/"runs"/datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")).resolve()
    for directory in ("runtime", "models", "results", "simulator", "scripts", "docs"):
        if output == RELEASE/directory or (RELEASE/directory) in output.parents:
            parser.error(f"output must not be inside frozen {directory}/")
    if output == RELEASE or RELEASE in output.parents and output.parent == RELEASE and output.name in ("results",):
        parser.error("output must be a new run directory")
    output.mkdir(parents=True, exist_ok=False)
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(RELEASE/"runtime"))
    os.environ.update(RS1_DOTNET=str(dotnet), STS2_RL_DOTNET_ROOT=str(dotnet.parent),
                      STS2_RL_ENGINE_LINE="build08", PYTHONDONTWRITEBYTECODE="1",
                      OMP_NUM_THREADS="1", MKL_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1",
                      KMP_BLOCKTIME="0", OMP_WAIT_POLICY="PASSIVE")
    evaluator = None
    try:
        selected = json.loads((RELEASE/"runtime/metadata/selected.json").read_text())
        seeds = [int(s) for s in json.loads((RELEASE/"results/validation_seeds.json").read_text())["seeds"]]
        if len(seeds) != 256 or len(set(seeds)) != 256 or any(s < 0 or s >= 2**63 for s in seeds):
            raise ValueError("retained validation seed contract differs")
        for role in ("choice", "battle"):
            entry = selected[role]
            if sha256(RELEASE/entry["path"]) != entry["file_sha256"]:
                raise ValueError(f"{role} checkpoint SHA-256 differs from the released model")
        # Preserve the certified guard module byte-for-byte; relocate only its file bindings.
        from act1_battle_finetune_v1 import battle_guard as guard
        guard.WRAPPER_DIR = RELEASE/"simulator/source/worker/GuardedBattleWorker"
        guard.BUILD_OUT = RELEASE/"simulator/bin/GuardedBattleWorker"
        guard.WRAPPER_DLL = guard.BUILD_OUT/"GuardedBattleWorker.dll"
        guard.CERTIFICATE = RELEASE/"simulator/provenance/GUARD_CERTIFICATE.json"
        guard.DEPENDENCY_DIR = RELEASE/"simulator/bin/worker_build08"
        from evaluation_adapter import Evaluation
        from fullrun_core import write_json
        manifest = dict(selected, _package=str(RELEASE), source_roots=[str(RELEASE/"runtime")],
                        runtime_root=str(RELEASE/"runtime"), runtime_environment={},
                        final_deadline_utc=(datetime.now(timezone.utc)+timedelta(seconds=args.timeout_seconds)).isoformat(),
                        cell_admission_hard_stop_s=10)
        evaluator = Evaluation(manifest, output)
        battle = evaluator.battle(selected["battle"])
        strategy, cfg = evaluator.strategy(selected["choice"])
        import torch
        import numpy
        run_manifest = {"schema":"sts2-ml-release-evaluation/v1", "version":"1.0.0", "evaluation_only":True,
                        "device":"cpu", "torch_threads":1, "python":sys.version, "torch":torch.__version__,
                        "numpy":numpy.__version__, "dotnet":str(dotnet), "output_dir":str(output),
                        "choice_sha256":selected["choice"]["file_sha256"], "battle_sha256":selected["battle"]["file_sha256"],
                        "requested_episodes":0 if args.check else episodes,
                        "seeds":[] if args.check else [str(s) for s in seeds[:episodes]],
                        "seed_namespace":"validation", "character":"Ironclad", "ascension":2, "acts":3,
                        "policy":"greedy", "native_workers":1, "slots_per_worker":8,
                        "timeout_seconds":args.timeout_seconds, "comparison":"retained validation data; not a fresh test"}
        write_json(output/"run_manifest.json", run_manifest)
        if args.check:
            write_json(output/"check.json", {"passed":True, "games":0, "choice_parameters":sum(p.numel() for p in strategy.model.parameters()),
                                           "battle_parameters":sum(p.numel() for p in battle.model.parameters()),
                                           "reward_id":evaluator.ctx.potion.reward_id,
                                           "vocabulary_cards":len(evaluator.ctx.vocab.card_ids)})
            print(json.dumps({"check":"passed", "games":0, "output_dir":str(output)}), flush=True)
            return 0
        summary = evaluator.cell("primary", selected["choice"], selected["battle"], 0, seeds[:episodes], "cells")
        summary["release_version"] = "1.0.0"
        summary["validation_subset"] = episodes != 256
        write_json(output/"summary.json", summary)
        print(json.dumps({"output_dir":str(output), "wins":summary["run_victories"], "attempted":episodes,
                          "win_rate":summary["win_rate_fixed"], "state":summary["state"]}), flush=True)
        return 0 if summary["state"] == "complete" else 1
    except BaseException as exc:
        (output/"failure.json").write_text(json.dumps({"error_type":type(exc).__name__, "error":str(exc),
                                                     "traceback":traceback.format_exc()}, indent=2)+"\n")
        raise
    finally:
        if evaluator is not None:
            evaluator.close()
        # Record loaded release modules to make the packaged dependency closure auditable.
        modules = sorted(str(Path(m.__file__).resolve().relative_to(RELEASE)) for m in list(sys.modules.values())
                         if getattr(m, "__file__", None) and Path(m.__file__).is_file() and RELEASE in Path(m.__file__).resolve().parents)
        (output/"loaded_modules.json").write_text(json.dumps(modules,indent=2)+"\n")


if __name__ == "__main__":
    raise SystemExit(main())
