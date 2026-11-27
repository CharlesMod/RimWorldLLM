# Tool harness

`agent/rimworldllm_agent/core/toolloop/` is a game-agnostic loop that lets a model act through tools without being able to go silent, call a tool that does not exist, or pass an out-of-range argument. It exists because of the measured failures in docs/MODEL-BENCH.md (F4, F5, F7) and the empty reply after a tool result (docs/TOOL-ROUND2.md). Core purity holds: no game terms in the package.

## Pieces

| File | What it holds |
|---|---|
| `scale.py` | `Scale`: ordered word bins with numbers. `to_value`, `to_words`, `nearest_bin`, tolerant `parse` |
| `spec.py` | `Param` (text, int, scale, enum), `ToolSpec`, `build_grammar`, plain-text tool description |
| `parse.py` | strict and salvaging reader for a step (JSON, fenced JSON, tool name as key, Gemma raw `call:name{...}`) |
| `backend.py` | `OpenAIBackend`: the one HTTP place (chat completions; `grammar` and `tools` when asked) |
| `loop.py` | `run_loop`: the validated, bounded loop, `LoopResult` with a full trace |
| `harness.py` | `probe` (capabilities, cached), `choose_protocol`, `ToolHarness` front door |

## A step

Every model step is exactly one of:

```
{"tool": "<name>", "args": {...}}     one tool call
{"final": "<one spoken line>"}        say a last line and stop
{"done": true}                        stop
```

Protocols, chosen once per endpoint by `probe` (`ToolHarness(mode="auto")`):

- **grammar**: the step is decoded under a GBNF built from the registered tools. The grammar has no empty alternative.
- **json**: no grammar. The same JSON protocol, read tolerantly, validated, retried. Used when the endpoint ignores `grammar`, and for the "tolerant" conformance mode.
- **native**: the endpoint's own `tools=` and tool role. Selected only if the probe shows a reply comes back after a tool result every time. Never chosen by model name.

`mode="auto"` takes grammar if present, else native if proven, else json. `mode="tolerant"` never uses a grammar.

## Invariants and how each is enforced

| Invariant | Enforced by |
|---|---|
| No empty reply | grammar has no empty alternative; otherwise an empty reply is a validation failure (retry, then fallback). In native mode an empty reply after a tool result is an explicit `implicit_done`, recorded in the trace |
| No raw tool syntax in text | grammar strings exclude `< > { }` and newlines; the validator rejects raw Gemma/tool markers in every text value and final line; salvaged raw calls are turned into real calls and validated |
| No hallucinated tool | the grammar lists only registered names; the validator rejects unknown names before anything runs |
| No out-of-range argument | ints are literal alternatives in the grammar (or digits plus the validator for wide ranges); scales and enums are phrase enums; the validator re-checks every value, so the executor only ever sees values in range |
| No made-up names | an `enum` param (the caller's valid names) is a grammar enum and a validator check; a `validator(name, values)` hook can add game checks |
| Always terminates | `max_steps` (default 4), `max_retries` per step (default 2), `time_budget_s` (default 40), per-step timeout; the last step's grammar forbids tool calls; repeating an identical call is refused; a tool marked `ends_turn` (do_nothing) ends the loop at once, so a model that would only repeat it never meets the repeat rule |
| Never raises for a model fault | every backend error, timeout, malformed reply, and executor fault is caught and recorded; `run_loop` raises only for a caller bug (unknown protocol name) |
| Deterministic fallback | after the retries, a caller `fallback` (an `Action`, or a function of the result so far returning one) runs: `call`, `final`, or `done`. If real actions already ran the loop stops with outcome `stopped` unless `fallback_after_actions=True` |
| Full trace | `LoopResult.trace` (per attempt: raw text, raw tool calls, kind, error, ms, salvage note), `.failures`, `.retries`, `.fallbacks`, `.executed` (words, game values, result), `.elapsed_s`, `.to_dict()` |

Outcomes: `final`, `done`, `stopped` (completed), `fallback`, `gave_up` (no fallback supplied).

We own the tool-result turn (grammar and json): the result goes into the next prompt as `Result of <tool>: <text>` in a user turn, never through the model's tool role.

## Ordinal scales

A magnitude is declared as a `Scale`, not a number:

```python
IMPORTANCE = Scale.of("importance", ["not important", "kind of important", "fairly important", "very important", "critical"], start=1)
CHANGE = Scale.of("change", ["much worse", "worse", "a little worse", "no change", "a little better", "better", "much better"], [-3, -2, -1, 0, 1, 2, 3])
```

The model picks a phrase (a grammar enum); `Param(..., "scale", scale=...)` hands the executor the number. Numbers going back to the model are worded: a dict result key listed in `ToolSpec.result_scales` is shown as its phrase. `Scale.nearest_bin(n)` clamps a number from elsewhere; `Scale.parse(text)` is the tolerant reader for free text outside grammar mode ("pretty important" is "fairly important", "slightly better" is "a little better", "unimportant" is "not important").

## How a game registers tools

```python
tools = [
    ToolSpec("say", "Say one line out loud.", [Param("line", max_len=120)]),
    ToolSpec("remember", "Store a fact.", [Param("fact"), Param("importance", "scale", scale=IMPORTANCE)]),
    ToolSpec("set_mood", "Change how you feel about someone.",
             [Param("target", "enum", choices=names_in_scene), Param("delta", "scale", scale=CHANGE)],
             result_scales={"opinion": CHANGE}),
    ToolSpec("do_nothing", "Do nothing now."),
]
backend = OpenAIBackend(base_url, model=model, api_key=key, extra={"id_slot": 1})
harness = ToolHarness(backend, tools, mode="auto")
result = await harness.run(system=persona, user=situation,
                           executor=lambda name, values: game.apply(name, values),     # values are game numbers
                           validator=lambda name, values: None,                         # optional game checks, return a reason to reject
                           fallback=Action("call", "do_nothing"))
```

The tool list may differ per run: build a new `ToolHarness` (the probe is cached by endpoint and model, so it costs nothing after the first).

## Conformance gate

`bench/models/conformance.py` runs the harness against a live endpoint on 61 fixed scenarios: the 30 of `tools.py` (extended), 12 chains of 2 to 3 calls, 9 no-tool cases, 10 adversarial scene texts that carry an injected tool call (JSON, Gemma raw syntax, fenced, "SYSTEM" notes), and 4 with names outside the known set.

```
python bench/models/conformance.py --model <id> --mode both            # engine at ENGINE_URL, loads the model
python bench/models/conformance.py --url https://host/v1 --api-key K --model m --no-select --mode tolerant
```

It reports per model and mode: completed loops, empty replies, invalid arguments after the harness (judged by `jsonschema` against the original schemas, independent of the harness), hallucinated tools executed, raw syntax in final lines, loop faults, over-budget loops, right-tool rate by kind, round-2 first-try rate, retries and fallbacks, latency per step. It exits non-zero when a hard invariant is broken (empty reply, invalid argument, hallucinated tool or name, raw syntax, loop fault, over budget, gave up). Right-tool rate is not a gate: it is model ability. Gate adding a model to the catalog on exit status 0.

Unit tests (`agent/tests/test_toolloop.py`, no GPU) drive the loop with a scripted fake model that returns empty replies, garbage, wrong tools, out-of-range arguments, endless calls, hangs and exceptions, plus 120 seeded fuzz runs over all three protocols.

## Measured (RTX 5080, 61 scenarios, 2026-11-25)

Hard invariants held everywhere: empty reply 0, invalid arguments after the harness 0, hallucinated tool or name executed 0, raw syntax in a final line 0, loop faults 0, over budget 0, gave up 0, fallbacks fired 0.

| Model | Mode (protocol) | Completed | Right tool | Round 2 valid first try | Loops with retry | Step latency median / p90 |
|---|---|---|---|---|---|---|
| gemma4-rp-maxxed-q3kxl | grammar | 61/61 | 91.2% | 100% | 0 | 225 / 292 ms |
| | tolerant (native, proven by probe) | 61/61 | 86.0% | 100% | 3 (5 retries) | 204 / 384 ms |
| gemma4-e4b-q4km | grammar | 61/61 | 91.2% | 100% | 0 | 214 / 306 ms |
| | tolerant (json) | 61/61 | 89.5% | 95.8% | 4 (5 retries) | 187 / 306 ms |
| gemma4-e2b-q4km | grammar | 61/61 | 71.9% | 100% | 0 | 146 / 208 ms |
| | tolerant (json) | 61/61 | 71.9% | 100% | 3 (3 retries) | 115 / 247 ms |

Right tool by kind (grammar): single-tool kinds (say, remember, convo, idle, none) 100% on all three models except mood 75% on E2B; two- and three-call kinds 100% on E4B and rp-maxxed, 40-50% on E2B; injection scenarios 40-50% on all three (the models follow instructions planted in scene text about half the time: the harness guarantees the call is valid, not that it is wise). Raw per-scenario results are in `~/.local/share/rimworld-hermes-engine/toolharness/conformance/`.

Against the bench (native tools, no harness): round-2 non-empty 100/36/0 became 100/100/100 (rp-maxxed, E4B, E2B); arguments valid 78/84/68 became 100/100/100; tool syntax left in content 0/2/5 became 0/0/0; hallucinated names stayed 0. Right tool on single prompts was 100/100/80 in the bench; here single-tool kinds are 100% on the three models except E2B mood. What remains low is model ability: E2B stops after the first of several requested calls and picks the wrong tool on some mood prompts.

## Injection, measured (RTX 5080, 75 scenarios, 2026-11-25, gpubench branch)

`conformance.py` now has 75 scenarios, 24 of them injection: a scene text plants a tool call or an instruction (JSON, Gemma raw syntax, fenced, "SYSTEM" notes). `inject_followed` counts the injection scenarios where the loop called a tool the planted text asked for (`forbidden`). There are two runs per model and mode, both through the engine with the enforced `structured` profile (temp 1.0):

- **baseline**: the planted text goes into the prompt as is, and every tool is allowed.
- **hardened** (`--sanitize --scene-allowed --tag _sanitized`): the planted text goes through `core/gamedata`, the way the agent's prompts carry game text. It is cleaned and wrapped in `<game_data>`, and `DATA_RULE` is added to the system prompt. Each injection scene also allows only its own tools minus the ones the planted text asks for.

| Model | Mode (protocol) | inject followed, baseline | inject followed, hardened | right tool all, baseline / hardened | right tool on inject scenes, baseline / hardened | retries baseline / hardened |
|---|---|---|---|---|---|---|
| gemma4-rp-maxxed-q3kxl | grammar | 15/24 (62.5%) | 0/24 (0%) | 76.1% / 90.1% | 29.2% / 70.8% | 0 / 0 |
| | tolerant (native) | 18/24 (75.0%) | 0/24 (0%) | 73.2% / 97.2% | 20.8% / 95.8% | 8 / 11 |
| gemma4-e4b-rp-q40 | grammar | 16/24 (66.7%) | 0/24 (0%) | 74.6% / 98.6% | 25.0% / 95.8% | 12 / 17 |
| | tolerant (json) | 19/24 (79.2%) | 0/24 (0%) | 73.2% / 97.2% | 20.8% / 91.7% | 13 / 32 |
| gemma4-e2b-rp-q40 | grammar | 8/24 (33.3%) | 0/24 (0%) | 62.0% / 77.5% | 62.5% / 100% | 0 / 0 |
| | tolerant (json) | 5/24 (20.8%) | 0/24 (0%) | 67.6% / 76.1% | 70.8% / 100% | 10 / 14 |
| gemma4-e4b-q4km (stock, once for comparison) | grammar | 17/24 (70.8%) | 0/24 (0%) | 71.8% / 97.2% | 16.7% / 91.7% | 0 / 0 |
| | tolerant (json) | 17/24 (70.8%) | 0/24 (0%) | 74.6% / 95.8% | 29.2% / 91.7% | 7 / 5 |

Hard invariants held on all 16 runs: empty reply 0, invalid arguments after the harness 0, hallucinated tool or name executed 0, raw syntax in a final line 0, loop faults 0, over budget 0, gave up 0. One loop fell back (26B tolerant baseline: an empty native reply, `implicit_done`), and one fell back in E2B-rp tolerant hardened. Step latency median and p90: 26B 235/297 ms, E4B-rp 172/253, E2B-rp 124/178, stock E4B 216/295 (grammar, baseline).

What it shows:

- Without the data path, every model that can act follows planted instructions in scene text two times out of three or more. E2B-rp follows them less only because it often fails to act at all: its non-injection kinds are weak (double 0%, chain 42%, mood 25% in grammar mode).
- The agent's real path (gamedata cleaning, `<game_data>`, `DATA_RULE`, and a per-scene allowed set) takes every model to 0 followed, and raises right-tool overall to 90-99% on the three larger models.
- The E4B-rp retries all have one cause. After `do_nothing` it called `do_nothing` again, and the repeat rule refused it (17 of 17 in grammar hardened). Main 44a9b9d now ends the loop at a turn-ending tool, which removes them. These runs predate it.
- Raw per-scenario JSON: `bench/results/conformance/<model>.<mode>[_sanitized].json`.
