// Port of agent/rimworldllm_agent/core/memory.py: witness-only memory, recall scoring, upkeep, gossip and confiding.
// The pydantic model class `Memory` lives in Minds.cs; the module functions are in MemoryOps.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>What remember_event needs from the game adapter (importance comes from game numbers, never a model).</summary>
    public interface IMemoryAdapter
    {
        double Importance(JObject ev, int pid);
        string FirstPerson(JObject ev, int pid);
        bool IsPrivate(JObject ev, int pid);
        bool IsPinned(JObject ev, int pid, List<string> firsts);
    }

    public class UpkeepResult
    {
        public int Merged;
        public int Dropped;
    }

    public static class MemoryOps
    {
        public const double GossipConfidence = 0.5;
        public const double ConfideConfidence = 0.7;

        private static readonly Regex Word = new Regex("[a-z']{3,}", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Stop = new HashSet<string> {
            "the", "and", "was", "with", "that", "his", "her", "for", "from", "into", "has", "had",
            "were", "are", "you", "your", "this", "then", "they", "them", "its", "who", "but", "not" };

        public static HashSet<string> Keywords(string text)
        {
            var r = new HashSet<string>();
            foreach (Match m in Word.Matches(text.ToLowerInvariant()))
                if (!Stop.Contains(m.Value)) r.Add(m.Value);
            return r;
        }

        private static int Inter<T>(ISet<T> a, ISet<T> b) { int n = 0; foreach (var x in a) if (b.Contains(x)) n++; return n; }

        /// <summary>Write a memory into each witness's mind (and only theirs). Returns what was written.</summary>
        public static List<(int, Memory)> RememberEvent(IDictionary<int, Mind> minds, JObject ev, IMemoryAdapter adapter, long? nowTime = null)
        {
            var written = new List<(int, Memory)>();
            JToken gameTok = ev["game"];
            long game = gameTok != null ? (long)gameTok : (nowTime ?? 0);
            var people = new List<int>();
            var actors = ev["actors"] as JArray;
            if (actors != null) foreach (var a in actors) people.Add((int)a["id"]);
            JToken seqTok = ev["seq"];
            bool hasSeq = seqTok != null && seqTok.Type != JTokenType.Null;
            long seq = hasSeq ? (long)seqTok : 0;
            var wit = ev["witnesses"] as JArray;
            if (wit == null) return written;
            foreach (var w in wit)
            {
                int pid = (int)w;
                Mind mind;
                if (!minds.TryGetValue(pid, out mind) || mind == null) continue;
                if (hasSeq && mind.Memories.Any(m => m.EventSeq == seq)) continue;          // idempotent per event
                double imp = Math.Max(0.0, Math.Min(1.0, adapter.Importance(ev, pid)));
                var typeTok = ev["type"];
                var mem = new Memory
                {
                    Id = mind.NextMemId(), Game = game, Text = adapter.FirstPerson(ev, pid),
                    Source = "saw", Confidence = 1.0, Importance = imp,
                    Visibility = adapter.IsPrivate(ev, pid) ? "private" : "public",
                    Pinned = adapter.IsPinned(ev, pid, mind.Firsts), People = people,
                    Etype = typeTok == null ? "" : (string)typeTok, EventSeq = hasSeq ? seq : (long?)null,
                };
                mind.Memories.Add(mem);
                if (mem.Etype != "" && !mind.Firsts.Contains(mem.Etype)) mind.Firsts.Add(mem.Etype);
                mind.UnreflectedImportance += imp;
                written.Add((pid, mem));
            }
            return written;
        }

        /// <summary>True for a memory of routine social contact with no outcome. A told memory (gossip, a confidence) is never routine.</summary>
        public static bool IsRoutineSocial(Memory m, IGameAdapter adapter)
        {
            return adapter != null && m.Source != "told" && adapter.RoutineSocial(m.Etype ?? "", m.Text ?? "");
        }

        /// <summary>Gossip or confiding: lower confidence, source traceable to the teller.</summary>
        public static Memory RememberTold(Mind mind, int tellerId, string tellerName, string text, long game,
                                          double importance = 0.3, List<int> people = null)
        {
            var mem = new Memory
            {
                Id = mind.NextMemId(), Game = game, Text = text, Source = "told", ToldBy = tellerName, ToldById = tellerId,
                Confidence = 0.6, Visibility = "public", Importance = importance,
                People = (people != null && people.Count > 0) ? people : new List<int> { tellerId }, Etype = "told",
            };
            mind.Memories.Add(mem);
            return mem;
        }

        public static double ScoreMemory(Mind mind, Memory m, long nowTime, ISet<int> scenePeople, ISet<string> sceneWords,
                                         int day = Mind.DefaultDayUnits)
        {
            double ageDays = (double)Math.Max(0L, nowTime - m.Game) / day;
            double recency = m.Pinned ? 1.0 : Math.Pow(0.5, ageDays / Mind.HalfLifeDays);
            double ent = 0.0;
            if (scenePeople.Count > 0)
            {
                var mp = new HashSet<int>(m.People);
                ent = (double)Inter(scenePeople, mp) / Math.Max(1, scenePeople.Count);
            }
            var mw = Keywords(m.Text);
            double kw = (sceneWords.Count > 0 && mw.Count > 0)
                ? (double)Inter(sceneWords, mw) / Math.Max(1, Math.Min(sceneWords.Count, mw.Count)) : 0.0;
            double relevance = Math.Min(1.0, ent + kw);
            return (0.30 * recency + 0.40 * m.Importance * m.Confidence + 0.30 * relevance);
        }

        /// <summary>Top 2-4: always k_min if available, up to k_max while the score clears the floor.</summary>
        public static List<Memory> SelectMemories(Mind mind, long nowTime, ISet<int> scenePeople, string sceneText,
                                                  int day = Mind.DefaultDayUnits, int kMin = 2, int kMax = 4, double floor = 0.25)
        {
            var words = Keywords(sceneText);
            var scored = mind.Memories.Select(m => new { s = ScoreMemory(mind, m, nowTime, scenePeople, words, day), m })
                .OrderBy(x => -x.s).ThenBy(x => -x.m.Game).ThenBy(x => x.m.Id, StringComparer.Ordinal).ToList();
            var chosen = new List<Memory>();
            int i = 0;
            foreach (var x in scored.Take(Math.Max(0, kMax)))
            {
                if (i < kMin || x.s >= floor) chosen.Add(x.m);
                i++;
            }
            return chosen.OrderBy(m => m.Game).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();   // stable order for the prompt
        }

        /// <summary>Memories, heaviest (decayed importance, pinned first) first.</summary>
        public static List<Memory> WeightSorted(Mind mind, long nowTime, int day = Mind.DefaultDayUnits)
        {
            return mind.Memories.OrderBy(m => -mind.MemoryWeight(m, nowTime, day)).ThenBy(m => -m.Game)
                .ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        // ---- upkeep: merge, decay, pin ------------------------------------------------------------
        /// <summary>Shared actors (Jaccard) weighted 0.4 plus word overlap (Jaccard) weighted 0.6.</summary>
        public static double Similarity(Memory a, Memory b)
        {
            var pa = new HashSet<int>(a.People); var pb = new HashSet<int>(b.People);
            var pu = new HashSet<int>(pa); pu.UnionWith(pb);
            double pj = pu.Count > 0 ? (double)Inter(pa, pb) / pu.Count : 0.0;
            var wa = Keywords(a.Text); var wb = Keywords(b.Text);
            var wu = new HashSet<string>(wa); wu.UnionWith(wb);
            double wj = wu.Count > 0 ? (double)Inter(wa, wb) / wu.Count : 0.0;
            return 0.4 * pj + 0.6 * wj;
        }

        /// <summary>Merge clusters of similar old ordinary memories into one summary line, then drop the originals.
        /// Returns how many were folded.</summary>
        public static int MergeOldMemories(Mind mind, long nowTime, double olderThanDays = 8.0,
                                           int day = Mind.DefaultDayUnits, double threshold = 0.5, int minGroup = 3)
        {
            double cutoff = nowTime - olderThanDays * day;
            var pool = mind.Memories.Where(m => !m.Pinned && m.Game < cutoff && m.SummaryOf == 0 && m.Visibility == "public")
                .OrderBy(m => m.Game).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            var clusters = new List<List<Memory>>();
            foreach (var m in pool)
            {
                List<Memory> best = null; double bestKey = 0;
                foreach (var c in clusters)
                {
                    double k = c.Min(x => Similarity(m, x));
                    if (best == null || k > bestKey) { best = c; bestKey = k; }
                }
                if (best != null && best.Min(x => Similarity(m, x)) >= threshold) best.Add(m);
                else clusters.Add(new List<Memory> { m });
            }
            int merged = 0;
            foreach (var ms in clusters)
            {
                if (ms.Count < minGroup) continue;
                foreach (var m in ms) mind.Memories.Remove(m);
                Memory top = ms[0];
                foreach (var m in ms) if (m.Importance > top.Importance) top = m;
                string et = ms.Where(m => m.Etype != "").Select(m => m.Etype).FirstOrDefault() ?? "";
                string what = (et != "" && ms.All(m => m.Etype == et)) ? et.Replace("_", " ") : "similar";
                var people = new SortedSet<int>(ms.SelectMany(m => m.People));
                mind.Memories.Add(new Memory
                {
                    Id = mind.NextMemId(), Game = ms.Max(m => m.Game),
                    Text = "Over those days there were " + ms.Count.ToString(CultureInfo.InvariantCulture) + " " + what + " moments; the worst: " + top.Text,
                    Importance = top.Importance * 0.8, People = people.ToList(),
                    Etype = what != "similar" ? et : "summary", SummaryOf = ms.Count,
                });
                merged += ms.Count;
            }
            return merged;
        }

        /// <summary>Drop unpinned memories whose decayed weight fell under the floor.</summary>
        public static int DropDecayed(Mind mind, long nowTime, double floor = 0.03, int day = Mind.DefaultDayUnits)
        {
            var dead = mind.Memories.Where(m => !m.Pinned && mind.MemoryWeight(m, nowTime, day) < floor).ToList();
            foreach (var m in dead) mind.Memories.Remove(m);
            return dead.Count;
        }

        /// <summary>Merge similar old memories, then drop the decayed ones. Deterministic, no model call.</summary>
        public static UpkeepResult Upkeep(Mind mind, long nowTime, int day = Mind.DefaultDayUnits, double floor = 0.03,
                                          double threshold = 0.5)
        {
            int merged = MergeOldMemories(mind, nowTime, day: day, threshold: threshold);
            int dropped = DropDecayed(mind, nowTime, floor, day);
            return new UpkeepResult { Merged = merged, Dropped = dropped };
        }

        // ---- gossip, confiding and secrets --------------------------------------------------------
        /// <summary>The speaker's own memory a told line most likely draws on. With allowTold (gossip chains) a memory the
        /// speaker was itself told can be retold, but never a private one.</summary>
        private static Memory SourceMemory(Mind speaker, string line, int? about, bool priv, bool allowTold = false)
        {
            var words = Keywords(line);
            Memory best = null; double s0 = -1.0, s1 = -1.0;
            foreach (var m in speaker.Memories)
            {
                if (m.Source == "told" && !priv && !allowTold) continue;
                if (allowTold && !priv && m.Visibility == "private") continue;
                if (priv && m.Visibility != "private") continue;
                if (about.HasValue && !priv && !m.People.Contains(about.Value)) continue;
                double a = (double)Inter(words, Keywords(m.Text)) / Math.Max(1, words.Count);
                double b = m.Importance;
                if (a > s0 || (a == s0 && b > s1)) { best = m; s0 = a; s1 = b; }
            }
            return best;
        }

        /// <summary>A gossip (public) or confide (private) line the game applied. See memory.py record_told_line.</summary>
        public static Memory RecordToldLine(IDictionary<int, Mind> minds, int speaker, int listener, IDictionary<int, string> names,
                                            string line, long game, string visibility, int? about = null, bool chain = false,
                                            double hopDecay = 0.8, ISet<int> minorIds = null)
        {
            Mind lm;
            if (!minds.TryGetValue(listener, out lm) || lm == null) return null;
            if (minorIds != null && minorIds.Count > 0
                && (minorIds.Contains(speaker) || minorIds.Contains(listener) || (about.HasValue && minorIds.Contains(about.Value)))
                && AgeGate.Romantic(line))
                return null;
            string sname, lname;
            if (!names.TryGetValue(speaker, out sname)) sname = "someone";
            if (!names.TryGetValue(listener, out lname)) lname = "someone";
            bool priv = visibility == "private";
            Mind sm; minds.TryGetValue(speaker, out sm);
            bool useChain = chain && !priv;
            Memory src = sm != null ? SourceMemory(sm, line, about, priv, useChain) : null;
            double conf = (priv ? ConfideConfidence : GossipConfidence) * (src != null ? src.Confidence : 1.0);
            var people = new List<int> { speaker };
            if (about.HasValue) people.Add(about.Value);
            if (src != null)
            {
                if (!src.ToldTo.Contains(lname)) src.ToldTo.Add(lname);
                if (priv && src.Visibility == "private") src.Visibility = "told";
            }
            var chainNames = new List<string>();
            int hops = 0; long? seq = null;
            if (useChain)
            {
                int srcHops = src != null ? (src.Hops != 0 ? src.Hops : (src.Source == "told" ? 1 : 0)) : 0;
                List<string> baseChain;
                if (src != null && src.Chain.Count > 0) baseChain = new List<string>(src.Chain);
                else if (src != null && !string.IsNullOrEmpty(src.ToldBy)) baseChain = new List<string> { src.ToldBy };
                else baseChain = new List<string>();
                chainNames = new List<string>(baseChain) { sname };
                hops = srcHops + 1;
                conf *= Math.Pow(hopDecay, srcHops);
                seq = src != null ? src.EventSeq : null;
                if (chainNames.Contains(lname)) return null;       // it came from this listener in the first place
                Memory old = null;
                foreach (var m in lm.Memories)
                    if (seq.HasValue && m.EventSeq == seq && m.Source != "saw") { old = m; break; }
                if (old != null)                                    // (a witness still gets the teller's version beside their own)
                {
                    old.Sources += 1;
                    if (conf > old.Confidence)
                    {
                        old.Text = sname + " told me: " + line;
                        old.Confidence = PyFmt.Round(conf, 3);
                        old.ToldBy = sname; old.ToldById = speaker; old.Chain = chainNames; old.Hops = hops;
                        if (src != null) old.Importance = Math.Max(old.Importance, Math.Max(0.3, 0.6 * src.Importance));
                    }
                    return old;
                }
            }
            var mem = RememberTold(lm, speaker, sname, sname + " told me: " + line, game,
                                   src != null ? Math.Max(0.3, 0.6 * src.Importance) : 0.3, people);
            mem.Confidence = PyFmt.Round(conf, 3); mem.Visibility = visibility;
            if (useChain) { mem.Chain = chainNames; mem.Hops = hops; mem.EventSeq = seq; }
            return mem;
        }

        /// <summary>May this memory be said outright to this listener?</summary>
        public static bool CanState(Memory m, string listenerName)
        {
            if (m.Visibility == "public") return true;
            if (m.Visibility == "told") return listenerName != null && m.ToldTo.Contains(listenerName);
            return false;                                          // private: hint, never state
        }

        /// <summary>Memory texts that must not be quoted to this listener (hinting is fine, quoting is not).</summary>
        public static List<string> SecretTexts(Mind mind, string listenerName)
        {
            return mind.Memories.Where(m => !CanState(m, listenerName)).Select(m => m.Text).ToList();
        }
    }
}
