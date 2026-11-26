#!/bin/bash
# on poe: item 7, aim/intent temperature A/B. Restarts the creative engine (saved PIDs) with game sampler profiles.
cd ~/creative; PY=~/creative/venv/bin/python; P=repo/bench/soak/creative_probe.py; U=http://127.0.0.1:47480
G="--set creative_ground=true --set aim_ground=true"
bench() { (cd repo/bench/models && for r in 1 2 3; do BENCH_OUT=~/creative/results/i7-$1/r$r PYTHONUNBUFFERED=1 $PY run_model.py gemma4-e4b-rp-q40 struct 2>&1 | tail -2; done); }
./engine.sh stop; ./engine.sh start ~/creative/profile-aim.json
for a in greedy:"" aim3:aim3 aim5:aim5; do n=${a%%:*}; s=${a#*:}
  $PY $P --url $U --reps 8 --features aim --arm a7-$n --out results/p-a7-$n $G --set aim_sampler=$s > logs/p-a7-$n.log 2>&1; echo "a7-$n $?" >> logs/item7.progress; done
bench greedy; echo "bench greedy" >> logs/item7.progress
./engine.sh stop; ./engine.sh start ~/creative/profile-s3.json; bench s3; echo "bench s3" >> logs/item7.progress
./engine.sh stop; ./engine.sh start ~/creative/profile-s5.json; bench s5; echo "bench s5" >> logs/item7.progress
./engine.sh stop; ./engine.sh start ~/creative/profile.json
echo DONE >> logs/item7.progress
