#!/bin/bash
# on poe: rich-fixture soak, kept creative defaults (repo = agent/creative tip), same seed/speed/days as before
cd ~/creative; PY=venv/bin/python; U=http://127.0.0.1:47480
$PY repo/bench/soak/soak.py --url $U --fixture rich --speed 1 --days 1.5 --adult off --out soak --run after-1x --max-real-min 50 --seed 31 > logs/soak-after.log 2>&1
echo "after $?" > logs/soak-after.done
