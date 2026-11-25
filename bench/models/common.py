"""Shared plumbing for the model bench: engine control, fixtures -> the agent's own Mind/Scene/prompt path."""
from __future__ import annotations

import asyncio, contextvars, json, os, re, statistics, subprocess, sys, time
from pathlib import Path

import httpx

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(REPO / "agent"))   # import the real agent package, not a copy

from hermesvoice_agent.core.config import LLMConfig                       # noqa: E402
from hermesvoice_agent.core.llm import HttpLLM                            # noqa: E402
from hermesvoice_agent.core.minds import CRel, FixedBio, Memory, Mind, VoiceCard  # noqa: E402
from hermesvoice_agent.core.passes import SanitizeCtx                     # noqa: E402
from hermesvoice_agent.core.prompt import Scene, build_prefix              # noqa: E402
from hermesvoice_agent.core.sanitize import name_tokens, prompt_cap_words  # noqa: E402
from hermesvoice_agent.games.rimworld.adapter import RimWorldAdapter      # noqa: E402

FX = REPO / "bench/m0/fixtures"
OUT = Path(os.environ.get("BENCH_OUT", Path.home() / ".local/share/rimworld-hermes-engine/bench/results"))
ENGINE = os.environ.get("ENGINE_URL", "http://127.0.0.1:47480")
MODELS = ["gemma4-rp-maxxed-q3kxl", "ling3-tiny-q4km", "gemma4-e4b-q4km", "gemma4-e2b-q4km"]
ADAPTER = RimWorldAdapter()


def fx(name):
    return json.loads((FX / name).read_text())


PAWNS = {p["id"]: p for p in fx("pawns.json")}
ACTOR_ID = {pid: i + 1 for i, pid in enumerate(PAWNS)}
NAMES = {ACTOR_ID[pid]: p["name"] for pid, p in PAWNS.items()}
NAME_TOKENS = {a: name_tokens(n) for a, n in NAMES.items()}
ALL_TOKENS = set().union(*NAME_TOKENS.values())


# ---------------- engine control ----------------
def eng(method, path, **kw):
    h = {"X-Hermes-Engine": "1"} if method != "GET" else {}
    r = httpx.request(method, f"{ENGINE}/engine/v1/{path}", headers=h, timeout=kw.pop("timeout", 30), **kw)
    return r.json() if r.content else {}


def select_model(model: str, timeout=600):
    st = eng("GET", "status")
    if st.get("model") != model or st.get("phase") != "ready":
        r = eng("POST", "install", json={"model": model})
        time.sleep(1)
    t0 = time.time()
    while time.time() - t0 < timeout:
        st = eng("GET", "status")
        if st.get("phase") == "ready" and st.get("model") == model:
            return st
        if st.get("phase") == "error":
            raise RuntimeError(st)
        time.sleep(1)
    raise TimeoutError(model)


def erase_slots(n=3):
    for i in range(n):
        try:
            httpx.post(f"{ENGINE}/slots/{i}?action=erase", timeout=30)
        except httpx.HTTPError:
            pass


def vram_mib():
    try:
        return int(subprocess.check_output(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"]).split()[0])
    except Exception:
        return None


# ---------------- agent client with raw-body capture ----------------
RAW: contextvars.ContextVar[list] = contextvars.ContextVar("RAW")


async def _hook(resp: httpx.Response):
    await resp.aread()
    try:
        lst = RAW.get()
    except LookupError:
        return
    try:
        lst.append(resp.json())
    except ValueError:
        lst.append({"_nonjson": resp.text[:500]})


def make_llm(temperature=None, timeout=120.0, **cfg_extra):
    cfg = LLMConfig(base_url=ENGINE, mode="openai", timeout_s=timeout, **cfg_extra)
    client = httpx.AsyncClient(timeout=timeout, event_hooks={"response": [_hook]})
    return HttpLLM(cfg, client=client)


def raw_of(body: dict) -> dict:
    """Pull the fields the agent ignores out of a chat completion body."""
    ch = (body.get("choices") or [{}])[0]
    m = ch.get("message", {})
    return {"content": m.get("content") or "", "reasoning": m.get("reasoning_content") or "",
            "finish": ch.get("finish_reason"), "completion_tokens": (body.get("usage") or {}).get("completion_tokens"),
            "timings": body.get("timings", {})}


# ---------------- fixtures -> agent objects ----------------
def short(name):
    m = re.search(r"'([^']+)'", name)
    return m.group(1) if m else name.split()[0]


def mind_for(pid: str) -> Mind:
    p = PAWNS[pid]
    vc = p["voice_card"]
    skills = ", ".join(f"{k} {v['level']}" for k, v in p["skills"].items())
    fixed = (f"Name: {p['name']}, {p['age']}, {p['gender']}, body type {p['body']}. Xenotype: {p['xenotype']}.\n"
             f"Backstory: {p['backstory']}\nTraits: {', '.join(p['traits'])}\nSkills: {skills}\n"
             f"Health: {'; '.join(p['health'])}\nFixed bio: {p['fixed_bio']}")
    m = Mind(actor_id=ACTOR_ID[pid], name=p["name"], fixed=FixedBio(text=fixed),
             voice=VoiceCard(register_line=vc["register"], tics=vc["tics"], examples=vc["examples"][:3]),
             evolving_bio=p["evolving_bio"], wants=p["wants"],
             c_relations=[CRel(name=r["name"], opinion=r["opinion"], feeling=r["kind"]) for r in p["relations"]])
    for i, mem in enumerate(p["memories"]):
        m.memories.append(Memory(id=mem["id"], game=1000 + i * 1000, text=mem["text"], importance=mem["importance"]))
    return m


def scene_of(pid: str, sc: dict) -> Scene:
    p = PAWNS[pid]
    others = [PAWNS[o] for o in sc.get("others", []) if o in PAWNS and o != pid]
    return Scene(where=sc["where"], when=sc["time"], present=[f"{o['name']} ({o['job']})" for o in others],
                 state=sc["state"], job=p.get("job", "idle"),
                 thoughts=[f"{t['text']} ({t['mood_offset']:+d})" for t in p["thoughts"]], trigger=sc["trigger"])


def prefix_for(pid: str, sc: dict):
    """(prefix, mind, SanitizeCtx, scene) via the agent's own build_prefix and sanitize context."""
    mind = mind_for(pid)
    mem = {m.id: m for m in mind.memories}
    mems = [mem[i] for i in sc.get("memory_ids", []) if i in mem]
    scene = scene_of(pid, sc)
    prefix = build_prefix(ADAPTER.block_a, mind, mems, list(sc.get("ledger", [])), scene)
    aid = ACTOR_ID[pid]
    known = {aid} | {ACTOR_ID[o] for o in sc.get("others", []) if o in ACTOR_ID}
    known |= {ACTOR_ID[o] for o in PAWNS if any(PAWNS[o]["name"] == r["name"] for r in PAWNS[pid]["relations"])}
    known_tokens = set().union(*(NAME_TOKENS[k] for k in known))
    # dead/unlisted relations (e.g. Hana Brandt) are known to the speaker through block C
    for r in PAWNS[pid]["relations"]:
        known_tokens |= name_tokens(r["name"])
    ctx = SanitizeCtx(NAME_TOKENS[aid], known_tokens, ALL_TOKENS, prompt_cap_words(prefix), set(ADAPTER.common_words),
                      third_person=mind.voice.speaks_third_person(NAME_TOKENS[aid]))
    return prefix, mind, ctx, scene


def med(xs):
    xs = [x for x in xs if x is not None]
    return statistics.median(xs) if xs else None


def p90(xs):
    xs = sorted(x for x in xs if x is not None)
    return xs[min(len(xs) - 1, int(0.9 * len(xs)))] if xs else None


def rate(flags):
    flags = list(flags)
    return round(sum(1 for f in flags if f) / len(flags), 4) if flags else None


def save(model, name, obj):
    d = OUT / model
    d.mkdir(parents=True, exist_ok=True)
    (d / f"{name}.json").write_text(json.dumps(obj, indent=1, ensure_ascii=False))
