# Developing RimWorldLLM

Technical notes moved here from the README when it was rewritten for players (2.1.0 beta).
For the engine see [ENGINE.md](ENGINE.md) (RimWorld side) and the
[easyEngine docs](https://github.com/CharlesMod/easyEngine/blob/main/docs/ENGINE.md); for the wire protocol
[BRIDGE-API.md](BRIDGE-API.md); for other mods [HOOK-API.md](HOOK-API.md); for the design [SPEC.md](SPEC.md).
To build a release zip, run `scripts/package-release.sh` (see its `--help`).

## How it works

Two HTTP channels:

1. **Inbound bridge** - the mod runs an HTTP server (default port `9765`, falling back through `9774`). The agent reads colony state, events and pawn minds, and makes pawns speak. Every request needs a bearer token; the mod writes the port and token to `<RimWorld config dir>/RimWorldLLM/bridge.json` for the agent to read.
2. **Outbound dial-out** - the mod POSTs colony events to a configurable agent URL so the agent can react to things happening in the world. It sends the same event objects as `GET /events`.

### Bridge API

The full contract is [docs/BRIDGE-API.md](BRIDGE-API.md): Part 1 is the game-agnostic protocol (connection, auth, envelope, op ids, events, `/say`, `/thought`, `/mind`, `/talk`), Part 2 the RimWorld schema (`/config`, `/colony`, `/pawn/{id}`, `/scene/{id}`, event types, intent effects). Other mods can add pawn context and talk requests through the [hook API](HOOK-API.md).

The bridge answers in the main menu too (`no_game` for game endpoints, `GET /health` always). The default bind address is `127.0.0.1`; a remote agent needs another bind address and a fixed shared token in settings.

### Dial-out events

Events are POSTed to the **Agent dial-out URL** as JSON, with `Authorization: Bearer <token>` when a token is set:

```json
{"type": "<event type>", "tick": 123456, "payload": { ... }}
```

Sending is asynchronous (one background sender, queue of 64, oldest dropped when full); failures are logged at most once per 30 s. 

## Colony events

The mod POSTs these to the agent URL (via the dial-out, as `{"type", "tick", "payload"}`). Only player-faction pawns on player-home maps count (deaths also cover colony animals); nothing is sent during world generation or save loading. Every kind has a toggle in mod settings, plus a master "Send colony events" toggle (all on by default). Events are throttled: per-(kind, pawn) cooldowns (injuries 30 s of game time, merged into one event; tending 20 s; mental breaks 10 s; social 60 s; level-ups and deaths none) and a global cap of 20 events per real minute (extras are dropped).

Every payload has `pawn` (`{id, name}`), `map` (`{index, home}`) and a human-readable `summary`, plus:

| `type`            | Extra payload fields |
|-------------------|----------------------|
| `injury`          | `count`, `total_severity`, `injuries[]` (`part`, `hediff`, `severity`, `damage_def`, `attacker`), `attackers[]` |
| `death`           | `animal`, `cause`, `culprit`, `killer` |
| `mental_break`    | `state`, `state_label`, `reason` |
| `skill_level_up`  | `skill`, `old_level`, `new_level`, `passion` |
| `tend`            | `pawn` is the patient; `doctor` (`{id, name}`), `quality` (0-1, null if unknown), `medicine` |
| `social`          | `pawn` is the initiator; `recipient` (`{id, name}`), `interaction`, `interaction_label` |
| `raid`            | no `pawn`; `faction`, `strategy`, `arrival`, `points`, `raider_count` |

## Building

Requires [.NET](https://dotnet.microsoft.com/) (SDK 10+).

```sh
dotnet build Mod/RimWorldLLM.csproj
```

The build compiles against the pinned RimWorld 1.7 API (`Krafs.Rimworld.Ref`) and outputs `RimWorldLLM.dll` to `Mod/1.7/Assemblies/`. `Newtonsoft.Json.dll` is copied next to it (RimWorld does not ship it); Harmony is not (it comes from the Harmony mod).

Set `RIMWORLD_DIR` (or pass `-p:RimWorldDir=...`) to your RimWorld install directory to deploy to `<dir>/Mods/RimWorldLLM` on every build (macOS from 1.7 uses the same `<dir>/Mods`; older macOS builds used `<dir>/RimWorldMac.app/Mods`). `About/`, the `1.7/` assemblies, `Defs/`, `Languages/` and `Textures/` are copied; `.pdb` files are skipped.

## Repository layout

```
Mod/
  About/About.xml            mod metadata
  Defs/                      thought and interaction defs used by the effects
  RimWorldLLM.csproj         build + deploy project
  Source/
    Bridge/                  game-agnostic bridge: HTTP server, auth, envelope, op ids, event ring, minds, talk queue (no game references)
    RimWorld/                RimWorld side: snapshots, events, Harmony hooks, say/effects, settings, hook API
  Languages/English/Keyed/   all player-facing text
  Textures/RimWorldLLM/      speech bubble art (Textures/src/ holds its generator; not shipped)
  1.7/Assemblies/            build output
Mod.Tests/                   xUnit tests for Bridge/ and the pure rules (not shipped); run `dotnet test Mod.Tests`
agent/                       the old Python agent (no longer shipped; the mind runs in the mod, Mod/Source/Mind); see agent/README.md
bench/                       model benches and soak tools (not shipped)
scripts/package-release.sh   builds and checks the release zip
docs/                        BRIDGE-API, HOOK-API, ENGINE, SPEC, SPEED, MODEL-BENCH, E4B-PASS, CREATIVE-ROUND, release/
```

With `EasyEngineDir` (default a sibling `../easyEngine` checkout) built, the deploy also copies the engine binaries to
`Engine/<os>-<arch>/`. Note the deploy copies
all of `Textures/`, including `Textures/src/`; the release script ships only `Textures/RimWorldLLM/`.

## Running an external agent over the HTTP bridge (optional)

The built-in mind uses the in-process door and needs none of this; the HTTP bridge stays opt-in for tools and other agents.

1. Install the [Harmony](https://steamcommunity.com/workshop/file/filedetails/?id=2009463077) mod (v3, Steam Workshop) and this mod.
2. Run your agent on its machine (the old one is in `agent/README.md`), or choose "My own endpoint" for just the LLM.
3. In RimWorld settings, enable RimWorldLLM and set:
   - **Bridge port** / **bind address** - where the in-mod HTTP server listens
   - **Agent dial-out URL** - where the mod POSTs colony events
   - **Shared token** - used for auth in both directions

## Settings keys and old saves

`RimWorldLLMSettings.ExposeData` reads and writes each setting by its field name. The setting `language` was removed (the mod saved and sent it but nothing read it): old settings files that still hold a `<language>` node load fine, because Scribe only looks up the keys the code asks for and ignores the rest. The same holds for any later removal. New in the settings rework: `customGgufPath` (the last path typed or picked under "Use my own GGUF file"; the engine holds the real choice). `GET /config` no longer carries `language`.
