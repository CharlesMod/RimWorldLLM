# Parity: bridge (core/bridge.py, games/rimworld/fakes.py)

C#: `Mod/Source/Mind/BridgeClient/` (BridgeTypes.cs, BridgeClient.cs, BridgeTransports.cs, InProcessTransport.cs),
`Mod.Tests/Mind/Fakes/FakeBridge.cs`. Seam decision: the bridge contract stays the seam; the mind calls the same
`ApiRouter` handlers through the game-thread queue (`BridgeServer.SubmitAsync`, served by `Pump`).

## Ported tests (Python -> C#, all in Mod.Tests/Mind/BridgeClientTests.cs)
| Python | C# |
|---|---|
| test_bridge: test_envelope_and_auth | EnvelopeAndAuth |
| test_write_op_id_expiry_duplicate | WriteOpIdExpiryDuplicate |
| test_poller_paging_and_gap | PollerPagingAndGap |
| test_epoch_change_flagged_and_mind_rev_conflict | EpochChangeFlaggedAndMindRevConflict |
| test_unreachable | Unreachable |
| test_from_config_reads_bridge_json | FromConfigReadsBridgeJson (BridgeLocator.Resolve; no Config object) |
| test_split: test_say_refusal_reasons_keep_bubble_and_record_nothing | SayRefusalReasonsKeepBubbleAndRecordNothing (used the `world` fixture but only its bridge/fb; ported without the Director) |
| test_split: test_adapter_and_transports_satisfy_the_protocols | AdapterAndTransportsSatisfyTheProtocols (bridge half: reflection over IBridge/BridgeClient/IEventSource/EventPoller) |

## Deferred
- (ported by the contract part, see parity-contract.md: test_core_imports_nothing_from_games and test_core_has_no_game_terms as C# lints over Core/Llm/ToolLoop/BridgeClient, test_core_event_normalised_from_wire, the adapter half of test_adapter_and_transports_satisfy_the_protocols. `ProtocolNames.RimWorld` moved to `RimWorldAdapter.WireNames` for the lint.)
- test_split: test_gossip_names_a_third_actor_and_downgrades_without_one and test_every_write_carries_an_op_id are ported in `ContractWorldTests` (Director + FakeLlm); `EveryWriteCarriesAnOpId` in the bridge tests stays as the bridge-level check.

## Added tests
BridgeClientTests: wire shapes (field order of say/thought/social/chronicle/recap/art/chat/goals/act/mind bodies, query strings,
auth header only when token non-empty and auth, default vs RimWorld ProtocolNames), error mapping (200 + ok:false, http_NNN,
bad_reply, unreachable detail), envelope absorption (errors too, only with the time field, sticky epoch), bridge.json locator
(wildcard bind rewrite, token precedence, default path). InProcessTransportTests (real BridgeServer + ApiRouter): pump-served
success + envelope, nothing runs without Pump, continuations off the pump thread, dedup 409, expired 410, no_game 503, closed
door, stop drains 503, stalled busy, 504 timeout + abandon, cancellation abandon, 413, health without pump, trusted (no token),
same JSON as HTTP, HttpBridgeTransport against the HTTP listener, BridgeRuntime serving in-process with HTTP off and across
HTTP start/stop. FakeBridgeTests: self-tests of every FakeBridge route.

## Behaviour differences from the Python (and why)
- `BridgeClient` takes an `IBridgeTransport` instead of an `httpx.AsyncClient` + base url: the url belongs to the transport (`HttpBridgeTransport`). The client computes the `Authorization` header (so the Python's auth rule is unchanged) and hands it to the transport on `BridgeRequest.Authorization`.
- A reply whose JSON is not an object (e.g. `[1]`) is `bad_reply`; the Python crashed with AttributeError on `body.get`.
- `on_epoch_change` (declared but never called in the Python) is not ported. `Bridge.epoch_changed` is `IBridge.EpochChanged` (get/set; the director clears it).
- `aclose` is `CloseAsync` (disposes the transport).
- Transport failures: `BridgeTransportException` and `HttpRequestException` map to `unreachable` (detail = message, or the type name when empty). An HTTP client timeout maps to `unreachable` as httpx.TimeoutException (an HTTPError) did.
- JSON is parsed with date parsing off (Python keeps date-like strings as str); numbers read as long/double (Python int/float). `int(x)` of a float truncates, as the Python.
- `put_goals(rev, **fields)` is `PutGoalsAsync(int rev, JObject fields, string opId)`; `act` bodies are copied (`DeepClone`) before op_id/expiry are added, as `dict(body)` was shallow.
- `from_config`'s Config coupling is split: `BridgeLocator.Resolve(bridgeUrl, bridgeToken, bridgeJsonPath, defaultBridgeJsonPath)` returns (url, token); `Connect(...)` builds an HTTP BridgeClient. The llm worker's config owns the arguments.
- FakeBridge: Python's `isinstance(x, int)` (which accepts bool) is implemented as "JSON integer, not bool" in the shape checks (e.g. schedule hours, work priorities); a bool there is rejected in C#, accepted in Python. Test-only fake.

## In-process door (new, not in the Python)
- `BridgeServer.EnableInProcess()` / `SubmitAsync(method, path, query, body, ct)`: enqueues an `InboundRequest` carrying a `TaskCompletionSource` (continuations run asynchronously, never on the game thread) into the same `_pending` queue; `Pump` serves it through `ApiRouter.Handle` and `Envelope.Json`, so op_id dedup, expiry, `needs_game` and every route's main-thread rule are unchanged. Same refusals as `HandleContext`: 413 too_large (body over 64 KiB), GET /health answered from the published state without a pump, 503 busy when the game thread has not pumped for `StalledAfter`, 504 timeout (abandons the queued request) after `GameThreadTimeout`, 503 no_game when the door is closed or the server stopped. In-process requests are trusted (no token) and are logged in `Requests` as authenticated (so the settings "agent connected" reading counts the mind). The in-flight gate (16) is HTTP only.
- `Stop()` closes both HTTP and the door; `Start()` closes only a previous listener (the door keeps its state). The HTTP code path is untouched.
- `BridgeRuntime.EnableInProcess()` makes a listener-less `BridgeServer` when none exists; `Start`/`Stop` of the HTTP side keep the door open and swap servers atomically; `FrameUpdate` already pumped whenever `Server != null`, so pumping runs with HTTP off. No change was needed in `Mod/Source/RimWorld/*.cs`; MindHost (wave 4) calls `RWLBridge.Runtime.EnableInProcess()` once, then builds `new InProcessTransport(() => RWLBridge.Runtime.Server)`. Note `RWLBridge.Start()` registers routes (`Routes.Register(Router)`) and runs at startup, so routes exist before the mind's first call; and RWLBridge.Start returns early (no HTTP) when the mod is disabled in settings, in-process unaffected.
