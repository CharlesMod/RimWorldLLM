#!/bin/bash
# ab.sh MODEL REPS  -- sampler A/B for line-side suites: alternate official(base) and old(oldrp) arms
G=~/.local/share/rimworld-hermes-engine/gpubench
W=/var/home/cmod/rimworld-hermes/.claude/worktrees/gpubench/bench/models
PY=~/.local/share/rimworld-hermes-engine/bench/venv/bin/python
M=$1; REPS=${2:-3}
for r in $(seq $REPS); do
  for arm in official old; do
    prof=$G/profiles/base.json; [ $arm = old ] && prof=$G/profiles/oldrp.json
    $G/engine.sh start $prof
    out=$G/results/ab/$arm/r$r; mkdir -p $out
    curl -s -X POST -H 'X-Easy-Engine: 1' -d '{"model":"'$M'"}' http://127.0.0.1:47480/engine/v1/install >/dev/null
    (cd $W && BENCH_OUT=$out PYTHONUNBUFFERED=1 $PY run_model.py $M lines conv voice) 2>&1 | tail -5
    curl -s http://127.0.0.1:47480/engine/v1/status | python3 -c 'import json,sys;s=json.load(sys.stdin);print("SAMPLER",json.dumps(s.get("sampler",{}).get("profiles",{}).get("rp")))'
    $G/engine.sh stop
  done
done
echo ABDONE $M
