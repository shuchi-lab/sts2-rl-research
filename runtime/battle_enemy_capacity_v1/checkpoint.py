"""Strict inference checkpoint loading; save/replay/resume operations omitted."""

from __future__ import annotations

from pathlib import Path

import torch

from crl import checkpoint as shared

from . import profiles

def load(path: Path, ctx=None, *, device="cpu", expected_profile: str | None = None):
    path = Path(path)
    blob = torch.load(path, map_location="cpu", weights_only=False)
    if blob.get("schema") != shared.CHECKPOINT_SCHEMA:
        raise ValueError("not a crl checkpoint")
    model = profiles.from_config(blob["model_config"], blob["state_dict"]["card_static"], blob["experiment_model"])
    if expected_profile is not None and profiles.identity(model)["profile"] != expected_profile:
        raise ValueError("checkpoint is not the requested experiment profile")
    if blob.get("meta", {}).get("model") != profiles.identity(model):
        raise ValueError("metadata model identity is missing or inconsistent")
    if ctx is not None:
        from act1_battle_finetune_v1 import isolation
        problems = shared.check_compatible(blob, ctx)
        isolation.require(ctx)
        isolation.check_record(blob.get(isolation.RECORD_KEY), what=str(path))
        if problems:
            raise ValueError(f"checkpoint incompatible with context: {problems}")
        blob["compatibility_problems"] = []
    model.load_state_dict(blob["state_dict"], strict=True)
    model.eval()
    from crl.dqn import resolve_device
    model.to(resolve_device(str(device)))
    blob["model_identity"] = profiles.identity(model)
    return model, blob
