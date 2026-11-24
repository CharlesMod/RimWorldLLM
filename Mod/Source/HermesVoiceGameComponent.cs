using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RimWorld;
using Verse;

namespace HermesVoice
{
    public class SayDto
    {
        public int? pawn_id;
        public string text;
    }

    public class HermesVoiceGameComponent : GameComponent
    {
        const int MaxSayChars = 500;
        public static HermesVoiceGameComponent Instance { get; private set; }
        public readonly ConversationLog ConversationLog = new ConversationLog();

        public HermesVoiceGameComponent(Game game) : base()
        {
            Instance = this;
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RestartBridge();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
        }

        // Runs every frame, even while paused (GameComponentTick does not).
        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            DrainInbound();
            AgentDialOut.FlushLogs();
        }

        /// <summary>Called when the game ends (return to main menu): stop serving and drop per-game state.</summary>
        public static void OnGameEnd()
        {
            HermesBridge.Stop();
            SpeechBubbles.Clear();
            Instance = null;
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            SpeechBubbles.Clear();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SpeechBubbles.Clear();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref ConversationLog.Entries, "hermesConversation", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ConversationLog.Entries == null)
                ConversationLog.Entries = new List<ConversationEntry>();
        }

        public void RestartBridge()
        {
            var s = HermesVoiceMod.Settings;
            if (s == null || !s.enabled)
            {
                HermesBridge.Stop();
                return;
            }
            HermesBridge.Start(s.bridgePort, s.bindAddress, s.sharedToken ?? "");
        }

        const int MaxRequestsPerFrame = 32;

        void DrainInbound()
        {
            InboundRequest req;
            int handled = 0;
            while (handled < MaxRequestsPerFrame && HermesBridge.Pending.TryDequeue(out req))
            {
                if (!req.TryClaim()) continue; // the HTTP side timed out and gave up
                handled++;
                try
                {
                    string route = req.Path;
                    if (req.Method == "GET" && (route == "/health" || route == "/"))
                        req.Complete(200, JsonConvert.SerializeObject(new { status = "ok", mod = "HermesVoice", version = HermesVoiceMod.Version }));
                    else if (req.Method == "GET" && route == "/state")
                        req.Complete(200, BuildStateJson());
                    else if (req.Method == "POST" && route == "/say")
                        HandleSay(req, req.Body);
                    else
                        req.Complete(404, "{\"error\":\"not found\"}");
                }
                catch (Exception e)
                {
                    Log.Warning($"[HermesVoice] Error handling {req.Method} {req.Path}: {e}");
                    req.Complete(500, "{\"error\":\"internal error\"}");
                }
            }
        }

        void HandleSay(InboundRequest req, string body)
        {
            int? pawnId;
            string text = "";
            try
            {
                var dto = JsonConvert.DeserializeObject<SayDto>(body);
                pawnId = dto?.pawn_id;
                text = dto?.text ?? "";
            }
            catch (Exception e)
            {
                req.Complete(400, JsonConvert.SerializeObject(new { error = "bad json: " + e.Message }));
                return;
            }
            if (pawnId == null)
            {
                req.Complete(400, "{\"error\":\"missing pawn_id\"}");
                return;
            }
            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                req.Complete(400, "{\"error\":\"empty text\"}");
                return;
            }
            if (text.Length > MaxSayChars) text = text.Substring(0, MaxSayChars);

            Pawn pawn = null;
            foreach (var map in Find.Maps)
            {
                pawn = map.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == pawnId.Value && p.Faction == Faction.OfPlayer);
                if (pawn != null) break;
            }
            if (pawn == null)
            {
                // Not on any map: a dead, caravan or otherwise unspawned player pawn is a conflict, not "unknown".
                var other = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.FirstOrDefault(p => p.thingIDNumber == pawnId.Value && p.Faction == Faction.OfPlayer);
                if (other == null)
                {
                    req.Complete(404, "{\"error\":\"pawn not found\"}");
                    return;
                }
                pawn = other;
            }
            if (pawn.Dead || !pawn.Spawned)
            {
                req.Complete(409, "{\"error\":\"pawn is dead or not spawned\"}");
                return;
            }
            SpeechBubbles.Add(pawn, text);
            ConversationLog.Add(pawn.thingIDNumber, pawn.LabelCap, text);
            req.Complete(200, JsonConvert.SerializeObject(new { ok = true, pawn = pawn.thingIDNumber, map = pawn.Map.Index, text }));
        }

        string BuildStateJson()
        {
            var pawns = new List<object>();
            foreach (var map in Find.Maps)
            {
                foreach (var pawn in map.PlayerPawnsForStoryteller)
                {
                    if (pawn == null) continue;
                    pawns.Add(new
                    {
                        id = pawn.thingIDNumber,
                        name = pawn.LabelCap,
                        kind = pawn.kindDef?.defName ?? "",
                        job = pawn.CurJob?.def?.label ?? "",
                        map = pawn.Map?.Index ?? -1,
                        pos = new { x = pawn.Position.x, y = pawn.Position.y, z = pawn.Position.z },
                        downed = pawn.Downed,
                        drafted = pawn.Drafted
                    });
                }
            }
            var state = new
            {
                game = new { tick = Find.TickManager.TicksGame, paused = Find.TickManager.Paused, maps = Find.Maps.Count, current_map = Find.CurrentMap?.Index ?? -1 },
                bridge = new { port = HermesBridge.Port, running = HermesBridge.IsRunning, version = HermesVoiceMod.Version },
                pawn_count = pawns.Count,
                pawns = pawns
            };
            return JsonConvert.SerializeObject(state);
        }
    }
}
