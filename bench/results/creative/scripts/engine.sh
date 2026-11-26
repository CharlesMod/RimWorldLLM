#!/bin/bash
# on poe: engine.sh start [profile.json] | stop | status  -- private creative engine home, GPU 0 (P40) only; PIDs saved
C=~/creative
case "$1" in
start)
  P=${2:-$C/profile.json}
  sleep 86400 & echo $! > $C/sleep.pid
  export HERMES_ENGINE_HOME=$C/home CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=0
  nohup $C/hermes-engine serve --profile "$P" --parent-pid $(cat $C/sleep.pid) >> $C/logs/engine.log 2>&1 < /dev/null &
  echo $! > $C/engine.pid
  for i in $(seq 240); do curl -sf http://127.0.0.1:47480/engine/v1/status | grep -q '"phase":"ready"' && break; sleep 1; done
  echo started engine $(cat $C/engine.pid); curl -s http://127.0.0.1:47480/engine/v1/status | head -c 600; echo ;;
stop)
  [ -f $C/engine.pid ] && kill $(cat $C/engine.pid) 2>/dev/null
  for i in $(seq 40); do kill -0 $(cat $C/engine.pid) 2>/dev/null || break; sleep 0.5; done
  [ -f $C/sleep.pid ] && kill $(cat $C/sleep.pid) 2>/dev/null
  rm -f $C/engine.pid $C/sleep.pid; echo stopped ;;
status) curl -s http://127.0.0.1:47480/engine/v1/status; echo ;;
esac
