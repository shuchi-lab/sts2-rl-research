"""Initialize the portable release before importing context modules."""
import os
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent

def bootstrap():
    os.environ["STS2_RL_ENGINE_LINE"] = "build08"
    os.environ["STS2_RL_INPUT_BUNDLE"] = str(ROOT / "data/frozen_three_act_214_inputs")
    return {"engine_line":"build08", "input_bundle":os.environ["STS2_RL_INPUT_BUNDLE"]}
