#!/bin/bash
# usage: run_all.sh [venv-python]  -- runs the whole suite per model, one at a time, then writes the report data
PY=${1:-$HOME/.local/share/rimworld-hermes-engine/bench/venv/bin/python}
cd "$(dirname "$0")"
for m in gemma4-rp-maxxed-q3kxl ling3-tiny-q4km gemma4-e4b-q4km gemma4-e2b-q4km; do
  PYTHONUNBUFFERED=1 $PY run_model.py $m 2>&1 | tee -a ${BENCH_LOG:-/dev/null}
done
