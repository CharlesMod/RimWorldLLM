# hermesvoice-agent

A game-agnostic engine that gives characters minds and short spoken lines, plus a RimWorld adapter.
It talks to the game over a bridge (HTTP, `docs/BRIDGE-API.md`) and to a llama.cpp server
(OpenAI-style `/v1/chat/completions`, or native `/completion` with `id_slot` and cache stats).

## Run

```
python -m venv .venv && .venv/bin/pip install -e '.[dev]'
hermesvoice-agent test-endpoint --llm-url http://127.0.0.1:8080     # the Test button
hermesvoice-agent run                                               # needs the game running
hermesvoice-agent replay events.jsonl                               # offline, fake bridge + fake LLM
.venv/bin/python -m pytest                                          # 132 tests; the `gpu` test is skipped
```

Config: defaults, then a TOML/JSON file (`--config` or `$HERMESVOICE_CONFIG`), then `HERMESVOICE_*`
environment variables (`HERMESVOICE_LLM_URL`, `_LLM_KEY`, `_LLM_MODEL`, `_LLM_MODE`, `_BRIDGE_URL`, `_BRIDGE_TOKEN`,
`_WINDOWS`). If no LLM URL is set there, the `llm` block of `bridge.json` is used. Without `bridge_url` the adapter's `bridge.json` location is read. Every LLM call is logged
to `<data_dir>/calls.jsonl` with prompt_n, cache_n, prompt_ms, predicted_ms, raw and effective prefill.

## Layout: core and adapter

```
hermesvoice_agent/
  core/            game-agnostic engine. Imports nothing from games/. No game words.
  games/rimworld/  the RimWorld adapter (RimWorldAdapter, bio/voice templates, block A text, fake bridge, replay)
  cli.py           entry point: core CLI shell + RimWorldAdapter
```

A test (`tests/test_split.py`) fails if a `core/` module imports from `games/` or contains
game terms (pawn, colony, tick, raid, injury, ...).

Core: LLM client and call log; bridge client and `EventPoller`; minds, memory (witness-only, importance,
pinning, 4-day half-life decay), ledger; prompt blocks A-G (A-E byte-stable per lease); lease manager
(windows, priority classes and preemption, dual-clock expiry, quotas, ambient pause, narrator coverage);
line, intent (GBNF) and sanitizer passes; conversations; the `Director` that wires them.

Time is an abstract game clock: the adapter names the envelope field and says how many units make a "day"
(`units_per_day`). Core only compares and subtracts it, plus real seconds. There is no fixed cast: actors
appear when a roster, event or talk request mentions them; `is_controlled(id)` marks a player character
that others talk to but that has no mind. The transport is behind `Bridge` and events behind `EventSource`
(Protocols in `core/bridge.py`), so a poll, a push queue, an emulator RAM reader or an in-process plugin
can stand in for HTTP.

### What a new adapter provides (`core/adapter.py`, `GameAdapter`)

- `protocol` (`ProtocolNames`): endpoint paths, the speaker field, and the envelope names for game time,
  world epoch and write expiry.
- `units_per_day`, `intent_labels` (first = "nothing"), `block_a` (world rules, speech rules, output format,
  label glosses), `common_words`, `narrator_prompt`, `default_bridge_json()`, `is_controlled()`, `replay()`.
- Events: `normalize_event` to the core shape `{seq, game, type, actors:[{id, role}], summary, severity,
  witnesses, ledger_id, data}`, `priority` (a `Cls`), `importance`, `is_pinned`, `first_person`,
  `conversation_pair`, `is_urgent_visual`, `is_ledger_end`, `told_intents`, `about_intents`.
- World reads: `roster`, `roster_ledger`, `name_info`, `snapshot_name`, `snapshot_job`, `relations`,
  `scene_present`, `build_scene`.
- Minds: `facts_hash`, `build_fixed_bio`, `build_voice_card`, `talk_weight`, `voice_example_prompt`.

Built (milestones 3 and 5): reflection (one JSON-schema call in an idle window; the only place block C changes),
memory upkeep (merge similar old memories, drop decayed ones, pins), gossip and confiding with visibility
bookkeeping, secrets (a sanitizer check against quoting a private memory), daily diaries (sent as `POST /thought`
with `kind: "diary"`), the narrator's chronicle (slot 0, idle-only, rolled by self-summary under 6,000 tokens;
stored in the agent data dir as chronicle.json/.jsonl/.md) and narrator coverage with a minimum gap. Idle work
runs one job at a time and is cancelled the moment an actor window is wanted. `replay tests/data/multiday.jsonl`
shows reflections, diaries and chronicle entries over four days. Player edits to director's notes or the voice
card win on a `PUT /mind` conflict.

Chronicle entries are also sent to the mod as `POST /chronicle {op_id, day, text, kind}` (on by default; set
`chronicle_bridge = false` to keep them in the agent's data dir only; an older mod without the endpoint just logs a warning).
`BridgeClient.get_chronicle(since_day)` reads `GET /chronicle` back.

Not built: Skyrim/Fallout or emulator adapters.

## Contract notes (docs/BRIDGE-API.md v2, Parts 1 and 2)

- Every write carries `op_id` (a uuid) and, where it can go stale, `expires_tick` (`expires_at` in core).
  A failed write is not remembered by the mod, so a retry runs again.
- `/say` takes `about` for gossip; the director picks a third actor from the event or the speaker's
  strongest relation, and downgrades gossip to "none" when there is nobody. Refusal reasons (`pair_cap`,
  `effects_off`, `not_negative`, `no_about`) arrive in `refused.effect`; the bubble still shows and the
  agent records nothing for a refused effect.
- `GET /talk` replies `{"requests": [...]}`; a request is answered by a `/say` carrying `talk_id`.
- A change of `save_epoch` makes the agent drop and re-read all minds; `gap: true` on `/events` makes it
  re-read the roster to resync the ledger.
- The fake bridge (`games/rimworld/fakes.py`) makes these choices where the contract is silent: gossip
  without `about` is refused `no_about`; `comfort` needs a negative mood thought on the listener; a per-pair
  per-intent cap (default 6) refuses with `pair_cap`.
