#!/usr/bin/env python3
"""Start the released Battle and Choice agents for the native game mod."""
from pathlib import Path
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent / 'python'))

if __name__ == '__main__':
    from sts2_autoplay.__main__ import main
    raise SystemExit(main())
