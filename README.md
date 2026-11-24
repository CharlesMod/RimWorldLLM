# HermesVoice

Live LLM-driven pawn dialogue for [RimWorld](https://rimworld.com/) 1.7.

HermesVoice watches your colony - injuries, deaths, mental breaks, skill level-ups, medical tending, social interactions, raids - and turns those moments into rich, in-character speech bubbles spoken by the pawns themselves.

The mod dials out to a [Hermes](https://github.com/NousResearch/Hermes) agent over your local network. The agent roleplays your colonists using their full in-game context, and the lines appear as overhead dialogue boxes, recorded to a persistent conversation log.

## How it works

Two HTTP channels, both authenticated with a shared bearer token:

1. **Inbound bridge** - the mod runs an HTTP server (default port `8765`). The agent reads colony state and makes pawns speak.
2. **Outbound dial-out** - the mod POSTs colony events to a configurable agent URL so the agent can react to things happening in the world.

### Bridge API

| Method | Path      | Description                                        |
|--------|-----------|----------------------------------------------------|
| `GET`  | `/health` | Liveness check                                     |
| `GET`  | `/state`  | Colony state: tick, map count, all player pawns   |
| `POST` | `/say`    | Make a pawn speak: `{"pawn_id": 1234, "text": "..."}` |

All requests require `Authorization: Bearer <token>` (or `?token=` on GETs) when a token is set in mod settings.

## Setup

1. Install the [Harmony](https://steamcommunity.com/workshop/file/filedetails/?id=2009463077) mod (v3, Steam Workshop) and this mod.
2. Run the Hermes bridge on your agent machine.
3. In RimWorld settings, enable HermesVoice and set:
   - **Bridge port** / **bind address** - where the in-mod HTTP server listens
   - **Agent dial-out URL** - where the mod POSTs colony events
   - **Shared token** - used for auth in both directions

## Building

Requires [.NET](https://dotnet.microsoft.com/) (SDK 10+).

```sh
dotnet build Mod/HermesVoice.csproj
```

The build compiles against the pinned RimWorld 1.7 API (`Krafs.Rimworld.Ref`) and outputs `HermesVoice.dll` to `Mod/1.7/Assemblies/`. Set `RIMWORLD_DIR` to your RimWorld install directory to auto-deploy to the Mods folder on every build.

## Repository layout

```
Mod/
  About/About.xml            mod metadata
  HermesVoice.csproj         build + deploy project
  Source/                    C# source (6 files)
    HermesVoiceMod.cs        mod entry point, settings window, Harmony
    HermesBridge.cs          in-mod HTTP bridge (agent -> mod)
    HermesVoiceGameComponent.cs  game-thread request routing, /state, /say, dial-out
    SpeechBubbles.cs         overhead dialogue bubbles
    ConversationLog.cs       persistent conversation history
    HermesVoiceSettings.cs   mod settings
  1.7/Assemblies/            build output
```

## License

MIT
