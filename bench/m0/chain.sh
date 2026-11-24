#!/usr/bin/env bash
cd "$(dirname "$0")"
while [ ! -f results/q1_done.flag ]; do sleep 5; done
./run_q2.sh > ~/.local/share/rimworld-hermes-m0/logs/run_q2.out 2>&1
./run_rest.sh > ~/.local/share/rimworld-hermes-m0/logs/run_rest.out 2>&1
