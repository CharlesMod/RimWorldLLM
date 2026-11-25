# bench/m0 - HermesVoice milestone 0 harness

Everything runs against one llama-server at a time on 127.0.0.1:8099 (override with `M0_URL`).
Scratch, venv and server logs live in `~/.local/share/rimworld-hermes-m0/` (never /tmp). Raw results: `results/`. Write-up: `docs/M0-RESULTS.md`.

Setup once: `python3 -m venv ~/.local/share/rimworld-hermes-m0/venv && venv/bin/pip install requests numpy scikit-learn`.
`~/.local/share/rimworld-hermes-m0/srv.sh <name> <model.gguf> <llama-server args>` starts a server in the background (pid in `srv.pid`) and waits for health; `stop.sh` kills it by that pid.
`model_q3kxl.txt` in that directory holds the path of the model under test.

| file | what |
|---|---|
| `lib.py` | fixtures loader, SPEC section 4 prompt builder (blocks A-G, Gemma template), streaming client, helpers |
| `fixtures/` | `pawns.json` (6 colonists), `scenes.json` (30), `scenes_neutral.json` (26, focal-neutral, for Q4), `grim.json` (20), `intent_labeled.json` (30) |
| `footprint.sh`, `speedprobe.py` | VRAM footprint and a small speed probe per `--n-cpu-moe` |
| `q1_lease.py`, `run_q1.sh` | Q1 lease latency (needs flags under test on the server) |
| `q2_spec.py`, `run_q2.sh` | Q2 speculative decoding, one server per spec type |
| `q3_conc.py` | Q3 three slots at once |
| `q4_voice.py gen\|score <label>` | Q4 voice distinctness (generation, then classifier + slop) |
| `q5_grim.py`, `q5_score.py` | Q5 grim content (Qwen needs `M0_CHATML=1`; `M0_NOGRIM=1` drops the licence sentence from block A) |
| `q6_intent.py` | Q6 intent pass, latency and accuracy |
| `ramcost.py` | RAM prompt-cache cost per cached pawn prefix |
| `run_rest.sh`, `run_final.sh`, `chain.sh` | drivers used for the real runs |
| `summarize.py` | builds `results/summary.md` from the JSON |

Rerun order for a new model: `footprint.sh` -> `run_q1.sh` -> `run_q2.sh` -> `run_rest.sh`/`run_final.sh`.
Run `q4_voice.py score` with `OMP_NUM_THREADS=1` (otherwise scikit-learn oversubscribes the CPU).
