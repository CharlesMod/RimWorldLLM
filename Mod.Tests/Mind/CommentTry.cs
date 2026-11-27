// Port of agent/rimworldllm_agent/games/rimworld/comment_try.py try_events(): try the documentary voice on recorded events, one line per
// moment and a day recap, against a FakeBridge. The CLI wrapper (argument parsing, printing, an HTTP llm) is the tools worker's:
//
//     var rows = await CommentTry.TryEventsAsync(events, llm);
//     foreach (var r in rows) Console.WriteLine(CommentTry.Format(r));     // the Python main()'s output format
//
// The gaps are zeroed so every moment speaks; nothing here touches a real game. Lives in Mod.Tests/Mind (FakeBridge is there).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;

namespace RimWorldLLM.Tests.Mind
{
    public sealed class CommentTryRow
    {
        public string Event;
        public string Kind;
        public string Mode;
        public string Line;
        public string Drop;
    }

    public static class CommentTry
    {
        /// <summary>One row per event in order ("moment"), then one "recap" row for the day after the last event.</summary>
        public static async Task<List<CommentTryRow>> TryEventsAsync(IList<JObject> events, ILlmClient llm)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "commenttry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var cfg = new MindConfig
                {
                    DataDir = tmp, BridgeUrl = "http://fake", BridgeToken = "tok", Wants = false,
                    CommentaryMinGapRealS = 0.0, CommentaryMinGapGame = 0,
                };
                var fb = new FakeBridge("tok");
                var adapter = new RimWorldAdapter();
                var bridge = FakeBridge.Client(fb);
                double real = 0.0;
                var d = new Director(cfg, bridge, llm, adapter, null, () => real, new PyRandom(1));
                await d.StartAsync();
                d.Steward.Vv = JObject.Parse("{\"playing\": true, \"settings\": {}}");
                var output = new List<CommentTryRow>();
                try
                {
                    async Task<(List<CommentaryLine> said, string drop)> Step(int game)
                    {
                        real += 40.0;                              // clear the 30 s floor
                        bridge.Env.Game = game;
                        int n = d.Commentary.Log.Count;
                        d.Commentary.RunOnce();
                        await d.DrainAsync();
                        var said = d.Commentary.Log.Skip(n).ToList();
                        string drop = said.Count == 0 && d.Commentary.Drops.Count > 0 ? d.Commentary.Drops[d.Commentary.Drops.Count - 1] : null;
                        return (said, drop);
                    }
                    int last = 0;
                    foreach (JObject e in events)
                    {
                        string summary = e["summary"] != null && PyJson.Truthy(e["summary"]) ? PyJson.Str(e["summary"]) : (e["type"] == null ? "None" : PyJson.Str(e["type"]));
                        string kind = e["type"] == null ? null : PyJson.Str(e["type"]);
                        last = Math.Max(last, e["game"] == null ? 0 : (int)JobsJson.PyInt(e["game"]));
                        int before = d.Commentary.Pending.Count;
                        d.Commentary.Note(e);
                        if (d.Commentary.Pending.Count == before)
                        {
                            output.Add(new CommentTryRow { Event = summary, Kind = kind, Mode = "moment", Line = null });
                            continue;
                        }
                        var (said, drop) = await Step(last);
                        output.Add(new CommentTryRow { Event = summary, Kind = kind, Mode = "moment", Line = said.Count > 0 ? said[0].Text : null, Drop = drop });
                    }
                    var (said2, _) = await Step((int)(PyFmt.FloorDiv(last, d.Day) + 1) * d.Day + 1);   // the next morning: the recap of the day
                    output.Add(new CommentTryRow { Event = "(the day ends)", Kind = "recap", Mode = "recap", Line = said2.Count > 0 ? said2[0].Text : null });
                }
                finally { d.Dispose(); }
                return output;
            }
            finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
        }

        /// <summary>The Python main()'s two-line print of a row.</summary>
        public static string Format(CommentTryRow r)
        {
            string why = string.IsNullOrEmpty(r.Drop) ? "" : " (dropped: " + r.Drop + ")";
            return "[" + r.Kind + "] " + r.Event + "\n    -> " + (!string.IsNullOrEmpty(r.Line) ? r.Line : "(no comment)" + why);
        }
    }
}
