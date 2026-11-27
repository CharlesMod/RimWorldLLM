// Port of agent/rimworldllm_agent/core/group.py: gathering talk (aliveness item 10): group scenes at meals and parties,
// eulogies, vows and speeches.
//
// The mod sends a "gathering" event (kind meal | party | wedding | funeral | speech | ritual, a lead, the people there) and a
// "gathering_end". Here a scene of up to group_max_speakers speakers runs on the two actor windows: each turn the next
// speaker gets a window (a lease whose prefix is built for that speaker; when a third speaker is due, the window of the one who
// spoke longest ago is released and re-leased, which leans on the server's prompt-cache restore), and block F carries every
// turn so far as heard speech. Lines go through the director's normal path (voice budget, quotas, expiry, the age gate).
// Meals and parties are SOCIAL class (they pause at 4x); ceremonies are REACTIVE. There is no intent pass in a group (the
// label is always "none", so no listener-specific effect) and no aim call (it would double the calls of a scene).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members group.py reads (the Python takes the whole director as d).</summary>
    public interface IGroupHost
    {
        MindConfig Cfg { get; }
        /// <summary>d.bridge_cfg: the mod's settings as the bridge reported them.</summary>
        JObject BridgeCfg { get; }
        IBridge Bridge { get; }
        IGameAdapter Adapter { get; }
        ILlmClient Llm { get; }
        LeaseManager Leases { get; }
        Chronicle Chronicle { get; }
        LoreBook Lore { get; }
        /// <summary>Game units in one day (d.day).</summary>
        long Day { get; }
        /// <summary>actor id -> name (d.names).</summary>
        IDictionary<int, string> Names { get; }
        /// <summary>actor id -> lower-cased-on-use name tokens (d.name_tokens).</summary>
        IDictionary<int, HashSet<string>> NameTokens { get; }
        Dictionary<int, Mind> Minds { get; }
        /// <summary>actors whose mind changed (d.dirty).</summary>
        ISet<int> Dirty { get; }
        /// <summary>d.dropped: (actor, reason) trail of dropped lines.</summary>
        IList<(int Actor, string Reason)> Dropped { get; }
        GateStats Gate { get; }
        /// <summary>d.weights: talk weight per actor.</summary>
        IDictionary<int, double> Weights { get; }
        /// <summary>d.line_count: lines spoken per actor.</summary>
        IDictionary<int, int> LineCount { get; }
        /// <summary>d.group: this module's per-director bookkeeping.</summary>
        GroupState Group { get; }
        /// <summary>d._conv_n: the conversation/scene counter (get and set).</summary>
        int ConvN { get; set; }
        /// <summary>The creative view (creative.on / creative.compose take the director as d).</summary>
        ICreativeHost Creative { get; }
        /// <summary>The grounding view (grounding.actor_card / other_reactive_waiting take the director as d).</summary>
        IGroundingHost Ground { get; }

        Now Now();
        bool IsMinor(int actorId);
        Task LearnAgesAsync(IEnumerable<int> ids);
        /// <summary>d._prepare: (ctx, scene, sig).</summary>
        Task<(SanitizeCtx Ctx, JObject Scene, string Sig)> PrepareAsync(int aid, string trigger, JObject ev, Lease lease, int? convWith = null);
        /// <summary>d._say: false when the game's liveness check refused (gone or out of range).</summary>
        Task<bool> SayAsync(int aid, Spoken sp, int? listener, int? expires, string conv = null, string talkId = null,
            bool combat = false, JObject ev = null, string vanillaRef = null, bool minor = false);
        Task PaceAsync(double lastReal, IEnumerable<Lease> leases);
        /// <summary>d._repeat_check. Python passes the Gathering, but only its turns are read, so the C# takes the turns.
        /// (null, null) when the repeat guard is off.</summary>
        (LineCheck Check, Action<CheckEvent> OnCheck) RepeatCheck(IList<Turn> turns, int speaker);
        void RememberOwn(int aid, string text);
        IList<string> LabelsFor(bool minor);
        /// <summary>d.wire_expiry: expires_tick for the mod (the item's deadline plus half a second of slack), or null.</summary>
        int? WireExpiry(WorkItem item);
    }

    public class Gathering
    {
        public string Id;
        public string Kind;
        public string Key;
        public int? Lead;
        public List<int> Couple;
        public int? Subject;
        /// <summary>everyone there that we hold a mind for</summary>
        public List<int> Pool;
        /// <summary>speaking order, at most group_max_speakers</summary>
        public List<int> Speakers;
        public Dictionary<int, string> Names;
        public string Label = "";
        public string Ideo = "";
        public string Room = "";
        public string Deceased = "";
        public string Story = "";
        /// <summary>a minor is among those there: every speaker carries the minor rule</summary>
        public bool Minor;
        public List<Turn> Turns = new List<Turn>();
        public string Ended = "";
        public int Cap;

        public Gathering(string id, string kind, string key, int? lead, List<int> couple, int? subject, List<int> pool,
            List<int> speakers, Dictionary<int, string> names, string label = "", string ideo = "", string room = "",
            string deceased = "", string story = "")
        {
            Id = id; Kind = kind; Key = key; Lead = lead; Couple = couple; Subject = subject; Pool = pool; Speakers = speakers;
            Names = names; Label = label; Ideo = ideo; Room = room; Deceased = deceased; Story = story;
        }
    }

    /// <summary>Per-director bookkeeping and counters (debug view and tests).</summary>
    public class GroupState
    {
        /// <summary>gathering keys already turned into a scene</summary>
        public HashSet<string> Seen = new HashSet<string>();
        /// <summary>keys whose gathering_end arrived</summary>
        public HashSet<string> Over = new HashSet<string>();
        /// <summary>story text -> game time it was last told</summary>
        public Dictionary<string, long> Told = new Dictionary<string, long>();
        public int Started;
        public int Skipped;
        public int Lines;
        public int Rotations;
        public Dictionary<string, int> Ends = new Dictionary<string, int>();
        public List<JObject> Log = new List<JObject>();
    }

    public static class Group
    {
        public static readonly string[] Kinds = { "meal", "party", "wedding", "funeral", "speech", "ritual" };
        public static readonly HashSet<string> Ceremonies = new HashSet<string> { "wedding", "funeral", "speech", "ritual" };
        /// <summary>turns at 1x, 2x, 3x, 4x</summary>
        public static readonly int[] CeremonyCaps = { 3, 3, 2, 1 };
        public const int StoryChars = 170;
        public const double StoryRepeatDays = 1.0;
        public static readonly Dictionary<string, string> Setting = new Dictionary<string, string>
        {
            ["meal"] = "a shared meal", ["party"] = "a party", ["wedding"] = "a wedding", ["funeral"] = "a funeral",
            ["speech"] = "a speech", ["ritual"] = "a ritual",
        };

        // Python \s (str.isspace) is wider than .NET \s: spell it out as Conversation does.
        private const string Ws = "[\\t-\\r\\u001c-\\u001f \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]";
        private static readonly Regex SentenceSplit = new Regex("(?<=[.!?])" + Ws + "+", RegexOptions.CultureInvariant);

        private const string RequestMark = "=== REQUEST ===";

        public static Cls SceneClass(string kind) { return Ceremonies.Contains(kind) ? Cls.Reactive : Cls.Social; }

        public static int Tier(double rate) { return rate <= 60 ? 0 : rate <= 180 ? 1 : rate <= 360 ? 2 : 3; }

        /// <summary>Turns for a scene at this speed. Meals and parties follow group_turns (0 at 4x: no scene); a ceremony always
        /// keeps its lead's line and never has more turns than speakers.</summary>
        public static int TurnCap(string kind, double rate, IList<int> groupTurns, int nSpeakers)
        {
            int t = Tier(rate);
            int bas = (groupTurns != null && groupTurns.Count > 0) ? groupTurns[Math.Min(t, groupTurns.Count - 1)] : 0;
            if (Ceremonies.Contains(kind))
                return Math.Min(nSpeakers, Math.Max(1, Math.Min(bas, CeremonyCaps[t])));
            return bas;
        }

        /// <summary>The payload a scene's work item carries (Python: {"gathering": g}); the object rides in a JRaw.</summary>
        public static JObject PayloadOf(Gathering g) { return new JObject { ["gathering"] = new JRaw(g) }; }

        public static Gathering GatheringOf(WorkItem item) { return (Gathering)((JValue)item.Payload["gathering"]).Value; }

        // ---- picking ---------------------------------------------------------------------
        internal static List<string> Sentences(string text)
        {
            var o = new List<string>();
            foreach (var part in SentenceSplit.Split(text))
            {
                var s = PyStr.Strip(part);
                if (PyStr.Len(s) > 20) o.Add(s);
            }
            return o;
        }

        /// <summary>A chronicle sentence or lore entry that names people at this gathering: (text, the one it names most, or null).
        /// Two or more of them beats one; lore names count for a little extra; one told in the last day is not told again.
        /// Null when there is none.</summary>
        public static (string Text, int? Opener)? PickStory(IGroupHost d, IList<int> ids, long nowGame)
        {
            var toks = new Dictionary<int, HashSet<string>>();
            var order0 = new List<int>();
            foreach (var i in ids)
            {
                if (!toks.ContainsKey(i)) order0.Add(i);
                HashSet<string> src;
                var set = new HashSet<string>();
                if (d.NameTokens.TryGetValue(i, out src)) foreach (var t in src) set.Add(t.ToLowerInvariant());
                toks[i] = set;
            }
            var cands = new List<(int Score, int Order, string Text)>();
            var st = d.Chronicle.State;
            int order = 0;
            var texts = st.Recent.Select(e => e.Text).Concat(st.Pending.Select(n => n.Text)).ToList();
            foreach (var t in texts)
            {
                foreach (var s in Sentences(t))
                {
                    order += 1;
                    var kw = MemoryOps.Keywords(s);
                    int named = order0.Count(i => toks[i].Overlaps(kw));
                    if (named > 0) cands.Add((2 * named, order, s));
                }
            }
            foreach (var e in d.Lore.Entries)
            {
                order += 1;
                var kw = MemoryOps.Keywords(e.Text);
                int named = order0.Count(i => toks[i].Overlaps(kw));
                cands.Add((1 + 2 * named, order, e.Text));
            }
            long window = (long)(StoryRepeatDays * d.Day);
            cands = cands.Where(c =>
            {
                long told;
                if (!d.Group.Told.TryGetValue(c.Text, out told)) told = -1000000000L;
                return nowGame - told >= window && c.Score >= 3;
            }).ToList();
            if (cands.Count == 0) return null;
            var top = cands.OrderByDescending(c => c.Score).ThenByDescending(c => c.Order).First();
            string text = GameData.Clean(top.Text, "event", StoryChars);
            var tkw = MemoryOps.Keywords(text);
            var counts = new Dictionary<int, int>();
            foreach (var i in order0) counts[i] = toks[i].Count(x => tkw.Contains(x));
            int best = order0.OrderByDescending(i => counts[i]).ThenBy(i => i).First();   // max(counts, key=(counts[i], -i))
            return (text, counts[best] > 0 ? (int?)best : null);
        }

        /// <summary>Speaking order: a wedding starts with the couple, other ceremonies with their lead, a meal or party with whoever the
        /// story is about (else the host or the most talkative); the rest follow by talk weight, warmer toward the first first.</summary>
        public static List<int> PlanSpeakers(string kind, int? lead, IList<int> couple, IList<int> pool, IDictionary<int, Mind> minds,
            IDictionary<int, double> weights, int limit, int? opener = null)
        {
            var pl = pool.Distinct().ToList();                                      // dict.fromkeys keeps the first of each
            var first = new List<int>();
            if (kind == "wedding") first = couple.Where(c => pl.Contains(c)).ToList();
            else if (Ceremonies.Contains(kind)) first = (lead != null && pl.Contains(lead.Value)) ? new List<int> { lead.Value } : new List<int>();
            else if (opener != null && pl.Contains(opener.Value)) first = new List<int> { opener.Value };
            else if (kind == "party" && lead != null && pl.Contains(lead.Value)) first = new List<int> { lead.Value };

            Func<int, double> score = i =>
            {
                double w;
                if (!weights.TryGetValue(i, out w)) w = 1.0;
                if (first.Count == 0) return w;
                Relation r = null;
                Mind m;
                if (minds.TryGetValue(i, out m)) m.Relations.TryGetValue(first[0].ToString(System.Globalization.CultureInfo.InvariantCulture), out r);
                return w * (1.0 + Math.Max(0, r != null ? (r.Opinion ?? 0) : 0) / 100.0);
            };
            var rest = pl.Where(i => !first.Contains(i)).OrderBy(i => -score(i)).ThenBy(i => i).ToList();
            return first.Concat(rest).Take(Math.Max(1, limit)).ToList();
        }

        /// <summary>Ceremonies follow the plan once; a meal cycles through its speakers. Never the same person twice running.</summary>
        public static int? NextSpeaker(Gathering g)
        {
            int i = g.Turns.Count;
            var sp = g.Speakers;
            if (sp.Count == 0) return null;
            if (Ceremonies.Contains(g.Kind)) return i < sp.Count ? (int?)sp[i] : null;
            int? last = g.Turns.Count > 0 ? (int?)g.Turns[g.Turns.Count - 1].Speaker : null;
            int pick = sp[i % sp.Count];
            if (last != null && pick == last.Value)
            {
                if (sp.Count < 2) return null;
                pick = sp[(i + 1) % sp.Count];
            }
            return pick;
        }

        // ---- prompt text -----------------------------------------------------------------
        private static string Or(string a, string b) { return string.IsNullOrEmpty(a) ? b : a; }

        private static string NameOr(Gathering g, int id, string dflt)
        {
            string n;
            return g.Names.TryGetValue(id, out n) ? n : dflt;
        }

        private static string OthersNames(Gathering g, int me)
        {
            var s = string.Join(", ", g.Pool.Where(i => i != me && g.Names.ContainsKey(i)).Select(i => g.Names[i]));
            return PyStr.Head(s, 90);
        }

        /// <summary>The scene sentence for one speaker (block E's "what just happened"; game text, cleaned by the prompt).</summary>
        public static string Trigger(Gathering g, int me)
        {
            string lead = g.Lead != null ? NameOr(g, g.Lead.Value, "") : "";
            string t;
            if (g.Kind == "wedding" && g.Couple.Count == 2)
            {
                string a = NameOr(g, g.Couple[0], "someone"), b = NameOr(g, g.Couple[1], "someone");
                t = g.Couple.Contains(me)
                    ? "You are marrying " + NameOr(g, g.Couple[1 - g.Couple.IndexOf(me)], "your partner") + "; everyone has gathered."
                    : a + " and " + b + " are getting married, and everyone has gathered.";
            }
            else if (g.Kind == "funeral")
            {
                t = "A funeral is being held for " + Or(g.Deceased, "someone who died") + "."
                    + (lead.Length > 0 && g.Lead != me ? " " + lead + " leads it." : "");
                if (g.Lead == me) t += " You are leading it.";
            }
            else if (g.Kind == "speech" || g.Kind == "ritual")
            {
                string what = Or(g.Ideo, Or(g.Label, Setting[g.Kind]));
                t = g.Lead == me
                    ? "You are giving " + Setting[g.Kind] + ": " + what + "."
                    : Or(lead, "Someone") + " is holding " + Setting[g.Kind] + ": " + what + ".";
            }
            else if (g.Kind == "party")
            {
                t = "A " + Or(g.Label, "party") + " is under way, with " + OthersNames(g, me) + ".";
            }
            else
            {
                t = "You are eating together with " + OthersNames(g, me) + (g.Room.Length > 0 ? " in the " + g.Room + "." : ".");
            }
            if (g.Story.Length > 0 && (g.Kind == "meal" || g.Kind == "party"))
                t += " A story the group knows: " + g.Story;
            return t;
        }

        /// <summary>(the block G request for this turn, max_tokens or null for the default).</summary>
        public static (string Request, int? MaxTokens) Request(Gathering g, int me)
        {
            bool first = g.Turns.Count == 0;
            if (g.Kind == "wedding")
            {
                if (g.Couple.Contains(me) && g.Turns.Count < g.Couple.Count)
                {
                    string other = GameData.Name(NameOr(g, g.Couple[1 - g.Couple.IndexOf(me)], ""), "your partner");
                    return ("=== REQUEST ===\nIt is the wedding. Say your vow to " + other + " aloud: one sincere line, in your own voice.", null);
                }
                return ("=== REQUEST ===\nThe vows have been said. React in one short, warm line, in your own voice.", null);
            }
            if (g.Kind == "funeral")
            {
                string who = g.Deceased.Length > 0 ? GameData.Name(g.Deceased, "the one who died") : "the one who died";
                if (g.Lead == me && first)
                    return ("=== REQUEST ===\nGive a short eulogy for " + who + " to everyone gathered, from what you remember of them. " +
                            "One or two plain sentences, in your own voice.", null);
                return ("=== REQUEST ===\nAdd one quiet line of your own about " + who + ", or answer what was just said. Keep it brief.", null);
            }
            if (g.Kind == "speech" || g.Kind == "ritual")
            {
                if (g.Lead == me && first)
                    return ("=== REQUEST ===\nSay one line to the people gathered, about what this occasion is for, in your own voice.", null);
                return ("=== REQUEST ===\nReact in one short line to what was just said, in your own voice.", null);
            }
            if (first && g.Story.Length > 0)
                return ("=== REQUEST ===\nYou all know the story in the scene. Bring it up to the others in your own words, one short line.", null);
            if (first)
                return ("=== REQUEST ===\nSay something to the others that fits the moment. One short line.", null);
            return ("=== REQUEST ===\nReact to what was just said: add a detail, laugh, or answer. One short line.", null);
        }

        // ---- creative turns: the eulogy and the vows -------------------------------------------------------------
        /// <summary>The turns that are written pieces: the lead's eulogy and each of the couple's vows.</summary>
        public static bool CeremonyTurn(Gathering g, int sp)
        {
            return (g.Kind == "funeral" && g.Lead == sp && g.Turns.Count == 0)
                || (g.Kind == "wedding" && g.Couple.Contains(sp) && g.Turns.Count < g.Couple.Count);
        }

        private static string BeforeRequest(string s)
        {
            int k = s.IndexOf(RequestMark, StringComparison.Ordinal);
            return k < 0 ? s : s.Substring(0, k);
        }

        private static IEnumerable<string> CardLinesNoMood(JObject card)
        {
            return Grounding.CardLines(card).Where(x => !x.StartsWith("Mood", StringComparison.Ordinal));
        }

        /// <summary>creative_ground: concrete details about the person the speech is about, as a data section ("" when none).
        /// Deceased: traits, background, a memory the speaker has of them, how they died. Couple: traits of both and what
        /// they share. Game text, cleaned.</summary>
        public static async Task<string> DetailsAsync(IGroupHost d, Gathering g, int me)
        {
            Mind mind;
            d.Minds.TryGetValue(me, out mind);
            var lines = new List<string>();
            if (g.Kind == "funeral" && g.Deceased.Length > 0)
            {
                string name = GameData.Name(g.Deceased, "the one who died");
                int? did = null;
                foreach (var kv in d.Names) if (kv.Value == g.Deceased) { did = kv.Key; break; }
                if (did != null)
                {
                    foreach (var x in CardLinesNoMood(await Grounding.ActorCardAsync(d.Ground, did.Value).ConfigureAwait(false)))
                        lines.Add(name + ": " + x);
                }
                var tok = Grounding.TokensOf(new[] { g.Deceased });
                if (mind != null)
                {
                    var mine = mind.Memories.Where(m => (did != null && m.People.Contains(did.Value)) || Grounding.Mentions(m.Text, tok)).ToList();
                    foreach (var m in mine.Skip(Math.Max(0, mine.Count - 2)))
                        lines.Add("A memory you have of " + name + ": " + GameData.Clean(m.Text, "event", 200));
                }
                var news = d.Chronicle.State.Recent.Where(e => Grounding.Mentions(e.Text, tok)).Select(e => e.Text).ToList();
                foreach (var t in news.Skip(Math.Max(0, news.Count - 2)))
                    lines.Add("What the record says: " + GameData.Clean(t, "event", 220));
            }
            else if (g.Kind == "wedding" && g.Couple.Contains(me) && g.Couple.Count == 2)
            {
                int other = g.Couple[1 - g.Couple.IndexOf(me)];
                string oname = GameData.Name(NameOr(g, other, ""), "your partner");
                foreach (var (who, nm) in new[] { (me, "You"), (other, oname) })
                {
                    foreach (var x in CardLinesNoMood(await Grounding.ActorCardAsync(d.Ground, who).ConfigureAwait(false)))
                        lines.Add(nm + ": " + x);
                }
                if (mind != null)
                {
                    var sh = mind.Memories.Where(m => m.People.Contains(other) && m.Visibility != "private").ToList();
                    foreach (var m in sh.Skip(Math.Max(0, sh.Count - 2)))
                        lines.Add("Something you share with " + oname + ": " + GameData.Clean(m.Text, "event", 200));
                }
            }
            if (lines.Count == 0) return "";
            return "=== ABOUT THEM ===\n" + GameData.DataSection("about", string.Join("\n", lines));
        }

        /// <summary>One eulogy or vow through creative.compose. With every flag off it is one call to say(req), as before.
        /// Returns a Spoken or a Dropped.</summary>
        private static async Task<object> ComposeTurnAsync(IGroupHost d, Gathering g, int sp, Mind mind, Lease lease, string req,
            Func<string, Task<object>> say)
        {
            string kind = g.Kind == "funeral" ? "eulogy" : "vow";
            string bas = req;
            if (Creative.On(d.Creative, "ground", kind))
                bas = (await DetailsAsync(d, g, sp).ConfigureAwait(false)) + req;
            var results = new List<object>();

            Func<string, string> withNote = note => string.IsNullOrEmpty(note) ? bas : bas.TrimEnd('\n') + "\n" + note;

            Func<string, Task<string>> write = async note =>
            {
                var r = await say(withNote(note)).ConfigureAwait(false);
                results.Add(r);
                var spoken = r as Spoken;
                return spoken != null ? spoken.Text : null;
            };

            Func<Task<string>> plan = async () =>
            {
                var msgs = PromptBlocks.LineMessages((string)lease.Data["prefix"], g.Turns.Select(t => (t.Name, t.Text)).ToList(), mind,
                    g.Names[sp], g.Turns.Count, BeforeRequest(bas) + "=== REQUEST ===\n" + Grounding.PlanAsk);
                var c = await d.Llm.CompleteAsync(Passes.Msgs(msgs), lease.Slot, "line", Grounding.PlanTokens, new[] { "\n" },
                    actor: sp).ConfigureAwait(false);
                return c.Text;
            };

            var (text, _) = await Creative.ComposeAsync(d.Creative, kind, lease.Slot, sp, BeforeRequest(bas), new HashSet<string>(),
                write, plan, () => Grounding.OtherReactiveWaiting(d.Ground, sp)).ConfigureAwait(false);
            for (int i = results.Count - 1; i >= 0; i--)
            {
                var s = results[i] as Spoken;
                if (s != null && s.Text == text) return s;
            }
            return results.Count > 0 ? results[results.Count - 1] : new Dropped("no_candidate");
        }

        public static string TemplateMemory(Gathering g, int me)
        {
            var n = g.Names;
            Turn first = g.Turns.Count > 0 ? g.Turns[0] : null;
            Turn last = g.Turns.Count > 0 ? g.Turns[g.Turns.Count - 1] : null;
            string setting;
            if (g.Kind == "wedding" && g.Couple.Count == 2)
                setting = "the wedding of " + NameOr(g, g.Couple[0], "two of us") + " and " + NameOr(g, g.Couple[1], "someone");
            else if (g.Kind == "funeral")
                setting = "the funeral of " + Or(g.Deceased, "one of ours");
            else if (g.Kind == "speech" || g.Kind == "ritual")
                setting = Setting[g.Kind] + (g.Ideo.Length > 0 || g.Label.Length > 0 ? " (" + Or(g.Ideo, g.Label) + ")" : "");
            else
                setting = Setting[g.Kind];
            if (first == null) return "I was at " + setting + ".";
            string who = first.Speaker == me ? "I" : first.Name;
            string s = "I was at " + setting + ". " + who + " spoke first: \"" + first.Text + "\"";
            if (!ReferenceEquals(last, first) && last.Speaker == me) s += " I said: \"" + last.Text + "\"";
            return s;
        }

        // ---- event handling --------------------------------------------------------------
        private static bool GatheringEventOff(IGroupHost d)
        {
            var ev = d.BridgeCfg == null ? null : d.BridgeCfg["events"] as JObject;
            var g = ev == null ? null : ev["gathering"];
            return g != null && g.Type == JTokenType.Boolean && !(bool)g;       // .get("gathering") is False
        }

        /// <summary>The older "funeral" event is covered by its gathering while gatherings are on: no second reaction.</summary>
        public static bool OwnsFuneral(IGroupHost d)
        {
            return d.Cfg.Gatherings && !GatheringEventOff(d);
        }

        private static bool Truthy(JToken t) { return t != null && PyJson.Truthy(t); }

        private static long PyInt(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (long)(double)t;
                case JTokenType.Boolean: return (bool)t ? 1 : 0;
                case JTokenType.String: return long.Parse(PyStr.Strip((string)t), System.Globalization.CultureInfo.InvariantCulture);
                default: throw new InvalidCastException("int() argument must be a string or a number, not '" + t.Type + "'");
            }
        }

        private static double PyFloat(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.Boolean: return (bool)t ? 1.0 : 0.0;
                case JTokenType.String: return double.Parse(PyStr.Strip((string)t), System.Globalization.CultureInfo.InvariantCulture);
                default: throw new InvalidCastException("float() argument must be a string or a number, not '" + t.Type + "'");
            }
        }

        private static int? OptInt(JToken t)
        {
            return t == null || t.Type == JTokenType.Null ? (int?)null : (int)PyInt(t);
        }

        /// <summary>Consume a gathering event (true) or leave it alone (false). A start becomes a queued scene when allowed.</summary>
        public static async Task<bool> HandleAsync(IGroupHost d, JObject e)
        {
            var tt = e["type"];
            string t = tt == null || tt.Type == JTokenType.Null ? "" : PyJson.Str(tt);
            if (t == "gathering_end")
            {
                var dat = e["data"];
                var kt = Truthy(dat) && dat is JObject ? ((JObject)dat)["key"] : null;
                d.Group.Over.Add(Truthy(kt) ? PyJson.Str(kt) : "");
                return true;
            }
            if (t != "gathering") return false;
            var gs = d.Group;
            var info = d.Adapter.GatherInfo(e);
            if (info == null || !d.Cfg.Gatherings || GatheringEventOff(d)) return true;
            string kind = (string)info["kind"], key = (string)info["key"];
            if (gs.Seen.Contains(key) || (kind == "meal" && !d.Cfg.MealTalk)) return true;
            var cls = SceneClass(kind);
            var now = d.Now();
            var ex = LeaseManager.RequestExpiry[cls];
            JToken gameTok = e["game"];
            long egame = gameTok == null || gameTok.Type == JTokenType.Null ? now.Game : PyInt(gameTok);   // e.get("game", now.game)
            long age = Math.Max(0, now.Game - egame);
            if (age > Math.Max(ex.Units.Value, (long)(ex.FloorRealS * now.Rate))) return true;           // a backlog event
            if (cls <= Cls.Social)
            {
                double roll = Inner.Sha1Mod("gather:" + key, 1000) / 1000.0;
                JToken tf;
                double freq = 1.0;
                if (d.BridgeCfg != null && d.BridgeCfg.TryGetValue("talk_frequency", out tf)) freq = PyFloat(tf);
                if (roll >= Math.Min(1.0, freq)) return true;
            }
            var pool = ((JArray)info["participants"]).Select(x => (int)x).Where(i => d.Minds.ContainsKey(i)).ToList();
            await d.LearnAgesAsync(pool).ConfigureAwait(false);
            var couple = ((JArray)info["couple"]).Select(x => (int)x).ToList();
            if (kind == "wedding")
            {
                // hard age gate: the vows are romantic, so both of the couple must be known adults, or there is no scene at all
                if (couple.Count != 2 || couple.Any(c => !d.Minds.ContainsKey(c) || d.IsMinor(c)))
                {
                    d.Gate.SkippedEvents += 1;
                    d.Gate.Note("wedding", new JObject { ["couple"] = new JArray(couple) });
                    gs.Skipped += 1;
                    return true;
                }
                pool = pool.Where(i => !d.IsMinor(i)).ToList();                   // only adults speak at a wedding
                foreach (var c in couple) if (!pool.Contains(c)) pool.Add(c);
            }
            if (pool.Count < 2)
            {
                gs.Skipped += 1;
                return true;
            }
            string story = null;
            int? opener = null;
            if (kind == "meal" || kind == "party")
            {
                var picked = PickStory(d, pool, now.Game);
                if (picked != null) { story = picked.Value.Text; opener = picked.Value.Opener; }
            }
            int? lead = OptInt(info["lead"]);
            var speakers = PlanSpeakers(kind, lead, couple, pool, d.Minds, d.Weights, d.Cfg.GroupMaxSpeakers, opener);
            var names = new Dictionary<int, string>();
            foreach (var i in pool)
            {
                string nm;
                names[i] = d.Names.TryGetValue(i, out nm) ? nm : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            d.ConvN += 1;
            var g = new Gathering("g-" + d.ConvN.ToString(System.Globalization.CultureInfo.InvariantCulture), kind, key,
                lead != null && pool.Contains(lead.Value) ? lead : null, couple, OptInt(info["subject"]), pool, speakers, names,
                (string)info["label"], (string)info["ideo"], (string)info["room"], (string)info["deceased"], story ?? "");
            g.Minor = pool.Any(i => d.IsMinor(i));
            var born = new Now(now.Game - age, now.Real - age / Math.Max(now.Rate, 1.0), now.Rate);
            var item = d.Leases.MakeItem(speakers[0], "gathering", born, e, cls: cls, payload: PayloadOf(g));
            if (d.Leases.Submit(item, now, d.Bridge.Env.Speed) == "queued") gs.Seen.Add(key);
            else gs.Skipped += 1;
            return true;
        }

        // ---- the scene -------------------------------------------------------------------
        /// <summary>A dict that keeps Python's insertion order across pop and re-insert (the window rotation does both).</summary>
        private sealed class Held
        {
            private readonly List<int> _keys = new List<int>();
            private readonly Dictionary<int, Lease> _d = new Dictionary<int, Lease>();
            public int Count { get { return _keys.Count; } }
            public bool Contains(int k) { return _d.ContainsKey(k); }
            public Lease this[int k]
            {
                get { return _d[k]; }
                set { if (!_d.ContainsKey(k)) _keys.Add(k); _d[k] = value; }
            }
            public List<int> Keys() { return _keys.ToList(); }
            public List<Lease> Values() { return _keys.Select(k => _d[k]).ToList(); }
            public Lease Pop(int k) { var l = _d[k]; _d.Remove(k); _keys.Remove(k); return l; }
        }

        /// <summary>The lease for sp: the one it holds, else a free window, else the window of whoever spoke longest ago (never the
        /// last speaker when another is held).</summary>
        private static Lease Window(IGroupHost d, Gathering g, WorkItem item, Held held, Dictionary<int, int> spoke, int sp)
        {
            if (held.Contains(sp)) return held[sp];
            var now = d.Now();
            Func<WorkItem> mk = () => d.Leases.MakeItem(sp, "gathering", now, item.Event, cls: item.Cls);
            Lease lease = held.Count < d.Cfg.ActorSlots.Count ? d.Leases.Acquire(mk(), now) : null;
            if (lease == null && held.Count > 0)
            {
                int? last = g.Turns.Count > 0 ? (int?)g.Turns[g.Turns.Count - 1].Speaker : null;
                var cands = held.Keys().Where(a => last == null || a != last.Value).ToList();
                if (cands.Count == 0) cands = held.Keys();
                int victim = cands[0];
                int vs = spoke.TryGetValue(victim, out int v0) ? v0 : -1;
                foreach (var a in cands.Skip(1))
                {
                    int s = spoke.TryGetValue(a, out int v1) ? v1 : -1;
                    if (s < vs) { victim = a; vs = s; }                           // min(): the first of equal keys wins
                }
                d.Leases.Release(held.Pop(victim), "rotated");
                d.Group.Rotations += 1;
                lease = d.Leases.Acquire(mk(), d.Now());
            }
            if (lease != null) held[sp] = lease;
            return lease;
        }

        public static async Task RunAsync(IGroupHost d, WorkItem item, Lease lease)
        {
            Lease lease0 = lease;
            Gathering g = GatheringOf(item);
            var gs = d.Group;
            var held = new Held();
            held[g.Speakers[0]] = lease0;
            var ctxs = new Dictionary<int, (Lease Lease, SanitizeCtx Ctx)>();
            var spoke = new Dictionary<int, int>();
            try
            {
                g.Cap = TurnCap(g.Kind, d.Now().Rate, d.Cfg.GroupTurns, g.Speakers.Count);
                if (g.Cap <= 0)
                {
                    gs.Skipped += 1;
                    return;
                }
                gs.Started += 1;
                double lastReal = d.Now().Real;
                while (g.Ended.Length == 0)
                {
                    if (g.Turns.Count > 0) await d.PaceAsync(lastReal, held.Values()).ConfigureAwait(false);
                    if (held.Values().Any(l => l.Preempted))
                    {
                        g.Ended = "preempted";
                        break;
                    }
                    if (gs.Over.Contains(g.Key))
                    {
                        g.Ended = "over";
                        break;
                    }
                    g.Cap = Math.Min(g.Cap, TurnCap(g.Kind, d.Now().Rate, d.Cfg.GroupTurns, g.Speakers.Count));
                    if (g.Turns.Count >= g.Cap)
                    {
                        g.Ended = "turn_cap";
                        break;
                    }
                    int? spn = NextSpeaker(g);
                    if (spn == null)
                    {
                        g.Ended = "no_speaker";
                        break;
                    }
                    int sp = spn.Value;
                    Lease ls = Window(d, g, item, held, spoke, sp);
                    if (ls == null)
                    {
                        g.Ended = "no_window";
                        break;
                    }
                    if (!ctxs.ContainsKey(sp) || !ReferenceEquals(ctxs[sp].Lease, ls))
                    {
                        var c0 = (await d.PrepareAsync(sp, Trigger(g, sp), item.Event, ls).ConfigureAwait(false)).Ctx;
                        c0.MinorInvolved = c0.MinorInvolved || g.Minor;
                        ctxs[sp] = (ls, c0);
                    }
                    SanitizeCtx ctx = ctxs[sp].Ctx;
                    var audience = g.Pool.Where(i => i != sp).Select(i => g.Names[i]).ToList();
                    Mind mind = d.Minds[sp];
                    ctx.Secrets = mind.Memories.Where(m => audience.Any(n => !MemoryOps.CanState(m, n))).Select(m => m.Text).ToList();
                    var (req, mt) = Request(g, sp);
                    ls.Generating = true;
                    var (check, onCheck) = d.RepeatCheck(g.Turns, sp);
                    int maxTok = (g.Kind == "funeral" && g.Lead == sp && g.Turns.Count == 0) ? d.Cfg.EulogyMaxTokens : d.Cfg.Llm.LineMaxTokens;

                    Func<string, Task<object>> say = request =>
                    {
                        object sdata;
                        string speech = ls.Data.TryGetValue("speech", out sdata) ? sdata as string : null;
                        return Passes.SpeakAsync(d.Llm, ls.Slot, mind, g.Names[sp], (string)ls.Data["prefix"],
                            g.Turns.Select(t => (t.Name, t.Text)).ToList(), g.Turns.Count, ctx, null, d.LabelsFor(ctx.MinorInvolved),
                            maxTok, request, check: check, retries: d.Cfg.RepeatRetries, onCheck: onCheck, speech: speech);
                    };

                    object res;
                    if (CeremonyTurn(g, sp)) res = await ComposeTurnAsync(d, g, sp, mind, ls, req, say).ConfigureAwait(false);
                    else res = await say(req).ConfigureAwait(false);
                    ls.Generating = false;
                    var dropped = res as Dropped;
                    if (dropped != null)
                    {
                        d.Dropped.Add((sp, dropped.Reason));
                        g.Ended = dropped.Reason == "repeat" ? "repeat" : "dropped";
                        break;
                    }
                    var spoken = (Spoken)res;
                    if (held.Values().Any(l => l.Preempted))
                    {
                        g.Ended = "preempted";
                        break;
                    }
                    if (item.Expired(d.Now()) && g.Turns.Count == 0)
                    {
                        g.Ended = "expired";
                        break;
                    }
                    int? lis;
                    if (g.Kind == "wedding" && g.Couple.Contains(sp) && g.Turns.Count < g.Couple.Count)
                        lis = g.Couple[1 - g.Couple.IndexOf(sp)];                   // a vow is said to the partner
                    else if (g.Turns.Count > 0)
                        lis = g.Turns[g.Turns.Count - 1].Speaker;                   // a reaction turns toward the last speaker
                    else
                        lis = null;
                    bool ok = await d.SayAsync(sp, spoken, lis, g.Turns.Count == 0 ? d.WireExpiry(item) : null, conv: g.Id,
                        ev: item.Event, minor: ctx.MinorInvolved).ConfigureAwait(false);
                    if (!ok)
                    {
                        g.Ended = "apart";                                          // the game's liveness check refused
                        break;
                    }
                    g.Turns.Add(new Turn(sp, g.Names[sp], spoken.Text, spoken.Intent));
                    spoke[sp] = g.Turns.Count;
                    gs.Lines += 1;
                    d.RememberOwn(sp, spoken.Text);
                    lastReal = d.Now().Real;
                    d.Leases.Quotas.Record(sp, null, d.Now(), false);
                    int lc;
                    d.LineCount[sp] = (d.LineCount.TryGetValue(sp, out lc) ? lc : 0) + 1;
                    foreach (var l in held.Values()) d.Leases.Touch(l, d.Now());
                    if (g.Turns.Count == 1 && g.Story.Length > 0) gs.Told[g.Story] = d.Bridge.Env.Game;
                    HashSet<int> present;
                    try
                    {
                        present = new HashSet<int>(d.Adapter.ScenePresent(await d.Bridge.SceneAsync(sp).ConfigureAwait(false))
                            .Where(p => p.CanHear).Select(p => p.Id));
                    }
                    catch (BridgeError)
                    {
                        g.Ended = "apart";
                        break;
                    }
                    g.Speakers = g.Speakers.Where(i => i == sp || present.Contains(i)).ToList();
                    if (!g.Speakers.Any(i => i != sp)) g.Ended = "apart";
                }
                if (g.Turns.Count > 0) Finish(d, g);
            }
            catch (Exception ex)
            {
                g.Ended = g.Ended.Length > 0 ? g.Ended : "error";
                if (ex is OperationCanceledException) throw;                       // Python re-raises the BaseException
                LlmLog.Warning("rimworldllm", "gathering failed: " + ex);
            }
            finally
            {
                string endKey = g.Ended.Length > 0 ? g.Ended : "done";
                int ec;
                gs.Ends[endKey] = (gs.Ends.TryGetValue(endKey, out ec) ? ec : 0) + 1;
                gs.Log.Add(new JObject
                {
                    ["id"] = g.Id, ["kind"] = g.Kind, ["key"] = g.Key, ["turns"] = g.Turns.Count, ["ended"] = g.Ended,
                    ["speakers"] = new JArray(g.Speakers),
                });
                foreach (var l in held.Values().Concat(new[] { lease0 }))
                {
                    l.Generating = false;
                    if (!l.Ended) d.Leases.Release(l, "gathering_end");
                }
            }
        }

        /// <summary>Everyone there keeps one memory of it (a template, no call), and a ceremony goes into the chronicle.</summary>
        public static void Finish(IGroupHost d, Gathering g)
        {
            long game = d.Bridge.Env.Game;
            double imp = Ceremonies.Contains(g.Kind) ? 0.5 : g.Kind == "meal" ? 0.3 : 0.35;
            var people = g.Pool.Take(6).ToList();
            foreach (var i in g.Pool)
            {
                Mind mind;
                if (!d.Minds.TryGetValue(i, out mind)) continue;
                mind.Memories.Add(new Memory
                {
                    Id = mind.NextMemId(), Game = game, Text = TemplateMemory(g, i), Source = "saw", Importance = imp,
                    People = people.Where(p => d.Names.ContainsKey(p)).ToList(), Etype = "gathering",
                });
                mind.UnreflectedImportance += 0.2;
                d.Dirty.Add(i);
            }
            if (Ceremonies.Contains(g.Kind) && g.Turns.Count > 0)
            {
                var t = g.Turns[0];
                string what = g.Kind == "wedding" ? "At the wedding"
                    : g.Kind == "funeral" ? "At the funeral of " + Or(g.Deceased, "one of ours")
                    : "At " + Setting[g.Kind];
                d.Chronicle.Note(game, what + ", " + t.Name + " said: \"" + t.Text + "\"", 0.4, false);
            }
        }
    }
}
