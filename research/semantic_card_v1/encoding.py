"""Encode explicit public values without a simulator or printed-value fallback."""

from collections.abc import Mapping, Sequence
from numbers import Real

import numpy as np

from .schema import (
    CARD_DENSE_FIELDS, CARD_TYPES, CURRENT_FLAGS, CURRENT_NUMERIC, RARITIES, TARGETS, slog,
)


def encode_current_card(
    *,
    card_type: str,
    rarity: str,
    target: str,
    numeric: Mapping[str, float | None] | None = None,
    flags: Mapping[str, bool | int] | None = None,
) -> np.ndarray:
    """Build one 84-wide record from already-observed values.

    Omitted/None numeric values have value=0 and known=0. Explicit zero has
    value=0 and known=1. This helper does not compute damage, payable cost,
    legality, or any other game mechanic. Callers supply current observations.
    """
    numeric = {} if numeric is None else numeric
    flags = {} if flags is None else flags
    unknown = set(numeric) - set(CURRENT_NUMERIC)
    if unknown:
        raise ValueError(f"Unknown numeric fields: {sorted(unknown)}")
    unknown = set(flags) - set(CURRENT_FLAGS)
    if unknown:
        raise ValueError(f"Unknown flags: {sorted(unknown)}")
    index = {name: i for i, name in enumerate(CARD_DENSE_FIELDS)}
    row = np.zeros(len(CARD_DENSE_FIELDS), dtype=np.float32)
    for prefix, value, allowed in (("type", card_type, CARD_TYPES),
                                   ("rarity", rarity, RARITIES), ("target", target, TARGETS)):
        if value not in allowed:
            raise ValueError(f"Unknown {prefix}: {value!r}; expected one of {allowed}")
        row[index[f"{prefix}:{value}"]] = 1
    for name, value in numeric.items():
        if value is None:
            continue
        if isinstance(value, (bool, np.bool_)) or not isinstance(value, Real) or not np.isfinite(value):
            raise ValueError(f"{name} must be a finite number or None, got {value!r}")
        row[index[name]] = slog(float(value))
        row[index[name + "_known"]] = 1
    for name, value in flags.items():
        if not isinstance(value, (Real, np.bool_)) or value not in (0, 1):
            raise ValueError(f"{name} must be a boolean or 0/1, got {value!r}")
        row[index[name]] = float(value)
    return row


def sparse_fields(row: np.ndarray, fields: Sequence[str]) -> list[dict]:
    """Readable nonzero entries; omitted positions are zero, not necessarily known."""
    if row.ndim != 1 or len(row) != len(fields):
        raise ValueError("Row shape does not match its field definitions")
    return [{"index": i, "name": name, "value": round(float(row[i]), 6)}
            for i, name in enumerate(fields) if row[i] != 0]
