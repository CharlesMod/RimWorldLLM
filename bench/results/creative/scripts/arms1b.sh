#!/bin/bash
# on poe: eulogy/vows rerun for arms whose ceremony turns bypassed compose in arms1 (probe fix d123476)
cd ~/creative; PY=venv/bin/python; P=repo/bench/soak/creative_probe.py; U=http://127.0.0.1:47480
run() { a=$1; shift; $PY $P --url $U --reps 4 --features eulogy,vows --arm $a --out results/p-$a-cer "$@" > logs/p-$a-cer.log 2>&1; echo "$a $?" >> logs/arms1b.progress; }
G="--set creative_ground=true --set aim_ground=true"
run ground $G
run plan $G --set creative_plan=true
run bo4auto $G --set creative_best_of=4 --set creative_rerank=auto
run bo4llm $G --set creative_best_of=4 --set creative_rerank=llm
run cliche $G --set cliche_retry=true
echo DONE >> logs/arms1b.progress
