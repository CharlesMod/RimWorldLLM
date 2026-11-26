#!/usr/bin/env python3
"""Controlled A/B probe of the creative features (art, lore, recap, aim, eulogy, vows) on the rich soak colony.

Every piece is made by the agent's OWN code path (core/art.py write_art, core/lore.py coin, core/recap.py build,
core/passes.py speak with the Director's _inner_kw, core/group.py request/trigger), so agent feature flags change the outputs.
The colony, the chronicle and the memories come from fixture_rich.py (the same colony `soak.py --fixture rich` runs).

  python bench/soak/creative_probe.py --fake --reps 2 --out DIR
  python bench/soak/creative_probe.py --url http://127.0.0.1:47480 --arm base --reps 3 --out DIR/base
  python bench/soak/creative_probe.py --url ... --arm ground --set aim_ground=true --set art_use_tale=true --out DIR/ground

--set key=value sets a field on the Director's cfg (dotted keys reach nested configs, e.g. llm.line_max_tokens; int, float, bool
and str are parsed to the field's type).  A key the agent code does not have yet is skipped with a warning, so the same command
line runs on older code.  One arm per invocation; give every arm the same --seed and --reps (the case order is fixed).
Writes DIR/probe.jsonl (one row per piece) and DIR/summary.json (per feature x arm).
"""
from __future__ import annotations

import argparse
import asyncio
import json
import random
import re
import statistics
import sys
import tempfile
import time
import types
from pathlib import Path

HERE = Path(__file__).resolve()
for _p in HERE.parents:
    if (_p / "agent" / "hermesvoice_agent").is_dir():
        sys.path.insert(0, str(_p / "agent"))
        break
sys.path.insert(0, str(HERE.parent))

import httpx  # noqa: E402

import fixture_rich as fx  # noqa: E402
from hermesvoice_agent.core import group, lore, recap  # noqa: E402
from hermesvoice_agent.core.art import ArtJob, write_art  # noqa: E402
from hermesvoice_agent.core.bridge import BridgeClient  # noqa: E402
from hermesvoice_agent.core.calllog import CallLog  # noqa: E402
from hermesvoice_agent.core.chronicle import Entry  # noqa: E402
from hermesvoice_agent.core.config import Config, LLMConfig  # noqa: E402
from hermesvoice_agent.core.director import Director  # noqa: E402
from hermesvoice_agent.core.fakes import FakeLLM  # noqa: E402
from hermesvoice_agent.core.llm import HttpLLM, LLMError  # noqa: E402
from hermesvoice_agent.core.minds import Memory, Relation  # noqa: E402
from hermesvoice_agent.core.passes import Dropped, speak  # noqa: E402
from hermesvoice_agent.games.rimworld.adapter import RimWorldAdapter  # noqa: E402
from hermesvoice_agent.games.rimworld.fakes import FakeBridge  # noqa: E402

FEATURES = ["art", "lore", "recap", "aim", "eulogy", "vows"]
DAY = fx.DAY
NOW = fx.BASE_DAY * DAY + 50000

# aim scenes: (case, speaker, listener, room, when, trigger) -- the trigger is game text (what just happened)
AIM_SCENES = [
    ("feud_cy_kit", 3, 5, "courtyard", "morning, clear", "You and Kit are in the courtyard again, the day after the fight."),
    ("feud_kit_cy", 5, 3, "workshop", "afternoon, clear", "Cy has just walked into the workshop where you are working."),
    ("death_dee_ana", 4, 1, "graveyard", "dawn, fog", "Your brother Hale was buried this morning; Ana is standing beside you."),
    ("minor_ana_tam", 1, 7, "kitchen", "evening, clear", "Your son Tam has been quiet since the raid and is sitting by the stove."),
    ("minor_tam_ana", 7, 1, "kitchen", "evening, clear", "Your mother asks how you are holding up after the raid."),
    ("wedding_cy_mara", 3, 9, "yard", "evening, clear", "You and Mara are about to be married; Kit is watching from the back."),
    ("secret_bram_ana", 2, 1, "bedroom", "night, clear", "Ana asks why you were out of bed at night."),
    ("grief_mara_dee", 9, 4, "dining room", "noon, clear", "Dee has not eaten since the funeral and you are sitting across from her."),
]
LORE_MAJORS = None          # filled from the chronicle: every major entry


# ------------------------------------------------------------------ the counting wrapper
class CountingLLM:
    """Thin wrapper with the same complete() signature: delegates, counts calls and sums call latency (slot-seconds)."""

    def __init__(self, inner):
        self.inner, self.calls, self.slot_s = inner, 0, 0.0

    def __getattr__(self, name):
        return getattr(self.inner, name)

    async def complete(self, messages, *, slot, kind="line", **kw):
        self.calls += 1
        t0 = time.perf_counter()
        try:
            return await self.inner.complete(messages, slot=slot, kind=kind, **kw)
        finally:
            self.slot_s += time.perf_counter() - t0

    async def aclose(self):
        if hasattr(self.inner, "aclose"):
            await self.inner.aclose()


# ------------------------------------------------------------------ cfg --set
def parse_sets(items: list[str]) -> list[tuple[str, str]]:
    out = []
    for it in items or []:
        if "=" not in it:
            raise SystemExit(f"--set needs key=value, got {it!r}")
        k, v = it.split("=", 1)
        out.append((k.strip(), v.strip()))
    return out


def _parse_as(cur, raw: str):
    if isinstance(cur, bool):
        if raw.lower() in ("1", "true", "yes", "on"):
            return True
        if raw.lower() in ("0", "false", "no", "off"):
            return False
        raise ValueError(f"not a bool: {raw!r}")
    if isinstance(cur, int):
        return int(raw)
    if isinstance(cur, float):
        return float(raw)
    return raw


def _guess(raw: str):
    for f in (lambda r: _parse_as(True, r), int, float):
        try:
            return f(raw)
        except ValueError:
            pass
    return raw


def apply_sets(cfg, sets: list[tuple[str, str]], warn=print) -> dict:
    """Set fields on cfg; returns {key: value} actually applied.  Missing keys warn and are skipped; bad values raise ValueError."""
    applied = {}
    for key, raw in sets:
        parts = key.split(".")
        obj = cfg
        for p in parts[:-1]:
            obj = getattr(obj, p, None)
            if obj is None:
                break
        leaf = parts[-1]
        if obj is None or not hasattr(obj, leaf):
            warn(f"warning: cfg has no field {key!r}; --set skipped (older code?)")
            continue
        cur = getattr(obj, leaf)
        try:
            val = _guess(raw) if cur is None else _parse_as(cur, raw)
        except ValueError as e:
            raise ValueError(f"--set {key}={raw}: {e}") from e
        setattr(obj, leaf, val)
        applied[key] = val
    return applied


# ------------------------------------------------------------------ colony
class Colony:
    def __init__(self, args, llm_inner, workdir: Path):
        self.args = args
        self.fb = FakeBridge("tok")
        self.fb.tick = NOW
        self.wedding_day = 0
        fx.build_colony(self.fb, random.Random(args.seed))
        self.fb.config.update({"adult_content": False, "recap": True, "recap_open": True, "wants_enabled": True, "pause_ambient_at_speed": 5})
        self.cfg = Config(bridge_url="http://fake", bridge_token="tok", data_dir=workdir,
                          llm=LLMConfig(base_url=args.url or "http://127.0.0.1:0", model=args.model, timeout_s=args.timeout),
                          wants=True, thought_enabled=True, inner_monologue=True, gatherings=True, meal_talk=True, art_enabled=True,
                          dreams=True, lore=True, gossip_chain=True, reputation=True, recap_enabled=True, bystanders=True,
                          firsts=True, habits=True, chronicle_enabled=True, diary_enabled=True, concerns=True, grudges=True)
        self.adapter = RimWorldAdapter()
        self.bridge = BridgeClient(httpx.AsyncClient(transport=self.fb.transport), "http://fake", "tok", self.adapter.protocol)
        self.llm = CountingLLM(llm_inner)
        self.d = Director(self.cfg, self.bridge, self.llm, self.adapter, rng=random.Random(args.seed + 2))
        self.applied = {}

    async def setup(self, sets):
        self.applied = apply_sets(self.d.cfg, sets, warn=lambda m: print(m, file=sys.stderr))
        await self.d.start()
        for pid in (6, 8):                           # Hale and Old Odo are dead by the time of the probe
            self.fb.pawns[pid]["alive"] = False
        d = self.d
        base = fx.BASE_DAY * DAY
        for e in fx.chronicle_entries(self.wedding_day):
            d.chronicle.state.recent.append(Entry(day=e["day"], game=e["day"] * DAY, kind=e["kind"], text=e["text"]))
            d.chronicle.state.last_day = max(d.chronicle.state.last_day, e["day"])
        for pid, mems in fx.MEMORIES.items():
            m = d.minds.get(pid)
            if m is None:
                continue
            for off, text, imp, people, etype, src in mems:
                m.memories.append(Memory(id=m.next_mem_id(), game=base + off, text=text, importance=imp, people=people, etype=etype, source=src))
        for (a, b), feel in fx.FEELINGS.items():
            m = d.minds.get(a)
            if m is not None:
                r = m.relations.setdefault(str(b), Relation(name=d.names.get(b, str(b))))
                r.feeling = feel
        for pid in d.minds:
            self.fb.scenes[pid] = {"room": {"role": "Kitchen", "impressiveness": 30, "clean": "clean", "temp": 20}, "present": [],
                                   "time_of_day": "morning", "weather": "clear", "danger": 0}

    def scene(self, speaker, listener, room, when, others=()):
        t, _, w = when.partition(",")
        self.fb.scenes[speaker] = {"room": {"role": room, "clean": "clean", "temp": 20},
                                   "present": [{"pawn": i, "job": "standing", "distance": 2, "can_hear": True}
                                               for i in ([listener] if listener else []) + list(others)],
                                   "time_of_day": t.strip(), "weather": w.strip() or "clear"}

    async def close(self):
        await self.bridge.aclose()
        await self.llm.aclose()


# ------------------------------------------------------------------ pieces
def norm(t: str) -> str:
    return " ".join(re.sub(r"[^a-z0-9' ]+", " ", (t or "").lower()).split())


async def timed(col: Colony, coro):
    c0, s0, t0 = col.llm.calls, col.llm.slot_s, time.perf_counter()
    try:
        res = await coro
    except LLMError as e:
        res = ("__error__", str(e))
    return res, {"latency_s": round(time.perf_counter() - t0, 4), "calls": col.llm.calls - c0, "slot_s": round(col.llm.slot_s - s0, 4)}


def row(args, feature, case, rep, actor, text, reason, meta, ctx=None, **extra):
    r = {"arm": args.arm, "sets": args.sets_applied, "feature": feature, "case": case, "rep": rep, "actor": actor,
         "text": text, "drop": reason, **meta, **extra}
    if ctx:
        r["context"] = ctx
    return r


async def run_art(col: Colony, args, rep) -> list[dict]:
    rows = []
    d = col.d
    for k, tale in enumerate(fx.ART_TALES):
        data = fx.art_data(0, k, 900 + k, col.wedding_day)
        if data is None:
            continue
        mk = fx.art_maker(0, k)
        if mk in (6, 8):
            mk = 1
        e = col.adapter.normalize_event(col.fb.add_event("art_created", [(mk, "maker")], f"{fx.NAMEBOOK[mk]} finished a {data['def_label']}.", [mk], 0.2, data=data))
        job = d.art.note(e, NOW)
        if job is None:
            rows.append(row(args, "art", tale[0], rep, mk, None, "no_job", {"latency_s": 0, "calls": 0, "slot_s": 0}))
            continue
        res, meta = await timed(col, write_art(d, job, d.cfg.narrator_slot))
        text, why = ((None, f"llm_error:{res[1]}") if isinstance(res, tuple) and res and res[0] == "__error__" else res)
        rows.append(row(args, "art", tale[0], rep, mk, text, why or None, meta,
                        {"maker": d.names.get(mk), "item": data["def_label"], "material": data.get("material"), "title": data["title"],
                         "game_text": data["description"], "tale": data.get("tale")}))
    return rows


async def run_lore(col: Colony, args, rep) -> list[dict]:
    rows = []
    d = col.d
    d.lore = lore.LoreBook()                    # each rep starts without earlier names
    for i, e in enumerate(x for x in d.chronicle.state.recent if x.kind == "major"):
        res, meta = await timed(col, lore.coin(d, e.text, e.game, e.day))
        if isinstance(res, tuple):
            text, why = None, f"llm_error:{res[1]}"
        else:
            text, why = (res.text, None) if res else (None, "none_or_dropped")
        rows.append(row(args, "lore", f"major{i}", rep, None, text, why, meta, {"event": e.text, "day": e.day}))
    return rows


async def run_recap(col: Colony, args, rep) -> list[dict]:
    d = col.d
    ents = [{"day": e.day, "kind": e.kind, "text": e.text} for e in d.chronicle.state.recent]
    wins = [("early", ents[0:5]), ("mid", ents[2:7]), ("late", ents[-6:])]
    rows = []
    for name, w in wins:
        res, meta = await timed(col, recap.build(d, w))
        if isinstance(res, tuple):
            text, why, lines = None, f"llm_error:{res[1]}", []
        else:
            lines = list(res)
            text, why = ("\n".join(lines), None) if lines else (None, "empty")
        rows.append(row(args, "recap", name, rep, None, text, why, meta, {"window": [e["text"] for e in w[-4:]]}, lines=lines))
    return rows


class _Lease:
    def __init__(self):
        self.data = {}


async def run_aim(col: Colony, args, rep) -> list[dict]:
    d = col.d
    rows = []
    for case, sp, li, room, when, trig in AIM_SCENES:
        mind = d.minds[sp]
        mind.inner = []                              # no carry-over between scenes and reps
        col.scene(sp, li, room, when)
        lease = _Lease()
        ev = {"actors": [{"id": sp, "role": "speaker"}, {"id": li, "role": "listener"}]}

        async def go():
            ctx, _, _ = await d._prepare(sp, trig, ev, lease, conv_with=li)
            minor = d.is_minor(sp) or d.is_minor(li) or ctx.minor_involved
            ctx.minor_involved = minor
            return await speak(d.llm, slot=d.cfg.narrator_slot, mind=mind, name=d.names[sp], prefix=lease.data["prefix"], turns=[],
                               turn_no=0, ctx=ctx, listener_name=d.names[li], labels=d.labels_for(minor),
                               max_tokens=d.cfg.llm.line_max_tokens, speech=lease.data.get("speech"), **d._inner_kw(mind, li))

        res, meta = await timed(col, go())
        ctxd = {"scene": f"{room}, {when}", "trigger": trig, "speaker": d.names[sp], "listener": d.names[li]}
        if isinstance(res, tuple):
            rows.append(row(args, "aim", case, rep, sp, None, f"llm_error:{res[1]}", meta, ctxd, label=None, delivery=None, line=None))
        elif isinstance(res, Dropped):
            rows.append(row(args, "aim", case, rep, sp, None, f"line_dropped:{res.reason}", meta, ctxd, label=None, delivery=None, line=None))
        else:
            rows.append(row(args, "aim", case, rep, sp, res.aim or None, None if res.aim else "no_aim", meta, ctxd,
                            label=res.intent, delivery=res.delivery, line=res.text))
    return rows


def _gathering(col: Colony, kind, lead, couple=(), deceased="") -> group.Gathering:
    d = col.d
    pool = [i for i in d.minds if i not in (6, 8)]
    return group.Gathering(id=f"probe-{kind}", kind=kind, key=f"probe-{kind}-{deceased or 'x'}", lead=lead, couple=list(couple), subject=None,
                           pool=pool, speakers=[lead] + [i for i in pool if i != lead][:3], names={i: d.names[i] for i in d.minds},
                           label=kind, deceased=deceased, minor=any(d.is_minor(i) for i in pool))


async def _group_turn(col: Colony, g: group.Gathering, me: int):
    d = col.d
    mind = d.minds[me]
    mind.inner = []
    lease = _Lease()
    trig = group.trigger(g, me)
    col.scene(me, None, "yard" if g.kind == "wedding" else "graveyard", "evening, clear", others=[i for i in g.pool if i != me][:4])
    ctx, _, _ = await d._prepare(me, trig, None, lease)
    ctx.minor_involved = ctx.minor_involved or g.minor
    ctx.secrets = []
    req, _ = group.request(g, me)
    mt = d.cfg.eulogy_max_tokens if (g.kind == "funeral" and me == g.lead) else d.cfg.llm.line_max_tokens
    return await speak(d.llm, slot=d.cfg.narrator_slot, mind=mind, name=g.names[me], prefix=lease.data["prefix"], turns=[], turn_no=0,
                       ctx=ctx, listener_name=None, labels=d.labels_for(ctx.minor_involved), max_tokens=mt, request=req,
                       speech=lease.data.get("speech"))


async def run_group(col: Colony, args, rep, feature) -> list[dict]:
    rows = []
    if feature == "eulogy":
        cases = [("eulogy_odo", _gathering(col, "funeral", 1, deceased="Old Odo"), 1), ("eulogy_hale", _gathering(col, "funeral", 4, deceased="Hale"), 4)]
    else:
        g = _gathering(col, "wedding", 4, couple=(3, 9))
        cases = [("vow_cy", g, 3), ("vow_mara", g, 9)]
    for case, g, me in cases:
        res, meta = await timed(col, _group_turn(col, g, me))
        cx = {"occasion": g.kind, "deceased": g.deceased, "couple": [g.names[c] for c in g.couple], "speaker": g.names[me]}
        if isinstance(res, tuple):
            rows.append(row(args, feature, case, rep, me, None, f"llm_error:{res[1]}", meta, cx))
        elif isinstance(res, Dropped):
            rows.append(row(args, feature, case, rep, me, None, res.reason, meta, cx))
        else:
            rows.append(row(args, feature, case, rep, me, res.text, None, meta, cx))
    return rows


# ------------------------------------------------------------------ summary
def jaccard(a: set, b: set) -> float:
    return len(a & b) / len(a | b) if a | b else 0.0


def pct(vals, p):
    if not vals:
        return None
    s = sorted(vals)
    return round(s[min(len(s) - 1, int(round(p * (len(s) - 1))))], 4)


def summarize(rows: list[dict], arm: str) -> dict:
    out: dict = {}
    for f in FEATURES:
        rs = [r for r in rows if r["feature"] == f]
        if not rs:
            continue
        lat = [r["latency_s"] for r in rs]
        ent = {"arm": arm, "n": len(rs), "dropped": sum(r["text"] is None for r in rs),
               "latency_median": round(statistics.median(lat), 4), "latency_p90": pct(lat, 0.9),
               "calls_mean": round(statistics.mean(r["calls"] for r in rs), 3),
               "slot_s_mean": round(statistics.mean(r.get("slot_s", 0) for r in rs), 4)}
        if f == "aim":
            texts = [r for r in rs if r["text"]]
            words = [norm(r["text"]).split() for r in texts]
            ent["words_mean"] = round(statistics.mean(len(w) for w in words), 2) if words else None
            ent["share_i_just_want"] = round(sum(norm(r["text"]).startswith("i just want") for r in texts) / len(texts), 3) if texts else None
            uniq = {norm(r["text"]) for r in texts}
            ent["distinct_aim_share"] = round(len(uniq) / len(texts), 3) if texts else None
            js = []
            for rep in {r["rep"] for r in texts}:
                ws = [set(norm(r["text"]).split()) for r in texts if r["rep"] == rep]
                js += [jaccard(ws[i], ws[j]) for i in range(len(ws)) for j in range(i + 1, len(ws))]
            ent["mean_pairwise_jaccard"] = round(statistics.mean(js), 4) if js else None
        out[f] = ent
    return out


# ------------------------------------------------------------------ main
async def amain(args) -> int:
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    feats = [f.strip() for f in args.features.split(",") if f.strip()]
    bad = [f for f in feats if f not in FEATURES]
    if bad:
        raise SystemExit(f"unknown feature(s) {bad}; choose from {FEATURES}")
    sets = parse_sets(args.set)
    work = tempfile.TemporaryDirectory(prefix="creative-probe-", dir=str(out))
    inner = FakeLLM(script={"lore": ["The Rice Row: where the old man died"] * 500}) if args.fake else HttpLLM(LLMConfig(base_url=args.url, model=args.model, timeout_s=args.timeout), CallLog(out / "calllog.jsonl"))
    col = Colony(args, inner, Path(work.name))
    await col.setup(sets)
    args.sets_applied = col.applied
    rows: list[dict] = []
    try:
        for rep in range(args.reps):
            random.seed(args.seed * 1000 + rep)
            for f in feats:
                if f == "art":
                    rows += await run_art(col, args, rep)
                elif f == "lore":
                    rows += await run_lore(col, args, rep)
                elif f == "recap":
                    rows += await run_recap(col, args, rep)
                elif f == "aim":
                    rows += await run_aim(col, args, rep)
                else:
                    rows += await run_group(col, args, rep, f)
    finally:
        try:
            await asyncio.wait_for(col.d.drain(), timeout=5)
        except Exception:  # noqa: BLE001 - a stuck background task must not lose the rows
            pass
        await col.close()
        work.cleanup()
    with (out / "probe.jsonl").open("w", encoding="utf-8") as fh:
        for r in rows:
            fh.write(json.dumps(r, ensure_ascii=False, default=str) + "\n")
    summ = {"arm": args.arm, "sets": col.applied, "seed": args.seed, "reps": args.reps, "features": summarize(rows, args.arm)}
    (out / "summary.json").write_text(json.dumps(summ, indent=1, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"out": str(out), "rows": len(rows), "summary": {f: {k: v for k, v in s.items() if k in ("n", "dropped", "latency_median", "calls_mean")}
                                                                     for f, s in summ["features"].items()}}, indent=1))
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--url", default=None, help="OpenAI-compatible engine (the agent's HttpLLM)")
    ap.add_argument("--fake", action="store_true", help="the agent's FakeLLM (no network)")
    ap.add_argument("--model", default="")
    ap.add_argument("--timeout", type=float, default=120.0)
    ap.add_argument("--reps", type=int, default=3)
    ap.add_argument("--features", default=",".join(FEATURES), help="comma list of: " + ",".join(FEATURES))
    ap.add_argument("--arm", default="base")
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE", help="set a cfg field (repeatable)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args(argv)
    if not args.fake and not args.url:
        ap.error("give --url or --fake")
    return asyncio.run(amain(args))


if __name__ == "__main__":
    sys.exit(main())
