"""Strict inference checkpoint schema loading; training artifact writers omitted."""

from __future__ import annotations

from pathlib import Path

import torch

STREAMS = ("choices", "maps", "fights", "runs", "replay_ledger", "replay_samples")

CHECKPOINT_SCHEMA = "rs1/strategy_checkpoint/v2"

def checkpoint_problems(blob) -> list[str]:
    """Why a strategy checkpoint is not one of THIS decision flow / encoding / model (empty if it is)."""
    from .contracts import DECISION_FLOW
    from .features import ENCODING_SCHEMA
    from .model import MODEL_SCHEMA
    if not isinstance(blob, dict) or blob.get("schema") != CHECKPOINT_SCHEMA:
        return [f"not a {CHECKPOINT_SCHEMA} checkpoint (schema {blob.get('schema') if isinstance(blob, dict) else None!r}"
                "; rs1/strategy_checkpoint/v1 is the historical v1 menu decision flow)"]
    out = []
    if blob.get("decision_flow") != DECISION_FLOW:
        out.append(f"decision flow {blob.get('decision_flow')!r} is not {DECISION_FLOW}")
    if blob.get("encoding_schema") != ENCODING_SCHEMA:
        out.append(f"encoding {blob.get('encoding_schema')!r} is not {ENCODING_SCHEMA}")
    mc = blob.get("model_config") or {}
    if mc.get("schema") != MODEL_SCHEMA or mc.get("decision_flow") != DECISION_FLOW:
        out.append(f"model config {mc.get('schema')!r}/{mc.get('decision_flow')!r} is not {MODEL_SCHEMA}/{DECISION_FLOW}")
    return out

def load_strategy_checkpoint(path: Path, map_location="cpu", weights_only: bool = True) -> dict:
    """Load a strategy checkpoint, failing closed on any other decision flow/encoding/model (old pilots included)."""
    from .contracts import ContractError
    blob = torch.load(Path(path), map_location=map_location, weights_only=weights_only)
    problems = checkpoint_problems(blob)
    if problems:
        raise ContractError(f"{path}: " + "; ".join(problems))
    return blob

import math

def _default(x):
    if hasattr(x, "tolist"):
        return x.tolist()
    if hasattr(x, "__dict__"):
        return x.__dict__
    return str(x)

def sanitize(obj):
    """A JSON-safe copy for failure diagnostics: nonfinite floats become explicit strings."""
    if isinstance(obj, float):
        return obj if math.isfinite(obj) else ("NaN" if math.isnan(obj) else ("Infinity" if obj > 0 else "-Infinity"))
    if isinstance(obj, dict):
        return {str(k): sanitize(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [sanitize(v) for v in obj]
    if obj is None or isinstance(obj, (str, int, bool)):
        return obj
    return sanitize(_default(obj))
