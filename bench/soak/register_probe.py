#!/usr/bin/env python3
"""Controlled register and character-card probe, through the agent's own prompt path (no re-implemented prompts).

1. Register pairs (matched cards, differing only in register inputs): smart/dull, sober/drunk, adult/child.
   ~12 scenes x reps: one spoken line (line_pass, sanitised) per pawn per scene per rep, plus one thought and one diary
   entry per pawn per rep. Per-pawn stats and Cohen's d (+ bootstrap 95% CI) per metric per pair.
2. Card cost: block B and the full prefix, in chars and tokens, full card vs the OLD snapshot fields.
3. Card grounding sample: 3 rich pawns x 8 lines in history-inviting scenes, saved with the card text for a judge.

  python bench/soak/register_probe.py --fake --reps 2 --out DIR
  python bench/soak/register_probe.py --url http://127.0.0.1:8080 --model e4b --reps 4 --out DIR

Thoughts and diary: the user message is assembled inline in core/idle.py (IdleRunner._thought/_diary, which need a
Director), so this file mirrors that assembly with the same pieces (adapter prompts, gamedata, speech.register_line,
sanitize_prose, same token caps/temperatures). It is not a copy of the prompts: the text comes from the adapter.
Writes DIR/probe.json (all rows) and DIR/probe-summary.json.
"""
from __future__ import annotations

import argparse, asyncio, copy, json, math, random, re, statistics, sys, time
from pathlib import Path

HERE = Path(__file__).resolve()
for _p in HERE.parents:                      # find <repo>/agent whatever the worktree
    if (_p / "agent" / "rimworldllm_agent").is_dir():
        sys.path.insert(0, str(_p / "agent"))
        break

import httpx  # noqa: E402

from rimworldllm_agent.core import agegate, gamedata as gd, speech  # noqa: E402
from rimworldllm_agent.core.common_words import COMMON_SENTENCE_START  # noqa: E402
from rimworldllm_agent.core.config import LLMConfig  # noqa: E402
from rimworldllm_agent.core.fakes import FakeLLM  # noqa: E402
from rimworldllm_agent.core.llm import HttpLLM, LLMError  # noqa: E402
from rimworldllm_agent.core.minds import FixedBio, Mind  # noqa: E402
from rimworldllm_agent.core.passes import SanitizeCtx, line_pass  # noqa: E402
from rimworldllm_agent.core.prompt import BIO_LINE_CAP, EVOLVING_CAP, block_b, build_prefix  # noqa: E402
from rimworldllm_agent.core.sanitize import name_tokens, prompt_cap_words, sanitize_prose  # noqa: E402
from rimworldllm_agent.games.rimworld import builders, register  # noqa: E402
from rimworldllm_agent.games.rimworld.adapter import RimWorldAdapter  # noqa: E402
from rimworldllm_agent.games.rimworld.fakes import rich_pawn  # noqa: E402

ADAPTER = RimWorldAdapter()
METRICS = ["mean_word_len", "words_per_sentence", "rare_share", "mattr50", "drop_rate"]

# (room role, time/weather, trigger)
SCENES = [
    ("hospital", "evening, clear", "You were just patched up after a bad cut on the arm."),
    ("dining room", "noon, clear", "A meal was just served: roast with vegetables."),
    ("outdoors", "dusk, smoke in the air", "A raid is coming over the ridge; the alarm just went off."),
    ("hallway", "morning, clear", "A colonist just called you useless in front of everyone."),
    ("workshop", "afternoon, clear", "A colonist just praised the quality of your work."),
    ("rec room", "late evening, clear", "Nothing has happened for hours and you are bored."),
    ("graveyard", "dawn, fog", "A close friend died last night and was just buried."),
    ("bedroom", "evening, clear", "A colonist just handed you a small gift."),
    ("outdoors", "midday, heavy rain and wind", "A storm is soaking everything and the wind will not stop."),
    ("research bench", "afternoon, clear", "The colony just finished an important research project."),
    ("rec room", "night, clear", "A party is under way: music, drinks, people laughing."),
    ("dining room", "evening, clear", "Two colonists are arguing loudly about whose turn it is to cook."),
]
HISTORY_SCENES = [
    ("campfire", "evening, clear", "A newcomer asks where you are from."),
    ("workshop", "afternoon, clear", "Someone mentions your old job."),
    ("campfire", "night, clear", "A quiet evening by the fire; nobody is talking."),
]
THOUGHT_SCENES = [0, 3, 6]      # memory lines for the thought prompt
DIARY_SCENES = [1, 4, 8, 10]    # events of the day for the diary prompt


# ------------------------------------------------------------------ pawns
def _gene(d: str, label: str) -> dict:
    return {"def": d, "label": label}


def _skills(intel: int | None, passion: str = "major") -> list[dict]:
    s = [{"skill": "Medicine", "level": 9, "passion": "minor"}]
    if intel is not None:
        s.insert(0, {"skill": "Intellectual", "level": intel, "passion": passion})
    return s


TRAIT = {
    "abrasive": {"def": "Abrasive", "label": "abrasive", "degree": 0, "desc": "Has a sharp tongue and little patience for small talk."},
    "kind": {"def": "Kind", "label": "kind", "degree": 0, "desc": "Warm and generous; goes out of their way to be pleasant."},
    "toosmart": {"def": "TooSmart", "label": "too smart", "degree": 0, "desc": "Learns quickly but forgets to eat, and is bothered by tedious company."},
}
DRUNK = {"conditions": [{"def": "AlcoholHigh", "label": "drunk", "part": None, "severity": 0.7, "tended": False}],
         "missing_parts": [], "pain": 0.0, "consciousness": 1.0, "talking": 1.0, "downed": False}


def make_pairs() -> dict[str, tuple[dict, dict]]:
    """pair -> (A, B) snapshots: same rich card, differing only in register inputs. A is the expected-higher register."""
    base = dict(skills=_skills(None), genes=[_gene("Hair_Dark", "dark hair")])
    smart = rich_pawn(11, "Ada", traits=[TRAIT["abrasive"], TRAIT["toosmart"]], skills=_skills(17),
                      genes=[_gene("AptitudeStrong_Intellectual", "strong intellectual aptitude")])
    dull = rich_pawn(12, "Ada", traits=[TRAIT["abrasive"]], skills=_skills(1),
                     genes=[_gene("AptitudePoor_Intellectual", "poor intellectual aptitude")])
    sober = rich_pawn(21, "Bram", traits=[TRAIT["kind"]], skills=_skills(8, "none"), genes=base["genes"])
    drunk = rich_pawn(22, "Bram", traits=[TRAIT["kind"]], skills=_skills(8, "none"), genes=base["genes"], health=copy.deepcopy(DRUNK))
    adult = rich_pawn(31, "Cora", age=30, traits=[TRAIT["kind"]], skills=_skills(8, "none"), genes=base["genes"])
    child = rich_pawn(32, "Cora", age=9, traits=[TRAIT["kind"]], skills=_skills(8, "none"), genes=base["genes"])
    return {"smart_vs_dull": (smart, dull), "sober_vs_drunk": (sober, drunk), "adult_vs_child": (adult, child)}


def make_grounding_pawns() -> list[dict]:
    return [
        rich_pawn(41, "Ada"),
        rich_pawn(42, "Mara", name={"first": "Mara", "nick": "Flint", "last": "Okoro"},
                  backstory={"child": {"title": "Tribal hunter's child", "desc": "Raised on the plains, learning to track and skin game before she could read."},
                             "adult": {"title": "Caravan guard", "desc": "Spent years walking beside salt caravans and sleeping with one hand on a spear."}},
                  traits=[TRAIT["abrasive"]], skills=_skills(6, "none"),
                  ideo={"name": "The Long Walkers", "memes": [{"def": "Tribal", "label": "tribal", "desc": "The old ways keep the tribe alive."}],
                        "precepts": ["Animal sacrifice: required"], "certainty": 0.7},
                  history={"arrival": "Walked in alone through a dust storm.", "tales": ["Fought off a pack of wargs"]}),
        rich_pawn(43, "Tobias", name={"first": "Tobias", "nick": "Tobi", "last": "Lund"}, age=52,
                  backstory={"child": {"title": "Orbital shipwright's son", "desc": "Born in a hull yard, he learned welding before multiplication."},
                             "adult": {"title": "Ship cook", "desc": "Fed a crew of forty on a freighter for twenty years, mostly on rations and stubbornness."}},
                  traits=[TRAIT["kind"]], skills=_skills(5, "none"),
                  history={"arrival": "Escaped a dying freighter in a lifepod.", "tales": ["Cooked a feast from almost nothing"]}),
    ]


# ------------------------------------------------------------------ prompt path
class Rig:
    def __init__(self, llm, tokenizer):
        self.llm = llm
        self.tok = tokenizer

    def mind_for(self, snap: dict, voice) -> Mind:
        names = {snap["id"]: builders.display_name(snap)}
        fixed = builders.build_fixed_bio(snap, names)
        return Mind(actor_id=snap["id"], name=builders.display_name(snap), fixed=fixed, voice=voice)

    def scene(self, snap: dict, mind: Mind, spec) -> "object":
        role, when, trig = spec
        sc = {"room": {"role": role}, "present": [], "time_of_day": when.split(",")[0], "weather": ", ".join(when.split(",")[1:]).strip()}
        return ADAPTER.build_scene(snap, sc, trig, {snap["id"]: mind.name}, {snap["id"]}, {snap["id"]} if agegate.is_minor(snap.get("age")) else set())

    def prefix_ctx(self, snap: dict, mind: Mind, scene):
        prefix = build_prefix(ADAPTER.block_a, mind, [], [], scene)
        toks = name_tokens(mind.name)
        minor = agegate.is_minor(snap.get("age"))
        ctx = SanitizeCtx(toks, set(toks), set(toks), prompt_cap_words(prefix), set(ADAPTER.common_words),
                          minor_involved=minor, minor_names=set(toks) if minor else set(),
                          third_person=mind.voice.speaks_third_person(toks), intent_labels=tuple(ADAPTER.intent_labels))
        return prefix, ctx

    async def line(self, snap, mind, spec) -> dict:
        scene = self.scene(snap, mind, spec)
        prefix, ctx = self.prefix_ctx(snap, mind, scene)
        s, _ = await line_pass(self.llm, slot=0, mind=mind, name=mind.name, prefix=prefix, turns=[], turn_no=0, ctx=ctx,
                               speech=scene.speech_short or None)
        return {"text": s.text, "dropped": not s.ok, "reason": "" if s.ok else s.reason,
                "right_now": scene.speech_now, "trigger": spec[2]}

    async def thought(self, snap, mind, words, idxs) -> dict:
        mem = "\n".join(f"- {gd.clean(SCENES[i][2], 'event')}" for i in idxs)
        user = (f"=== WHO YOU ARE ===\n{gd.data_section('bio', gd.clean(mind.fixed.text, 'line', max_len=BIO_LINE_CAP, multiline=True))}"
                f"=== WHO YOU ARE BECOMING ===\n{gd.data_section('character', gd.clean(mind.evolving_bio or '(nothing yet)', 'desc', max_len=EVOLVING_CAP))}"
                f"=== WHAT IS ON YOUR MIND ===\n{gd.data_section('memories', mem)}"
                f"=== REQUEST ===\n{speech.register_line(mind.name, words)}{ADAPTER.thought_request}")
        minor = agegate.is_minor(snap.get("age"))
        system = ADAPTER.thought_system + "\n\n" + gd.DATA_RULE + (("\n\n" + agegate.MINOR_RULE) if minor else "")
        return await self._prose(snap, mind, user, system, "thought", 40, ["\n"], 0.9, 2, 18)

    async def diary(self, snap, mind, words, idxs) -> dict:
        today = "\n".join(f"- {gd.clean(SCENES[i][2], 'event')}" for i in idxs)
        user = (f"=== WHO YOU ARE ===\n{gd.data_section('bio', gd.clean(mind.fixed.text, 'line', max_len=BIO_LINE_CAP, multiline=True))}"
                f"=== WHO YOU ARE BECOMING ===\n{gd.data_section('character', gd.clean(mind.evolving_bio or '(nothing yet)', 'desc', max_len=EVOLVING_CAP))}"
                f"=== WHAT HAPPENED TODAY ===\n{gd.data_section('events', today)}"
                f"=== REQUEST ===\n{speech.register_line(mind.name, words)}{ADAPTER.diary_request}")
        system = ADAPTER.diary_system + "\n\n" + gd.DATA_RULE
        return await self._prose(snap, mind, user, system, "diary", 160, [], 0.8, 5, 90)

    async def _prose(self, snap, mind, user, system, kind, max_tokens, stop, temp, lo, hi) -> dict:
        try:
            c = await self.llm.complete([("system", system), ("user", user)], slot=0, kind=kind, max_tokens=max_tokens,
                                        stop=stop, temperature=temp, actor=mind.actor_id)
        except LLMError as e:
            return {"text": None, "dropped": True, "reason": f"llm_error:{e}"}
        toks = name_tokens(mind.name)
        minor = agegate.is_minor(snap.get("age"))
        s = sanitize_prose(c.text, known_names=set(toks), all_names=set(toks), prompt_words=prompt_cap_words(user),
                           common_words=set(ADAPTER.common_words), speaker_names=set(toks), min_words=lo, max_words=hi,
                           involves_minor=minor, minor_names=set(toks) if minor else set())
        return {"text": s.text, "dropped": not s.ok, "reason": "" if s.ok else s.reason}


# ------------------------------------------------------------------ stats
WORD = re.compile(r"[A-Za-z']+")


def words_of(t: str) -> list[str]:
    return WORD.findall(t or "")


def text_metrics(t: str) -> dict:
    w = words_of(t)
    if not w:
        return {}
    sents = [s for s in re.split(r"[.!?]+", t) if words_of(s)]
    return {"mean_word_len": sum(len(x) for x in w) / len(w),
            "words_per_sentence": len(w) / max(1, len(sents)),
            "rare_share": sum(1 for x in w if x.lower() not in COMMON_SENTENCE_START) / len(w)}


def mattr(words: list[str], window: int = 50) -> float | None:
    w = [x.lower() for x in words]
    if not w:
        return None
    if len(w) <= window:
        return len(set(w)) / len(w)
    return statistics.fmean(len(set(w[i:i + window])) / window for i in range(len(w) - window + 1))


def cohen_d(a: list[float], b: list[float]) -> float | None:
    if len(a) < 2 or len(b) < 2:
        return None
    va, vb = statistics.variance(a), statistics.variance(b)
    sp = math.sqrt(((len(a) - 1) * va + (len(b) - 1) * vb) / (len(a) + len(b) - 2))
    if sp == 0:
        return 0.0 if statistics.fmean(a) == statistics.fmean(b) else None
    return (statistics.fmean(a) - statistics.fmean(b)) / sp


def boot_ci(a: list[float], b: list[float], n: int = 1000, seed: int = 7) -> list[float | None]:
    if len(a) < 2 or len(b) < 2:
        return [None, None]
    rng = random.Random(seed)
    ds = []
    for _ in range(n):
        d = cohen_d([rng.choice(a) for _ in a], [rng.choice(b) for _ in b])
        if d is not None:
            ds.append(d)
    if len(ds) < 20:
        return [None, None]
    ds.sort()
    return [round(ds[int(0.025 * len(ds))], 3), round(ds[int(0.975 * len(ds)) - 1], 3)]


def samples(rows: list[dict], pawn: str, kind: str) -> dict[str, list[float]]:
    """Per-metric sample vectors for one pawn and kind. word-level metrics: one value per kept text; MATTR: one per rep
    (that rep's texts joined); drop_rate: one 0/1 per text."""
    out: dict[str, list[float]] = {m: [] for m in METRICS}
    by_rep: dict[int, list[str]] = {}
    for r in rows:
        if r["pawn"] != pawn or r["kind"] != kind:
            continue
        out["drop_rate"].append(1.0 if r["dropped"] else 0.0)
        if r["dropped"]:
            continue
        for k, v in text_metrics(r["text"]).items():
            out[k].append(v)
        by_rep.setdefault(r["rep"], []).append(r["text"])
    for texts in by_rep.values():
        v = mattr(words_of(" ".join(texts)))
        if v is not None:
            out["mattr50"].append(v)
    return out


def pair_stats(rows, a: str, b: str, kind: str) -> dict:
    sa, sb = samples(rows, a, kind), samples(rows, b, kind)
    res = {}
    for m in METRICS:
        x, y = sa[m], sb[m]
        d = cohen_d(x, y)
        res[m] = {"a_mean": round(statistics.fmean(x), 4) if x else None, "b_mean": round(statistics.fmean(y), 4) if y else None,
                  "n_a": len(x), "n_b": len(y), "cohen_d": None if d is None else round(d, 3), "d_ci95": boot_ci(x, y)}
    return res


# ------------------------------------------------------------------ card cost
def old_snapshot(p: dict) -> dict:
    """The pawn reduced to the OLD snapshot fields: no backstory/trait/meme descs, precepts, xenotype_desc, history, appearance."""
    q = copy.deepcopy(p)
    for part in (q.get("backstory") or {}).values():
        if isinstance(part, dict):
            part["desc"] = ""
    for t in q.get("traits") or []:
        t["desc"] = ""
    ideo = q.get("ideo")
    if isinstance(ideo, dict):
        for m in ideo.get("memes") or []:
            m["desc"] = ""
        ideo["precepts"] = []
    q["xenotype_desc"] = ""
    q["history"] = {}
    q["appearance"] = ""
    return q


async def count_tokens(client: httpx.AsyncClient | None, url: str | None, text: str) -> tuple[int, str]:
    if client and url:
        for path in ("/tokenize", "/v1/tokenize"):
            try:
                r = await client.post(url.rstrip("/") + path, json={"content": text}, timeout=20)
                if r.status_code == 200:
                    j = r.json()
                    if isinstance(j.get("tokens"), list):
                        return len(j["tokens"]), path
                    if isinstance(j.get("count"), int):
                        return j["count"], path
            except (httpx.HTTPError, ValueError):
                pass
    return math.ceil(len(text) / 4), "chars/4"


async def card_cost(rig: Rig, client, url, probes: list[tuple[str, dict, object]]) -> dict:
    rows, method = [], "chars/4"
    spec = SCENES[0]
    for tag, snap, voice in probes:
        row = {"pawn": tag}
        for label, s in (("full", snap), ("old", old_snapshot(snap))):
            mind = rig.mind_for(s, voice)
            scene = rig.scene(s, mind, spec)
            prefix = build_prefix(ADAPTER.block_a, mind, [], [], scene)
            b = block_b(mind)
            bt, m1 = await count_tokens(client, url, b)
            pt, m2 = await count_tokens(client, url, prefix)
            method = m2 if m2 != "chars/4" or method == "chars/4" else method
            row[label] = {"block_b_chars": len(b), "prefix_chars": len(prefix), "block_b_tokens": bt, "prefix_tokens": pt}
        for k in ("block_b_chars", "prefix_chars", "block_b_tokens", "prefix_tokens"):
            row["delta_" + k] = row["full"][k] - row["old"][k]
        rows.append(row)
    mean = {k: round(statistics.fmean(r[k] for r in rows), 1) for k in rows[0] if k.startswith("delta_")} if rows else {}
    return {"token_method": method, "per_pawn": rows, "mean_delta": mean}


# ------------------------------------------------------------------ run
async def run(args) -> int:
    out = Path(args.out).expanduser()
    out.mkdir(parents=True, exist_ok=True)
    client = None
    if args.fake:
        llm = FakeLLM()
    else:
        client = httpx.AsyncClient(timeout=args.timeout)
        llm = HttpLLM(LLMConfig(base_url=args.url, mode="openai", model=args.model, timeout_s=args.timeout), client=client)
    rig = Rig(llm, None)
    t0 = time.time()
    rows: list[dict] = []
    pairs = make_pairs()
    pair_voices = {k: builders.build_voice_card(a) for k, (a, _) in pairs.items()}   # one card per pair, shared by both
    probes = []
    for pname, (a, b) in pairs.items():
        for side, snap in (("A", a), ("B", b)):
            tag = f"{pname}:{side}"
            probes.append((tag, snap, pair_voices[pname]))
            mind = rig.mind_for(snap, pair_voices[pname])
            words = register.effective_words(snap)
            for rep in range(args.reps):
                for si, spec in enumerate(SCENES):
                    r = await rig.line(snap, mind, spec)
                    rows.append({"pawn": tag, "pair": pname, "kind": "line", "rep": rep, "scene": si, **r})
                for kind, fn, idxs in (("thought", rig.thought, THOUGHT_SCENES), ("diary", rig.diary, DIARY_SCENES)):
                    r = await fn(snap, mind, words, idxs)
                    rows.append({"pawn": tag, "pair": pname, "kind": kind, "rep": rep, "scene": -1,
                                 "trigger": " | ".join(SCENES[i][2] for i in idxs), **r})
    # grounding
    grounding = []
    for snap in make_grounding_pawns():
        voice = builders.build_voice_card(snap)
        mind = rig.mind_for(snap, voice)
        tag = f"ground:{mind.name}"
        probes.append((tag, snap, voice))
        lines = []
        for i in range(8):
            spec = HISTORY_SCENES[i % len(HISTORY_SCENES)]
            r = await rig.line(snap, mind, spec)
            lines.append({"i": i, "scene": spec[2], **r})
        grounding.append({"pawn": tag, "card_text": mind.fixed.text, "block_b": block_b(mind), "lines": lines})
        for l in lines:
            rows.append({"pawn": tag, "pair": "grounding", "kind": "grounding_line", "rep": l["i"], "scene": l["i"] % 3, **{k: l[k] for k in ("text", "dropped", "reason", "right_now", "trigger")}})
    cost = await card_cost(rig, client, args.url if not args.fake else None, probes)
    if client:
        await client.aclose()
    summary = {"mode": "fake" if args.fake else args.url, "model": args.model, "reps": args.reps, "scenes": len(SCENES),
               "seconds": round(time.time() - t0, 1), "common_words_source": "core/common_words.COMMON_SENTENCE_START (rare = not in it)",
               "register_pairs": {}, "card_cost": cost,
               "rows": len(rows), "drops": sum(1 for r in rows if r["dropped"]),
               "grounding_pawns": [g["pawn"] for g in grounding]}
    for pname in pairs:
        summary["register_pairs"][pname] = {
            kind: pair_stats(rows, f"{pname}:A", f"{pname}:B", kind) for kind in ("line", "thought", "diary")}
        summary["register_pairs"][pname]["_a_words"] = register.effective_words(pairs[pname][0])
        summary["register_pairs"][pname]["_b_words"] = register.effective_words(pairs[pname][1])
        summary["register_pairs"][pname]["_b_right_now"] = next((r["right_now"] for r in rows if r["pawn"] == f"{pname}:B" and r["kind"] == "line"), "")
    (out / "probe.json").write_text(json.dumps({"rows": rows, "grounding": grounding, "card_cost": cost}, indent=1))
    (out / "probe-summary.json").write_text(json.dumps(summary, indent=1))
    print(json.dumps({"out": str(out), "rows": len(rows), "drops": summary["drops"], "seconds": summary["seconds"]}))
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--url", help="OpenAI-compatible endpoint base (e.g. http://127.0.0.1:8080)")
    ap.add_argument("--fake", action="store_true", help="use the agent's FakeLLM (no network)")
    ap.add_argument("--model", default="")
    ap.add_argument("--reps", type=int, default=4)
    ap.add_argument("--out", default=str(Path.home() / ".local/share/rimworld-hermes-engine/e4bpass/probe"))
    ap.add_argument("--timeout", type=float, default=120.0)
    args = ap.parse_args(argv)
    if not args.fake and not args.url:
        ap.error("need --url or --fake")
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
