#!/usr/bin/env bash
# usage: footprint.sh <csv-out> <label> [extra server args]  -- starts server (Q3_K_XL, -np3 -c196608 q4_0), records footprint + a speed probe, stops it.
D=$HOME/.local/share/rimworld-hermes-m0; B=$(cd "$(dirname "$0")" && pwd)
out=$1; label=$2; shift 2
M=$(cat $D/model_q3kxl.txt)
if $D/srv.sh fp_$label $M -c 196608 -ctk q4_0 -ctv q4_0 -fa on --cache-ram 8192 -lv 5 "$@" >/dev/null 2>&1; then
  pid=$(cat $D/srv.pid)
  proc=$(nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader,nounits | awk -F', ' -v p=$pid '$1==p{print $2}')
  tot=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits)
  log=$D/logs/fp_$label.log
  wt=$(grep "CUDA0 model buffer" $log | tail -1 | sed 's/.*= *//;s/ MiB//'); cpu=$(grep "CPU_Mapped model buffer" $log | tail -1 | sed 's/.*= *//;s/ MiB//')
  kv=$(grep "CUDA0 KV buffer" $log | sed 's/.*= *//;s/ MiB//' | awk '{s+=$1}END{print s}'); cb=$(grep "CUDA0 compute buffer" $log | tail -1 | sed 's/.*= *//;s/ MiB//')
  speed=$($D/venv/bin/python $B/speedprobe.py)
  echo "$label,\"$*\",ok,$proc,$tot,$wt,$cpu,$kv,$cb,$speed" >> $out
  $D/stop.sh >/dev/null
else
  echo "$label,\"$*\",OOM,,,,,,," >> $out; $D/stop.sh >/dev/null
fi
