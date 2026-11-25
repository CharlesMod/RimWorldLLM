#!/usr/bin/env bash
D=$HOME/.local/share/rimworld-hermes-m0; B=$(cd "$(dirname "$0")" && pwd); M=$(cat $D/model_q3kxl.txt); PY=$D/venv/bin/python
cd $B
$D/srv.sh fin_n6 $M -ctk q4_0 -ctv q4_0 -fa on --cache-ram 8192 -c 196608 --n-cpu-moe 6 || exit 1
$PY q3_conc.py 5 > results/q3_conc.txt 2>&1; mv results/q3_conc.json results/q3_conc_n6.json
$D/stop.sh >/dev/null
Q=$HOME/.hive/models/Qwen3.8-27B-ASCII-Condensed-Heretic-Q3_K_XL.gguf
$D/srv.sh qwen $Q -ctk q4_0 -ctv q4_0 -fa on -c 8192 -np 1 --cache-ram 0 || { echo QWENFAIL; exit 1; }
nvidia-smi --query-gpu=memory.used --format=csv,noheader > results/qwen_vram.txt
$PY q5_grim.py qwen27b 3 > results/q5_qwen27b.txt 2>&1
$D/stop.sh >/dev/null
echo FINALDONE > results/final_done.flag
