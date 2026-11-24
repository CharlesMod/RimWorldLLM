#!/usr/bin/env bash
# Q1 on Q3_K_XL: target shape (swa off) with cache-ram on/off, then the 4-way grid at -c 49152 --n-cpu-moe 10 (swa-full needs the room)
D=$HOME/.local/share/rimworld-hermes-m0; B=$(cd "$(dirname "$0")" && pwd); M=$(cat $D/model_q3kxl.txt); PY=$D/venv/bin/python
cd $B
base="-ctk q4_0 -ctv q4_0 -fa on"
run() { name=$1; shift; $D/srv.sh q1_$name $M $base "$@" || { echo "FAILED $name"; $D/stop.sh; return; }; $PY q1_lease.py $name 5 > results/q1_$name.txt 2>&1; $D/stop.sh >/dev/null; }
run tgt_cramON_swaOFF  -c 196608 --n-cpu-moe 6 --cache-ram 8192
run tgt_cramOFF_swaOFF -c 196608 --n-cpu-moe 6 --cache-ram 0
for cr in 8192 0; do for sw in "" "--swa-full"; do
  run grid_cram${cr}_swa${sw:+FULL}${sw:-OFF} -c 49152 --n-cpu-moe 10 --cache-ram $cr $sw
done; done
echo ALLDONE > results/q1_done.flag
