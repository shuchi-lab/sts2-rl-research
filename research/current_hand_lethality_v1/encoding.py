"""Encode native judge output; this module does not simulate or search combats.

``encode`` consumes already encoded base dense inputs in the frozen schema.
It masks two enemy columns, appends the six judge flags, and returns fresh arrays.
"""

import math
from numbers import Real

import numpy as np

from .schema import (
    ACTION_FIELDS, BASE_ACTION_FIELDS, BASE_ENEMY_FIELDS, ENEMY_FIELDS,
    MASKED_ENEMY_INDICES, PROFILES, SCHEMA,
)


def _rows(raw):
    if not isinstance(raw, dict):
        raise ValueError("observation must be an object")
    enemies, legal = raw.get("enemies"), raw.get("legal")
    if not isinstance(enemies, list) or not isinstance(legal, list):
        raise ValueError("observation requires enemies and legal lists")
    return enemies, legal


def feature_arrays(raw, *, enabled=True):
    """Return float32 ``[enemies, 2]`` and ``[legal options, 4]`` flag arrays.

    Missing action-result rows stay all zero (unknown/not applicable). The enabled
    profile requires a matching native schema; the baseline needs no judge block.
    Targeted actions select their own target; untargeted actions aggregate living
    enemies. The native arrays already track original physical enemy indices.
    """
    enemies, legal = _rows(raw)
    ne, no = len(enemies), len(legal)
    enemy_flags = np.zeros((ne, len(ENEMY_FIELDS)), dtype=np.float32)
    action_flags = np.zeros((no, len(ACTION_FIELDS)), dtype=np.float32)
    if not enabled:
        return enemy_flags, action_flags

    data = raw.get("current_hand_lethality")
    if not isinstance(data, dict) or data.get("schema") != SCHEMA:
        raise ValueError(f"enabled profile requires native schema {SCHEMA}")
    if not isinstance(data.get("enemies"), list) or len(data["enemies"]) != ne:
        raise ValueError("native enemy rows must align with observation enemies")
    if not isinstance(data.get("actions"), list):
        raise ValueError("native action results must be a list")
    try:
        alive = []
        for i, (enemy, row) in enumerate(zip(enemies, data["enemies"])):
            hp = enemy["hp"]
            if isinstance(hp, bool) or not isinstance(hp, Real) or not math.isfinite(hp):
                raise ValueError("enemy hp must be finite and numeric")
            if type(row["Found"]) is not bool or type(row["Complete"]) is not bool:
                raise ValueError("enemy flags must be JSON booleans")
            alive.append(hp > 0)
            if alive[-1]:
                enemy_flags[i] = [row["Found"], row["Complete"]]

        seen = set()
        for row in data["actions"]:
            i, hand, target = row["Option"], row["Hand"], row["Target"]
            if type(i) is not int or not 0 <= i < no or i in seen:
                raise ValueError("native action option is invalid or duplicated")
            if type(hand) is not int or hand < 0 or type(target) is not int or not -1 <= target < ne:
                raise ValueError("native action hand or target is invalid")
            seen.add(i)
            option = legal[i]
            if (option.get("kind") != "play_card" or option.get("hand_index") != hand
                    or option.get("target", -1) != target):
                raise ValueError("native action must match its legal option's hand and target")
            immediate, found = row["Immediate"], row["Found"]
            if (not isinstance(immediate, list) or not isinstance(found, list)
                    or len(immediate) != ne or len(found) != ne):
                raise ValueError("native action enemy arrays have the wrong width")
            flags = immediate + found + [row["ImmediateKnown"], row["Complete"]]
            if any(type(value) is not bool for value in flags):
                raise ValueError("action flags must be JSON booleans")
            indices = [target] if target >= 0 else [j for j in range(ne) if alive[j]]
            action_flags[i] = [any(immediate[j] for j in indices), any(found[j] for j in indices),
                               row["ImmediateKnown"], row["Complete"]]
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("malformed native lethality observation") from error
    return enemy_flags, action_flags


def _matrix(values, rows, width, name):
    try:
        result = np.array(values, dtype=np.float32, copy=True)
    except (TypeError, ValueError, OverflowError) as error:
        raise ValueError(f"{name} must contain numeric float32-compatible values") from error
    if result.shape != (rows, width):
        raise ValueError(f"{name} must have shape ({rows}, {width}); got {result.shape}")
    if not np.isfinite(result).all():
        raise ValueError(f"{name} must contain only finite values")
    return result


def encode(raw, enemy_features, action_features, *, profile="masked_lethality"):
    """Mask and append to base inputs; return new ``[N,32]`` and ``[A,10]`` arrays.

    Base features must already use ``BASE_ENEMY_FIELDS`` / ``BASE_ACTION_FIELDS``
    order and their original transforms. No base transform is applied here.
    Both profiles mask maximum HP and HP fraction. Only masked_lethality exposes
    judge flags. Callers' arrays and the raw observation are never modified.
    """
    if profile not in PROFILES:
        raise ValueError(f"profile must be one of {PROFILES}")
    enemies, legal = _rows(raw)
    enemy = _matrix(enemy_features, len(enemies), len(BASE_ENEMY_FIELDS), "enemy_features")
    action = _matrix(action_features, len(legal), len(BASE_ACTION_FIELDS), "action_features")
    ef, af = feature_arrays(raw, enabled=profile == "masked_lethality")
    enemy[:, MASKED_ENEMY_INDICES] = 0
    return np.concatenate((enemy, ef), axis=1), np.concatenate((action, af), axis=1)
