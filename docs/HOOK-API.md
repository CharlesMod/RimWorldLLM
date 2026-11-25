# HermesVoice hook API (for other mods)

Reference `HermesVoice.dll` (or reflect on the class) and call the statics of
`HermesVoice.HermesVoiceAPI`. All calls are safe to make from the game thread at any time,
including when the bridge is not running.

## `RegisterPawnContext(string key, Func<Pawn, string> provider)`

Adds a line of context to every `GET /pawn/{id}` snapshot, under `extra[key]`. The agent
sees it when it writes that pawn's lines.

- The provider runs on the game thread on each snapshot, so keep it cheap.
- Registering the same key again replaces the function.
- Returning `null` leaves the key out for that pawn. An exception is logged once and the key
  is skipped.

```csharp
HermesVoiceAPI.RegisterPawnContext("MyMod.Faith", p => p.GetComp<CompFaith>()?.Summary);
```

## `RequestTalk(Pawn pawn, string text)`

Queues a request for the agent: "this pawn should say something about `text`". It appears in
`GET /talk` as `{"id": "t-1", "pawn": 12, "text": "...", "from": "mod"}` and is removed when
the agent answers with `POST /say` carrying that `talk_id`. The queue holds the last 100.

```csharp
HermesVoiceAPI.RequestTalk(pawn, "just received the royal title of Knight");
```

See `docs/BRIDGE-API.md` for the wire protocol.
