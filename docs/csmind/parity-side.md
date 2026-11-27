# Parity: side modules (grounding, chronicle, creative, steward board, steward digest/rules/tools)

Branch `agent/csmind-side`. C# is Verse-free (`Mod/Source/Mind/`), tested through `Mod.Tests`.

## Files
| Python | C# |
|---|---|
| core/grounding.py | Mod/Source/Mind/Core/Grounding.cs |
| core/chronicle.py | Mod/Source/Mind/Core/Chronicle.cs |
| core/creative.py | Mod/Source/Mind/Core/Creative.cs |
| core/steward_board.py | Mod/Source/Mind/Core/StewardBoard.cs |
| games/rimworld/steward_digest.py | Mod/Source/Mind/RimWorld/StewardDigest.cs |
| games/rimworld/steward_rules.py | Mod/Source/Mind/RimWorld/StewardRules.cs, StewardExpr.cs (the condition evaluator) |
| games/rimworld/steward_tools.py | Mod/Source/Mind/RimWorld/StewardTools.cs (`StewardSession`, `Outcome`, `StewardKit`) |
| strategy/rules.json, steward_strategy.md, steward_system.txt | Mod/Source/Mind/RimWorld/Resources/ (byte-identical copies) |
| core/bridge.py post_chat stream_id / post_chat_status | IBridge, BridgeClient, FakeBridge (commit ddfef97) |

Embedding: the three resource files are `EmbeddedResource` with fixed LogicalNames (`RimWorldLLM.Mind.rules.json`, `RimWorldLLM.Mind.steward_strategy.md`, `RimWorldLLM.Mind.steward_system.txt`,
read through `StewardResources.Text`), declared in `Mod/RimWorldLLM.csproj`. `Mod.Tests/RimWorldLLM.Tests.csproj` also embeds the
test goldens (`steward_golden.json`, `steward_digest_sample.json`, `core_golden.json`); the tests csproj compiles `Mind/**` directly,
so it needs the same resource declarations, which is why it is touched.

## Tests
C# tests added: StewardRulesTests 245 (Theory rows counted), StewardToolsTests 25, CreativeTests 20, CoreSideTests 17, plus 2 bridge
tests = 309 (suite 1332 -> 1641, all green, mod build 0 warnings).

Goldens are generated from the Python by `~/.local/share/rimworld-hermes-engine/csmind/side/gen_steward_golden.py` and
`gen_core_golden.py` (venv python, `PYTHONPATH=.` from `agent/`) and checked in under `Mod.Tests/Mind/Data/`.

### Ported
- test_steward_rules: all but the three below (every rule test; plus goldens: ~235 conditions over fact sets, all shipped rules over
  random facts, derive_facts, alerts_section at several caps, render_digest over mutated digests).
- test_steward_more: test_sample_digest_is_small_and_the_cap_holds, test_digest_text_is_cleaned (StewardToolsTests).
- test_steward: the tool-body tests with FakeBridge (test_playing_runs_many_actions_with_resolved_ids, test_schedule_attack_hold_and_time_bodies,
  test_a_refusal_goes_back..., test_unknown_names_and_policies_come_back_as_errors) driven through `StewardSession.RunAsync`;
  test_event_wake_kinds and test_other_events_only_queue... as the `StewardKit.Wake` Theory (the queueing half needs the cycle: deferred).
- test_system_prompt_says_only_listed_tools, the strategy-text test.
- test_creative: every test but test_config_accepts_lists_for_per_kind_fields (load_config; covered by the llm part's ConfigTests).
- test_creative_ground: test_recap_ranking_puts_the_biggest_first (CoreSideTests). Config defaults are ConfigTests (llm part).
- test_diary_chronicle_pacing: test_chronicle_budget_trims_hard_when_the_budget_is_small, test_chronicle_survives_restart_and_follows_a_rolled_back_save.
- test_steward_chat_status: test_client_bodies is the bridge tests (ddfef97).
- Not in Python, added: board/grounding/chronicle goldens (plain, render_board, board_tools, Edits.apply and try_add, card_lines,
  tokens/mentions/pick_mentioning, importance, rank_entries, referents, est_tokens, notes, due, build_messages at five budgets), the
  append-only chronicle files, the tiny-budget stop.

### Deferred (need the Director, the world fixture, or core/steward.py; they port with those)
- test_steward.py: every `boot(world)`/cycle test (chat wakes, cursor, heartbeat, paused, floor, advisor mode, own note, loop on slot 0,
  silent final lines, minor/romance line, dead backend, unreadable digest, catch-up, switch-on wake) and the queueing half of
  test_other_events_only_queue_for_the_next_cycle.
- test_steward_more.py: board batching/rev conflict, idle yield, recap waits, the strategy-order tests (load_strategy lives in core/steward.py),
  letters/alerts not voiced, wants off, slot pin; test_steward_try (CLI).
- test_steward_rules: test_user_turn_has_alerts_after_why_you_woke, test_cycle_sends_alerts_to_the_model, the load_strategy half of
  test_shipped_strategy_loaded_capped_and_override_order.
- test_steward_chat_status: the cycle tests (thinking/idle/final, backend raises, no final, rejecting bridge).
- test_creative_ground: art queue/prompt, lore, ceremony, inner, aim, sampler routing, art v2 (art.py, lore.py, group.py, inner.py, Director).
- test_diary_chronicle_pacing: diary tests, chronicle-through-Director, idle tests, chronicle bridge on/off, narrator coverage, the
  chronicle client contract (bridge.chronicle; the bridge part).
- Not applicable: test_package_data_lists_strategy_files (pyproject; the files are embedded).

## Behaviour differences from the Python
- **Chronicle.BuildMessages**: Python loops forever when the budget is smaller than the fixed text and the summary is already at 200 code
  points; C# stops (breaks) when the summary no longer shrinks. Tested (ImpossiblyTinyBudgetStopsInsteadOfLooping).
- **Newlines**: chronicle.jsonl/.md are always written with `\n` (Python text mode writes `\r\n` on Windows).
- **Names**: pydantic `Note`/`Entry` are `ChronicleNote` (value equality, used where Python tests `n not in notes`) and `ChronicleEntry`.
- **Hosts are interfaces**: `IGroundingHost`, `ICreativeHost`, `IChronicleAdapter`, `IGroundingAdapter`, `IIdleYield` replace the
  duck-typed director/adapter; the `d.cfg` getattr-with-default reads become `CreativeSettings` (built by `CreativeSettings.From(MindConfig)`).
- **casefold / lower**: `str.casefold` is `ToLowerInvariant` plus sharp-s; `str.lower` is `ToLowerInvariant` (differs for a few special
  letters such as the dotted capital I and the Kelvin sign).
- **Rule conditions** (`StewardExpr`, replaces `ast`): hand-written parser, same accepted set; all syntax errors say `syntax: invalid syntax`;
  disallowed constructs are reported where the parser meets them; `\N{...}` escapes are refused (RuleError); division by zero or integer
  overflow gives "skip" (no rule fires) instead of raising; the most-common-schedule tie-break is first seen (Python's is set order, arbitrary).
- **Malformed digest items** (wrong types) are treated as absent where Python would raise.
- **`rules` parameter**: `StewardRules.FiredRules` and `AlertsSection` take an optional rules list where the Python tests monkeypatch `load_rules`.
- **StewardBoard.Plain** takes `object`; 0, false and "" give "" as Python's `text or ""`.
- **Tool values**: `StewardSession.RunAsync(tool, JObject values)` takes the values `ToolSpec.Check` produced; errors are `{"error": ...}`
  exactly as the Python strings.
- `core/steward.py` is not ported here; `StewardKit` is the whole adapter-side surface it calls.

## Public API for other workers
```csharp
// Core/Grounding.cs
interface IGroundingAdapter { bool HasCreativeCard; JObject CreativeCard(JObject snapshot); IReadOnlyList<KeyValuePair<string,int>> RecapImportance; }
interface IIdleYield { bool ActorsWantWindows(); }
interface IGroundingHost { IBridge Bridge; IGroundingAdapter Adapter; LeaseManager Leases; IIdleYield Idle; }
static class Grounding {
  Task<JObject> ActorCardAsync(IGroundingHost d, int aid); List<string> CardLines(JObject card);
  HashSet<string> TokensOf(IEnumerable<string> names); bool Mentions(string text, ISet<string> tokens);
  List<string> PickMentioning(IList<string> texts, ISet<string> tokens, int k, bool fallbackRecent = true);
  int Importance(IGroundingAdapter a, string text, string kind = ""); List<JObject> RankEntries(IGroundingAdapter a, IList<JObject> entries, int window = 12, int top = 4);
  List<string> Referents(IEnumerable<string> texts, IEnumerable<string> names, int k = 8);
  bool OtherReactiveWaiting(IGroundingHost d, int? me); bool IdleShouldYield(IGroundingHost d); }
// Core/Chronicle.cs
interface IChronicleAdapter { string ChronicleRequest, ChronicleSystem, ChronicleSummarySystem; }
class Chronicle { Chronicle(string dataDir = null, long unitsPerDay = 60000); ChronicleState State; List<ChronicleEntry> Log; string LastDrop;
  void Load(); void Save(); void Rollback(long game); void Note(long game, string text, double severity = 0.3, bool major = false);
  ChronicleDue Due(int today); List<LlmMessage> BuildMessages(IChronicleAdapter, string kind, int day, IList<ChronicleNote> notes, IList<string> ledgerLines, int budget, IList<string> loreLines = null);
  Task<ChronicleEntry> WriteAsync(ILlmClient llm, IChronicleAdapter ad, int slot, int today, IList<string> ledgerLines, IDictionary<int,HashSet<string>> nameTokens, int budget = 6000, int maxTokens = 220, ISet<string> commonWords = null, ISet<string> minorNames = null, IList<string> loreLines = null);
  Task RollAsync(ILlmClient, IChronicleAdapter, int slot); static int EstTokens(string); static int MessagesTokens(IEnumerable<LlmMessage>); }
// Core/Creative.cs
interface ICreativeHost { ILlmClient Llm; CreativeSettings Cfg; List<PieceStats> CreativeLog; }
static class Creative { List<string> FindCliches(string); double AutoScore(string text, string facts, IEnumerable<string> names, string kind = ""); string CleanPlan(string);
  bool On(ICreativeHost d, string feature, string kind); int BestOf(ICreativeHost d, string kind);
  Task<(string Text, PieceStats Stats)> ComposeAsync(ICreativeHost d, string kind, int? slot, int? actor, string facts, ISet<string> names, Func<string,Task<string>> write, Func<Task<string>> plan = null, Func<bool> shouldYield = null); }
// Core/StewardBoard.cs
class Edits { Edits(); Edits(IEnumerable<EditOp>); bool Any; JObject Apply(JObject board); string TryAdd(JObject board, EditOp op); }
static class StewardBoard { string Plain(object text, int cap = 300); List<ToolSpec> BoardTools(JObject board, bool advisor = false); string RenderBoard(JObject board, int logLines = 6); }
// RimWorld/
static class StewardDigest { Dictionary<string,int> PawnNames(JObject digest); string Runs(string sched); string RenderDigest(JObject digest, int cap = 6000); }
static class StewardRules { List<PlaybookRule> LoadRules(); void ResetCache(); Dictionary<string,object> DeriveFacts(JObject digest);
  List<(long Priority,string Id,string Action)> FiredRules(IDictionary<string,object> facts, IEnumerable<PlaybookRule> rules = null);
  string AlertsSection(JObject digest, int cap = 1500, IEnumerable<PlaybookRule> rules = null); }
class StewardKit { HashSet<string> SilentEvents; string Wake(JObject evt); string EventLine(JObject evt); string SystemPrompt(bool advisor);
  string DefaultStrategy(); string PlaybookAlerts(JObject digest); string RenderDigest(JObject digest); StewardSession Session(JObject digest, IBridge bridge, bool advisor); }
class StewardSession { Dictionary<string,int> Pawns; List<ToolSpec> Tools; HashSet<string> Acting; Task<Outcome> RunAsync(string tool, JObject values); }
class Outcome { JObject Result; string Verb, Note; bool Ok; }
```
