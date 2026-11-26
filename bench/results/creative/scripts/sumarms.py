import json, sys, glob, os
# usage: sumarms.py RESULTS_DIR arm...  -> table of per-feature n/dropped/latency/calls (+aim metrics)
R = sys.argv[1]
for arm in sys.argv[2:]:
    p = os.path.join(R, f"p-{arm}", "summary.json")
    if not os.path.exists(p):
        print(arm, "missing"); continue
    s = json.load(open(p))["features"]
    for f, v in s.items():
        extra = "" if f != "aim" else f" ijw={v.get('share_i_just_want')} distinct={v.get('distinct_aim_share')} jac={v.get('mean_pairwise_jaccard')} words={v.get('words_mean')}"
        print(f"{arm:10s} {f:7s} n={v['n']:3d} drop={v['dropped']:3d} lat_med={v['latency_median']} p90={v.get('latency_p90')} calls={v.get('calls_mean')} slot_s={v.get('slot_s_mean')}{extra}")
