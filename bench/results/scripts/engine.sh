#!/bin/bash
# engine.sh start <profile.json> | stop    -- private gpubench engine home; PIDs saved in gpubench/
G=~/.local/share/rimworld-hermes-engine/gpubench
case "$1" in
start)
  sleep 86400 & echo $! > $G/sleep.pid
  export EASY_ENGINE_HOME=$G/home
  nohup ~/easyEngine/dist/linux-amd64/easy-engine serve --profile "$2" --parent-pid $(cat $G/sleep.pid) >> $G/engine.log 2>&1 &
  echo $! > $G/engine.pid
  for i in $(seq 60); do curl -sf http://127.0.0.1:47480/engine/v1/status >/dev/null && break; sleep 0.5; done
  echo started engine $(cat $G/engine.pid) profile $2 ;;
stop)
  [ -f $G/engine.pid ] && kill $(cat $G/engine.pid) 2>/dev/null
  for i in $(seq 40); do kill -0 $(cat $G/engine.pid) 2>/dev/null || break; sleep 0.5; done
  [ -f $G/sleep.pid ] && kill $(cat $G/sleep.pid) 2>/dev/null
  rm -f $G/engine.pid $G/sleep.pid; echo stopped ;;
esac
