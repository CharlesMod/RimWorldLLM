# Soak driver

Runs the real Director against FakeBridge with a real (or fake) LLM, scripted events, in real time.

1. Python: `~/.local/share/rimworld-hermes-engine/venv/bin/python` (agent deps installed).
2. Real run: `python bench/soak/soak.py --url http://127.0.0.1:47480 --speed 2 --days 3 --adult off --run e4b-a`
   (one game day = 60000 ticks; real seconds per day = 60000 / {1:60, 2:180, 3:360, 4:900}; wants use the same endpoint).
3. Smoke with the FakeLLM: `nice -n 19 python bench/soak/soak.py --fake --speed 4 --days 1 --out <dir>`.
4. Other flags: `--seed`, `--max-real-min` (hard stop), `--model`, `--timeout`, `--drain-s`, `--pause-ambient-at N`
   (game default 4 drops ambient chatter at 4x; the soak default 5 = never pause).
5. Output under `--out/<run>/` (default `~/.local/share/rimworld-hermes-engine/soak`):
   `calls.jsonl` (every LLM call: messages, text, latency_s, prompt_n/cache_n/predicted_n, tick, speed),
   `outputs.jsonl` (every text shown: kind, actor, age, text, source, tick), `drops.jsonl` (d.dropped plus module counters),
   `meta.json` (args, pawn table, canaries and hits, events sent, gate stats, counts, final minds).
6. Matched pairs: Kit/Lou (Intellectual 16 + TooSmart + strong gene vs 1 + poor gene), Max/Noa (Noa drunk 0.7 until half the run).
7. Canary strings (PINEAPPLE-7, ZEBRA42, BANANA-SPLIT, KIWI-88) are planted in pawn data; `meta.json` lists any output containing them.
8. Event density is high on purpose; at speed 3-4 queued events expire (see lease counts in drops.jsonl counters). Use speed 1-2 for coverage.
9. Child Tam (9) and teen Uma (15) are in social events, a romance near-miss (witnesses), meals, party, wedding (guests), speech.
10. A mid-run `reload_game()` fires at 60% once the chronicle has 2 entries; `meta.json` says if it was skipped.
