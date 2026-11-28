// Port of agent/rimworldllm_agent/core/dreams.py: Dreams (aliveness item 6): a sleeping actor gets one surreal thought cloud per night.
//
// The mod publishes "sleep_start" (the sleeper its only witness) when an actor falls asleep in a bed. The director hands it to
// DreamBook.Note; the idle runner takes due jobs (Due) and RunDreamAsync writes the fragment with one short LLM call, then sends it
// as POST /thought kind "dream" with a surface line and adds it to the inner stream.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members dreams.py reads.</summary>
    public interface IDreamHost
    {
        IBridge Bridge { get; }
        ILlmClient Llm { get; }
        MindConfig Cfg { get; }
        IGameAdapter Adapter { get; }
        JObject BridgeCfg { get; }
        IDictionary<int, string> Names { get; }
        IDictionary<int, Mind> Minds { get; }
        IDictionary<int, HashSet<string>> NameTokens { get; }
        DreamBook DreamBook { get; }
        /// <summary>d.dropped: (actor, reason) of every idle job that came to nothing.</summary>
        IList<(int Actor, string Reason)> Dropped { get; }
        /// <summary>d.dirty: the actors whose mind must be saved.</summary>
        ISet<int> Dirty { get; }
        /// <summary>d.idle.log: the idle runner's record of what it made.</summary>
        IList<JObject> IdleLog { get; }
        int Day { get; }
        HashSet<string> MinorTokens();
        bool IsMinor(int actorId);
    }

    public sealed class DreamJob
    {
        public int Actor;
        public int Game;          // when the actor fell asleep
        public int Night;
        public DreamJob(int actor, int game, int night) { Actor = actor; Game = game; Night = night; }
    }

    public sealed class DreamBook
    {
        public Dictionary<int, DreamJob> Pending = new Dictionary<int, DreamJob>();
        public Dictionary<int, Dictionary<int, int>> Dreamt = new Dictionary<int, Dictionary<int, int>>();     // actor -> night -> dreams already made

        /// <summary>Queue a dream for the sleeper of a sleep_start event. Returns the actors queued.</summary>
        public List<int> Note(JObject e, IDreamHost d, int nowGame)
        {
            if (JobsJson.Str(e, "type", null) != Dreams.SleepEvent || !Dreams.Enabled(d)) return new List<int>();
            JToken g = JobsJson.Get(e, "game");
            int game = g != null ? (int)JobsJson.PyInt(g) : nowGame;
            int night = Dreams.NightOf(game, d.Day);
            var o = new List<int>();
            JToken actors = JobsJson.Get(e, "actors");
            if (actors == null) return o;
            foreach (var a in (IEnumerable<JToken>)actors)
            {
                int aid = (int)JobsJson.PyInt(a["id"]);
                if (!d.Minds.ContainsKey(aid) || Pending.ContainsKey(aid)) continue;
                if (!Dreamt.TryGetValue(aid, out var made)) Dreamt[aid] = made = new Dictionary<int, int>();
                made.TryGetValue(night, out int count);
                if (count >= d.Cfg.DreamsPerNight) continue;
                made[night] = count + 1;                       // the night's one roll is spent whether or not it dreams
                foreach (var old in made.Keys.Where(n => n < night - 2).ToList()) made.Remove(old);
                if (Dreams.Roll(aid, night) >= d.Cfg.DreamChance) continue;
                Pending[aid] = new DreamJob(aid, game, night);
                o.Add(aid);
            }
            return o;
        }

        public bool Stale(DreamJob job, int nowGame, IDreamHost d)
        {
            return nowGame - job.Game > d.Cfg.DreamMaxAgeGame || nowGame < job.Game;
        }

        /// <summary>Jobs worth starting, oldest first; stale ones are dropped here.</summary>
        public List<DreamJob> Due(IDreamHost d)
        {
            int now = d.Bridge.Env.Game;
            var o = new List<DreamJob>();
            foreach (var kv in Pending.OrderBy(kv => kv.Value.Game).ThenBy(kv => kv.Key).ToList())
            {
                int aid = kv.Key;
                var job = kv.Value;
                if (Stale(job, now, d)) { Pending.Remove(aid); d.Dropped.Add((aid, "dream:late")); }
                else if (Dreams.DreamMemories(d, aid).Count > 0) o.Add(job);
                else { Pending.Remove(aid); d.Dropped.Add((aid, "dream:no_memories")); }
            }
            return o;
        }
    }

    public static class Dreams
    {
        public const string SleepEvent = "sleep_start";
        public const string DreamKind = "dream";
        public const int DreamMinWords = 3;
        public const int DreamMaxWords = 16;
        public const int DreamMemoriesN = 3;
        public const int DreamThoughts = 2;

        public static bool Enabled(IDreamHost d)
        {
            return d.Cfg.Dreams && !JobsJson.IsFalse(JobsJson.Get(d.BridgeCfg, "dreams")) && JobsJson.EventNotOff(d.BridgeCfg, "sleep");
        }

        /// <summary>Nights run 18:00 to 18:00, so a sleep that crosses midnight is one night.</summary>
        public static int NightOf(int game, int day) { return (int)JobsJson.FloorDiv(game + JobsJson.FloorDiv(day, 4), day); }

        /// <summary>int(sha1("dream:{aid}:{night}").hexdigest(), 16) % 1000 / 1000</summary>
        public static double Roll(int aid, int night)
        {
            byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes("dream:" + aid.ToString(CultureInfo.InvariantCulture) + ":" + night.ToString(CultureInfo.InvariantCulture)));
            var big = new BigInteger(h, isUnsigned: true, isBigEndian: true);
            return (double)(int)(big % 1000) / 1000;
        }

        /// <summary>Today's (and yesterday evening's) public memories, heaviest first: a private one never reaches the map.</summary>
        public static List<Memory> DreamMemories(IDreamHost d, int aid)
        {
            var mind = d.Minds[aid];
            int now = d.Bridge.Env.Game;
            long floor = now - d.Day - JobsJson.FloorDiv(d.Day, 4);
            return MemoryOps.WeightSorted(mind, now, d.Day).Where(m => m.Game >= floor && m.Visibility == "public" && !(d.Cfg.SpeechChecks && MemoryOps.IsRoutineSocial(m, d.Adapter))).Take(DreamMemoriesN).ToList();
        }

        private static bool Asleep(JObject snap)
        {
            return !JobsJson.IsFalse(JobsJson.Get(snap, "asleep"));      // an older mod without the field: the delay is the guard
        }

        // inner.recent_inner / thoughts_text / add_inner (the inner worker's module; the three small helpers dreams.py needs)
        private static List<InnerThought> RecentInner(Mind mind, int n)
        {
            var es = mind.Inner;
            return n > 0 ? es.Skip(Math.Max(0, es.Count - n)).ToList() : new List<InnerThought>();
        }

        private static string ThoughtsText(IEnumerable<InnerThought> entries)
        {
            return string.Join("\n", entries.Select(t => "- " + GameData.Clean(t.Text, "line")));
        }

        private static void AddInner(Mind mind, string text, string kind, long game, int keep)
        {
            mind.Inner.Add(new InnerThought { Game = game, Text = text, Kind = kind });
            if (keep > 0 && mind.Inner.Count > keep) mind.Inner = mind.Inner.Skip(mind.Inner.Count - keep).ToList();
        }

        /// <summary>Write and send one dream. Returns the text, or null when nothing was sent (the reason goes to d.Dropped).</summary>
        public static async Task<string> RunDreamAsync(IDreamHost d, DreamJob job, Lease lease)
        {
            int aid = job.Actor;
            var mind = d.Minds[aid];
            d.DreamBook.Pending.Remove(aid);
            JObject snap;
            try { snap = await d.Bridge.ActorAsync(aid).ConfigureAwait(false); }
            catch (BridgeError) { return null; }
            if (!Asleep(snap)) { d.Dropped.Add((aid, "dream:woke")); return null; }
            var mems = DreamMemories(d, aid);
            if (mems.Count == 0) return null;
            var stream = d.Cfg.InnerMonologue ? RecentInner(mind, DreamThoughts) : new List<InnerThought>();
            var minors = d.MinorTokens();
            bool involvesMinor = d.IsMinor(aid) || mems.Any(m => m.People.Any(p => d.IsMinor(p)));
            string memLines = string.Join("\n", mems.Select(m => "- " + GameData.Clean(m.Text, "event")));
            string words = await Speech.RegisterPhraseAsync(d.Adapter, d.Bridge, aid, mind, snap).ConfigureAwait(false);
            string user = "=== WHO YOU ARE ===\n" + GameData.DataSection("bio", GameData.Clean(mind.Fixed.Text, "line", PromptBlocks.BioLineCap, true))
                + "=== WHAT HAPPENED TODAY ===\n" + GameData.DataSection("memories", memLines)
                + (stream.Count > 0 ? "=== WHAT YOU WERE THINKING ===\n" + GameData.DataSection("thoughts", ThoughtsText(stream)) : "")
                + "=== REQUEST ===\n" + Speech.RegisterLine(mind.Name, words) + d.Adapter.DreamRequest;
            string system = d.Adapter.DreamSystem + "\n\n" + GameData.DataRule + (involvesMinor ? "\n\n" + AgeGate.MinorRule : "");
            lease.Generating = true;
            Completion c;
            try
            {
                c = await d.Llm.CompleteAsync(new List<LlmMessage> { new LlmMessage("system", system), new LlmMessage("user", user) }, lease.Slot,
                    DreamKind, d.Cfg.DreamMaxTokens, new[] { "\n" }, null, 1.0, aid).ConfigureAwait(false);
            }
            catch (LlmError)
            {
                lease.Generating = false;
                return null;
            }
            lease.Generating = false;
            if (lease.Preempted) return null;
            var toks = d.NameTokens;
            var allnames = JobsJson.Union(toks.Values);
            var known = new HashSet<string>(toks.TryGetValue(aid, out var mine) ? mine : new HashSet<string>());
            foreach (var m in mems) foreach (var p in m.People) if (toks.TryGetValue(p, out var pt)) known.UnionWith(pt);
            var speaker = toks.TryGetValue(aid, out var sp) ? sp : new HashSet<string>();
            var s = Sanitize.SanitizeProse(c.Text, known, allnames, Sanitize.PromptCapWords(user), new HashSet<string>(d.Adapter.CommonWords), speaker,
                DreamMinWords, DreamMaxWords, involvesMinor, minors);
            string text = s.Ok ? s.Text : null;
            if (text != null && (PyStr.Len(text) > d.Cfg.ThoughtMaxChars || AgeGate.Violation(text, involvesMinor, minors))) text = null;
            if (text == null)
            {
                d.Dropped.Add((aid, "dream:" + (!s.Ok ? s.Reason : "rejected")));
                return null;
            }
            try                                                  // the actor may have woken while the model wrote
            {
                if (!Asleep(await d.Bridge.ActorAsync(aid).ConfigureAwait(false))) { d.Dropped.Add((aid, "dream:woke")); return null; }
            }
            catch (BridgeError) { return null; }
            int now = d.Bridge.Env.Game;
            if (d.Cfg.InnerMonologue)
            {
                AddInner(mind, text, DreamKind, now, d.Cfg.InnerStreamKeep);
                d.Dirty.Add(aid);
            }
            d.IdleLog.Add(new JObject { ["kind"] = "dream", ["actor"] = aid, ["name"] = d.Names.TryGetValue(aid, out var nm) ? (JToken)nm : JValue.CreateNull(), ["game"] = now, ["text"] = text });
            try { await d.Bridge.ThoughtAsync(aid, text, null, DreamKind, text).ConfigureAwait(false); }
            catch (BridgeError e) { LlmLog.Warning("rimworldllm", "dream refused for " + aid + ": " + e.Message); }
            return text;
        }
    }
}
