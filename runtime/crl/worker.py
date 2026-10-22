"""Build and drive the persistent interactive C# worker (JSON lines over stdin/stdout)."""

from __future__ import annotations

import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import engine_source, paths

PROTOCOL = "crl.worker_protocol/v1"
OBSERVATION_SCHEMA = "crl.raw_observation/v3"
REFERENCED_ASSEMBLIES = ("CombatWorker.dll", "Sts2Emulator.dll", "System.IO.Hashing.dll")
OWNED_MANIFEST_SCHEMA = "crl.worker_build_manifest/owned-engine-v1"
# Hello hashes that must equal the owned build manifest's outputs.
OWNED_HELLO_OUTPUTS = {
    "engine_assembly_sha256": "Sts2Emulator.dll",
    "cds_worker_assembly_sha256": "CombatWorker.dll",
    "rl_worker_assembly_sha256": "RlCombatWorker.dll",
}


class WorkerError(RuntimeError):
    pass


def _dotnet_build(project: Path, out: Path, verbose: bool) -> list[str]:
    cmd = [
        str(paths.DOTNET), "build", str(project), "-c", "Release", "-m:2",
        "-nodeReuse:false", "-p:UseSharedCompilation=false", "-o", str(out),
    ]
    proc = subprocess.run(cmd, env=paths.worker_env(), capture_output=True, text=True)
    if verbose or proc.returncode != 0:
        print(proc.stdout[-4000:], proc.stderr[-4000:])
    if proc.returncode != 0:
        raise WorkerError("dotnet build failed")
    return cmd


def build(verbose: bool = False) -> dict:
    """Compile the active engine line's worker (``paths.ENGINE_LINE``) and write its manifest."""
    if paths.ENGINE_LINE == paths.ENGINE_LINE_BUILD08:
        return build_owned(verbose)
    return build_frozen(verbose)


def build_frozen(verbose: bool = False) -> dict:
    """Compile worker/RlWorker into build/worker against the frozen collector DLLs."""
    out = paths.BUILD / "worker"
    cmd = _dotnet_build(paths.FROZEN_WORKER_PROJECT, out, verbose)
    manifest = {
        "built_at_utc": datetime.now(timezone.utc).isoformat(),
        "command": cmd,
        "source": {p.name: paths.sha256_file(p) for p in sorted(paths.FROZEN_WORKER_SRC.glob("*")) if p.is_file()},
        "output": {p.name: paths.sha256_file(p) for p in sorted(out.glob("*.dll"))},
        "referenced_cds_assemblies": {
            name: {
                "cds_build": paths.sha256_file(paths.CDS_BUILD / name),
                "copied": paths.sha256_file(out / name),
            }
            for name in REFERENCED_ASSEMBLIES
        },
        "cds_build_manifest_sha256": paths.sha256_file(paths.CDS_BUILD_MANIFEST),
    }
    for name, h in manifest["referenced_cds_assemblies"].items():
        if h["cds_build"] != h["copied"]:
            raise WorkerError(f"{name}: copied assembly differs from the collector build")
    paths.write_json(paths.FROZEN_WORKER_BUILD_MANIFEST, manifest)
    return manifest


def build_owned(verbose: bool = False) -> dict:
    """Compile engine/whole_game_build08 -> worker/CollectorWorker -> worker/RlWorkerBuild08
    into build/worker_build08. Refuses to build owned sources that differ from SOURCE.json."""
    source_doc = engine_source.verify_owned_tree()
    out = paths.BUILD08_WORKER_DLL.parent
    cmd = _dotnet_build(paths.BUILD08_WORKER_PROJECT, out, verbose)
    sdk = subprocess.run([str(paths.DOTNET), "--version"], env=paths.worker_env(), capture_output=True, text=True)
    manifest = {
        "schema": OWNED_MANIFEST_SCHEMA,
        "engine_line": paths.ENGINE_LINE_BUILD08,
        "engine_version": paths.BUILD08_ENGINE_VERSION,
        "built_at_utc": datetime.now(timezone.utc).isoformat(),
        "command": cmd,
        "dotnet_sdk_version": sdk.stdout.strip(),
        "source": {
            "engine": {"path": str(paths.OWNED_ENGINE_SRC.relative_to(paths.ROOT)),
                       "digest": engine_source.tree_digest(paths.OWNED_ENGINE_SRC)},
            "engine_source_manifest_sha256": paths.sha256_file(paths.OWNED_ENGINE_SOURCE_MANIFEST),
            "collector_worker": {"path": str(paths.COLLECTOR_WORKER_SRC.relative_to(paths.ROOT)),
                                 "files": engine_source.tree_files(paths.COLLECTOR_WORKER_SRC)},
            "rl_worker": {"path": str(paths.BUILD08_WORKER_SRC.relative_to(paths.ROOT)),
                          "files": engine_source.tree_files(paths.BUILD08_WORKER_SRC)},
        },
        "upstream": source_doc["upstream"],
        "training_overlay_patch_sha256": source_doc["collector_overlay"]["patch_sha256"],
        "combat_rl_engine_changes": source_doc["combat_rl_sync"],
        "output": {p.name: paths.sha256_file(p) for p in sorted(out.glob("*.dll"))},
        "not_referenced": "combat_data_simulator build outputs; every assembly here is compiled from combat_rl sources",
    }
    paths.write_json(paths.BUILD08_WORKER_BUILD_MANIFEST, manifest)
    return manifest


def build_digest(manifest_path: Path | None = None) -> str:
    """Digest of the worker build manifest's compiled identity (not the timestamp).

    Frozen-line manifests keep their original formula (output + referenced assemblies), so
    every recorded ``provenance_digest`` on that line is reproduced unchanged.
    """
    m = paths.read_json(manifest_path or paths.WORKER_BUILD_MANIFEST)
    if m.get("schema") == OWNED_MANIFEST_SCHEMA:
        return paths.digest({"schema": m["schema"], "engine_version": m["engine_version"], "output": m["output"]})
    return paths.digest({"output": m["output"], "referenced": m["referenced_cds_assemblies"]})


def check_owned_hello(hello: dict, manifest_path: Path | None = None) -> None:
    """The running owned worker must be the build its manifest describes."""
    m = paths.read_json(manifest_path or paths.BUILD08_WORKER_BUILD_MANIFEST)
    if m.get("schema") != OWNED_MANIFEST_SCHEMA:
        raise WorkerError(f"{manifest_path}: not an owned-engine build manifest")
    if hello.get("engine_line") != m["engine_version"]:
        raise WorkerError(f"worker engine_line {hello.get('engine_line')!r} != manifest {m['engine_version']!r}")
    for key, dll in OWNED_HELLO_OUTPUTS.items():
        if hello[key] != m["output"][dll]:
            raise WorkerError(f"running {dll} {hello[key][:12]} differs from the build manifest {m['output'][dll][:12]}; "
                              "rebuild with `python -m crl build`")


class Worker:
    """One worker process hosting any number of independent fights (env ids)."""

    def __init__(self, dll: Path = paths.WORKER_DLL):
        if not dll.exists():
            raise WorkerError(f"{dll} missing; run `python -m crl build`")
        self.proc = subprocess.Popen(
            [str(paths.DOTNET), str(dll)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env=paths.worker_env(), text=True, bufsize=1 << 20,
        )
        self.hello = self.call({"cmd": "hello"})
        if self.hello["protocol"] != PROTOCOL or self.hello["observation_schema"] != OBSERVATION_SCHEMA:
            raise WorkerError(f"worker protocol mismatch: {self.hello['protocol']} / {self.hello.get('observation_schema')} "
                              f"(expected {PROTOCOL} / {OBSERVATION_SCHEMA}); rebuild with `python -m crl build`")
        if Path(dll).resolve() == paths.BUILD08_WORKER_DLL.resolve():
            check_owned_hello(self.hello)
        self.templates: set[str] = set()

    def call(self, cmd: dict) -> dict:
        try:
            self.proc.stdin.write(json.dumps(cmd, separators=(",", ":")) + "\n")
            self.proc.stdin.flush()
            line = self.proc.stdout.readline()
        except BrokenPipeError as e:
            raise WorkerError(f"worker died: {self.proc.stderr.read()[-2000:]}") from e
        if not line:
            raise WorkerError(f"worker exited: {self.proc.stderr.read()[-2000:]}")
        r = json.loads(line)
        if not r.get("ok"):
            raise WorkerError(f"{r.get('cmd')}: {r.get('error_type')}: {r.get('message')}\n{r.get('stack')}")
        return r

    def add_template(self, template_id: str, engine_input: dict) -> None:
        if template_id not in self.templates:
            self.call({"cmd": "add_template", "template_id": template_id, "engine_input": engine_input})
            self.templates.add(template_id)

    def reset(self, env: int, template_id: str, env_seed: int, *, max_steps: int, max_turns: int,
              rng_mode: str = "training", purity_check: bool = False, allow_potions: bool = False) -> dict:
        cmd = {
            "cmd": "reset", "env": env, "template_id": template_id, "env_seed": env_seed,
            "max_steps": max_steps, "max_turns": max_turns, "rng_mode": rng_mode, "purity_check": purity_check,
        }
        if allow_potions:  # potion_rl_v1 opt-in; the default command is unchanged
            if "potion_actions" not in self.hello:
                raise WorkerError("allow_potions requested but this worker build has no potion_actions contract")
            cmd["allow_potions"] = True
        r = self.call(cmd)
        if allow_potions and r.get("allow_potions") is not True:
            raise WorkerError(f"env {env}: worker did not confirm allow_potions (a build that ignores the flag)")
        return r

    def step(self, env: int, decision: int, option: int) -> dict:
        return self.call({"cmd": "step", "env": env, "decision": decision, "option": option})

    def step_many(self, steps: list[tuple[int, int, int]]) -> list[dict]:
        r = self.call({"cmd": "step_many", "steps": [{"env": e, "decision": d, "option": o} for e, d, o in steps]})
        return r["results"]

    def engine_dump(self, env: int) -> dict:
        return self.call({"cmd": "engine_dump", "env": env})["engine"]

    def close(self) -> None:
        if self.proc.poll() is None:
            try:
                self.proc.stdin.write('{"cmd":"quit"}\n')
                self.proc.stdin.flush()
                self.proc.wait(timeout=10)
            except Exception:
                self.proc.kill()

    def __enter__(self) -> "Worker":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()
