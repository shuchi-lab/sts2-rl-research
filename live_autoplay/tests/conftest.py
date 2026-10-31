import sys
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PACKAGE / 'python'))
sys.path.insert(0, str(PACKAGE))
sys.dont_write_bytecode = True
