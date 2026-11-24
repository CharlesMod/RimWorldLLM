using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
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
            DrainInbound();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref ConversationLog.Entries, "hermesConversation", LookMode.Value);
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

        void DrainInbound()
        {
            InboundRequest req;
            while (HermesBridge.Pending.TryDequeue(out req))
            {
                try
                {
                    if (!AuthOk(req))
                    {
                        req.Complete(401, "{\"error\":\"unauthorized\"}");
                        continue;
                    }
                    string route = req.Path;
                    if (req.Method == "GET" && (route == "/health" || route == "/"))
                        req.Complete(200, "{\"status\":\"ok\",\"mod\":\"HermesVoice\",\"version\":\"1.0.0\"}");
                    else if (req.Method == "GET" && route == "/state")
                        req.Complete(200, BuildStateJson());
                    else if (req.Method == "POST" && route == "/say")
                        HandleSay(req, req.Body);
                    else
                        req.Complete(404, "{\"error\":\"not found\"}");
                }
                catch (Exception e)
                {
                    Log.Message($"[HermesVoice] Error handling {req.Method} {req.Path}: {e}");
                    req.Complete(500, "{\"error\":\"internal error\"}");
                }
            }
        }

        bool AuthOk(InboundRequest req)
        {
            var s = HermesVoiceMod.Settings;
            string token = s?.sharedToken ?? "";
            if (string.IsNullOrWhiteSpace(token)) return true;
            string auth = req.GetHeader("Authorization");
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return auth.Substring(7).Trim() == token;
            if (!string.IsNullOrEmpty(req.Query))
            {
                foreach (var pair in req.Query.Split('&'))
                {
                    var kv = pair.Split('=');
                    if (kv.Length == 2 && kv[0] == "token")
                    {
                        try { if (Uri.UnescapeDataString(kv[1]) == token) return true; } catch { }
                    }
                }
            }
            return false;
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
            var map = Find.CurrentMap;
            Pawn pawn = map != null ? map.PlayerPawnsForStoryteller.FirstOrDefault(p => p.thingIDNumber == pawnId.Value) : null;
            if (pawn == null)
            {
                req.Complete(404, "{\"error\":\"pawn not found\"}");
                return;
            }
            SpeechBubbles.Add(pawn, text);
            ConversationLog.Add(pawn.thingIDNumber, pawn.LabelCap, text);
            DialOutToAgent("said", $"pawn {pawn.thingIDNumber} ({pawn.LabelCap}) said: {text}");
            req.Complete(200, JsonConvert.SerializeObject(new { ok = true, pawn = pawn.thingIDNumber, text }));
        }

        string BuildStateJson()
        {
            var map = Find.CurrentMap;
            var pawns = new List<object>();
            if (map != null)
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
                game = new { tick = Find.TickManager.TicksGame, maps = Find.Maps.Count },
                bridge = new { port = HermesBridge.Port, running = HermesBridge.IsRunning, version = "1.0.0" },
                pawn_count = pawns.Count,
                pawns = pawns
            };
            return JsonConvert.SerializeObject(state);
        }

        public void DialOutToAgent(string eventType, string payload)
        {
            var s = HermesVoiceMod.Settings;
            if (s == null || string.IsNullOrWhiteSpace(s.agentUrl)) return;
            string url = s.agentUrl;
            string token = s.sharedToken ?? "";
            string body = JsonConvert.SerializeObject(new
            {
                type = eventType,
                tick = Find.TickManager.TicksGame,
                payload = payload
            });
            var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            new Thread(() =>
            {
                try
                {
                    var content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json");
                    if (!string.IsNullOrEmpty(token))
                        content.Headers.Add("Authorization", "Bearer " + token);
                    using (var resp = client.PostAsync(url, content).GetAwaiter().GetResult()) { }
                }
                catch (Exception e)
                {
                    Log.Message($"[HermesVoice] Dial-out to agent failed: {e.Message}");
                }
            }).Start();
        }
    }
}
