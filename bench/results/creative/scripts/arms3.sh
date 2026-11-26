#!/bin/bash
# on poe: round 3 (art v2, kept config vs base incl. aims), then item 7. Waits for soak-before to finish first.
cd ~/creative; PY=venv/bin/python; P=repo/bench/soak/creative_probe.py; U=http://127.0.0.1:47480
while [ ! -f logs/soak-before.done ]; do sleep 20; done
run() { a=$1; shift; $PY $P --url $U --reps 6 --arm $a --out results/p-$a "$@" > logs/p-$a.log 2>&1; echo "$a $?" >> logs/arms3.progress; }
run r3artv2 --features art --set creative_ground_for=art
run r3baseaim --features aim
run r3final --set creative_ground_for=lore,recap,eulogy --set creative_plan_for=lore,eulogy --set creative_best_of_for=recap --set creative_rerank=auto --set aim_ground=true
echo DONE >> logs/arms3.progress
bash item7.sh > logs/item7.log 2>&1
