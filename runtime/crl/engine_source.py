"""Source identity of combat_rl's owned training engine (stdlib only).

``engine/whole_game_build08/SOURCE.json`` (written by ``infrastructure/sync_build08_engine.py``)
records which accepted whole-game source the owned copy came from, the patches applied on top
of it, and every resulting file's sha256. ``crl.worker.build_owned`` refuses to compile owned
sources that differ from that record, so a build manifest never names a source state that is
not on disk.
"""

from __future__ import annotations

from pathlib import Path

from . import paths

SOURCE_SCHEMA = "crl.owned_engine_source/v1"
EXCLUDED_DIRS = frozenset(("bin", "obj"))


def tree_files(root: Path) -> dict[str, str]:
    """sha256 of every file under ``root`` (bin/obj excluded), keyed by POSIX relative path."""
    out = {}
    for p in sorted(root.rglob("*")):
        rel = p.relative_to(root)
        if p.is_file() and not EXCLUDED_DIRS.intersection(rel.parts[:-1]):
            out[rel.as_posix()] = paths.sha256_file(p)
    return out


def tree_digest(root: Path) -> str:
    return paths.digest(tree_files(root))


def verify_owned_tree(source_manifest: Path | None = None, engine_src: Path | None = None) -> dict:
    """The owned engine tree must equal the files recorded in SOURCE.json; returns the record."""
    doc = paths.read_json(source_manifest or paths.OWNED_ENGINE_SOURCE_MANIFEST)
    if doc.get("schema") != SOURCE_SCHEMA:
        raise ValueError(f"owned engine source manifest schema {doc.get('schema')!r} != {SOURCE_SCHEMA}")
    actual = tree_files(engine_src or paths.OWNED_ENGINE_SRC)
    recorded = doc["owned"]["files"]
    if actual != recorded:
        changed = sorted(k for k in set(actual) | set(recorded) if actual.get(k) != recorded.get(k))
        raise ValueError(f"owned engine sources differ from SOURCE.json: {changed[:10]}; "
                         "re-run infrastructure/sync_build08_engine.py --write after review")
    return doc
