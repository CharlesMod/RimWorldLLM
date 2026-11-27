// Port of agent/rimworldllm_agent/core/art.py: art descriptions (aliveness item 5). When a maker finishes a piece of art, a short
// description written from the community's real history replaces the game's generated tale text on the info card.
//
// An art_created event becomes a queued job (ArtQueue.Note). The idle runner takes it (Take) and WriteArtAsync writes the text with
// one narrator-slot call (or a plan, candidates and a pick: Creative.ComposeAsync); the text goes back with POST /art. Game text in the
// prompt (names, the game's own tale text, chronicle lines, memories) is cleaned and wrapped in data sections; the system prompt
// carries the data rule. The text is sanitised and checked by the age gate.
//
// The Python took the director as `d`; here the members art.py reads are IArtHost.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members art.py reads. Cfg is the real MindConfig; Creative/Ground are the (differently typed) views creative.py and
    /// grounding.py take of the same director.</summary>
    public interface IArtHost
    {
        ICreativeHost Creative { get; }
        IGroundingHost Ground { get; }
        IBridge Bridge { get; }
        ILlmClient Llm { get; }
        MindConfig Cfg { get; }
        IGameAdapter Adapter { get; }
        ModelTier Tier { get; }
        /// <summary>GET /config of the mod (d.bridge_cfg).</summary>
        JObject BridgeCfg { get; }
        ArtQueue Art { get; }
        Chronicle Chronicle { get; }
        IDictionary<int, string> Names { get; }
        IDictionary<int, Mind> Minds { get; }
        /// <summary>d.name_tokens: every word that names an actor, by actor id.</summary>
        IDictionary<int, HashSet<string>> NameTokens { get; }
        GateStats Gate { get; }
        /// <summary>Game-time units in one day (d.day).</summary>
        int Day { get; }
        HashSet<string> MinorTokens();
        bool IsMinor(int actorId);
    }

    public sealed class ArtJob
    {
        public int Thing;
        public int Maker;
        public int Game;                     // when it was finished
        public string Label = "";
        public string Quality = "";
        public string Title = "";
        public string Tale = "";
        public string Material = "";         // the stuff it is made of (game text)
        public string TaleDef = "";          // the depicted tale: its def, summary, game day and the people in it (all game text)
        public string TaleSummary = "";
        public int? TaleDay;
        public List<string> TalePeople = new List<string>();
    }

    /// <summary>Bounded, newest first. Held in memory only: a restart forgets what was waiting.</summary>
    public sealed class ArtQueue
    {
        public const int MaxQueue = 6;

        public List<ArtJob> Jobs = new List<ArtJob>();
        public int Dropped;
        /// <summary>The mod answered 404 to POST /art.</summary>
        public bool Unsupported;

        /// <summary>Queue the job an art_created event describes (null when it names no maker or thing).</summary>
        public ArtJob Note(JObject e, int game)
        {
            JToken dt = JobsJson.Get(e, "data");
            JToken data = dt != null && PyJson.Truthy(dt) ? dt : new JObject();
            int? maker = null;
            JToken actors = JobsJson.Get(e, "actors");
            if (actors is JArray aa)
                foreach (var a in aa)
                {
                    JToken role = JobsJson.Get(a, "role");
                    if (role != null && role.Type == JTokenType.String && (string)role == "maker")
                    {
                        maker = (int)JobsJson.PyInt(PyAccess.Item(a, "id"));
                        break;
                    }
                }
            int thing;
            try { thing = (int)JobsJson.PyInt(PyAccess.Item(data, "thing_id")); }
            catch (Exception ex) when (ex is PyKeyError || ex is PyTypeError || ex is PyValueError) { return null; }
            if (maker == null) return null;
            JToken gt = JobsJson.Get(e, "game");
            var job = new ArtJob
            {
                Thing = thing, Maker = maker.Value, Game = gt != null ? (int)JobsJson.PyInt(gt) : game,
                Label = JobsJson.Str(data, "def_label"), Quality = JobsJson.Str(data, "quality"), Title = JobsJson.Str(data, "title"),
                Tale = JobsJson.Str(data, "description"), Material = JobsJson.StrOrEmpty(data, "material"),
            };
            JToken tale = JobsJson.Get(data, "tale");
            if (tale is JObject to)
            {
                job.TaleDef = JobsJson.StrOrEmpty(to, "def");
                job.TaleSummary = JobsJson.StrOrEmpty(to, "summary");
                try
                {
                    JToken day = JobsJson.Get(to, "day");
                    job.TaleDay = day != null && day.Type != JTokenType.Null ? (int?)(int)JobsJson.PyInt(PyAccess.Item(to, "day")) : null;
                }
                catch (Exception ex) when (ex is PyTypeError || ex is PyValueError) { job.TaleDay = null; }
                // the people in the tale: the one list-valued field (the game names its key; core does not)
                var lists = to.Properties().Where(p => p.Name != "def" && p.Name != "summary" && p.Name != "day" && p.Value is JArray)
                              .Select(p => (JArray)p.Value).ToList();
                job.TalePeople = lists.Count > 0 ? lists[0].Select(x => PyJson.Str(x)).Take(3).ToList() : new List<string>();
            }
            Jobs = Jobs.Where(j => j.Thing != thing).Concat(new[] { job }).ToList();
            if (Jobs.Count > MaxQueue)
            {
                Dropped += Jobs.Count - MaxQueue;
                Jobs = Jobs.Skip(Jobs.Count - MaxQueue).ToList();
            }
            return job;
        }

        public void Expire(int game, int day)
        {
            var keep = Jobs.Where(j => game - j.Game <= day).ToList();
            Dropped += Jobs.Count - keep.Count;
            Jobs = keep;
        }

        /// <summary>The newest job still within a game day, removed from the queue.</summary>
        public ArtJob Take(int game, int day)
        {
            Expire(game, day);
            if (Jobs.Count == 0) return null;
            var j = Jobs[Jobs.Count - 1];
            Jobs.RemoveAt(Jobs.Count - 1);
            return j;
        }

        /// <summary>True when a job is still within its game day (read-only; Take is what removes it).</summary>
        public bool Pending(int game, int day) { return Jobs.Any(j => game - j.Game <= day); }

        public void PutBack(ArtJob job)
        {
            if (Jobs.All(j => j.Thing != job.Thing))
            {
                Jobs.Insert(0, job);                       // the oldest slot: newer pieces still go first
                if (Jobs.Count > MaxQueue) Jobs = Jobs.Skip(Jobs.Count - MaxQueue).ToList();
            }
        }

        public void Rollback(int game) { Jobs = Jobs.Where(j => j.Game <= game).ToList(); }

        public int Count { get { return Jobs.Count; } }
    }

    public static class Art
    {
        public const int MaxMemories = 3;
        public const int MaxChronicle = 3;

        // The wording is the adapter's (art_system, art_request); these are the game-neutral fallbacks.
        public const string ArtSystem =
            "You write the short description on the info card of a work of art made by a member of the community. Plain " +
            "words, third person, past tense. State only what the data says. Never invent people, places, events or numbers. " +
            "No flowery prose, no moral. Nothing romantic or sexual about anyone, ever.";
        public const string ArtRequest = "Write the description: 2 or 3 plain sentences, under 55 words, one paragraph.";

        /// <summary>The only quality words the game's art text itself carries.</summary>
        public static readonly HashSet<string> GameQuality = new HashSet<string> { "masterwork", "legendary" };

        public const string GroundRequest =
            "Write the description: 2 or 3 plain sentences, under 55 words, one paragraph. Say what the piece " +
            "depicts as the data gives it (who, where, the weather if given, how it ended) in your own words, not the " +
            "game text's order. Name the material once. If the maker remembers this event, let one detail of that " +
            "memory show in how the scene is treated. Never name the maker's traits, mood or the quality grade. Use " +
            "only people and events the data names.";

        public static bool Enabled(IArtHost d)
        {
            return ModelTier.FeatureOn(d.Tier, d.Cfg.ArtEnabled, d.Cfg.ArtSmall) && !d.Art.Unsupported
                && !JobsJson.IsFalse(JobsJson.Get(d.BridgeCfg, "art_descriptions"))
                && JobsJson.EventNotOff(d.BridgeCfg, "art_created")
                && JobsJson.EventNotOff(d.BridgeCfg, "art");
        }

        private static string Maker(IArtHost d, int id) { return d.Names.TryGetValue(id, out string n) ? n : "Someone"; }

        private static Mind MindOf(IArtHost d, int id) { return d.Minds.TryGetValue(id, out Mind m) ? m : null; }

        private static string SystemText(IArtHost d)
        {
            return (d.Adapter.ArtSystem ?? ArtSystem) + "\n\n" + GameData.DataRule + "\n" + AgeGate.MinorRule;
        }

        /// <summary>(messages, the maker memories used). With creative_ground the data is richer (see GroundMessages); card is the maker's
        /// traits and mood from Grounding.ActorCardAsync (null: none known).</summary>
        public static (List<LlmMessage> Messages, List<Memory> Mems) ArtMessages(IArtHost d, ArtJob job, JObject card = null, string request = null)
        {
            if (Creative.On(d.Creative, "ground", "art")) return GroundMessages(d, job, card ?? new JObject(), request);
            long now = d.Bridge.Env.Game;
            string maker = Maker(d, job.Maker);
            Mind mind = MindOf(d, job.Maker);
            var mems = new List<Memory>();
            if (mind != null)
                mems = MemoryOps.WeightSorted(mind, now, d.Day).Where(m => m.Visibility != "private").Take(MaxMemories).ToList();
            var recent = Grounding.Tail(d.Chronicle.State.Recent.Select(e => e.Text).ToList(), MaxChronicle);
            var facts = new List<string> { "Maker: " + GameData.Name(maker), "Item: " + Or(GameData.Clean(job.Label, "label"), "artwork") };
            if (job.Quality.Length > 0) facts.Add("Quality: " + GameData.Clean(job.Quality, "label"));
            if (job.Title.Length > 0) facts.Add("Title: " + GameData.Clean(job.Title, "name"));
            if (job.Tale.Length > 0) facts.Add("The game's own text for it: " + GameData.Clean(job.Tale, "desc"));
            string hist = Or(string.Join("\n", recent.Select(t => "- " + GameData.Clean(t, "event", 200))), "- (nothing written yet)");
            string mem = Or(string.Join("\n", mems.Select(m => "- " + GameData.Clean(m.Text, "event"))), "- (nothing in particular)");
            string user = "=== THE ARTWORK ===\n" + GameData.DataSection("artwork", string.Join("\n", facts))
                + "=== RECENT HISTORY ===\n" + GameData.DataSection("history", hist)
                + "=== WHAT THE MAKER REMEMBERS ===\n" + GameData.DataSection("memories", mem)
                + "=== REQUEST ===\n" + (!string.IsNullOrEmpty(request) ? request : (d.Adapter.ArtRequest ?? ArtRequest));
            return (new List<LlmMessage> { new LlmMessage("system", SystemText(d)), new LlmMessage("user", user) }, mems);
        }

        private static string Or(string a, string b) { return a.Length > 0 ? a : b; }

        private static List<Memory> Mentioning(IEnumerable<Memory> ms, HashSet<string> tokens, HashSet<string> taleKw)
        {
            return ms.Where(m => Grounding.Mentions(m.Text, tokens) || (taleKw.Count > 0 && taleKw.Overlaps(MemoryOps.Keywords(m.Text)))).ToList();
        }

        private static (List<LlmMessage>, List<Memory>) GroundMessages(IArtHost d, ArtJob job, JObject card, string request)
        {
            long now = d.Bridge.Env.Game;
            string maker = Maker(d, job.Maker);
            Mind mind = MindOf(d, job.Maker);
            var people = job.TalePeople.Where(p => p.Length > 0).Select(p => GameData.Name(p)).ToList();
            var all = new List<string> { maker };
            all.AddRange(people);
            var tokens = Grounding.TokensOf(all);
            var taleKw = job.TaleSummary.Length > 0 ? MemoryOps.Keywords(job.TaleSummary) : new HashSet<string>();
            var facts = new List<string> { "Maker: " + GameData.Name(maker), "Item: " + Or(GameData.Clean(job.Label, "label"), "artwork") };
            if (job.Quality.Length > 0 && GameQuality.Contains(Grounding.Casefold(GameData.Clean(job.Quality, "label"))))
                facts.Add("Quality: " + GameData.Clean(job.Quality, "label"));
            if (job.Title.Length > 0) facts.Add("Title: " + GameData.Clean(job.Title, "name"));
            if (job.Material.Length > 0) facts.Add("Material: " + GameData.Clean(job.Material, "label"));
            if (job.Tale.Length > 0) facts.Add("The game's own text for it: " + GameData.Clean(job.Tale, "desc"));
            if (job.TaleSummary.Length > 0) facts.Add("The event it depicts: " + GameData.Clean(job.TaleSummary, "desc", 200));
            if (people.Count > 0) facts.Add("People in it: " + string.Join(", ", people));
            if (job.TaleDay != null && d.Day != 0)
            {
                long ago = JobsJson.FloorDiv(now, d.Day) - job.TaleDay.Value;
                if (ago >= 0)
                    facts.Add("It happened " + (ago == 0 ? "today" : ago == 1 ? "1 day ago" : ago.ToString(System.Globalization.CultureInfo.InvariantCulture) + " days ago"));
            }
            var mems = new List<Memory>();
            if (mind != null)
            {
                var ms = MemoryOps.WeightSorted(mind, now, d.Day).Where(m => m.Visibility != "private").ToList();
                var hit = Mentioning(ms, tokens, taleKw);
                mems = (hit.Count > 0 ? hit : ms).Take(MaxMemories).ToList();
            }
            var recent = d.Chronicle.State.Recent.Select(e => e.Text).ToList();
            var ent = Grounding.Tail(recent.Where(t => Grounding.Mentions(t, tokens) || (taleKw.Count > 0 && taleKw.Overlaps(MemoryOps.Keywords(t)))).ToList(), MaxChronicle);
            if (ent.Count == 0) ent = Grounding.Tail(recent, MaxChronicle);
            string hist = Or(string.Join("\n", ent.Select(t => "- " + GameData.Clean(t, "event", 200))), "- (nothing written yet)");
            string mem = Or(string.Join("\n", mems.Select(m => "- " + GameData.Clean(m.Text, "event"))), "- (nothing in particular)");
            // only the traits (no mood, no mood thoughts, no background): they shape the treatment and are never named
            var traits = new List<string>();
            if (card["traits"] is JArray ta)
                foreach (var t in ta)
                    if (PyJson.Truthy(t)) traits.Add(GameData.Clean(PyJson.Str(t), "label"));
            string makerSec = traits.Count > 0
                ? "=== THE MAKER (these shape the treatment only; never name them) ===\n" + GameData.DataSection("maker", "Traits: " + string.Join(", ", traits))
                : "";
            string user = "=== THE ARTWORK ===\n" + GameData.DataSection("artwork", string.Join("\n", facts))
                + makerSec
                + "=== HISTORY THAT TOUCHES IT ===\n" + GameData.DataSection("history", hist)
                + "=== WHAT THE MAKER REMEMBERS ===\n" + GameData.DataSection("memories", mem)
                + "=== REQUEST ===\n" + (!string.IsNullOrEmpty(request) ? request : (d.Adapter.ArtRequestGround ?? GroundRequest));
            return (new List<LlmMessage> { new LlmMessage("system", SystemText(d)), new LlmMessage("user", user) }, mems);
        }

        /// <summary>(text, reason): the sanitised description, or null and why it was dropped. LlmError is not caught here.</summary>
        public static async Task<(string Text, string Reason)> WriteArtAsync(IArtHost d, ArtJob job, int slot)
        {
            bool ground = Creative.On(d.Creative, "ground", "art");
            JObject card = ground ? await Grounding.ActorCardAsync(d.Ground, job.Maker).ConfigureAwait(false) : null;
            var (msgs, mems) = ArtMessages(d, job, card);
            var toks = d.NameTokens;
            var allnames = JobsJson.Union(toks.Values);
            var known = new HashSet<string>(toks.TryGetValue(job.Maker, out var mk) ? mk : new HashSet<string>());
            foreach (var m in mems)
                foreach (int p in m.People)
                    if (toks.TryGetValue(p, out var pt)) known.UnionWith(pt);
            // people the prompt's own data names (the depicted event, its people, the history lines) may be named too:
            // the game's own text names them, and refusing them dropped most real descriptions as unknown_name
            var dataWords = JobsJson.WordSet(JobsJson.BeforeRequest(msgs[1].Text));
            foreach (var ts in toks.Values)
                if (ts.Overlaps(dataWords)) known.UnionWith(ts);
            bool minor = d.IsMinor(job.Maker);
            string why = "sanitize";

            async Task<string> write(string note)
            {
                var m = JobsJson.WithNote(msgs, note);
                var c = await d.Llm.CompleteAsync(m, slot, "art", d.Cfg.ArtMaxTokens, new string[0], null, 0.7, job.Maker).ConfigureAwait(false);
                var s = Sanitize.SanitizeProse(c.Text, known, allnames, Sanitize.PromptCapWords(m[1].Text),
                    new HashSet<string>(d.Adapter.CommonWords), toks.TryGetValue(job.Maker, out var sp) ? sp : new HashSet<string>(),
                    5, 75, minor, d.MinorTokens());
                if (!s.Ok)
                {
                    why = !string.IsNullOrEmpty(s.Reason) ? s.Reason : "sanitize";
                    return null;
                }
                if (AgeGate.Violation(s.Text, minor, d.MinorTokens()))
                {
                    d.Gate.SkippedEvents++;
                    why = "agegate";
                    return null;
                }
                return s.Text;
            }

            async Task<string> plan()
            {
                var c = await d.Llm.CompleteAsync(JobsJson.PlanMessages(msgs), slot, "art", Grounding.PlanTokens, new[] { "\n" }, null, 0.7, job.Maker).ConfigureAwait(false);
                return c.Text;
            }

            string facts = JobsJson.BeforeRequest(msgs[1].Text);
            var (text, _) = await Creative.ComposeAsync(d.Creative, "art", slot, job.Maker, facts, known, write, plan, () => Grounding.IdleShouldYield(d.Ground)).ConfigureAwait(false);
            return !string.IsNullOrEmpty(text) ? (text, "") : (null, why);
        }
    }
}
