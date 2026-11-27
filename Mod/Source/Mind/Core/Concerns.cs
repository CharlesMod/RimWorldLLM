// Port of agent/rimworldllm_agent/core/concerns.py: ongoing concerns, a few lasting worries per actor (Mind.Concerns), alive for
// days until something resolves them. Births come from events (the adapter's concern_births; importance is the game's, never a
// model call) and from reflection (one optional new worry). Resolutions come from events (concern_resolutions) or from a
// reflection that lists the worry's id.
//
// The module functions are in ConcernOps (a static class named Concerns would be shadowed by the director's Concerns property);
// the director-side driver is ConcernEngine over IConcernsHost.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members the concern engine reads (the Python takes the whole director as d).</summary>
    public interface IConcernsHost
    {
        MindConfig Cfg { get; }
        Dictionary<int, Mind> Minds { get; }
        IGameAdapter Adapter { get; }
        IBridge Bridge { get; }
        /// <summary>actor id -> name (d.names).</summary>
        IDictionary<int, string> Names { get; }
        GateStats Gate { get; }
        /// <summary>actors whose mind changed (d.dirty).</summary>
        ISet<int> Dirty { get; }
        Chronicle Chronicle { get; }
        LeaseManager Leases { get; }
        /// <summary>Game units in one day (d.day).</summary>
        long Day { get; }
        /// <summary>d.now(): the play clock.</summary>
        Now Now();
        ISet<int> MinorIds();
        bool IsMinor(int actorId);
        ISet<string> MinorTokens();
    }

    /// <summary>The context sanitize_concern reads (a dict in the Python).</summary>
    public sealed class ConcernCtx
    {
        public ISet<string> Known = new HashSet<string>();
        public ISet<string> All = new HashSet<string>();
        public ISet<string> PromptWords = new HashSet<string>();
        public ISet<string> CommonWords;
        public ISet<string> Speaker = new HashSet<string>();
        public bool Minor;
        public ISet<string> MinorNames;
    }

    public static class ConcernOps
    {
        public const int MaxWords = 16;
        public const int MinWords = 3;
        public const int WorriesShown = 2;

        /// <summary>appended to the adapter's reflection request when concerns are on</summary>
        public const string ReflectConcernRequest =
            " Also (optional): concern: one new lasting worry you now hold, first person, under 14 words, or an empty " +
            "string; resolved: ids of the worries listed under your worries that are now over (empty list if none).";

        public static JObject ReflectConcernProps()
        {
            return new JObject
            {
                ["concern"] = new JObject { ["type"] = "string" },
                ["resolved"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
            };
        }

        /// <summary>REFLECT_SCHEMA plus the two optional concern fields (not required) when concerns are on.</summary>
        public static JObject ReflectSchema(JObject baseSchema, bool on)
        {
            if (!on) return baseSchema;
            var r = (JObject)baseSchema.DeepClone();
            var props = (JObject)r["properties"];
            foreach (var kv in ReflectConcernProps()) props[kv.Key] = kv.Value;
            return r;
        }

        public static List<Concern> OpenConcerns(Mind mind)
        {
            return mind.Concerns.Where(c => c.Status == "open").ToList();
        }

        /// <summary>Texts for block C, oldest first (stable between concern changes).</summary>
        public static List<string> WorryLines(Mind mind, int limit = WorriesShown)
        {
            return OpenConcerns(mind).Take(Math.Max(0, limit)).Select(c => c.Text).ToList();
        }

        /// <summary>The reflection prompt's list of open worries, with ids ('' when none).</summary>
        public static string WorriesBlock(Mind mind)
        {
            return string.Join("\n", OpenConcerns(mind).Select(c => c.Id + ": " + GameData.Clean(c.Text, "line")));
        }

        /// <summary>Open a concern unless it duplicates an open one (same kind and hint, or same text). At the cap the least
        /// important open concern yields to a more important newcomer (it fades); otherwise the newcomer is dropped.</summary>
        public static Concern AddConcern(Mind mind, string text, IList<int> about, string kind, string hint, double importance, long game, int cap)
        {
            text = string.Join(" ", Sanitize.PySplit(text));
            if (text.Length == 0 || cap <= 0) return null;
            foreach (var c in OpenConcerns(mind))
                if (c.Text == text || (!string.IsNullOrEmpty(hint) && c.ResolveHint == hint && c.Kind == kind)) return null;
            var opened = OpenConcerns(mind);
            if (opened.Count >= cap)
            {
                Concern weakest = opened[0];
                foreach (var c in opened)
                    if (c.Importance < weakest.Importance || (c.Importance == weakest.Importance && c.Born < weakest.Born)) weakest = c;
                if (importance <= weakest.Importance) return null;
                weakest.Status = "faded"; weakest.Closed = game; weakest.Outcome = "";
            }
            mind.ConcernCounter += 1;
            var n = new Concern
            {
                Id = "w" + mind.ConcernCounter.ToString(CultureInfo.InvariantCulture), Text = text, About = new List<int>(about), Kind = kind,
                Born = game, ResolveHint = hint, Importance = PyFmt.Round(importance, 2),
            };
            mind.Concerns.Add(n);
            return n;
        }

        public static void CloseConcern(Concern c, long game, string status, string outcome = "")
        {
            c.Status = status; c.Closed = game; c.Outcome = outcome;
        }

        public static List<Concern> FadeOld(Mind mind, long game, long fadeUnits)
        {
            var o = new List<Concern>();
            foreach (var c in OpenConcerns(mind))
                if (game - c.Born >= fadeUnits) { CloseConcern(c, game, "faded"); o.Add(c); }
            return o;
        }

        public static int DropClosed(Mind mind, long game, long keepUnits)
        {
            var keep = mind.Concerns.Where(c => c.Status == "open" || game - c.Closed < keepUnits).ToList();
            int n = mind.Concerns.Count - keep.Count;
            mind.Concerns = keep;
            return n;
        }

        /// <summary>Resolved concerns still on record (for the diary).</summary>
        public static List<Concern> RecentClosed(Mind mind, long game, long keepUnits)
        {
            return mind.Concerns.Where(c => c.Status == "resolved" && game - c.Closed < keepUnits).ToList();
        }

        /// <summary>A reflection's new worry, cleaned like an aim: '' when it fails the sanitizer or the age gate.</summary>
        public static string SanitizeConcern(string text, ConcernCtx ctx)
        {
            if (string.IsNullOrEmpty(text) || Sanitize.PyStrip(text).Length == 0) return "";
            var s = Sanitize.SanitizeProse(text, ctx.Known, ctx.All, ctx.PromptWords, ctx.CommonWords, ctx.Speaker, MinWords, MaxWords, ctx.Minor, ctx.MinorNames);
            if (!s.Ok || AgeGate.Violation(s.Text, ctx.Minor, ctx.MinorNames)) return "";
            return s.Text;
        }
    }

    /// <summary>Director-side driver. Holds no state of its own: everything lives in Mind.Concerns.</summary>
    public sealed class ConcernEngine
    {
        private readonly IConcernsHost d;
        public int Born, Resolved, Faded, Payoffs;

        public ConcernEngine(IConcernsHost d) { this.d = d; }

        public bool On { get { return d.Cfg.Concerns; } }

        public bool Loved(int holder, int other)
        {
            Mind m;
            Relation r = null;
            if (d.Minds.TryGetValue(holder, out m)) m.Relations.TryGetValue(other.ToString(CultureInfo.InvariantCulture), out r);
            return r != null && r.Opinion != null && r.Opinion >= d.Cfg.ConcernLovedOpinion;
        }

        internal static long GameOf(JObject e, long dflt)
        {
            JToken t = e["game"];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            if (t.Type == JTokenType.Float) return (long)t.Value<double>();
            if (t.Type == JTokenType.Integer) return t.Value<long>();
            if (t.Type == JTokenType.Boolean) return t.Value<bool>() ? 1 : 0;
            return long.Parse(Sanitize.PyStrip((string)t), CultureInfo.InvariantCulture);
        }

        // ---- events ---------------------------------------------------------
        public void OnEvent(JObject e)
        {
            if (!On) return;
            var ad = d.Adapter;
            long game = GameOf(e, d.Bridge.Env.Game);
            var keys = new Dictionary<string, string>();
            foreach (var (k, what) in ad.ConcernResolutions(e)) keys[k] = what;
            if (keys.Count > 0)
            {
                foreach (var kv in d.Minds.ToList())
                {
                    foreach (var c in ConcernOps.OpenConcerns(kv.Value))
                    {
                        string what = null;
                        if (!string.IsNullOrEmpty(c.ResolveHint)) keys.TryGetValue(c.ResolveHint, out what);
                        if (what != null) Resolve(kv.Key, c, what, game);
                    }
                }
            }
            var holders = d.Minds.Keys.OrderBy(k => k).ToList();
            foreach (var spec in ad.ConcernBirths(e, holders, Loved)) Birth(spec, game);
        }

        private static int ToInt(JToken t)
        {
            if (t.Type == JTokenType.Float) return (int)t.Value<double>();
            if (t.Type == JTokenType.String) return int.Parse(Sanitize.PyStrip((string)t), CultureInfo.InvariantCulture);
            return (int)t.Value<long>();
        }

        public Concern Birth(JObject spec, long game)
        {
            int aid = ToInt(spec["actor"]);
            Mind mind;
            if (!d.Minds.TryGetValue(aid, out mind)) return null;
            var about = new List<int>();
            if (spec["about"] is JArray ja) foreach (var a in ja) about.Add(ToInt(a));
            string nm = null;
            if (about.Count > 0) { string v; d.Names.TryGetValue(about[0], out v); nm = v ?? "someone"; }
            string who = about.Count > 0 ? GameData.Name(nm) : "someone";
            string text = PyJson.Str(spec["text"]).Replace("{who}", who);
            var minors = d.MinorIds();
            if (AgeGate.Violation(text, d.IsMinor(aid) || about.Any(minors.Contains), d.MinorTokens()))
            {
                d.Gate.SkippedEvents += 1;
                return null;
            }
            double imp = spec["importance"] == null ? 0.5 : spec["importance"].Value<double>();
            var c = ConcernOps.AddConcern(mind, text, about, spec["kind"] == null ? "" : PyJson.Str(spec["kind"]),
                spec["hint"] == null ? "" : PyJson.Str(spec["hint"]), imp, game, d.Cfg.ConcernsMax);
            if (c == null) return null;
            Born++;
            d.Dirty.Add(aid);
            if (d.Cfg.InnerMonologue) Inner.AddInner(mind, c.Text, "concern", game, d.Cfg.InnerStreamKeep);
            return c;
        }

        // ---- closing --------------------------------------------------------
        /// <summary>Resolved: payoff line, chronicle note, kept a day, then dropped by upkeep.</summary>
        public void Resolve(int aid, Concern c, string what, long game)
        {
            ConcernOps.CloseConcern(c, game, "resolved", what);
            Resolved++;
            d.Dirty.Add(aid);
            string v;
            d.Names.TryGetValue(aid, out v);
            string name = GameData.Name(v ?? "Someone");
            d.Chronicle.Note(game, name + "'s worry ended: " + GameData.Clean(what, "event"), 0.3, false);
            if (d.Cfg.ConcernPayoff)
            {
                string trig = "Your worry is over: " + GameData.Clean(c.Text, "line") + " — " + GameData.Clean(what, "event");
                var now = d.Now();
                var item = d.Leases.MakeItem(aid, "concern_resolved", now, null, null, Cls.Reactive,
                    new JObject { ["trigger"] = trig, ["concern"] = c.Id }, 0.3);
                if (d.Leases.Submit(item, now, d.Bridge.Env.Speed) == "queued") Payoffs++;
            }
        }

        /// <summary>Daily: fade the old, drop what the diary no longer needs.</summary>
        public void Upkeep()
        {
            if (!On) return;
            long game = d.Bridge.Env.Game;
            long fadeU = (long)(d.Cfg.ConcernFadeDays * d.Day), keepU = (long)(d.Cfg.ConcernKeepDays * d.Day);
            foreach (var kv in d.Minds.ToList())
            {
                int n = ConcernOps.FadeOld(kv.Value, game, fadeU).Count;
                Faded += n;
                if (n > 0 || ConcernOps.DropClosed(kv.Value, game, keepU) > 0) d.Dirty.Add(kv.Key);
            }
        }

        // ---- reflection -----------------------------------------------------
        public void ApplyReflection(int aid, ReflectResult res, long game)
        {
            if (!On) return;
            var mind = d.Minds[aid];
            var byId = new Dictionary<string, Concern>();
            foreach (var c in ConcernOps.OpenConcerns(mind)) byId[c.Id] = c;
            var seen = new HashSet<string>();
            foreach (var cid in res.Resolved)
            {
                if (!seen.Add(cid)) continue;
                Concern c;
                if (byId.TryGetValue(Sanitize.PyStrip(cid), out c)) Resolve(aid, c, "you decided it was behind you", game);
            }
            string text = res.Concern ?? "";
            if (Sanitize.PyStrip(text).Length > 0)
            {
                var c = ConcernOps.AddConcern(mind, text, new int[0], "reflection", "", 0.4, game, d.Cfg.ConcernsMax);
                if (c != null)
                {
                    Born++;
                    d.Dirty.Add(aid);
                    if (d.Cfg.InnerMonologue) Inner.AddInner(mind, c.Text, "concern", game, d.Cfg.InnerStreamKeep);
                }
            }
        }

        /// <summary>Data for the wants prompt ('' when none or off).</summary>
        public string WantsLines(Mind mind)
        {
            if (!On) return "";
            return string.Join("\n", ConcernOps.OpenConcerns(mind).Select(c => "- " + GameData.Clean(c.Text, "line")));
        }
    }
}
