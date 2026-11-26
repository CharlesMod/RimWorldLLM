#!/bin/bash
# on poe: round 2 probe arms (rare pieces), reps 6, enriched judge context
cd ~/creative; PY=venv/bin/python; P=repo/bench/soak/creative_probe.py; U=http://127.0.0.1:47480
run() { a=$1; shift; $PY $P --url $U --reps 6 --features art,lore,recap,eulogy,vows --arm $a --out results/p-$a "$@" > logs/p-$a.log 2>&1; echo "$a $?" >> logs/arms2.progress; }
G="--set creative_ground=true"
run r2base
run r2ground $G
run r2plan $G --set creative_plan=true
run r2bo4llm $G --set creative_best_of=4 --set creative_rerank=llm
run r2bo4auto $G --set creative_best_of=4 --set creative_rerank=auto
run r2cliche $G --set cliche_retry=true
echo DONE >> logs/arms2.progress
