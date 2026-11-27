// Port of games/rimworld/replay.py (replay) and core/cli.py _replay's printing. It lives in the test project because it runs the Director against
// the FakeBridge and the offline FakeLlm (the Mod ships neither); RimWorldAdapter.ReplayAsync stays NotSupported.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;

namespace RimWorldLLM.Tests.Mind.ReplayTool
{
    public static class Replay
    {
        public static (List<JObject> Pawns, List<JObject> Events) LoadJsonl(string path)
        {
            var pawns = new List<JObject>(); var events = new List<JObject>();
            foreach (string raw in File.ReadAllText(path).Split('\n'))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#", StringComparison.Ordinal)) continue;
                var d = JObject.Parse(ln);
                if (d["pawn"] != null && d["type"] == null) pawns.Add((JObject)d["pawn"]); else events.Add(d);
            }
            return (pawns, events);
        }

        /// <summary>Let idle work finish: one idle job starts per step, so step until a step produces nothing new.</summary>
        static async Task SettleAsync(Director d, int limit = 40)
        {
            for (int i = 0; i < limit; i++)
            {
                var before = (d.Idle.Log.Count, d.Dropped.Count);
                await d.StepAsync();
                await d.DrainAsync();
                if ((d.Idle.Log.Count, d.Dropped.Count) == before && !d.Idle.Active()) return;
            }
        }

        static MindConfig Copy(MindConfig c)
        {
            var n = new MindConfig();
            foreach (PropertyInfo p in typeof(MindConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.CanWrite) p.SetValue(n, p.GetValue(c));
            return n;
        }

        /// <summary>Run the director over recorded events. Across days, the evening and the turn of the day are given idle time, so
        /// reflections, diaries and chronicle entries appear after the lines they follow.</summary>
        public static async Task<List<JObject>> RunAsync(string path, MindConfig cfg, ILlmClient llm = null)
        {
            var (pawns, events) = LoadJsonl(path);
            var fb = new FakeBridge();
            foreach (var p in pawns) fb.Pawns[(int)p["id"]] = p;
            var ids = new SortedSet<int>();
            foreach (var e in events)
            {
                foreach (var x in (e["pawns"] as JArray) ?? new JArray()) ids.Add((int)x["id"]);
                foreach (var w in (e["witnesses"] as JArray) ?? new JArray()) ids.Add((int)w);
            }
            foreach (int i in ids) if (!fb.Pawns.ContainsKey(i)) fb.AddPawn(i, "Pawn" + i);
            var adapter = new RimWorldAdapter();
            int upd = adapter.UnitsPerDay;
            string tmp = Path.Combine(Path.GetTempPath(), "rimworldllm-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);                                   // never touch the real chronicle
            try
            {
                var c = Copy(cfg);
                c.DataDir = tmp; c.TurnGapTicks = 0; c.TurnGapFloorRealS = 0.0;       // offline: no reading time
                var bridge = FakeBridge.Client(fb, fb.Token);
                var d = new Director(c, bridge, llm ?? new FakeLlm(), adapter, null, () => fb.Tick / 60.0);
                try
                {
                    await d.StartAsync();
                    int? curDay = null;
                    foreach (var e in events.OrderBy(e => e["seq"] == null ? 0 : (int)e["seq"]))
                    {
                        int tick = e["tick"] != null ? (int)e["tick"] : fb.Tick;
                        int day = tick / upd;
                        if (curDay != null && day > curDay)
                        {
                            fb.Tick = Math.Max(fb.Tick, curDay.Value * upd + (int)(0.75 * upd));    // the evening: diaries
                            await SettleAsync(d);
                            fb.Tick = Math.Max(fb.Tick, (curDay.Value + 1) * upd + 10);             // the turn of the day: the chronicle
                            await SettleAsync(d);
                        }
                        curDay = day;
                        fb.Tick = Math.Max(fb.Tick, tick);
                        fb.Seq = Math.Max(fb.Seq, (e["seq"] != null ? (int)e["seq"] : fb.Seq + 1) - 1);
                        var actors = ((e["pawns"] as JArray) ?? new JArray()).Select(p => new KeyValuePair<int, string>((int)p["id"], (string)p["role"] ?? "")).ToList();
                        var wit = ((e["witnesses"] as JArray) ?? new JArray()).Select(w => (int)w).ToList();
                        fb.AddEvent((string)e["type"], actors, (string)e["summary"] ?? "", wit, e["severity"] != null ? (double)e["severity"] : 0.5,
                                    e["ledger_id"] == null || e["ledger_id"].Type == JTokenType.Null ? null : (string)e["ledger_id"], e["data"] as JObject);
                        await d.StepAsync();
                        await d.DrainAsync();
                    }
                    if (curDay != null)
                    {
                        fb.Tick = Math.Max(fb.Tick, curDay.Value * upd + (int)(0.75 * upd));
                        await SettleAsync(d);
                        fb.Tick = Math.Max(fb.Tick, (curDay.Value + 1) * upd + 10);
                        await SettleAsync(d);
                    }
                    else await SettleAsync(d);
                    var o = new List<JObject>(d.Said);
                    foreach (var (pid, why) in d.Dropped) o.Add(new JObject { ["actor"] = pid, ["dropped"] = why });
                    o.AddRange(d.Idle.Log);
                    return o;
                }
                finally { d.Dispose(); }
            }
            finally { try { Directory.Delete(tmp, true); } catch (Exception) { } }
        }

        /// <summary>core/cli.py _replay's output, one string per printed line.</summary>
        public static List<string> Format(IEnumerable<JObject> items)
        {
            var o = new List<string>();
            foreach (var item in items)
            {
                string k = (string)item["kind"];
                if (k == "reflection")
                    o.Add("[reflection] " + item["name"] + ": " + item["bio"] + "  wants: " + string.Join("; ", ((JArray)item["wants"]).Select(x => (string)x)) +
                          "  (relations " + item["relations"] + ", merged " + item["merged"] + ")");
                else if (k == "diary") o.Add("[diary day " + ((int)item["day"] + 1) + "] " + item["name"] + ": " + item["text"]);
                else if (k == "chronicle") o.Add("[chronicle day " + ((int)item["day"] + 1) + ((string)item["entry_kind"] == "major" ? " major" : "") + "] " + item["text"]);
                else if (item["dropped"] != null) o.Add("  (actor " + item["actor"] + " dropped: " + item["dropped"] + ")");
                else
                {
                    string intent = item["intent"] != null ? (string)item["intent"] : "none";
                    string tag = intent != "none" ? " -> " + intent : "";
                    o.Add((item["name"] != null ? (string)item["name"] : "?") + ": " + (item["text"] != null ? (string)item["text"] : "") + tag);
                }
            }
            return o;
        }
    }
}
