"""Section 1: single-speaker lines through the agent's real line_pass (prompt blocks, stop, cap, sanitizer)."""
from __future__ import annotations
import re, time
from common import *
from rimworldllm_agent.core.passes import line_pass
from rimworldllm_agent.core.prompt import line_messages
from rimworldllm_agent.core.sanitize import _REFUSAL
import checks as C

CAP = 48


def check_text(t: str, *, ctx, pawn_tokens, prefix_words, raw_meta=None) -> dict:
    """Run every check on a text. True = pass."""
    prompt_caps = ctx.prompt_words
    d = {
        "nonempty": C.is_nonempty(t),
        "one_line": C.is_one_line(t),
        "no_markdown": not C.has_markdown(t),
        "no_stage": not C.has_stage_direction(t),
        "no_prefix": not C.has_speaker_prefix(t, ctx.all_names | pawn_tokens),
        "no_quotes": not C.wrapped_in_quotes(t),
        "no_refusal": not C.is_refusal(t, _REFUSAL),
        "no_moralising": not C.is_moralising(t),
        "in_character": C.in_character(t, pawn_tokens),
        "no_invented": not C.invented_entities(t, prompt_caps, ctx.all_names | ctx.known_names, ctx.common_words),
        "terminal": C.ends_terminal(t),
        "charset": C.ascii_typographic_only(t),
        "spelling": not C.spelling_errors(t, prefix_words),
        "grammar": not C.grammar_errors(t),
    }
    if raw_meta is not None:
        d["within_cap"] = C.within_cap(raw_meta.get("completion_tokens"), raw_meta.get("finish"), CAP)
        d["no_think"] = not C.thinking_leak(raw_meta.get("content", ""), raw_meta.get("reasoning", ""))
    return d


def scenarios():
    out = [(s["focal"], s) for s in fx("scenes.json")] + [(s["focal"], s) for s in fx("grim.json")]
    return out


async def run(llm, reps=2):
    rows = []
    sc_list = scenarios()
    i = 0
    for rep in range(reps):
        for pid, sc in sc_list:
            prefix, mind, ctx, scene = prefix_for(pid, sc)
            pawn_tokens = NAME_TOKENS[ACTOR_ID[pid]]
            pwords = {w.lower() for w in re.findall(r"[A-Za-z']+", prefix)}
            slot = i % 3
            i += 1
            lst = []
            RAW.set(lst)
            t0 = time.perf_counter()
            s, msgs = await line_pass(llm, slot=slot, mind=mind, name=mind.name, prefix=prefix, turns=[], turn_no=rep,
                                      ctx=ctx, max_tokens=CAP)
            wall = time.perf_counter() - t0
            rm = raw_of(lst[-1]) if lst else {"content": "", "reasoning": "", "finish": None, "completion_tokens": None}
            raw = rm["content"]
            row = {"scene": sc["id"], "kind": sc.get("kind"), "grim": sc.get("kind") == "grim", "pawn": pid, "rep": rep, "slot": slot,
                   "raw": raw, "reasoning": rm["reasoning"][:200], "finish": rm["finish"], "tokens": rm["completion_tokens"],
                   "wall_s": round(wall, 3), "kept": s.ok, "sanitized": s.text, "drop_reason": s.reason}
            row["raw_checks"] = check_text(raw.strip(), ctx=ctx, pawn_tokens=pawn_tokens, prefix_words=pwords, raw_meta=rm)
            row["raw_problems"] = {"spelling": C.spelling_errors(raw, pwords), "grammar": C.grammar_errors(raw),
                                   "invented": C.invented_entities(raw, ctx.prompt_words, ctx.all_names | ctx.known_names, ctx.common_words)}
            if s.ok:
                row["san_checks"] = check_text(s.text, ctx=ctx, pawn_tokens=pawn_tokens, prefix_words=pwords)
                row["san_checks"]["within_words"] = len(s.text.split()) <= 35
                row["san_problems"] = {"spelling": C.spelling_errors(s.text, pwords), "grammar": C.grammar_errors(s.text)}
            if rep == 0:    # does the model keep writing past the newline the agent stops on?
                lst2 = []
                RAW.set(lst2)
                try:
                    c2 = await llm.complete(msgs, slot=slot, kind="line", max_tokens=CAP, stop=[], actor=mind.actor_id)
                    row["multi_line_raw"] = c2.text.strip()
                    row["keeps_writing"] = "\n" in c2.text.strip()
                except Exception as e:
                    row["multi_line_raw"] = f"ERR {e}"
            rows.append(row)
    return rows


def summarise(rows) -> dict:
    n = len(rows)
    out = {"n": n, "grim_n": sum(r["grim"] for r in rows)}
    keys = list(rows[0]["raw_checks"])
    out["raw_pass"] = {k: rate(r["raw_checks"].get(k) for r in rows) for k in keys}
    kept = [r for r in rows if r["kept"]]
    out["kept_rate"] = rate(r["kept"] for r in rows)
    out["sanitizer_drop_rate"] = round(1 - out["kept_rate"], 4)
    reasons = {}
    for r in rows:
        if not r["kept"]:
            k = r["drop_reason"].split(":")[0]
            reasons[k] = reasons.get(k, 0) + 1
    out["drop_reasons"] = reasons
    if kept:
        out["sanitized_pass"] = {k: rate(r["san_checks"].get(k) for r in kept) for k in kept[0]["san_checks"]}
    out["grim_raw_refusal_rate"] = rate(not r["raw_checks"]["no_refusal"] or not r["raw_checks"]["no_moralising"] for r in rows if r["grim"])
    out["grim_drop_rate"] = rate(not r["kept"] for r in rows if r["grim"])
    out["keeps_writing_after_newline"] = rate(r.get("keeps_writing") for r in rows if "keeps_writing" in r)
    out["raw_all_pass"] = rate(all(r["raw_checks"].values()) for r in rows)
    out["sanitized_all_pass"] = rate(all(r["san_checks"].values()) for r in kept) if kept else None
    out["words_median"] = med([len(r["sanitized"].split()) for r in kept])
    out["tokens_median"] = med([r["tokens"] for r in rows])
    out["wall_s_median"] = med([r["wall_s"] for r in rows])
    out["duplicate_lines"] = n - len({(r["sanitized"] or r["raw"]).lower() for r in rows})
    step = max(1, n // 12)
    out["samples"] = [{"scene": r["scene"], "pawn": r["pawn"], "raw": r["raw"], "sanitized": r["sanitized"], "drop": r["drop_reason"]} for r in rows[::step][:12]]
    return out
