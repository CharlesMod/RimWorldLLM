# Parity: tools (MindHarness, golden parity, speedsim, wants wiring)

Branch `agent/csmind-tools`. Nothing in `agent/` was edited.

## Harness: `tools/MindHarness/MindHarness.csproj` (net10.0 exe, ships nowhere)

Links `Mod/Source/Mind/**` plus the `Mod.Tests` fakes (`Recorders.cs`, FakeBridge, FakeLlm).

    dotnet run --project tools/MindHarness -- replay <recording.jsonl> [--llm URL --mode openai|native --model M --key K] --data-dir D
    dotnet run --project tools/MindHarness -- speedsim [--speed N]
    dotnet run --project tools/MindHarness -- steward-try <args as steward_try.py> --data-dir D
    dotnet run --project tools/MindHarness -- comment-try ...        (hook only, see below)

* `replay` uses FakeLlm unless `--llm` is given. With `--llm` it uses a real HttpLlm, prints decisions and lines like the Python and logs every raw request body and reply to `<data-dir>/llm-log.jsonl`.
* `--data-dir` is required for every command that writes; it never defaults to the real agent data dir.
* `speedsim` prints the same table and detail lines as `speedsim.py`. `SPEEDSIM_TRACE=1` adds per-step timing; `SPEEDSIM_SETTLE_MS` (default 0.5) sets the clock-jump settle window.
* `comment-try`: delta2's commentary function does not exist on this branch, so only the dispatcher entry exists and it exits with a "not wired" message. Wire it after the merge.

## End-to-end golden parity

Capture scripts: `~/.local/share/rimworld-hermes-engine/csmind/tools/` (`capture_golden.py`, `capture_steward.py`, `make_mixed.py`). They monkeypatch the Python FakeBridge to record every LLM request (messages, grammar, json_schema, slot, kind, max_tokens, stop) and every bridge write minus op_ids, in order. Goldens are embedded in `Mod.Tests/Mind/Data/e2e-*.json`.

| scenario | LLM calls | bridge writes | match |
|---|---|---|---|
| multiday.jsonl | 101 | 120 | 100%, byte for byte |
| mixed (agent/tests/data + vivarium digest sample) | 109 | 174 | 100%, byte for byte |
| chitchat | - | - | Skipped, pending delta2 wording |
| steward grammar/advisor/json/native | - | - | Skipped; 4/4 match when delta2's main-branch resources (steward_system.txt line 15 OPEN DIALOGS, steward_strategy.md line 75 loose materials) are synced in. Un-skip after the merge. |

The steward stream id embeds the pid; it is normalised as `steward-PID-`. The HttpLlm HTTP-body golden is deferred.

## Speedsim

`SpeedSim.cs` ports `speedsim.py` and `SpeedSimTests.cs` ports `test_speed_sim.py` test for test (17). The asyncio virtual-time loop is replaced by a single-thread `TaskScheduler` (`VirtualTime`) with a jumping clock. Output at x1..x4 equals the Python table on lines, shed, conv, idle counts, and within a few percent on delay and util (e.g. x3 dly50 207 vs 207, dly90 1989 vs 1989; util 0.12 vs 0.10).

Intended differences and known limits:
* ConfigureAwait(false) continuations escape to the thread pool under a custom scheduler, so the sim is not bit-deterministic; the clock jumps only after a pool quiescence check plus a 0.5 ms settle window. Without the settle window a running pool item (JIT, parse) was invisible and the clock jumped 3-6 s of virtual time over a poll (this was the 3x polling gap found earlier).
* Test bounds on delays carry slack for this.

## Director seams (minimal edits to Director*/Idle)

* `Director` ctor takes optional `TaskScheduler underlying = null, Func<double, CancellationToken, Task> delay = null` (virtual time); defaults give production behaviour.
* `Director.Probes` (Say, Finish, Ingested hooks) replace Python's monkeypatched spies; null in production.
* `Idle.Preempt`: `var lease = Lease;` local copy, fixing a NullReferenceException race (Lease cleared by another thread between check and use). Also a latent production race.

## Wants wiring

`test_wants_wiring` ported through `MindRunner.MakeToolBackend` and the runner in `MindRunnerTests.cs`.

## Not ported

Steward `dialogs` / `paused` args of steward_try (delta2). comment-try (see above).
