# Why the reply after a tool result is empty on E4B and E2B

Measured 2026-11-25 on llama-server b12680 (`--jinja --reasoning off -np 3`) through the engine. Scripts: `~/.local/share/rimworld-hermes-engine/toolharness/r2*.py`. The bench numbers this explains: round-2 non-empty 100% (gemma4-rp-maxxed), 36% (E4B), 0% (E2B) (docs/MODEL-BENCH.md, tools section).

## Cause

The server, the parser and the template are not at fault. After the tool result the **model itself ends its turn at once**. The first token it samples is the end-of-turn token (`<turn|>`, token id 1), so `finish_reason` is `stop` after one token and `content` is `""`. The fine-tune decides this: the same rendered prompt tail gives P(end-of-turn) = 0.00 on gemma4-rp-maxxed, 0.40 to 0.98 on E2B and 0.70 on E4B.

Two things make it likely on the small models:

1. **The native turn has no generation prompt after a tool response.** `/apply-template` for the round-2 messages ends in the middle of the open model turn:

```
<|turn>model
<|tool_call>call:say{line:<|"|>Pip! What in the blazes was that?! You clumsy oaf!<|"|>}<tool_call|><|tool_response>response:say{value:<|"|>{"ok": true}<|"|>}<tool_response|>
```

   There is no `<|turn>model` and no `<|channel>thought\n<channel|>` (the empty thought that `enable_thinking: false` adds on round 1). The model sees a call and its answer inside a turn it already considers finished. For an action-only prompt ("tell her off") the call was the whole job, so ending is the natural next token.
2. **The prompt told it to act only through tools.** With nothing left to do the best continuation is silence.

## Evidence

| Check | Result |
|---|---|
| `/completion` on the rendered round-2 prompt, E4B, temperature 0, `n_probs` 6 | one token, `stop_type: eos`; top token id 1 (`<turn|>`), logprob -0.357 (P = 0.70); next are `\n` 0.07, `call` 0.07, `<|tool_call>` 0.03 |
| same, E2B, seven different first-round calls | P(eos) = 0.96, 0.98, 0.94, 0.84, 0.50, 0.40, 0.42; action-only "say" prompts are the worst |
| same, gemma4-rp-maxxed, same seven | P(eos) = 0.00 every time. The rendered tails are byte-identical (`...<tool_response|>`), so the template is not the variable |
| `/v1/chat/completions` round 2, E4B, `enable_thinking` false or unset | `finish_reason: stop`, `content: ""`, no `reasoning_content`, no `tool_calls`, 3 of 3 |
| same with `enable_thinking: true` | the model reasons ("The task is complete.") and then writes `I told her off.` in 1 of 3, empty content in 2 of 3. Thinking only helps a little and costs time |
| `tool_choice: required` on round 2 | forces a call, but a made-up one (`set_mood` on all 3). That swaps silence for an invented action |
| tool message without `name`, with a plain-text body (`Done. Your line was spoken aloud.`), content string vs `{"ok": true}` | all still empty 3 of 3. Tool-result format is not the cause |
| raw `/completion`, rendered prompt + `<turn|>\n<|turn>model\n` (open a fresh model turn) | `<|tool_call>call:do_nothing{}<tool_call|>` 3 of 3: with a new turn it speaks, but only to call another tool |
| raw `/completion`, rendered prompt + `<|channel>thought\n<channel|>` | writes a thought in plain text ("The user wants me to ... no further action"), then `<channel|><|tool_call>call:do_nothing{}`; the thought lands in `content` |
| tool result sent as plain text in a user turn (no tool role), chat endpoint | answers every time (3 of 3 non-empty) |
| parser swallowing text | no: the raw token stream is the empty string; nothing was parsed away |

`--reasoning off` is not the cause either: turning thinking on changes the output a little (above) but does not fix it, and the first-round call is fine under both.

## Failing and working request shapes

Failing (native tool role, E4B/E2B):

```json
{"messages": [
  {"role": "system", "content": "..."}, {"role": "user", "content": "Pip just dropped a full tray of stew... Tell her off out loud."},
  {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "type": "function", "function": {"name": "say", "arguments": "{\"line\":\"Pip! ...\"}"}}]},
  {"role": "tool", "tool_call_id": "c1", "name": "say", "content": "{\"ok\": true}"}],
 "tools": [...], "tool_choice": "auto", "chat_template_kwargs": {"enable_thinking": false}}
```
-> `finish_reason: stop`, `content: ""`.

Working (what the harness sends; we own the result turn): a user turn with the result and the allowed next moves, every step decoded under a grammar that has no empty alternative:

```json
{"messages": [
  {"role": "system", "content": "<base>\n<protocol and tool list in plain text>"}, {"role": "user", "content": "<request>"},
  {"role": "assistant", "content": "{\"tool\":\"say\",\"args\":{\"line\":\"Pip! ...\"}}"},
  {"role": "user", "content": "Result of say: ok\nReply with the next JSON object: another tool call, a final line, or done."}],
 "grammar": "<root ::= call... | final | done>"}
```
-> a call, `{"final": "..."}` or `{"done":true}`, 61 of 61 scenarios on all three models, no retry needed on E2B/E4B in grammar mode (docs/TOOL-HARNESS.md).

## What follows for the design

- Do not rely on the native tool-role turn for small models. It is not a template bug we can patch; it is the model's habit. Decide per model by a probe (`probe()` in `core/toolloop/harness.py`): three native calls, each followed by a result turn; the native form is chosen only if all three get a reply. gemma4-rp-maxxed passes (and runs native in tolerant mode); E4B and E2B fail and run the plain form.
- A grammar with no empty alternative removes the failure whatever the model prefers: an end-of-turn token cannot be sampled where the grammar requires `{`.
- An empty reply is still possible on endpoints without grammars (native tool mode). There the loop reads "empty after a tool result" as an explicit done (`implicit_done` in the trace) and "empty on the first step" as a failure to retry; nothing waits for text.
