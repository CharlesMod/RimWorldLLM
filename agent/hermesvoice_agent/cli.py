"""Entry point: the engine with the RimWorld adapter."""
from __future__ import annotations

import sys

from .core.cli import main as _main
from .games.rimworld import RimWorldAdapter


def main(argv: list[str] | None = None) -> int:
    return _main(RimWorldAdapter(), argv)


if __name__ == "__main__":
    sys.exit(main())
