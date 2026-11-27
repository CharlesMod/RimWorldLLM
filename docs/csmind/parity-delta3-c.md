# delta3 sub-worker C: trade, growth moments, settings mirror

Python commits: 9ceabbb, f8a0741, b58ab78 (and the settings mirror). KEEPER DECISION: no caravans at all.

## File map
| Python | C# |
|---|---|
| `games/rimworld/steward_trade.py` (`TradeKit`, `LOOKS`, `parse_lines`, `render_offer`) | `Mod/Source/Mind/RimWorld/StewardTrade.cs` (`TradeKit`, `TradeKit.Looks/Acts`, `TradeKit.ParseLines`, `TradeKit.RenderOffer`) |
| `steward_tools.py` trade wiring (`self.trade`, tools, `acting`, look branch of `run`, `_verb_body` dispatch) | `StewardTools.cs` `StewardSession.Trade`, `Tools`/`Acting`, `RunAsync` look branch, `VerbBody` default case |
| `steward_tools.py` `choose` growth branch + `passions`/`trait` params | `StewardTools.cs` `Choose` / `BuildTools` |
| `ADVISOR_NOTE` wording ("you have no tool for") | `StewardKit.AdvisorNote` |
| `core/bridge.py look(path, params)` | `IBridge.LookAsync(path, query)` + `BridgeClient.LookAsync` |
| `fakes.py` `/trade/offer`, `looks`, `offers`, trade act shape/note, choose passions/trait | `Mod.Tests/Mind/Fakes/FakeBridge.cs` (`Looks`, `Offers`, `TradeOffer`, `trade` verb) |
| `tests/data/dialogs_growth.json`, `steward_digest_{trader,orbital,settlement}.json` | `Mod.Tests/Mind/Data/` (embedded, csproj) |

## Settings mirror
The mod side (`VivariumState.ToJson`, `RimWorldLLMSettings`, docs/SETTINGS.md rows for `narratorTrade` ... `narratorNeverSell`,
`narratorCaravans`) is already in this branch. The Python agent never reads the trade settings (the mod enforces the caps), and the
C# Mind reads `Vv["settings"]` by name (`heartbeat_hours`, `slow_for_combat`, `answer_dialogs`...), so nothing was needed on the Mind side
and no SETTINGS.md row changed.

## Differences from the Python
- No caravans: `caravan_plan`, `caravan`, `render_plan`, quest/destination/animal/pawn-name logic, `OPS`/`PURPOSES`/`MAX_PAWNS` are not ported.
  `LOOKS = (trade_offer)`, `ACTS = (trade)`. A digest `caravans[].can_trade` still makes that settlement a trade partner (the trade tool's logic).
- `_names`/`_where`/`_goods`/`_quest_check` (caravan helpers) not ported.
- `look` takes the query as an ordered list of pairs (`IList<KeyValuePair<string,string>>`) instead of a dict.
- Tests call `RunAsync` instead of `_verb_body` (not visible to the test assembly), so growth bodies are checked in the bridge's ActLog.

## Tests
Ported (`Mod.Tests/Mind/StewardTradeTests.cs`, 35 xunit cases): no_trade_block_no_trade_tools, trader_digest_has_trade_tools_and_acting_split,
orbital_trader_and_settlement_digest (trade part), a_caravan_on_a_settlement_makes_it_a_trade_partner (partner part), trade_posts_parsed_lines_and_why,
trade_one_side_and_none_words, parse_lines_forms_and_errors (+ Python-captured theories), trade_errors, trade_with_a_trader_that_cannot_trade_is_explained,
refusal_passes_through (trade), act_extra_fields_reach_the_model, look_tools_render_and_are_not_acting, look_game_error_is_a_tool_error,
advisor_one_task_counts_trade_and_caravan (trade), advisor_note_no_longer_names_trade, tool_section_budget (trade+build), signatures_and_grammar (trade),
cycle_looks_then_trades_and_advisor_allows_one_act, test_growth_render_and_tool (tool part) from test_steward_dialogs.py; extra: RenderOffer forms/cap capture.

Dropped by keeper decision (caravans): test_offers_are_not_quest_targets, test_caravan_form_maps_names_and_quest, test_caravan_send_and_cancel,
test_caravan_errors, test_quest_that_cannot_be_worked_is_refused_with_its_reason, test_unknown_animal_names_only_with_an_animals_list,
test_caravan_plan_look, test_plan_problems_render, test_settlement_caravan_and_quest_lines, and the caravan halves of the mixed tests above.

Deferred to the merge (belong to B: digest renderer, rules, resources): test_trader_digest_lines, test_cant_trade_trader_is_marked,
test_trade_blocks_missing_or_partial_keys_are_tolerated, test_trade_strings_are_cleaned, test_big_digest_fits_the_cap_and_trade_shrinks_before_the_base,
test_steps_order_shrinks_build_then_trade_then_base, test_every_sample_digest_fits_the_cap, test_quest_offer_fact_and_rule, test_trader_facts_fire_the_trade_rule,
test_prompt_text_is_amended_not_appended; the render half of test_growth_render_and_tool; test_growth_prompt_line (steward_system.txt, B's resource).
The embedded `steward_digest_{trader,orbital,settlement}.json` are byte copies of the Python data (B embeds the same files: keep one).

## Public API
- `TradeKit(JObject digest)`: `Has`, `Partners`, `Tools`, `Names`, `Acting`, `Body(tool, v) -> (verb, body)`, `LookAsync(tool, v, IBridge)`.
- `TradeKit.ParseLines(string, what) -> List<JObject>`, `TradeKit.RenderOffer(JObject) -> string`.
- `IBridge.LookAsync(string path, IList<KeyValuePair<string,string>> query = null) -> Task<JObject>`.
- `StewardSession.Trade`.

## Goldens that now fail until the lead regenerates them
The new `ADVISOR_NOTE` wording changes every advisor-mode system prompt, so these five fail by golden (they carry the old note; the lead regenerates them from the Python after the merge):
`StewardGoldenTests.PromptsAreByteIdenticalToPython` for catch_up, chat_advisor, many_chat, chat_markup (steward_golden.json), and `E2eGoldenTests.StewardTryMatchesThePythonCallForCall(advisor)` (e2e-steward-advisor.json). Everything else (3325 tests) passes.
