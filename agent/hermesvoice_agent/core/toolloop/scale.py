"""Ordinal scales: a magnitude the model picks as words, the game reads as a number.

A small model is unreliable at numbers inside a range (importance 100 on a 1-5 scale) but good at picking
one phrase from a short list. A ``Scale`` is that list, in order, with the number each phrase stands for.
"""
from __future__ import annotations

import re
from dataclasses import dataclass


def _norm(s: str) -> str:
    return re.sub(r"[^a-z0-9 ]+", " ", s.lower()).strip()


# modifier words fall into three classes so "pretty important" can meet "fairly important"
_CLASS = {}
for _w in ("very", "extremely", "highly", "really", "super", "hugely", "greatly", "much", "far", "lots", "totally", "utterly"):
    _CLASS[_w] = "<S>"
for _w in ("fairly", "pretty", "quite", "rather", "moderately", "reasonably"):
    _CLASS[_w] = "<M>"
for _w in ("slightly", "barely", "somewhat", "mildly", "little", "bit", "kind", "sort", "marginally", "minorly"):
    _CLASS[_w] = "<W>"
_DROP = {"of", "a", "an", "the", "at", "all", "is", "it", "i", "feel", "so", "to", "be"}


def _tokens(text: str) -> set[str]:
    out: set[str] = set()
    for w in _norm(text).split():
        if w in _CLASS:
            out.add(_CLASS[w])
        elif w in _DROP:
            continue
        elif w.startswith("un") and len(w) > 4:
            out.update({"not", w[2:]})
        else:
            out.add(w)
    return out


@dataclass(frozen=True)
class Scale:
    """``bins`` are (phrase, value) pairs ordered from lowest to highest."""
    name: str
    bins: tuple[tuple[str, int], ...]

    def __post_init__(self):
        if len(self.bins) < 2:
            raise ValueError("a scale needs at least two bins")
        vals = [v for _, v in self.bins]
        if vals != sorted(vals) or len(set(vals)) != len(vals):
            raise ValueError("bin values must be strictly increasing")
        if len({_norm(p) for p, _ in self.bins}) != len(self.bins):
            raise ValueError("bin phrases must be distinct")

    @classmethod
    def of(cls, name: str, phrases: list[str], values: list[int] | None = None, start: int = 1) -> "Scale":
        vals = values if values is not None else list(range(start, start + len(phrases)))
        return cls(name, tuple(zip(phrases, vals)))

    @property
    def words(self) -> list[str]:
        return [p for p, _ in self.bins]

    @property
    def values(self) -> list[int]:
        return [v for _, v in self.bins]

    @property
    def lo(self) -> int:
        return self.bins[0][1]

    @property
    def hi(self) -> int:
        return self.bins[-1][1]

    def to_value(self, phrase: str) -> int:
        """Exact (case/punctuation-insensitive) phrase to number. KeyError when it is not a bin."""
        n = _norm(phrase)
        for p, v in self.bins:
            if _norm(p) == n:
                return v
        raise KeyError(phrase)

    def has(self, phrase: object) -> bool:
        try:
            self.to_value(str(phrase))
            return True
        except KeyError:
            return False

    def nearest_bin(self, number: float) -> tuple[str, int]:
        """The bin whose value is closest to ``number`` (ties go to the lower one); out-of-range clamps to the end."""
        return min(self.bins, key=lambda b: (abs(b[1] - number), b[1]))

    def to_words(self, number: float) -> str:
        """Phrase to show the model for a number the game produced."""
        return self.nearest_bin(number)[0]

    def parse(self, text: object) -> int | None:
        """Tolerant parse for free text (outside grammar mode). Returns a value, or None when nothing fits.

        Order: exact phrase, a bare number (nearest bin), then the bin that shares the most content words with the text,
        modifier words compared by strength class (strong, middle, weak); ties go toward the middle of the scale.
        by class-aware word overlap, so "pretty important" lands on "fairly important" and "slightly better" on "a little better".
        """
        if isinstance(text, bool):
            return None
        if isinstance(text, (int, float)):
            return self.nearest_bin(text)[1]
        t = _norm(str(text))
        if not t:
            return None
        try:
            return self.to_value(t)
        except KeyError:
            pass
        if re.fullmatch(r"-?\d+(\.\d+)?", str(text).strip()):
            return self.nearest_bin(float(str(text).strip()))[1]
        want = _tokens(t)
        mid = (len(self.bins) - 1) / 2
        best, best_key = None, None
        for idx, (p, _) in enumerate(self.bins):
            have = _tokens(p)
            content = {w for w in want & have if not w.startswith("<")}
            if not content:
                continue
            jac = len(want & have) / len(want | have)
            key = (jac, -abs(idx - mid))
            if best_key is None or key > best_key:
                best, best_key = idx, key
        return None if best is None else self.bins[best][1]


# Two ready-made scales a game can use as they are or copy.
IMPORTANCE = Scale.of("importance", ["not important", "kind of important", "fairly important", "very important", "critical"], start=1)
CHANGE = Scale.of("change", ["much worse", "worse", "a little worse", "no change", "a little better", "better", "much better"], [-3, -2, -1, 0, 1, 2, 3])
