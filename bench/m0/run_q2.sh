#!/usr/bin/env bash
# Q2 speculative decoding on Q3_K_XL. Group 1: target shape, ngram types. Group 2: smaller shape (draft model needs VRAM), none vs E4B draft.
D=$HOME/.local/share/rimworld-hermes-m0; B=$(cd "$(dirname "$0")" && pwd); M=$(cat $D/model_q3kxl.txt); PY=$D/venv/bin/python
E4B=/var/mnt/hive-train/weights/bakeoff/gemma-e4b/gemma-4-E4B-it-Q4_K_M.gguf
cd $B; base="-ctk q4_0 -ctv q4_0 -fa on --cache-ram 8192"
run() { name=$1; shift; $D/srv.sh q2_$name $M $base "$@" || { echo "FAILED $name"; $D/stop.sh; return; }; $PY q2_spec.py $name 60 > results/q2_$name.txt 2>&1; $PY q2_spec.py ${name}_r2 60 > results/q2_${name}_r2.txt 2>&1; $D/stop.sh >/dev/null; }
T="-c 196608 --n-cpu-moe 6"
run tgt_none $T
run tgt_ngram-simple $T --spec-type ngram-simple
run tgt_ngram-map-k $T --spec-type ngram-map-k
run tgt_ngram-mod $T --spec-type ngram-mod
run tgt_ngram-map-k4v $T --spec-type ngram-map-k4v
S="-c 49152 --n-cpu-moe 14"
run small_none $S
run small_draftE4B $S --spec-type draft-simple -md $E4B -ngld 99 --spec-draft-n-max 4
echo ALLDONE > results/q2_done.flag
