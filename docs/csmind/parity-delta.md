# Parity: delta (Python main changes b05aebb..5a5a1b8 ported into the existing C#)

Python source diff: `git diff b05aebb 5a5a1b8 -- agent`. Ported here: core/adapter, core/bridge, core/config, core/toolloop
(loop, spec), games/rimworld adapter, block_a, fakes (FakeBridge), steward_tools, steward_system.txt, new textfill. Not here
(other workers or the later director/idle/steward port): core/director.py, core/idle.py, core/steward.py, steward_try.py,
inner/jobs modules.

## What changed in C#
| Python | C# |
|---|---|
| core/adapter.py `drops_meta(text, context)` | `IGameAdapter.DropsMeta(string, IEnumerable<string>)`, `GameAdapterBase` default false |
| adapter `idle_fill()` (RimWorldAdapter, returns `textfill.fill_once`) | `IGameAdapter.IdleFill()` returning `Func<ITextFillHost,int,Task<int>>`; base default null (Python `getattr(adapter, "idle_fill", None)`) |
| games/rimworld/adapter.py (chitchat wording, `_NO_META_RULE`, `drops_meta`, `first_person` chitchat rewrite, `vanilla_request`) | `RimWorld/Adapter.cs`; `AdapterText.cs` regenerated with contract/gen_adapter_text.py; `BlockAText.cs` gains the no-meta line (hand edit, checked by ContractGoldenTests against Python) |
| core/bridge.py `text_queue(n)`, `text_fill(id, text, title?, op_id?)` | `IBridge.TextQueueAsync(int n = 4)`, `TextFillAsync(string id, string text, string title = null, string opId = null)`; `BridgeClient`; `FakeBridge` (Mod.Tests) with `TextSupported` (default false), `TextItems`, `TextFillLog`, `TextPendingExtra`, plus a test hook `OnTextQueue` (Python tests monkeypatch `bridge.text_queue`; the hook runs after the reply is built) |
| core/config.py `text_fill: bool = True` | `MindConfig.TextFill` (JSON `text_fill`, after `art_max_tokens`) |
| toolloop/spec.py `Param.same_call`, `ToolSpec.call_key` | `Param.SameCall` (ctor arg `sameCall`), `ToolSpec.CallKey(JObject)`; Loop.cs uses it for both the repeat check and `RunCalls` |
| steward_tools.py `why` param on every acting tool, `WHY_MAX = 80`, `_body` wrapper (why cleaned with `gd.clean(.., "why", 80)`, stripped, sent only when non-empty), set_time no longer sends `reason` | `StewardTools.cs` (`WhyParam`, `Body` over `VerbBody`) |
| steward_system.txt | `Resources/steward_system.txt` copied byte for byte |
| games/rimworld/textfill.py | `RimWorld/TextFill.cs` (static class `TextFill`) + `Core/TextFillHost.cs` (`ITextFillHost`) |

Goldens: `Mod.Tests/Mind/ContractGoldenData.cs` regenerated with contract/gen_golden.py (events 3-5, prompt blocks and adapter wording changed).

## TextFill host interface (Wave 3 convention)
`ITextFillHost` (Core/TextFillHost.cs) lists what textfill.py reads of the Python director `d`:
`IBridge Bridge`, `ILlmClient Llm`, `double TextFillWaitS {get;set;}`, `bool TextQueueLive {get;set;}`,
`HashSet<string> TextTried {get;}` (Python `d.__dict__.setdefault("text_tried", set())`; entries are `TextFill.IdKey(id)`, the id's
JSON text), `List<(int Actor, string Reason)> Dropped {get;}`. The Director implements it. `TextFill.FillOnceAsync(host, slot)`
returns the number of texts stored; LlmError propagates (idle backs off).

## What idle.py must do (for the director worker porting core/idle.py)
1. Director gets `TextQueueLive = false` and `TextFillWaitS = 0.0` (director.py `__init__`).
2. Constants `TEXT_FILL_ACTOR = -2` (pseudo actor: real minds are >= 0, -1 is the narrator in the drop log), `TEXT_FILL_GAP_S = 10.0`;
   IdleRunner field `text_next = 0.0` (director clock).
3. At the END of the idle job chain (after the wants loop, so it is the lowest job) call `_start_text_fill()`.
4. `text_fill_fn()`: `adapter.IdleFill()` is null, or `cfg.TextFill` is false, or `bridge_cfg["text_fill"]` is exactly false -> null (off);
   otherwise `adapter.IdleFill()`. (Missing key in bridge_cfg means on.)
5. `_start_text_fill()`: fn null, or `!_ok("textfill", -2)`, or `clock() < text_next` -> false. Else `text_next = clock() + 10.0` and
   `_start_actor("textfill", -2, _text_fill(fn))`. The lease is an ordinary actor window with class IDLE, so real work preempts it.
6. `_text_fill(fn)`: `d.TextFillWaitS = 0.0; lease.generating = true; n = await fn(d, lease.slot);
   text_next = max(text_next, clock() + d.TextFillWaitS); if n > 0: log.append({"kind":"textfill","count":n,"game":bridge.env.game})`.
   `LlmError` -> `_fail("textfill", -2)`; any other exception -> log + `_fail("textfill", -2)`.
7. The art job stands down once the text queue answers: `anything_due()` adds `&& !d.TextQueueLive` to the art clause
   (`art.enabled(d) and not d.text_queue_live and _ok("art", -1) and d.art.pending(...)`), and `_start_art()` returns false when
   `d.TextQueueLive` (`not art.enabled(d) or d.text_queue_live or not _ok("art", -1)`). A 404 sets `TextQueueLive = false` again.
8. Other idle.py deltas in the same commit range: `_thought`: after the too-long check, `if text != null and adapter.DropsMeta(text,
   [m.text for m in mems])` -> `text = null; s = Sanitized(null, "meta_talk")`, and the drop log entry is `thought:meta_talk`.
   director.py vanilla line (around the `speak` of a vanilla social): after sanitising, `if s.ok and adapter.DropsMeta(s.text,
   [vanilla["kind"], vanilla["sentence"]])` -> `s = Sanitized(None, "meta_talk")`, so the result is `Dropped("meta_talk")` and nothing is
   said; the director also imports `Sanitized`.

## steward_try.py (for the later steward_try port)
After the CHAT lines it now prints `REJECTED: <f>` for each entry of `tr.get("failures")` (the loop's rejected calls, e.g. "set_work with
these arguments was already done"), before `BRIDGE ACTS:`. Needs the steward trace to carry `failures` (core/steward.py, the jobs worker).

## core/steward.py (jobs worker)
The "→" automatic notes after acts are removed there (the game prints the greentext line itself); tests changed accordingly
(`lines(w, "note") == []`). Not touched here.

## Tests
C# added: 29 (1961 -> 1990, theory rows counted): TextFillTests 13 (batch, schema, messages, clean x4, fill_once x5, bridge routes),
VanillaSocialAdapterTests 12, StewardToolsTests +3 (why passes, why on every acting tool, why capped; two existing tests edited for
`why`), ToolLoopTests +1 (commentary param). ConfigTests default JSON gains `text_fill`.

Python tests ported:
- test_textfill.py: test_batch_first_plus_same_kind_up_to_three, test_schema_ids_bounds_and_title,
  test_messages_carry_context_example_and_minor_line, test_clean_* (4), test_fill_once_* (4 of 4; the stale-queue one through
  `FakeBridge.OnTextQueue`).
- test_toolloop.py: test_a_commentary_param_does_not_make_a_repeat_a_new_call.
- test_vanilla_social.py (adapter parts): test_prompt_fits_each_kind, test_unknown_interaction_uses_its_label,
  test_chitchat_request_describes_the_situation_not_the_name, test_the_no_meta_rule_is_in_the_speech_thought_diary_and_reaction_prompts,
  test_chitchat_memory_text_does_not_name_the_interaction, test_drops_meta_only_with_a_chitchat_scene.
- test_steward.py (tool/body parts): test_schedule_attack_hold_and_time_bodies (why, no reason), the refusal test (why),
  test_why_passes_from_tool_to_act_body_and_missing_why_sends_none (without the narrator-lines assertion),
  test_why_is_in_every_acting_tool_and_optional, test_why_is_cleaned_and_capped.

Deferred (need the Director/world fixture or core/steward.py):
- test_textfill.py: test_art_idle_job_skipped_when_text_queue_live, test_idle_runs_text_fill_last_when_idle,
  test_idle_text_fill_yields_to_other_jobs_and_to_real_work, test_idle_text_fill_honours_both_flags,
  test_idle_text_fill_404_backs_off_five_minutes (idle runner, director clock, leases, art queue).
- test_vanilla_social.py: test_a_meta_thought_is_dropped_and_counted, test_a_meta_vanilla_line_is_dropped_for_chitchat,
  test_the_same_words_are_kept_for_an_insult (world: director step/drain), test_insult_line_prompt..., tier and dropped-line tests
  (already deferred earlier).
- test_steward.py: the "no notes after acts" assertions in test_advisor_mode..., test_playing_runs_many_actions...,
  test_why_passes... (core/steward.py cycle), test_only_plain_player_lines_are_answered (steward cycle, chat feed).

## Behaviour differences / notes
- `TextTried` holds `IdKey(id)` strings (the id's JSON text) instead of raw ids so numeric or odd ids compare like Python values;
  string ids behave identically. By-id lookup of the model's reply uses the same key (a non-string id in the reply never matches a
  string id, as in Python).
- `LlmLog.Warning/Debug("rimworldllm", ...)` replace `log.warning/log.debug`.
- `ValText` for a non-container uses `Js.Raw` so numbers go through `GameData.Clean` like `gd.clean(v, ...)` (numbers print as Python str()).
- Python `int(it.get(...))` raising on odd values (None, junk) maps to `Js.Int` raising PyTypeError/PyValueError; the idle job treats it
  as any other exception (log + backoff).
