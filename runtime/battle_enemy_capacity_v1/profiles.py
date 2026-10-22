"""Exact experiment identities around the unchanged WidthQNet implementation."""
from __future__ import annotations

import torch

from .model import REFERENCE_SHAPES, WidthQNet
from battle_width_scan_v1.profiles import WIDTH_KEYS

MODEL_SCHEMA = "bec1/model/v1"
BASE = dict(card_hidden=128, card_dim=64, pile_dim=64, eff_hidden=128, eff_dim=64,
            enemy_hidden=256, enemy_dim=128, state_dim=256, head=128)
PROFILES = {
    'E': dict(BASE, **{}),
    'enemy_hidden2x': dict(BASE, **{'enemy_hidden': 512}),
    'enemy_deep': dict(BASE, **{}),
    'enemy_hidden4x': dict(BASE, **{'enemy_hidden': 1024}),
    'enemy_wide_deep': dict(BASE, **{'enemy_hidden': 512}),
    'state1p5x': dict(BASE, **{'state_dim': 384}),
    'enemy_output2x_state1p5x': dict(BASE, **{'enemy_dim': 256, 'state_dim': 384}),
}
PARAMETERS = {'E': 597365, 'enemy_hidden2x': 666997, 'enemy_deep': 663157, 'enemy_hidden4x': 806261, 'enemy_wide_deep': 929653, 'state1p5x': 825589, 'enemy_output2x_state1p5x': 973173}
INPUTS = {'E': (1013, 486), 'enemy_hidden2x': (1013, 486), 'enemy_deep': (1013, 486), 'enemy_hidden4x': (1013, 486), 'enemy_wide_deep': (1013, 486), 'state1p5x': (1013, 614), 'enemy_output2x_state1p5x': (1269, 742)}
ENEMY_LAYERS = {'E': [256], 'enemy_hidden2x': [512], 'enemy_deep': [256, 256], 'enemy_hidden4x': [1024], 'enemy_wide_deep': [512, 512], 'state1p5x': [256], 'enemy_output2x_state1p5x': [256]}


def widths(profile: str) -> dict:
    if profile not in PROFILES:
        raise ValueError(f"unknown bwc1 profile {profile!r}")
    return dict(PROFILES[profile])


def identity(model: WidthQNet) -> dict:
    profile = getattr(model, "experiment_profile", None)
    if profile not in PROFILES or model.widths != PROFILES[profile]:
        raise ValueError("model does not have an exact bwc1 profile identity")
    if model.enemy_layers != ENEMY_LAYERS[profile]:
        raise ValueError("enemy depth differs from profile")
    if model.config.get("configuration") is not None:
        raise ValueError("bwc1 uses the unprofiled historical constructor")
    for key in ("sizes", "n_enchantments", "feature_dims", "potion_options", "enemy_count", "dueling",
                "identity_init_scale"):
        if model.config.get(key) != REFERENCE_SHAPES[key]:
            raise ValueError(f"production model config differs at {key}")
    got = (model.parameter_count(), model.state_in, model.cand_in)
    want = (PARAMETERS[profile], *INPUTS[profile])
    if got != want or not model.potion_options or not model.enemy_count or model.dueling:
        raise ValueError(f"production shapes/semantics differ: {got} != {want}")
    return {"model_schema": MODEL_SCHEMA, "profile": profile, "widths": widths(profile),
            "enemy_layers": list(model.enemy_layers), "parameters": got[0], "state_input": got[1], "candidate_input": got[2],
            "player_dim": model.player_dim, "model_class": "battle_enemy_capacity_v1.model.WidthQNet"}


def _wrap(profile: str, sizes, static, n_ench, dims) -> WidthQNet:
    model = WidthQNet(sizes, static, n_ench, configuration=None, **widths(profile), feature_dims=dims,
                      potion_options=True, enemy_count=True, dueling=False, identity_init_scale=0.1, enemy_layers=ENEMY_LAYERS[profile])
    model.experiment_profile = profile
    identity(model)
    return model


def build(profile: str, context) -> WidthQNet:
    return _wrap(profile, context.vocab.sizes, torch.from_numpy(context.vocab.card_static),
                 context.adapter.describe()["n_enchantments"], context.adapter.dims)


def build_reference(profile: str) -> WidthQNet:
    r = REFERENCE_SHAPES
    return _wrap(profile, r["sizes"], torch.zeros(r["card_static"]), r["n_enchantments"], r["feature_dims"])


def from_config(config: dict, static: torch.Tensor, ident: dict) -> WidthQNet:
    if ident.get("model_schema") != MODEL_SCHEMA:
        raise ValueError("not a bwc1 model identity")
    profile = ident.get("profile")
    if {k: config.get(k) for k in WIDTH_KEYS} != widths(profile):
        raise ValueError("checkpoint widths differ from its exact profile")
    if config.get("enemy_layers") != ENEMY_LAYERS[profile]:
        raise ValueError("checkpoint enemy depth differs from profile")
    if config.get("configuration") is not None:
        raise ValueError("historical profiled checkpoints are not bwc1")
    cfg = dict(config)
    sizes, ench = cfg.pop("sizes"), cfg.pop("n_enchantments")
    model = WidthQNet(sizes, static, ench, **cfg)
    model.experiment_profile = profile
    if identity(model) != ident:
        raise ValueError("checkpoint architecture identity differs from rebuilt model")
    return model
