using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    public static partial class Routes
    {
        const int MaxSayChars = 500;

        static void RegisterWrites(ApiRouter r)
        {
            r.Add("POST", "/say", true, Say);
            r.Add("POST", "/thought", true, Thought);
            r.Add("POST", "/chronicle", true, ChroniclePost);
            r.Add("GET", "/chronicle", true, ChronicleGet);
            r.Add("POST", "/recap", true, RecapPost);
            r.Add("GET", "/mind/{id}", true, MindGet);
            r.Add("PUT", "/mind/{id}", true, MindPut);
            r.Add("GET", "/talk", true, Talk);
            RegisterWants(r);
            RegisterArt(r);
            try { TextHook.Register(r); }      // never let the text hooks take the other routes down
            catch (Exception e) { Log.Error("[RimWorldLLM] written texts unavailable: " + e); }
        }

        static Pawn PawnArg(JObject body, string field, bool required, out ApiResult err)
        {
            err = null;
            JToken t = body[field];
            if (t == null || t.Type == JTokenType.Null)
            {
                if (required) err = ApiResult.Fail("bad_request", field + " is required");
                return null;
            }
            if (t.Type != JTokenType.Integer) { err = ApiResult.Fail("bad_request", field + " must be a pawn id (integer)"); return null; }
            var p = FindPawn(t.Value<int>());
            if (p == null) err = ApiResult.Fail("not_found", "no pawn " + t.Value<int>() + " (" + field + ")");
            return p;
        }

        // --------------------------------------------------------------- say
        static ApiResult Say(RouteContext c)
        {
            var b = c.Body;
            ApiResult err;
            var speaker = PawnArg(b, "pawn", true, out err);
            if (err != null) return err;
            var listener = PawnArg(b, "listener", false, out err);
            if (err != null) return err;
            var about = PawnArg(b, "about", false, out err);
            if (err != null) return err;

            string text = ((string)b["text"] ?? "").Trim();
            if (text.Length == 0) return ApiResult.Fail("bad_request", "text is empty");
            if (text.Length > MaxSayChars) text = text.Substring(0, MaxSayChars);
            string intent = ((string)b["intent"] ?? "none").Trim().ToLowerInvariant();
            if (!EffectTable.Known(intent)) return ApiResult.Fail("bad_request", "unknown intent '" + intent + "'");
            bool combat = b["combat"] != null && b["combat"].Type == JTokenType.Boolean && b.Value<bool>("combat");
            string talkId = (string)b["talk_id"];
            string vanillaRef = (string)b["vanilla_ref"];       // set when the line was written for one of the game's own interactions

            // Liveness, on the game thread, right before anything shows.
            if (speaker.Dead) return ApiResult.Fail("conflict", "speaker is dead");
            if (!speaker.Spawned) return ApiResult.Fail("conflict", "speaker is not spawned");
            if (!speaker.Awake()) return ApiResult.Fail("conflict", "speaker is asleep or unconscious");
            if (!speaker.RaceProps.Humanlike) return ApiResult.Fail("bad_request", "speaker is not a person");
            if (listener != null && listener != speaker)
            {
                if (listener.Dead) return ApiResult.Fail("conflict", "listener is dead");
                if (!listener.Spawned) return ApiResult.Fail("conflict", "listener is not spawned");
                if (listener.Map != speaker.Map) return ApiResult.Fail("conflict", "listener is on another map");
                float level = listener.health?.capacities?.GetLevel(PawnCapacityDefOf.Hearing) ?? 0f;
                float dist = speaker.Position.DistanceTo(listener.Position);
                var room = speaker.GetRoom();
                bool same = room != null && room == listener.GetRoom();
                bool los = same || GenSight.LineOfSight(speaker.Position, listener.Position, speaker.Map);
                if (!Rules.Witnessed(dist, level, same, los)) return ApiResult.Fail("conflict", "listener cannot hear the speaker");
            }
            if (listener == speaker) listener = null;

            // Show.
            SpeechBubbles.Add(speaker, text, (string)b["kind"], intent, combat);
            DirectorCamera.OnConversation(speaker, listener);
            bool fromPlayer = talkId != null && RWLBridge.Talk.Contains(talkId);
            bool fromChat = talkId != null && VivariumGame.TalkFrom(talkId) == "chat";
            if (talkId != null) RWLBridge.Talk.Remove(talkId);
            // The chat log gets exactly one colonist line per successful /say (a reply to the player's @colonist line
            // included: that line is written here only, never by the chat bar or ChatLive).
            ChatFeed.Say(VivariumGame.State.Chat, VivariumGame.NowTick, speaker.thingIDNumber, speaker.LabelShort,
                listener != null ? listener.LabelShort : "", text);

            // A line for a vanilla interaction goes onto that interaction's log entry and thoughts, and adds no effect of its own.
            string attach = null;
            bool attached = false;
            if (!string.IsNullOrEmpty(vanillaRef))
            {
                var r = QuotedInteractions.TryAttach(vanillaRef, speaker, listener, text);
                attached = r == PendingTable<VanillaPending>.Result.Attached;
                attach = r.ToString().ToLowerInvariant();
                intent = "none";
            }

            // Effects, under the C# caps.
            var witnesses = new List<Pawn>();
            if (intent == "insult")
            {
                var spec = new EventSpec { Map = speaker.Map, At = speaker.Position };
                var ids = new HashSet<int>(Witnesses.Compute(spec));
                witnesses = speaker.Map.mapPawns.AllPawnsSpawned.Where(p => ids.Contains(p.thingIDNumber)).ToList();
            }
            var outcome = EffectGuard.MayApply(vanillaRef)
                ? SayEffects.Apply(intent, speaker, listener, about, witnesses, text)
                : new SayEffects.Outcome { Refused = "vanilla_interaction" };

            // One log entry per line: the effect wording when an effect was applied, never the line twice.
            bool logged = attached;
            if (!attached)
                try
                {
                    var idef = DefDatabase<InteractionDef>.GetNamedSilentFail("RWL_Say");
                    if (idef != null)
                    {
                        Find.PlayLog.Add(new PlayLogEntry_RWLSay(idef, speaker, listener, text, outcome.Effect));
                        logged = true;
                    }
                    if (combat) Find.BattleLog.Add(new BattleLogEntry_RWLSay(speaker, listener, text));
                }
                catch (Exception e) { Log.Warning("[RimWorldLLM] play log write failed: " + e.Message); }

            var logEntry = RimWorldLLMGameComponent.Instance?.ConversationLog.Add(speaker.thingIDNumber, speaker.LabelCap, text);
            if (logEntry != null)
            {
                logEntry.ListenerId = listener != null ? listener.thingIDNumber : 0;
                logEntry.ListenerName = listener != null ? listener.LabelCap.ToString() : "";
                logEntry.Trigger = fromPlayer ? "player" : "agent";
                logEntry.Intent = intent;
                logEntry.Effect = outcome.Effect ?? "";
                logEntry.Refused = outcome.Effect == null && intent != "none" ? (outcome.Refused ?? "") : "";
            }

            var applied = new JObject
            {
                ["bubble"] = true,
                ["log"] = logged,
                ["effect"] = outcome.Effect != null ? (JToken)outcome.Effect : JValue.CreateNull(),
                ["thoughts"] = new JArray(outcome.Thoughts)
            };
            if (attach != null) applied["attached"] = attach;
            var body = new JObject { ["applied"] = applied };
            if (outcome.Refused != null && intent != "none") body["refused"] = new JObject { ["effect"] = outcome.Refused };
            return ApiResult.Success(body);
        }

        // ----------------------------------------------------------- thought
        static ApiResult Thought(RouteContext c)
        {
            ApiResult err;
            var p = PawnArg(c.Body, "pawn", true, out err);
            if (err != null) return err;
            string text = ((string)c.Body["text"] ?? "").Trim();
            if (text.Length == 0) return ApiResult.Fail("bad_request", "text is empty");
            string kind = MindBook.KindThought;
            JToken kt = c.Body["kind"];
            if (kt != null && kt.Type != JTokenType.Null)
            {
                kind = kt.Type == JTokenType.String ? (string)kt : "";
                if (!MindBook.KnownKind(kind)) return ApiResult.Fail("bad_request", "kind must be \"thought\", \"diary\" or \"dream\"");
            }
            int tick = Find.TickManager.TicksGame;
            if (kind == MindBook.KindDiary) RWLBridge.Minds.AddDiary(p.thingIDNumber, tick, text);
            else RWLBridge.Minds.AddThought(p.thingIDNumber, tick, text);
            // A thought also shows on the map as a short cloud (never a diary). "surface" is the short line for the map.
            if (kind == MindBook.KindThought) SpeechBubbles.AddThought(p, (string)c.Body["surface"], text);
            // A dream is stored like a thought and drawn as the same cloud, even over a sleeper (the draw path never
            // skips sleeping pawns); the dreams setting turns the cloud off, the Mind tab still shows it.
            else if (kind == MindBook.KindDream && RimWorldLLMMod.Settings != null && RimWorldLLMMod.Settings.dreamsEnabled)
                SpeechBubbles.AddThought(p, (string)c.Body["surface"], text);
            ChatFeed.Thought(VivariumGame.State.Chat, VivariumGame.NowTick, kind, p.thingIDNumber, p.LabelShort, text);
            return ApiResult.Success(new JObject { ["stored"] = true });
        }

        // --------------------------------------------------------- chronicle
        static ApiResult ChroniclePost(RouteContext c)
        {
            var b = c.Body;
            if (b["day"] == null || b["day"].Type != JTokenType.Integer || b.Value<long>("day") < 0 || b.Value<long>("day") > int.MaxValue)
                return ApiResult.Fail("bad_request", "day (a non-negative integer) is required");
            string text = ((string)b["text"] ?? "").Trim();
            if (text.Length == 0) return ApiResult.Fail("bad_request", "text is empty");
            string kind = ChronicleBook.KindDay;
            JToken kt = b["kind"];
            if (kt != null && kt.Type != JTokenType.Null)
            {
                kind = kt.Type == JTokenType.String ? (string)kt : "";
                if (!ChronicleBook.KnownKind(kind)) return ApiResult.Fail("bad_request", "kind must be \"day\", \"major\" or \"lore\"");
            }
            int count = RWLBridge.Chronicle.Add(b.Value<int>("day"), kind, text, Find.TickManager.TicksGame);
            ChatFeed.Chronicle(VivariumGame.State.Chat, VivariumGame.NowTick, b.Value<int>("day"), text);
            return ApiResult.Success(new JObject { ["stored"] = true, ["count"] = count });
        }

        static ApiResult ChronicleGet(RouteContext c)
        {
            int since = 0;
            string q = c.Q("since_day");
            if (!string.IsNullOrEmpty(q) && !int.TryParse(q, out since)) return ApiResult.Fail("bad_request", "since_day must be an integer");
            return ApiResult.Success(new JObject { ["entries"] = RWLBridge.Chronicle.Since(since) });
        }

        // ------------------------------------------------------------- recap
        // A neutral letter, not a message bubble or a dialog: it waits in the letter stack, never pauses the game and
        // is easy to ignore, and the player can re-read it from the stack until they dismiss it.
        static ApiResult RecapPost(RouteContext c)
        {
            var b = c.Body;
            if (b["epoch"] == null || b["epoch"].Type != JTokenType.Integer) return ApiResult.Fail("bad_request", "epoch (an integer) is required");
            var arr = b["lines"] as JArray;
            if (arr == null) return ApiResult.Fail("bad_request", "lines (an array of strings) is required");
            var raw = new List<string>();
            foreach (var t in arr)
            {
                if (t.Type != JTokenType.String) return ApiResult.Fail("bad_request", "lines must be strings");
                raw.Add((string)t);
            }
            var lines = RecapGate.CleanLines(raw);
            if (lines.Count == 0) return ApiResult.Fail("bad_request", "lines is empty");
            var comp = RimWorldLLMGameComponent.Instance;
            if (comp == null) return ApiResult.Fail("no_game");
            var verdict = comp.RecapGate.Check(b.Value<int>("epoch"), Environment.TickCount64, RimWorldLLMMod.Settings.recapOnLoad);
            if (verdict != RecapVerdict.Accept) return ApiResult.Fail(RecapGate.ErrorCode(verdict), verdict.ToString());
            string colony = Faction.OfPlayer != null ? Faction.OfPlayer.Name : "";
            Find.LetterStack.ReceiveLetter(UiUtil.T("RWL_Recap_Title", colony), string.Join("\n\n", lines), LetterDefOf.NeutralEvent);
            ChatFeed.Recap(VivariumGame.State.Chat, VivariumGame.NowTick, lines);
            return ApiResult.Success(new JObject { ["shown"] = true });
        }

        // -------------------------------------------------------------- mind
        static ApiResult MindPawn(RouteContext c, out Pawn p)
        {
            return ResolvePawn(c, out p);
        }

        static ApiResult MindGet(RouteContext c)
        {
            Pawn p;
            var err = MindPawn(c, out p);
            if (err != null) return err;
            return ApiResult.Success(RWLBridge.Minds.Get(p.thingIDNumber));
        }

        static ApiResult MindPut(RouteContext c)
        {
            Pawn p;
            var err = MindPawn(c, out p);
            if (err != null) return err;
            var b = c.Body;
            if (b["rev"] == null || b["rev"].Type != JTokenType.Integer) return ApiResult.Fail("bad_request", "rev (integer) is required");
            if (b["mind"] == null) return ApiResult.Fail("bad_request", "mind is required (an object, or null to clear)");
            int newRev;
            var minds = RWLBridge.Minds;
            switch (minds.Put(p.thingIDNumber, b.Value<int>("rev"), b["mind"], out newRev))
            {
                case MindBook.PutResult.Conflict:
                    return ApiResult.Fail("conflict", "stale rev; the stored rev is " + newRev, new JObject { ["rev"] = newRev });
                case MindBook.PutResult.TooLarge:
                    return ApiResult.Fail("too_large", "mind is over " + MindBook.MaxMindBytes + " bytes");
            }
            return ApiResult.Success(new JObject { ["rev"] = newRev });
        }

        // -------------------------------------------------------------- talk
        static ApiResult Talk(RouteContext c)
        {
            return ApiResult.Success(new JObject { ["requests"] = RWLBridge.Talk.ToJson() });
        }
    }
}
