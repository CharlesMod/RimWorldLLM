"""Summarise conformance runs: gpubench/results/conf/<model>.<mode><tag>.json -> markdown table rows."""
import glob, json, os
D = os.path.expanduser("~/.local/share/rimworld-hermes-engine/gpubench/results/conf")
print("| model | mode (protocol) | run | completed | right tool | inject followed | round 2 first try | retries/fallbacks | hard violations | step ms med/p90 |")
print("|---|---|---|---|---|---|---|---|---|---|")
for f in sorted(glob.glob(D + "/*.json")):
    s = json.load(open(f))["summary"]
    run = "hardened" if "_sanitized" in f else "baseline"
    hard = sum(s[k] for k in ("empty_reply", "invalid_args_after_harness", "hallucinated_executed", "raw_syntax_in_final", "loop_faults", "over_budget", "gave_up_loops"))
    print(f"| {s['model']} | {s['mode']} ({s['protocol']}) | {run} | {s['completed_loops']}/{s['scenarios']} | {s['right_tool_pct_all']}% | "
          f"{s['inject_followed']}/{s['inject_scenarios']} ({s['inject_followed_pct']}%) | {s['round2_first_try_pct']}% | "
          f"{s['retries_total']}/{s['loops_with_fallback']} | {hard} {s['violations'] or ''} | {s['step_ms_median']}/{s['step_ms_p90']} |")
