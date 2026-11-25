#!/usr/bin/env bash
# Q3, Q4, Q6, Q5(gemma) on the target shape (Q3_K_XL, -np3 -c196608 q4_0, n-cpu-moe 6); then Q3 again at n-cpu-moe 11 (3 GB RimWorld cap); then Q5 on Qwen.
D=$HOME/.local/share/rimworld-hermes-m0; B=$(cd "$(dirname "$0")" && pwd); M=$(cat $D/model_q3kxl.txt); PY=$D/venv/bin/python
export OMP_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1
cd $B
$D/srv.sh rest_main $M -ctk q4_0 -ctv q4_0 -fa on --cache-ram 8192 -c 196608 --n-cpu-moe 6 || exit 1
$PY q3_conc.py 5 > results/q3_conc.txt 2>&1; mv results/q3_conc.json results/q3_conc_n6.json
$PY q6_intent.py q3kxl > results/q6_q3kxl.txt 2>&1
$PY q5_grim.py gemma_q3kxl 3 > results/q5_gemma_q3kxl.txt 2>&1
$PY q4_voice.py gen q3kxl > results/q4_gen.txt 2>&1
$PY q4_voice.py score q3kxl > results/q4_score_q3kxl.txt 2>&1
$D/stop.sh >/dev/null
# 3 GB RimWorld cap shape
$D/srv.sh rest_n11 $M -ctk q4_0 -ctv q4_0 -fa on --cache-ram 8192 -c 196608 --n-cpu-moe 11 || exit 1
$PY q3_conc.py 5 > results/q3_conc_n11.txt 2>&1; mv results/q3_conc.json results/q3_conc_n11.json
$D/stop.sh >/dev/null
echo MAINDONE > results/rest_done.flag
