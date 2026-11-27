# Parity: delta4 (Python agent changes f9474bd..HEAD ported into the C# Mind)

Python source: `git log f9474bd..HEAD --no-merges -- agent` (9b9ee0c, 5aa76ac, 804db8c, efe3e3b, f95d48a, 9f5d028, 4fb37b5, c884c482). Branch `agent/csmind-delta4`.
Three items: Biotech (steward_bio), trade2 (quests declined, walking trade, trade turn), regenerated goldens.

## File map
| Python | C# |
|---|---|
| `games/rimworld/steward_bio.py` (BioKit) | `Mod/Source/Mind/RimWorld/StewardBio.cs` (`BioKit`: Has, Adults, Mechs, Children, Babies, Prisoners, Germs, Kinds, Packs, Carers, Tools, Names, `Body(tool, v)`) |
| `steward_digest._bio_lines`, four bio levels, 4-tuple shrink steps | `StewardDigest.cs` (`BioLevel`, `BioLevels`, `BioLines`, `Steps` 13 four-int tuples, `Render(d, lv, bl, tl, bio, combat)`) |
| trade view for the trade turn (`render_trade`) | `StewardDigest.TradeView(JObject d, IList<string> arrived)` |
| `steward_rules` bio facts + `quest_offer_cannot`, rules.json | `StewardRules.cs`, `Resources/rules.json` (byte copy) |
| `steward_biotech.md`, `steward_trade.txt`, strategy, system prompt | `Mod/Source/Mind/RimWorld/Resources/` (byte copies; csprojs embed them; `StewardResources.BiotechName/TradeName`) |
| `kit.strategy_extra(digest)` | `IStewardKit.StrategyExtra(JObject)` (mirrors `getattr`), appended after the strategy in `Steward` |
| trade kit hooks (`trade_tools`, `trade_session`, `trade_system_prompt`, `trade_skip`, `render_trade`) | `IStewardTradeKit` (optional interface, as `IStewardCombatKit`); `StewardKit` implements it |
| `Steward._run_trade`, `trade_seen`, `trade_new`, `TRADE_*` | `Core/Steward.cs` (`RunTradeAsync` internal, `TradeSeen`, `TradeNew`, `TradeSteps=4, TradeMaxTokens=300, TradeFinalMax=120, TradeBudgetS=60, TradeStepTimeoutS=30`) |
| `steward_try --trade-turn` | `tools/MindHarness/StewardTry.cs` (`tradeTurn` parameter), `Program.cs` (`--trade-turn`) |
| fake bridge Biotech verbs, walking trade, traders in /vivarium | `Mod.Tests/Mind/Fakes/FakeBridge.cs` |

Children (age < 13 by the digest's `pawns[].age`) are refused in `draft` (named) and `order`; `draft all` leaves them out. Message `children stay out of fights`.
The trade turn runs once per new trader id (sorted ordinally), after chat, dialogs and urgent events, before the heartbeat; never in a fight, while paused or in advisor
mode; skipped (trader still marked seen) when food is under 3 days or on a critical starvation alert. It does not move `LastGame`.

## Decisions
- Caravans: the Python itself has them off now, so the old `nocaravan` capture patch is gone. All goldens come from the unpatched Python. The C# has no caravan tool; the fake
  bridge answers `/act/caravan` with 409 "caravans are off" (test `TheNarratorCaravanRouteIsRefused`).
- Goldens regenerated: steward_golden.json (adds 5 bio samples, about 17 edge/malformed bio digests, two trade digests and a `trade_view` array), jobs-steward-golden.json,
  e2e-steward-{grammar,advisor,json,native}.json and new e2e-steward-{trade,trade_skip,bio}.json. steward_build_golden.json, e2e-chitchat/mixed/multiday are unchanged.
- Bio e2e uses only rejected or refused calls: an acting bio verb's note carries a random `op_id` in the Python, so it cannot be golden.
- No new config keys (the Python diff has none); docs/SETTINGS.md is unchanged.

## Python quirks kept
- "the babys are: Dee" (naive plural in the unknown-name error).
- `str.lower` semantics for genepack matching; pawn-id keys normalise 5, 5.0 and true alike (a string stays distinct) as Python dict keys do.
- Capture-script stream ids contain the PID; the existing e2e test already tolerates them.

## Tests
- New: `StewardBioTests` (23, all 16 Python tests of test_steward_bio.py, parametrised ones expanded), `StewardTradeTurnTests` (11, all of test_steward_tradeturn.py).
- `StewardTradeTests`: caravan tests replaced by `NoDigestOffersACaravanTool`, `ACaravanAtASettlementIsNoTradePartner`, `TheNarratorCaravanRouteIsRefused`; added the walking-trade,
  fake-digest, quest-lines, pending-trade, steps-order, quest-decline rule and amended prompt-text tests; `ToolSectionBudget` ported to the new assertion.
- Python tests deferred: none. Python tests dropped with the feature (caravan_form, caravan_send_and_cancel, caravan_errors, caravan_plan_look, plan_problems_render,
  quest cannot be worked refusal, unknown animal names) were already absent from the C# in delta3 and are removed from the Python too.
