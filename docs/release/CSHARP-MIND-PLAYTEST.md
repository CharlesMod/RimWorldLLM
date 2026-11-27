# Playtest: the C# mind

What the keeper checks in game after the move of the agent from Python into the mod (docs/CSHARP-MIND.md). Everything here can be
done on one machine; the own-endpoint items need an OpenAI-compatible endpoint (an easyEngine on another computer is fine).
Note the version, the mode (local or own endpoint) and `calls.jsonl` lines for anything that fails.

## Before you start
1. Install the new zip over the old mod folder. Keep your old data folder (`rimworldllm-agent`) in place: item 14 depends on it.
2. The zip has no `Engine/agent` folder and no Python or uv anywhere.

## Talking
1. **Narrator chat.** Press Enter, ask the narrator something. It answers in the chat panel.
2. **@colonist.** `@Name hello` is answered by that colonist, in their voice.
3. **Live line.** While the model works the chat shows "thinking..." with progress (a percentage or count while the prompt is read),
   then "typing..." with the line growing, then the finished line. No stuck "thinking" after an answer or after an error.
4. **Colonists talk** to each other on their own (idle talk, group scenes), with speech bubbles; the frequency slider still works.
5. **Thoughts, diaries, dreams.** Thought clouds appear; a pawn's Mind tab fills its Diary; sleepers get a dream cloud.
6. **Chronicle and art.** After a day or a major event a chronicle entry appears (Voices window, second tab); art descriptions show on
   new artworks.
7. **Recap on load.** Loading a save shows the "Previously on..." recap (if enabled), once.

## Narrator plays (vivarium)
8. Switch Narrator plays on: the steward acts (work priorities, schedules, policies, drafting) and each action prints a greentext line.
   Switch it off: the narrator answers as an advisor and does one task when asked.
9. Combat, trade and Biotech turns, if you have the DLC and the situation: a trade offer is handled once per trader; a raid gets a
   combat plan; a baby/child request goes through the bio rules (children stay out of fights). Note anything that stalls.

## Endpoints
10. **Own endpoint, no engine.** Choose My own endpoint with a URL and key; start a game. No engine process starts, chat is answered,
    colonists talk. Wrong key: the mod shows Pair again or an error in the status line, not a freeze.
11. **Local mode during a model load.** Start the engine and a game while a model is loading: the status says "waiting for the model",
    the first request waits (up to 15 minutes) instead of failing, and lines start once the model is ready. Swapping model does not
    restart the mind.

## Performance
12. **No frame hitches** with the mind busy: with several colonists chatting and the steward running, watch the frame time (and a
    fast game speed). The game stays smooth while the mind waits on the model.
13. At the main menu the mind is idle ("waiting for a game"); returning to the menu and loading another save restarts it cleanly.

## Memory and settings
14. **Memory carried over.** Using a data folder from a Python-era install (and a save from that time), colonists remember earlier
    conversations, diaries and the chronicle are still there, and `calls.jsonl` keeps growing with the new rows (they now carry `request_id`).
15. **Storage > Remove memory** (with and without an engine): the mind stops, the folder is emptied (only what the guard allows),
    the mind restarts when a game is loaded; no error in the log.
16. **Status line** (Settings, Model and Advanced): "running", "waiting for the model", "waiting for a game", "off" (mod disabled),
    and "error: ..." (for example point the own endpoint at a closed port; it clears after the endpoint is fixed or after 120 s).
17. **config.toml.** Create `<data folder>/config.toml` with a change (for example `talk_frequency` or a `[llm]` `temperature`);
    within a few seconds the mind restarts with it. A broken file shows an error in the status line and the game keeps running; fixing
    it recovers without restarting the game.
18. Mod disable (Mods list) and re-enable: the mind stops and starts.

## From the wiring notes (docs/csmind/parity-wire.md)
- Hosting: the mind starts on game load and stops at the main menu and on mod disable; a failing start retries after 15 s.
- Engine profile: no `agent` block, no uv or Python download during setup, and an old engine's agent status is ignored.
- LLM timeouts: local 900 s (idle time between pieces when streaming), own endpoint 120 s; a long model load can still cut one steward
  step (60 s step, 120 s budget), which just retries on the next cycle.
- Live chat: no HTTP behind it now; a cancelled line disappears, a finished line replaces the live one.
- Memory removal never deletes outside the data folder; the held state auto-releases after 600 s.
- The `agentUrl` dial-out for external agents still works when the HTTP bridge is on.
