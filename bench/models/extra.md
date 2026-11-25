## Findings: what to change in the agent, engine or settings

None of these were changed in this branch.

**F1. Sanitizer drops sentence-initial "It" (agent bug).** `core/sanitize.py` `_bad_name`: at a sentence start only a vocative (`Zed, ...`) or a capitalised word followed by a verb counts as a name, and "It" is not in `_COMMON` (only "it's" is). So "It is good meat, Pip." is dropped as `unknown_name:It`. This was 6 of the 100 Q3 lines (every drop for that model), 1 for e4b and 1 for e2b. Fix: add "it" (and "its", "this", "these", "those", "they're") to `_COMMON`. Repro: `bench/models/tests/test_agent_findings.py`.

**F2. Sanitizer drops possessives of known names (agent bug).** `_NAME_TOKEN` captures `Hana's`, and `lower_ok` holds `hana`, so "I keep Hana's shelf exact." and "Okay so Moth's panicking" are dropped as `unknown_name:Moth's`. It also rejects reflection bios (`run_reflection` returns None): all but one of the 5-15% reflection rejections in this bench are this (the other: a bio starting 'Quiet is easier now.', the same sentence-initial rule as F1). Fix: compare the token with a trailing `'s` removed. Repro in the same test file.

**F3. Third-person narration and bullets pass the sanitizer (agent gap, Ling).** "Tuk feels the bandage. Tuk does not like it." and "- Six minutes. No sound." are kept by `sanitize_line` (it strips only a leading dash). A cheap guard: drop or re-ask when the line starts with the speaker's own name followed by a verb. The leading `- ` is already stripped by the sanitizer, so only the narration is the real gap.

**F4. Tool-argument ranges are not enforced (engine/llama.cpp behaviour, prompt).** `minimum`/`maximum` in the tool schema do not reach the tool-call grammar of this llama-server build, and the Gemma models often ignore them when they sit only in the schema (importance 10 or 100, delta 10 or -20). Fix: state the range in the tool description text, and clamp in the executor. Ling did not need it (100% valid).

**F5. Gemma tool calls sometimes stay as raw text (engine/llama.cpp b12680 parser).** With `tool_choice=auto`, e2b left 5 and e4b 2 of 30 calls as `remember{fact:<|"|>...<|"|>,importance:2}` in `content` and no `tool_calls`. With `tool_choice=required` this never happened (0 of 60). If the mod ever relies on tools, use `required` (with `do_nothing` as the "nothing to do" call) for Gemma.

**F6. Repetition in conversations (settings).** The agent samples with temperature 0.8, top_k 64, top_p 0.95, min_p 0.05 and no repetition control. A speaker repeating its own earlier line verbatim appeared in 4 of 10 e2b and 2 of 10 Ling conversations, and Ling repeats the same line across scenes (17% duplicates). The M0 study already tried DRY (`dry_multiplier 0.8, dry_base 1.75, dry_allowed_length 2, dry_penalty_last_n 512`); passing those in `HttpLLM._sampler` for the line pass would likely remove it. Not tried here.

**F7. `json_schema` with a bare `enum` is unreliable on /v1/chat/completions (engine/llama.cpp).** The intent pass in the agent uses a GBNF grammar, which works for every model (the schema mode scored 30% valid on e2b, 93% on Ling, 100% on the other two). A single probe without a system message on e2b also produced a `reasoning_content` of "Thinking Process" under `--reasoning off`; the agent's own request (system turn plus `enable_thinking: false`) never leaked a thought in 400 lines. No action needed unless the agent moves the intent pass to `json_schema`.

**F8. The real line is 11-17 tokens, not 24-40.** The agent's prompt ("One line only", `stop: ["\n"]`) yields a median 12-17 token line. The speed table therefore also shows a 24-40 token case made by asking for two or three sentences (rows `long_*`); it is not what the agent sends today.

**F9. Ling and `--jinja`.** No chat-template problem found: 0 thinking leaks, no template residue in 100 lines, tool calls parse in 100% of runs, and a tool result fed back gets a sensible answer in 100% of rounds. The Ling issues are about writing style (F3, F6), not the template.

**F10. Tools and the "nothing to do" case.** With `tool_choice=required` the models must call something; `do_nothing` was used correctly 40-60% of the time on the five no-tool prompts. With `auto`, Ling called `say()` on 2 of 5 such prompts.

### Method notes and limits

- Grammar and spelling: no Java here, so `language_tool_python` was not run. `pyspellchecker` plus a rule set stood in; it found no faults in any model's lines, so treat 100% as "no obvious faults", not "perfect grammar".
- Fixtures drive the prompt, so the dead colonist Hana Brandt is a known name only through the relations in block C.
- Conversation "answers the previous turn" is a lexical heuristic (shared content word, direct address, yes/no opener); read the quoted exchanges.
- Voice distinctness: the M0 venv (`~/.local/share/rimworld-hermes-m0/venv`) has no numpy or scikit-learn, and `q4_voice.py` generates through a Gemma-only raw template, so the same TF-IDF char n-gram scorer was run from the bench venv on lines made through the agent's real line path. Lines per pawn: 12 neutral scenes.
- Speed uses the agent's exact request body, streamed; "cold (slot erased)" erases the slot first; "cold, new pawn prefix" leaves another pawn's prefix in the slot (block A still matches).
- Tool tests use temperature 0.2 and a six-line system prompt; all five tools have JSON schemas with required fields and numeric ranges.
- Reproduce: `bench/models/run_all.sh` (engine up on 47480), `rescore.py`, `report.py <results> verdicts.json extra.md`. Raw per-model JSON lives in `~/.local/share/rimworld-hermes-engine/bench/results/<model>/`.
