"""Section 4: OpenAI-style tool calls on /v1/chat/completions with five game tools."""
from __future__ import annotations
import json, time
import httpx, jsonschema
from common import *

TOOLS = [
    {"type": "function", "function": {"name": "say", "description": "Say one line of speech out loud.", "parameters": {
        "type": "object", "properties": {"line": {"type": "string"}}, "required": ["line"], "additionalProperties": False}}},
    {"type": "function", "function": {"name": "remember", "description": "Store a fact in your memory.", "parameters": {
        "type": "object", "properties": {"fact": {"type": "string"}, "importance": {"type": "integer", "minimum": 1, "maximum": 5}},
        "required": ["fact", "importance"], "additionalProperties": False}}},
    {"type": "function", "function": {"name": "set_mood", "description": "Change how you feel about a person.", "parameters": {
        "type": "object", "properties": {"target": {"type": "string"}, "delta": {"type": "integer", "minimum": -3, "maximum": 3}},
        "required": ["target", "delta"], "additionalProperties": False}}},
    {"type": "function", "function": {"name": "start_conversation", "description": "Walk over and start talking with someone.", "parameters": {
        "type": "object", "properties": {"with_name": {"type": "string"}, "topic": {"type": "string"}},
        "required": ["with_name", "topic"], "additionalProperties": False}}},
    {"type": "function", "function": {"name": "do_nothing", "description": "Do nothing this moment.", "parameters": {
        "type": "object", "properties": {}, "additionalProperties": False}}},
]
SCHEMAS = {t["function"]["name"]: t["function"]["parameters"] for t in TOOLS}
SYSTEM = ("You are Rook, a gruff 58-year-old cook in a RimWorld colony. You act only by calling the tools you are given. "
          "Call exactly the tools the situation needs and nothing more. If nothing needs doing, do not call a tool.")

# (user text, expected tool names, kind)
CASES = (
    [(t, {"say"}, "single") for t in [
        "Pip just dropped a full tray of stew on the floor. Tell her off out loud.",
        "Lux says your cooking is cold again. Answer him out loud, sharply.",
        "The raiders are at the gate. Shout a warning to everyone.",
        "Hana's shelf is untouched in the corner. Say something quietly to the empty room about her.",
        "Moth asks if the soup needs salt. Tell her what you think, out loud."]]
    + [(t, {"remember"}, "single") for t in [
        "You just saw Lux steal a loaf from the pantry at night. Write that down in your memory, it matters a lot.",
        "Ash told you the east wall is weak and will fail in the next storm. Commit it to memory.",
        "Pip laughed in the kitchen for the first time since the raid. Save that as a small but dear memory.",
        "The well water tastes of iron since the raid. Make a note of it, minor importance.",
        "Moth saved your life with a field dressing. Record it, top importance."]]
    + [(t, {"set_mood"}, "single") for t in [
        "Lux just threw his plate at you. Your feelings about him get a lot worse. Record that change.",
        "Pip stayed late to help you scrub the stove. Adjust your feelings toward Pip, a little warmer.",
        "Ash apologised for breaking your knife. Slightly improve your opinion of Ash.",
        "Moth lied to you about the missing flour. Lower your opinion of Moth a bit."]]
    + [(t, {"start_conversation"}, "single") for t in [
        "You want to ask Pip to teach the new kid knife work. Go and start a conversation with her about that.",
        "Ash is alone at the wall looking grim. Walk over and start talking with him about the storm.",
        "You need to settle the food rationing with Lux. Begin a conversation with him on rationing.",
        "Moth is in the infirmary. Go and talk to her about your back pain."]]
    + [(t, {"do_nothing"}, "single") for t in [
        "It is the dead of night and you are asleep in your bed. Nothing is happening. Take no action.",
        "Everyone is at work, the stove is off, nothing needs your attention right now. Just wait."]]
    + [(t, {"say", "remember"}, "double") for t in [
        "Lux insulted your stew in front of everyone. Snap back at him out loud, and also save this insult in your memory as important.",
        "Pip brought you tea for your back. Thank her out loud and remember her kindness, importance 3."]]
    + [(t, {"say", "set_mood"}, "double") for t in [
        "Ash covered for you when the cook shift ran late. Say thanks to him out loud and raise your opinion of him by 2.",
        "Moth blamed you for the spoiled food. Tell her she's wrong out loud and lower your opinion of her by 2."]]
    + [("Lux wasted food again. Go start a conversation with Lux about waste, and remember it was the third time, importance 2.", {"start_conversation", "remember"}, "double")]
    + [(t, set(), "none") for t in [
        "Just answer in plain words, no tools: what is two plus two?",
        "Do not use any tool. Reply with the single word: ready.",
        "A message from the game engine: 'save complete'. No action is required of you.",
        "Describe in plain text, without calling a tool, what a stove is.",
        "The weather report says clear skies tomorrow. This is only information; you need not act on it."]]
)


def validate_args(name, args) -> bool:
    try:
        jsonschema.validate(args, SCHEMAS[name])
        return True
    except Exception:
        return False


def post(client, messages, choice, extra=None):
    body = {"messages": messages, "tools": TOOLS, "tool_choice": choice, "max_tokens": 200, "temperature": 0.2,
            "cache_prompt": True, "chat_template_kwargs": {"enable_thinking": False}}
    t0 = time.perf_counter()
    r = client.post(f"{ENGINE}/v1/chat/completions", json=body, timeout=120)
    wall = time.perf_counter() - t0
    try:
        j = r.json()
    except ValueError:
        j = {"_text": r.text[:300]}
    return r.status_code, j, wall


def parse_calls(j):
    ch = (j.get("choices") or [{}])[0]
    m = ch.get("message", {})
    calls = []
    for tc in m.get("tool_calls") or []:
        f = tc.get("function", {})
        raw = f.get("arguments")
        try:
            args = json.loads(raw) if isinstance(raw, str) else (raw or {})
            ok = isinstance(args, dict)
        except ValueError:
            args, ok = None, False
        calls.append({"name": f.get("name"), "args": args, "parsed": ok, "id": tc.get("id"), "raw_args": raw})
    return calls, m.get("content") or "", ch.get("finish_reason"), m


def run(choice: str):
    rows = []
    with httpx.Client() as client:
        for text, exp, kind in CASES:
            msgs = [{"role": "system", "content": SYSTEM}, {"role": "user", "content": text}]
            st, j, wall = post(client, msgs, choice)
            calls, content, fin, m = parse_calls(j)
            names = [c["name"] for c in calls]
            row = {"prompt": text, "kind": kind, "expected": sorted(exp), "status": st, "called": names, "content": content[:200],
                   "finish": fin, "wall_s": round(wall, 2), "calls": calls,
                   "tool_calls_present": bool(calls), "args_parse": all(c["parsed"] for c in calls) if calls else None,
                   "hallucinated": [n for n in names if n not in SCHEMAS],
                   "args_valid": all(c["name"] in SCHEMAS and c["parsed"] and validate_args(c["name"], c["args"]) for c in calls) if calls else None}
            if kind == "none":
                row["right"] = (not calls) or set(names) == {"do_nothing"}
            elif kind == "single":
                row["right"] = bool(calls) and names[0] in exp
                row["exact"] = set(names) == exp and len(names) == 1
            else:
                row["right"] = exp <= set(names)
            row["tool_in_content"] = (not calls) and ("<tool_call>" in content or '"name"' in content or "tool_call" in content)
            # second round: feed a tool result back
            if calls and st == 200:
                am = {"role": "assistant", "content": m.get("content") or "", "tool_calls": m.get("tool_calls")}
                tmsgs = msgs + [am] + [{"role": "tool", "tool_call_id": c["id"] or f"call_{i}", "name": c["name"], "content": json.dumps({"ok": True})} for i, c in enumerate(calls)]
                st2, j2, w2 = post(client, tmsgs, "auto")
                c2, content2, fin2, _ = parse_calls(j2)
                row["round2"] = {"status": st2, "ok": st2 == 200 and (bool(content2.strip()) or bool(c2)), "called": [c["name"] for c in c2],
                                 "content": content2[:160], "finish": fin2, "wall_s": round(w2, 2),
                                 "completes_missing": (kind == "double" and exp <= (set(names) | {c["name"] for c in c2}))}
            rows.append(row)
    return rows


def summarise(rows):
    act = [r for r in rows if r["kind"] != "none"]
    called = [r for r in rows if r["calls"]]
    r2 = [r["round2"] for r in rows if "round2" in r]
    return {"n": len(rows), "http_errors": sum(r["status"] != 200 for r in rows),
            "tool_calls_parse_rate": rate(r["args_parse"] for r in called) if called else None,
            "tool_calls_present_on_action_prompts": rate(r["tool_calls_present"] for r in act),
            "right_tool_rate_single": rate(r["right"] for r in rows if r["kind"] == "single"),
            "exact_single_rate": rate(r["exact"] for r in rows if r["kind"] == "single"),
            "right_tools_rate_double": rate(r["right"] for r in rows if r["kind"] == "double"),
            "no_tool_correct_rate": rate(r["right"] for r in rows if r["kind"] == "none"),
            "args_valid_rate": rate(r["args_valid"] for r in called) if called else None,
            "hallucinated_tool_calls": sum(len(r["hallucinated"]) for r in rows),
            "hallucinated_names": sorted({n for r in rows for n in r["hallucinated"]}),
            "tool_call_text_in_content": sum(r["tool_in_content"] for r in rows),
            "round2_n": len(r2), "round2_ok_rate": rate(x["ok"] for x in r2) if r2 else None,
            "round2_completes_missing_rate": rate(x["completes_missing"] for r in rows for x in [r.get("round2")] if x and r["kind"] == "double"),
            "latency_median_s": med([r["wall_s"] for r in rows]),
            "failures": [{"prompt": r["prompt"], "expected": r["expected"], "called": r["called"], "content": r["content"], "args": [c["raw_args"] for c in r["calls"]]}
                         for r in rows if not r["right"] or r["args_valid"] is False][:6]}
