"""F6: DRY repetition control on the line pass, on and off, per model. Uses the bench's own lines and conversation sections.

    python dry.py MODEL [reps]      (the engine must be up; selects MODEL)
Writes results/<model>/dry.json under BENCH_OUT-like dir ~/.local/share/rimworld-hermes-engine/toolharness/dry.
"""
import asyncio, json, re, sys
from pathlib import Path
from common import *
import lines, conv, checks as C

OUTD = Path.home() / ".local/share/rimworld-hermes-engine/toolharness/dry"


def distinct(texts, n=2):
    g = []
    for t in texts:
        w = re.findall(r"[a-z']+", t.lower())
        g += [tuple(w[i:i + n]) for i in range(len(w) - n + 1)]
    return round(len(set(g)) / max(1, len(g)), 3)


async def one(model, dry, conv_reps):
    llm = make_llm(line_dry=dry)
    try:
        erase_slots()
        lrows = await lines.run(llm)
        ls = lines.summarise(lrows)
        crows, cs = [], []
        for _ in range(conv_reps):
            erase_slots()
            rows = await conv.run(llm)
            crows += rows
        cs = conv.summarise(crows)
    finally:
        await llm.aclose()
    texts = [(r["sanitized"] or r["raw"]) for r in lrows]
    ctexts = [x["text"] for r in crows for x in r["turns"] if "text" in x]
    return {"dry": dry, "lines": {"n": ls["n"], "duplicate_lines": ls["duplicate_lines"], "raw_all_pass": ls["raw_all_pass"], "sanitizer_drop_rate": ls["sanitizer_drop_rate"],
                                  "words_median": ls["words_median"], "tokens_median": ls["tokens_median"], "distinct2": distinct(texts),
                                  "spelling": ls["raw_pass"]["spelling"], "grammar": ls["raw_pass"]["grammar"], "in_character": ls["raw_pass"]["in_character"],
                                  "samples": [(r["scene"], r["raw"]) for r in lrows[::10][:10]]},
            "conv": {"exchanges": cs["exchanges"], "turns": cs["turns"], "repetition_loop_exchanges": cs["repetition_loop_exchanges"],
                     "turn_drop_rate": cs["turn_drop_rate"], "answers_previous_rate": cs["answers_previous_rate"], "distinct2": distinct(ctexts),
                     "cross_repeat_turns": sum(r["rep"]["cross"] for r in crows), "inner_repeat_turns": sum(r["rep"]["inner"] for r in crows),
                     "samples": [[(x["speaker"], x.get("text")) for x in r["turns"]] for r in crows[:3]]}}


async def main(model, conv_reps):
    select_model(model)
    await asyncio.sleep(3)
    out = {}
    for dry in (False, True, False, True):   # off/on twice: sampling noise is part of the answer
        out.setdefault("on" if dry else "off", []).append(await one(model, dry, conv_reps))
        print(model, "dry" if dry else "off", json.dumps({k: {kk: vv for kk, vv in v.items() if kk != "samples"} for k, v in out["on" if dry else "off"][-1].items() if isinstance(v, dict)}), flush=True)
    OUTD.mkdir(parents=True, exist_ok=True)
    (OUTD / f"{model}.json").write_text(json.dumps(out, indent=1, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 3))
