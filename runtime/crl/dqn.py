"""Original device resolution helper; learner and optimizer implementation omitted."""

from __future__ import annotations

import torch

def resolve_device(name: str) -> torch.device:
    try:
        dev = torch.device(name)
    except RuntimeError as e:
        raise ValueError(f"unsupported device {name!r} (cpu, cuda[:N] or mps)") from e
    if dev.type == "cuda":
        if not torch.cuda.is_available():
            raise RuntimeError(f"device {name!r} requested but torch.cuda.is_available() is False "
                               f"(torch {torch.__version__}, built for CUDA {torch.version.cuda})")
        if dev.index is not None and dev.index >= torch.cuda.device_count():
            raise RuntimeError(f"device {name!r} requested but only {torch.cuda.device_count()} CUDA devices exist")
    elif dev.type == "mps":
        if not torch.backends.mps.is_available():
            raise RuntimeError(f"device {name!r} requested but MPS is not available")
    elif dev.type != "cpu":
        raise ValueError(f"unsupported device {name!r} (cpu, cuda[:N] or mps)")
    return dev
