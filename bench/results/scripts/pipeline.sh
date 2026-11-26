#!/bin/bash
# After ab.sh: structured A/B, conformance+injection, rp-vs-stock suite, prefill probe. One model at a time.
G=~/.local/share/rimworld-hermes-engine/gpubench
W=/var/home/cmod/rimworld-hermes/.claude/worktrees/gpubench/bench/models
PY=~/.local/share/rimworld-hermes-engine/bench/venv/bin/python
R=$G/results
rm_() { (cd $W && BENCH_OUT=$1 PYTHONUNBUFFERED=1 $PY run_model.py "${@:2}") 2>&1 | tail -3; }
guard() { if ss -ltn | grep -q ':4480 '; then echo "STOP: :4480 llama-server appeared"; $G/engine.sh stop; exit 3; fi; }
E4R=gemma4-e4b-rp-q40; E2R=gemma4-e2b-rp-q40; E4=gemma4-e4b-q4km; E2=gemma4-e2b-q4km; BIG=gemma4-rp-maxxed-q3kxl
$G/engine.sh start $G/profiles/base.json
for M in $BIG $E4R; do for r in 1 2 3; do guard; echo "== struct official $M r$r"; rm_ $R/struct/official/r$r $M struct; done; done
for M in $BIG $E4R $E2R $E4; do
  guard; echo "== conformance baseline $M"
  (cd $W && $PY conformance.py --model $M --mode both --out $R/conf) 2>&1 | tail -4
  guard; echo "== conformance hardened $M"
  (cd $W && $PY conformance.py --model $M --mode both --sanitize --scene-allowed --tag _sanitized --out $R/conf) 2>&1 | tail -4
done
for M in $E4R $E4 $E2R $E2; do
  guard; echo "== suite full $M"; rm_ $R/suite/r1 $M
  for r in 2 3; do guard; echo "== suite lines conv voice $M r$r"; rm_ $R/suite/r$r $M lines conv voice; done
done
for M in $E4R $E2R $BIG; do
  guard; echo "== prefill probe $M"
  curl -s -X POST -H 'X-Hermes-Engine: 1' -d '{"model":"'$M'"}' http://127.0.0.1:47480/engine/v1/install >/dev/null
  for i in $(seq 180); do [ "$(curl -s http://127.0.0.1:47480/engine/v1/status | python3 -c 'import json,sys;s=json.load(sys.stdin);print(s.get("phase"),s.get("model"))')" = "ready $M" ] && break; sleep 1; done
  mkdir -p $R/prefill; N=20 $PY $G/prefill_probe.py $M > $R/prefill/$M.json 2>&1; tail -c 300 $R/prefill/$M.json
done
$G/engine.sh stop
$G/engine.sh start $G/profiles/greedy.json
for M in $BIG $E4R; do for r in 1 2; do guard; echo "== struct greedy $M r$r"; rm_ $R/struct/greedy/r$r $M struct; done; done
$G/engine.sh stop
echo PIPEDONE
