# Parity: build (vivarium phase 2: the steward's builder tools)

Python from main's 70b1c53 (merged in b36f883): `games/rimworld/steward_build.py` and the changes to `steward_digest.py`, `steward_tools.py`,
`steward_system.txt`, `strategy/rules.json`, `strategy/steward_strategy.md`, `fakes.py`, `tests/data/steward_digest_sample.json`.

## Files
| Python | C# |
|---|---|
| games/rimworld/steward_build.py (`BuildKit`) | Mod/Source/Mind/RimWorld/StewardBuild.cs (`BuildKit`) |
| steward_tools.py `Session.build`, tools/acting wiring, `_verb_body` fallthrough, ADVISOR_NOTE | StewardTools.cs (`StewardSession.Build`, `StewardKit.AdvisorNote`); RimWorldStewardKit needs no change (the session is wrapped as before) |
| steward_digest.py `_BUILD`, `_STEPS`, `_build_lines`, `_room_line`, `_num`, `_words` | StewardDigest.cs (`BuildLevels`, `Steps`, `BuildLines`, `RoomLine`, `Num`, `Words`) |
| steward_system.txt, strategy/rules.json, strategy/steward_strategy.md | Mod/Source/Mind/RimWorld/Resources/ (byte-identical copies) |
| fakes.py (phase 2 verbs accepted, note `verb: k v, ...` minus why) | Mod.Tests/Mind/Fakes/FakeBridge.cs (two small localized edits) |
| tests/data/steward_digest_sample.json | Mod.Tests/Mind/Data/steward_digest_sample.json |

## Goldens regenerated (venv Python; scripts under ~/.local/share/rimworld-hermes-engine/csmind/)
- `Mod.Tests/Mind/Data/steward_golden.json` by `build/gen_steward_golden.py` (the side script plus 14 build-block digests appended at the end of the
  digest list; the digest renderings at five caps each now cover the BUILD lines and every shrink step).
- `Mod.Tests/Mind/Data/jobs-steward-golden.json` by `jobs/ts/capture_steward.py` (prompts now carry the new system prompt, strategy, BUILD digest lines).
- New `Mod.Tests/Mind/Data/steward_build_golden.json` by `build/gen_build_golden.py`: for six build blocks (typical, the sample's, empty, absent, malformed,
  many rooms/benches) the rendered tool list, GBNF grammar, tool names, acting set, and the check error / result / verb / note / logged body of 40 calls.
  The fake bridge's note carries a random op id; both sides normalise it to `op_id X`.

## Tests
`StewardBuildTests` (23 xunit tests): all 18 of test_steward_build.py test-for-test, the 4 build-block digest tests of test_steward_more.py
(`BuildBlockRendersCompactly`, `...MissingOrPartialKeysAreTolerated`, `...IsCleaned`, `...ShrinksFirstThenCapHolds`), and the golden test above.
Existing golden/steward tests are unchanged and pass against the regenerated data.

### Deferred
None from this change: every Python test added by 70b1c53 is ported (they need only the kit, FakeBridge and the digest renderer, no Director).

## Behaviour differences from the Python
- A digest value of the wrong type where Python would raise (a truthy non-dict `home`, a non-iterable `can_build`/`stuff`/`ores`/`rooms`/`benches`
  such as a number, a dict `rooms` list slice) is treated as empty/absent instead of raising. Strings and dicts are iterated as Python does
  (characters, keys) in the BuildKit word lists.
- `str.lower()` is `ToLowerInvariant` (as elsewhere).

## Public API
```csharp
public sealed class BuildKit { BuildKit(JObject digest); bool Has; List<ToolSpec> Tools; HashSet<string> Names;
  (string verb, JObject body) Body(string tool, JObject values); /* throws PyValueError with the Python message */ }
StewardSession.Build  // BuildKit; Tools = the original tools + Build.Tools; Acting includes the build tools
```
