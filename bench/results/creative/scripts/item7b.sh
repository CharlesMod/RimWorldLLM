#!/bin/bash
# on poe: item 7 redo of the intent-accuracy bench (jsonschema was missing) + check the aim profile is applied
cd ~/creative; PY=~/creative/venv/bin/python
chk() { curl -s -D - -o /dev/null -H "X-Easy-Sampler: $1" -H "Content-Type: application/json" http://127.0.0.1:47480/v1/chat/completions -d '{"messages":[{"role":"user","content":"hi"}],"max_tokens":2}' | grep -i "Sampler-Applied"; }
bench() { (cd repo/bench/models && for r in 1 2 3; do BENCH_OUT=~/creative/results/i7-$1/r$r PYTHONUNBUFFERED=1 $PY run_model.py gemma4-e4b-rp-q40 struct 2>&1 | tail -1; done); }
./engine.sh stop; ./engine.sh start ~/creative/profile-aim.json; echo "aim3 -> $(chk aim3)"; echo "aim5 -> $(chk aim5)"; echo "structured -> $(chk structured)"
bench greedy
./engine.sh stop; ./engine.sh start ~/creative/profile-s3.json; echo "s3 structured -> $(chk structured)"; bench s3
./engine.sh stop; ./engine.sh start ~/creative/profile-s5.json; echo "s5 structured -> $(chk structured)"; bench s5
./engine.sh stop; ./engine.sh start ~/creative/profile.json
echo DONE
