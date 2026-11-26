#!/bin/bash
# on poe: rich-fixture soak, main behaviour (5154462 export in repo-before, all creative flags off)
cd ~/creative; PY=venv/bin/python; U=http://127.0.0.1:47480
$PY repo-before/bench/soak/soak.py --url $U --fixture rich --speed 1 --days 1.5 --adult off --out soak --run before-1x --max-real-min 50 --seed 31 > logs/soak-before.log 2>&1
echo "before $?" > logs/soak-before.done
