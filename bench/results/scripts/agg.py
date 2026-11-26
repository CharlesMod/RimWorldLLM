"""Aggregate bench results over reps.  agg.py LABEL=dir[,dir...] ...  (each dir = <results>/<model>)
Prints one JSON object per label (means over reps, sums for counts)."""
import json, os, statistics as st, sys
sys.path.insert(0, "/var/home/cmod/rimworld-hermes/.claude/worktrees/gpubench/agent")
from hermesvoice_agent.core import loopguard as LG

def ld(d, f):
    p = os.path.join(d, f + ".json")
    return json.load(open(p)) if os.path.exists(p) else None

def distinct2(texts):
    bg = [tuple(w[i:i + 2]) for t in texts for w in [t.lower().split()] for i in range(len(w) - 1)]
    return round(len(set(bg)) / max(1, len(bg)), 3)

def mean(xs):
    xs = [x for x in xs if x is not None]
    return round(st.mean(xs), 4) if xs else None

def agg(dirs):
    o = {"reps": len(dirs)}
    L = [x for x in (ld(d, "lines") for d in dirs) if x]
    if L:
        o["lines_n"] = sum(x["summary"]["n"] for x in L)
        o["lines_raw_all_pass"] = mean([x["summary"]["raw_all_pass"] for x in L])
        o["lines_drop_rate"] = mean([x["summary"]["sanitizer_drop_rate"] for x in L])
        o["lines_dups"] = sum(x["summary"]["duplicate_lines"] for x in L)
        o["lines_words_median"] = mean([x["summary"]["words_median"] for x in L])
        fails = {}
        for x in L:
            for k, v in x["summary"]["raw_pass"].items():
                fails[k] = fails.get(k, 0) + round((1 - v) * x["summary"]["n"])
        o["lines_check_fails"] = {k: v for k, v in fails.items() if v}
        o["lines_distinct2"] = mean([distinct2([r["raw"].split("\n")[0] for r in x["rows"]]) for x in L])
        reasons = {}
        for x in L:
            for k, v in x["summary"]["drop_reasons"].items():
                reasons[k] = reasons.get(k, 0) + v
        o["lines_drop_reasons"] = reasons
    C = [x for x in (ld(d, "conv") for d in dirs) if x]
    if C:
        ex = sum(x["summary"]["exchanges"] for x in C)
        o["conv_exchanges"] = ex
        o["conv_loop_exchanges"] = sum(x["summary"]["repetition_loop_exchanges"] for x in C)
        o["conv_answers_prev"] = mean([x["summary"]["answers_previous_rate"] for x in C])
        o["conv_turn_drop"] = mean([x["summary"]["turn_drop_rate"] for x in C])
        o["conv_writes_other"] = mean([x["summary"]["writes_other_line_rate"] for x in C])
        flagged, convs_flag, texts = 0, 0, []
        for x in C:
            for r in x["rows"]:
                turns, hit_any = [], False
                ids = {}
                for t in r["turns"]:
                    if "text" not in t:
                        continue
                    sp = ids.setdefault(t["speaker"], len(ids))
                    texts.append(t["text"])
                    if turns and LG.find_repeat(t["text"], sp, turns):
                        flagged += 1; hit_any = True
                    turns.append((sp, t["text"]))
                convs_flag += hit_any
        o["conv_loopguard_flagged_turns"] = flagged
        o["conv_loopguard_flagged_exchanges"] = convs_flag
        o["conv_distinct2"] = distinct2(texts)
    V = [x for x in (ld(d, "voice") for d in dirs) if x]
    if V:
        o["voice_pawn_id_acc"] = mean([x["summary"]["pawn_id_acc_mean"] for x in V])
        o["voice_pawn_id_acc_each"] = [x["summary"]["pawn_id_acc_mean"] for x in V]
        o["voice_slop_rate"] = mean([x["summary"]["slop_rate"] for x in V])
        o["voice_dups"] = sum(x["summary"]["dup_lines"] for x in V)
    S = [x for x in (ld(d, "struct") for d in dirs) if x]
    if S:
        for mode in ("grammar", "schema", "free"):
            o[f"intent_{mode}_acc"] = mean([x["intent"]["summary"][mode]["accuracy"] for x in S if mode in x["intent"]["summary"]])
            o[f"intent_{mode}_valid"] = mean([x["intent"]["summary"][mode]["schema_valid_rate"] for x in S if mode in x["intent"]["summary"]])
        o["intent_grammar_acc_each"] = [x["intent"]["summary"]["grammar"]["accuracy"] for x in S]
        for k in ("json_parse_rate", "schema_valid_rate", "accepted_by_agent_rate", "latency_median_s"):
            o["reflect_" + k] = mean([x["reflection"]["summary"][k] for x in S])
    return o

for a in sys.argv[1:]:
    lab, ds = a.split("=", 1)
    print(json.dumps({"label": lab, **agg([d for d in ds.split(",") if os.path.isdir(d)])}))
