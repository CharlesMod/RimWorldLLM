# Parity: wiring the C# mind into the mod

Where the C# mind (MindRunner + Director) differs from the Python agent once it is hosted by the mod.

## Hosting
- Python: a separate process (wheel, uv, python) launched by the engine, reading bridge.json over HTTP.
- C#: `MindHost` (Mod/Source/RimWorld) owns a `MindSupervisor` (Mod/Source/Bridge). The mind runs on the thread pool, reaches the game
  only through `IBridge` over `InProcessTransport` (`RWLBridge.Runtime.EnableInProcess()`, served on the game thread by `FrameUpdate`),
  starts when a game is loaded and the mod is enabled, stops at `GenScene.GoToMainMenu` and on mod disable. `Tick` never throws and never
  blocks a frame. A failing start is retried after 15 s.
- Restart key: engine base URL, own endpoint (url/model/key), data dir, config file stamp, mirrored toggles. The local model name is not in
  the key (a model swap does not restart the mind).

## Engine profile
- The `agent` block (wheel, python, args, llm_url_env, env) is gone from `EngineLauncher.BuildProfile`; no wheel lookup; the spawn env
  no longer carries RIMWORLDLLM_BRIDGE_JSON. An old engine's "agent" status JSON is ignored.

## LLM
- Local: the engine's `/v1`; `llm.timeout_s` = 900 (the engine holds a request while a model loads; streamed: idle time).
- Own (`llmSource` = own): `llmBaseUrl/llmModel/llmApiKey` called directly, 120 s; the mod launches/attaches no engine.
- A file value for `llm.timeout_s` wins. HttpLlm request details are unchanged.
- Known limit: the steward's fixed step (60 s) and budget (120 s) timeouts still cut a steward step during a long model load.

## Settings
- Mod settings (endpoint, pause_ambient_at_speed, wants, dreams, art_enabled, recap_enabled, text_fill, data_dir) override the file.
- `<data_dir>/config.toml` (else `config.json`) is read by MindConfig; every MindConfig key is reachable; bridge_json/url/token are dropped.
- Python read many `RIMWORLDLLM_*` variables; C# reads only `RIMWORLDLLM_DATA_DIR` (and Steward.LoadStrategy still reads
  `RIMWORLDLLM_STEWARD_STRATEGY`, an extra variable in Core/Steward.cs).
- The mod no longer polls `GET /config` for the mind: the mind sees the settings through the config build (restart on change).

## Live chat
- Python: ChatLive over HTTP (/chat/begin ...). C#: `InProcessChatLive` over `RWLBridge.Runtime.Vivarium.Live` (no HTTP).
  Begin for a narrator also queues `Budget.NewCycle` on the game thread (what the /chat/status route did). `End(id, text)` is a
  no-op because the final line posted to /chat with the stream id already ends the live line; `End(id, null)`/`Cancel` drop it.
- Streaming is on for narrator answers in both modes; `return_progress` is sent only when the engine is detected; a non-stream fallback is kept.
- Added hook: `MindRunner.ChatLiveFactory` (Func<IBridge, IChatLive>), the only edit to Mind/Core.

## Status
- Settings Model/Advanced: "Narrator and colonists: running / waiting for the model / waiting for a game / off / error: <last error>"
  (an error shows for 120 s). Replaces the "agent connected" reading.

## Memory removal
- The mind is stopped (and awaited) before the data dir is deleted, then restarted when a game is loaded (stale hold auto-releases at 600 s).
  Works without an engine. Every `AgentDataDir.Check` guard is kept.

## Kept on purpose
- `AgentDialOut` (agentUrl, sendColonyEvents): the documented push contract for external agents over the opt-in HTTP bridge.
