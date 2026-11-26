#!/bin/bash
# on poe: poe_server.sh start <model.gguf> <official|greedy> | stop   -- P40 GPU 0 (PCI order), port 47490, engine flags
B=~/gemma4-e-rp/src/llama.cpp/build/bin; D=~/rimworld-bench
case "$1" in
start)
  T=1; [ "$3" = greedy ] && T=0
  CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=0 LD_LIBRARY_PATH=$B nohup $B/llama-server -m "$2" -ngl 999 -np 3 -c 196608 \
    -ctk q4_0 -ctv q4_0 --no-kv-unified -fa on --jinja --reasoning off --cache-reuse 256 --no-webui --metrics --slots \
    --temp $T --top-k 64 --top-p 0.95 --min-p 0 --repeat-penalty 1 --dry-multiplier 0 -a bench \
    --host 127.0.0.1 --port 47490 > $D/logs/server-$3.log 2>&1 < /dev/null &
  echo $! > $D/server.pid
  for i in $(seq 120); do curl -sf http://127.0.0.1:47490/health >/dev/null && break; sleep 1; done
  echo started $(cat $D/server.pid); nvidia-smi --query-compute-apps=gpu_bus_id,pid,used_memory --format=csv,noheader | grep $(cat $D/server.pid) ;;
stop)
  kill $(cat $D/server.pid) 2>/dev/null; sleep 3; kill -0 $(cat $D/server.pid) 2>/dev/null && echo STILL || echo stopped; rm -f $D/server.pid ;;
esac
