// Port of agent/rimworldllm_agent/games/rimworld/adapter.py: BRIDGE-API v2 wire names, colonist bios and voices, RimWorld event ranking.
// The prompt wording (reflection_system, ...) is in AdapterText.cs, generated from the Python so it stays byte for byte.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    public sealed partial class RimWorldAdapter : GameAdapterBase
    {
        public override Moment CommentaryMoment(JObject e, ICommentaryHost director) { return Moments.ForEvent(e, director); }

        private static readonly Dictionary<string, Cls> EventClass = new Dictionary<string, Cls>
        {
            { "player_talk", Cls.Player },
            { "death", Cls.Urgent }, { "mental_break", Cls.Urgent }, { "raid_start", Cls.Urgent },
            { "injury", Cls.Reactive }, { "tend", Cls.Reactive },
            { "social", Cls.Social },
            { "level_up", Cls.Ambient }, { "thought", Cls.Ambient },
            { "first_cue", Cls.Reactive },
            { "letter", Cls.Idle }, { "alert", Cls.Idle }, { "dialog", Cls.Idle },        // the steward reads these; nobody speaks about them (silent_events)
        };
        // First-time reactions (core/firsts): what a pawn may react to once, and the words for "the first time you have ever ...".
        private static readonly Dictionary<string, string> FirstCues = new Dictionary<string, string>
        {
            { "snow", "seen snow fall" }, { "eclipse", "seen an eclipse" }, { "aurora", "seen an aurora" }, { "solar_flare", "seen a solar flare" },
            { "corpse", "seen a dead body" }, { "beautiful_room", "stood in a room this beautiful" }, { "no_table", "had to eat with no table" },
        };
        /// <summary>Vanilla interactions that are an argument a bystander may react to (core/bystanders).</summary>
        public static readonly HashSet<string> ArgumentInteractions = new HashSet<string> { "Insult", "Slight" };
        // What the game's own interactions are, in words the line prompt can use, so the line fits (an insult reads as an insult).
        // A chitchat names no topic of its own: describe the situation (a passing exchange), never the word. The marker phrase
        // is how DropsMeta later recognises a thought or line whose scene was only that.
        private const string Passing = "passing exchange";
        private const string PassingKind = "a passing exchange about something really going on around them right now";
        private static readonly Regex ChitchatSummary = new Regex(@"^(?<a>.+?) -> (?<b>.+?): Chitchat\.?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex MetaTalk = new Regex(
            @"chit[\s-]?chat|small[\s-]?talk|\bidle (talk|chatter|chat)\b|\b(all|this|that|enough|more|so much|too much|such)\s+(the\s+)?" +
            @"(talk|talking|chatter|chatting|yapping|yakking|gossip|jabber|prattle|blather)\b|" +
            @"\b(stop|quit|cut|enough|tired of|sick of)\b[^.!?]{0,20}\b(talking|chatting|chatter|yapping)\b|\bwhy (does|do|is) everyone (talk|chat)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        internal const string NoMetaRule = "Don't remark on the conversation or the act of talking itself; react to the people and what's happening.";
        private static readonly Dictionary<string, string> InteractionKind = new Dictionary<string, string>
        {
            { "Insult", "an insult" }, { "Slight", "a cutting remark that slights them" }, { "KindWords", "kind words" },
            { "Chitchat", PassingKind }, { "DeepTalk", "a deep, personal conversation" },
            { "RomanceAttempt", "a romantic advance" }, { "MarriageProposal", "a marriage proposal" }, { "Breakup", "ending their relationship" },
            { "ConvertIdeoAttempt", "an attempt to win them over to a belief" }, { "Reassure", "reassurance" }, { "Counsel", "counsel" },
        };
        private static readonly string[] SubjectRoles = { "victim", "subject", "target" };
        private static readonly string[] Firsts = { "death", "mental_break", "raid_start", "quest", "funeral", "lodger_arrive" };
        // game ticks per real second at each game speed: 1 = 60, 2 = 180, 3 = 360, dev speed 4 = 900 (speed 0 is a pause)
        private static readonly Dictionary<int, double> TicksPerS = new Dictionary<int, double> { { 0, 60.0 }, { 1, 60.0 }, { 2, 180.0 }, { 3, 360.0 }, { 4, 900.0 } };

        /// <summary>The RimWorld mod's wire names: /pawn, /colony, pawn, tick, save_epoch, expires_tick.</summary>
        public static readonly ProtocolNames WireNames = new ProtocolNames
        {
            ActorPath = "/pawn", ScenePath = "/scene", MindPath = "/mind", RosterPath = "/colony",
            ActorField = "pawn", TimeField = "tick", EpochField = "save_epoch", ExpiryField = "expires_tick"
        };

        private static readonly HashSet<string> RomanticInteractions = new HashSet<string> { "RomanceAttempt", "MarriageProposal", "Breakup" };

        private readonly string _blockA = BlockAText.Text;

        public override string Name { get { return "rimworld"; } }
        public override ProtocolNames Protocol { get { return WireNames; } }
        // The steward kit (steward_tools.py's StewardKit): one instance; SilentEvents is its silent_events ({"letter","alert","dialog"}).
        private readonly RimWorldLLM.Mind.RimWorld.StewardKit _stewardKit = new RimWorldLLM.Mind.RimWorld.StewardKit();
        private RimWorldStewardKit _stewardKitView;
        public override ISet<string> SilentEvents { get { return new HashSet<string>(_stewardKit.SilentEvents); } }
        public override object StewardKit { get { return _stewardKitView ?? (_stewardKitView = new RimWorldStewardKit(_stewardKit)); } }
        public override int UnitsPerDay { get { return 60000; } }      // ticks

        public override double UnitsPerSecond(int speed)
        {
            double v;
            return TicksPerS.TryGetValue(speed, out v) ? v : (speed < 1 ? 60.0 : 900.0);
        }

        public override IReadOnlyList<string> IntentLabels { get { return new[] { "none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain" }; } }
        // intents and vanilla interactions that are romantic: refused whenever either party is a minor (core/agegate)
        public override ISet<string> RomanticIntents { get { return new HashSet<string> { "flirt", "romance", "romantic", "seduce", "woo", "propose", "lovin" }; } }
        public override ISet<string> CommonWords { get { return new HashSet<string> { "colony", "colonist", "colonists", "rimworld", "pirates", "tribals", "mechanoids" }; } }
        public override string BlockA { get { return _blockA; } }
        public override ISet<string> ArgumentIntents { get { return new HashSet<string> { "insult", "slight" }; } }

        /// <summary>Cleaned facts about one character for creative prompts: trait labels, backstory title, how they feel now.</summary>
        public override JObject CreativeCard(JObject snap)
        {
            var traits = new List<string>();
            foreach (var t in Js.ItemsGet(snap, "traits").Take(5))
            {
                string lab = t is JObject ? (Js.Truthy(Js.Get(t, "label")) ? PyJson.Str(Js.Get(t, "label")) : Js.StrOr(t, "def", "")) : Js.Str(t);
                traits.Add(GameData.Clean(lab, "label"));
            }
            var bs = Js.Get(snap, "backstory");
            string title = "";
            if (bs is JObject)
            {
                foreach (var k in new[] { "adult", "child" })
                {
                    var v = Js.Get(bs, k);
                    if (v is JObject && Js.Truthy(Js.Get(v, "title"))) { title = GameData.Clean(Js.S(Js.Get(v, "title")), "label"); break; }
                }
            }
            else if (bs != null && bs.Type == JTokenType.String) title = GameData.Clean((string)bs, "label");
            var needs = Js.Get(snap, "needs");
            string mood = "";
            var m = Js.Get(needs, "mood");
            if (Js.IsNum(m, noBool: true))
            {
                double mv = Js.Dbl(m) <= 1 ? Js.Dbl(m) : Js.Dbl(m) / 100;
                mood = mv < 0.2 ? "very low" : mv < 0.4 ? "low" : mv < 0.65 ? "middling" : mv < 0.85 ? "good" : "high";
            }
            var ths = new JArray();
            foreach (var t in Js.ItemsGet(Js.Get(snap, "mood"), "thoughts").Take(2))
                if (t is JObject && Js.Truthy(Js.Get(t, "label"))) ths.Add(GameData.Clean(Js.S(Js.Get(t, "label")), "label"));
            return new JObject
            {
                { "traits", new JArray(traits.Where(t => t.Length > 0)) }, { "backstory_title", title }, { "mood", mood }, { "mood_thoughts", ths }
            };
        }

        public override Task<List<JObject>> ReplayAsync(string path, object cfg, object llm)
        {
            // TODO(csmind-merge): games/rimworld/replay.py is ported with the director/cli wave (RimWorld/Replay.cs).
            throw new NotSupportedException("replay is ported in a later wave");
        }

        public override string DefaultBridgeJson() { return DefaultBridgeJsonFor(Environment.GetEnvironmentVariable("HOME")); }

        public static string DefaultBridgeJsonFor(string home)
        {
            if (string.IsNullOrEmpty(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios", "RimWorldLLM", "bridge.json");
        }

        public override bool IsControlled(int actorId) { return false; }      // RimWorld has no single player character

        // ---- events
        public override Cls Priority(string eventType)
        {
            Cls c;
            return eventType != null && EventClass.TryGetValue(eventType, out c) ? c : Cls.Ambient;
        }

        public override JObject NormalizeEvent(JObject raw)
        {
            var e = (JObject)raw.DeepClone();
            JToken tick = e["tick"];
            JToken game = tick != null ? tick : (e["game"] ?? new JValue(0L));
            e.Remove("tick");
            e["game"] = Js.Int(game);
            JToken pawns = e["pawns"];
            JToken src = pawns != null ? pawns : (e["actors"] ?? new JArray());
            e.Remove("pawns");
            var actors = new JArray();
            foreach (var p in Js.Items(src))
                actors.Add(new JObject { { "id", Js.Int(p["id"]) }, { "role", Js.Get(p, "role") == null ? new JValue("") : Js.Get(p, "role").DeepClone() } });
            e["actors"] = actors;
            return e;
        }

        private static double SeverityOf(JObject ev, double dflt)
        {
            var s = Js.Get(ev, "severity");
            return s == null ? dflt : Js.Float(s);
        }

        private static JObject DataOf(JObject ev) { return Js.Get(ev, "data") as JObject ?? new JObject(); }

        public override double Importance(JObject ev, int actorId)
        {
            double sev = SeverityOf(ev, 0.0);
            var data = DataOf(ev);
            double imp = sev;
            if (Js.StrOr(ev, "type", null) == "death" || Js.Truthy(Js.Get(data, "died"))) imp = 1.0;
            if (data.ContainsKey("mood_offset")) imp = Math.Max(imp, Math.Min(1.0, Math.Abs(Js.Float(data["mood_offset"])) / 20.0));
            if (Js.Get(data, "opinion") != null) imp += Math.Min(0.3, Math.Abs(Js.Float(Js.Get(data, "opinion"))) / 100.0 * 0.3);
            foreach (var a in Js.ItemsGet(ev, "actors"))
                if (Js.Int(a["id"]) == actorId && SubjectRoles.Contains(Js.StrOr(a, "role", "\u0000")))
                    imp += 0.15;
            return Math.Max(0.0, Math.Min(1.0, imp));
        }

        public override bool IsPinned(JObject ev, int actorId, List<string> firsts)
        {
            var data = DataOf(ev);
            string type = Js.StrOr(ev, "type", "");
            if (type == "death" || Js.Truthy(Js.Get(data, "died")) || Js.Truthy(Js.Get(data, "betrayal"))) return true;
            if (SeverityOf(ev, 0.0) >= 0.9) return true;
            if (type == "first_cue") return true;                       // written only for a pawn's first: never folded away
            return !firsts.Contains(type) && Firsts.Contains(Js.StrOr(ev, "type", "\u0000"));
        }

        public override string FirstPerson(JObject ev, int actorId)
        {
            string summary = PyStr.Strip(Js.StrOr(ev, "summary", ""));
            var m = Js.StrOr(ev, "type", null) == "social" ? ChitchatSummary.Match(summary) : null;
            if (m != null && m.Success)                       // the game's label names the interaction, not what was said
                summary = m.Groups["a"].Value + " and " + m.Groups["b"].Value + " had a " + Passing + " while going about their day; what was said wasn't noted.";
            var roles = new Dictionary<int, string>();
            foreach (var a in Js.ItemsGet(ev, "actors")) roles[Js.Int(a["id"])] = Js.StrOr(a, "role", "");
            string role;
            if (roles.TryGetValue(actorId, out role) && SubjectRoles.Contains(role)) return "It happened to me: " + summary;
            if (roles.ContainsKey(actorId)) return "I was part of it: " + summary;
            return "I saw it: " + summary;
        }

        /// <summary>(key, phrase) when this event is a first-time cue: the mod's first_cue events, a first raid, a first colonist death.</summary>
        public override (string Key, string Phrase)? FirstOf(JObject ev)
        {
            string t = Js.StrOr(ev, "type", null);
            var data = DataOf(ev);
            if (t == "first_cue")
            {
                string c = Js.StrOr(data, "cue", "");
                string phrase;
                return FirstCues.TryGetValue(c, out phrase) ? (c, phrase) : ((string, string)?)null;
            }
            if (t == "raid_start") return ("raid", "seen a raid");
            if (t == "death" && !Js.Truthy(Js.Get(data, "animal"))) return ("colonist_death", "seen a colonist die");
            return null;
        }

        /// <summary>The two actors of a vanilla insult or slight, else null (core/bystanders).</summary>
        public override List<int> ArgumentParties(JObject ev)
        {
            var data = DataOf(ev);
            if (Js.StrOr(ev, "type", null) != "social" || !ArgumentInteractions.Contains(Js.StrOr(data, "interaction", ""))) return null;
            var pair = ConversationPair(ev);
            return pair == null ? null : new List<int> { pair.Value.Item1, pair.Value.Item2 };
        }

        /// <summary>A "gathering" event as plain data for core/group: kind, key, lead, couple, subject, participants and the game's own
        /// words (label, ideo, deceased), or null when it is not a usable gathering.</summary>
        public override JObject GatherInfo(JObject ev)
        {
            if (Js.StrOr(ev, "type", null) != "gathering") return null;
            var data = DataOf(ev);
            string kind = Js.StrOr(data, "kind", "");
            if (!new[] { "meal", "party", "wedding", "funeral", "speech", "ritual" }.Contains(kind)) return null;
            var roles = Js.ItemsGet(ev, "actors").Select(a => (Id: Js.Int(a["id"]), Role: Js.StrOr(a, "role", ""))).ToList();
            int? subject = roles.Where(r => r.Role == "subject").Select(r => (int?)r.Id).FirstOrDefault();
            var ids = roles.Where(r => r.Role != "subject").Select(r => r.Id).ToList();
            var leadTok = Js.Get(data, "lead");
            int? lead;
            string leadStr = Js.Str(leadTok);
            if ((leadTok != null && leadTok.Type == JTokenType.Integer) || (leadStr.Length > 0 && leadStr.All(ch => ch >= '0' && ch <= '9')))
                lead = Js.Int(leadTok);
            else lead = roles.Where(r => r.Role == "lead").Select(r => (int?)r.Id).FirstOrDefault();
            var couple = Js.ItemsGet(data, "couple").Select(x => Js.Int(x)).ToList();
            if (couple.Count == 0) couple = roles.Where(r => r.Role == "couple").Select(r => r.Id).ToList();
            JToken keyTok = Js.Truthy(Js.Get(data, "key")) ? Js.Get(data, "key") : Js.Get(ev, "seq");
            return new JObject
            {
                { "kind", kind }, { "key", Js.Str(keyTok) }, { "lead", lead.HasValue ? new JValue(lead.Value) : JValue.CreateNull() },
                { "couple", new JArray(couple.Take(2)) }, { "subject", subject.HasValue ? new JValue(subject.Value) : JValue.CreateNull() },
                { "participants", new JArray(ids) }, { "label", Js.StrOr(data, "label", "") }, { "ideo", Js.StrOr(data, "ideo", "") },
                { "room", Js.StrOr(data, "room", "") }, { "deceased", Js.StrOr(data, "deceased_name", "") },
            };
        }

        public override (int, int)? ConversationPair(JObject ev)
        {
            if (Js.StrOr(ev, "type", null) != "social") return null;
            var ids = Js.ItemsGet(ev, "actors").Select(a => Js.Int(a["id"])).ToList();
            return ids.Count >= 2 ? (ids[0], ids[1]) : ((int, int)?)null;
        }

        /// <summary>The game's own interaction between two actors, when the mod asked for words for it: reference, pair,
        /// priority tier ("high" or "idle"), the game's own sentence and what kind of thing it was.</summary>
        public override JObject VanillaSocial(JObject ev)
        {
            var data = DataOf(ev);
            if (Js.StrOr(ev, "type", null) != "social" || !Js.Truthy(Js.Get(data, "vanilla")) || !Js.Truthy(Js.Get(data, "ref"))) return null;
            var pair = ConversationPair(ev);
            if (pair == null) return null;
            string key = Js.StrOr(data, "interaction", "");
            var wt = Js.Get(data, "window_ticks");
            int windowTicks = Math.Max(1, Js.Truthy(wt) ? Js.Int(wt) : 417);
            var wr = Js.Get(data, "window_real_s");
            double windowReal = Js.Truthy(wr) ? Js.Float(wr) : 0.0;
            string sentence = PyStr.Strip(Js.Truthy(Js.Get(data, "vanilla_text")) ? PyJson.Str(Js.Get(data, "vanilla_text")) : Js.StrOr(ev, "summary", ""));
            string kind;
            if (!InteractionKind.TryGetValue(key, out kind) || kind.Length == 0)
                kind = Js.Truthy(Js.Get(data, "interaction_label")) ? PyJson.Str(Js.Get(data, "interaction_label")) : "a social moment";
            return new JObject
            {
                { "ref", Js.Str(Js.Get(data, "ref")) }, { "initiator", pair.Value.Item1 }, { "recipient", pair.Value.Item2 },
                { "tier", Js.StrOr(data, "tier", null) == "high" ? "high" : "idle" },
                { "window_ticks", windowTicks }, { "window_real_s", windowReal }, { "sentence", sentence },
                { "romantic", RomanticInteractions.Contains(key) }, { "kind", kind }, { "key", key },
                { "outcome", new JArray(Js.ItemsGet(data, "outcome").Select(x => Js.S(x).Trim()).Where(x => x.Length > 0).Take(3)) },
            };
        }

        // What each game interaction asks of the line: the intent and its tone. The line is about the people and the scene, not about a
        // random subject the game's play-log sentence names (a chitchat's "topic" is a flavour noun picked at random).
        private static readonly Dictionary<string, string> InteractionTone = new Dictionary<string, string>
        {
            { "Insult", "cutting: an open attack, aimed at something real in the scene or about the person" },
            { "Slight", "a small, cold put-down about something real in the scene or about the person" },
            { "KindWords", "warm and specific: kind words that fit who they are and how they are doing" },
            { "Chitchat", "casual and passing, about something that is really going on around you right now; never assume food or a meal unless someone is eating" },
            { "DeepTalk", "personal and unguarded: something from your own real history, fears or hopes, not the weather or work" },
            { "RomanceAttempt", "a romantic advance, in your own manner" },
            { "MarriageProposal", "a marriage proposal, in your own manner" },
            { "Breakup", "ending it between you, in your own manner" },
            { "ConvertIdeoAttempt", "trying to win them over to what you believe" },
            { "Reassure", "reassuring: steady words to someone who needs them" },
            { "Counsel", "counsel: advice that fits their trouble" },
        };
        // The topic in the game's log sentence is a random flavour noun for these: never passed on.
        private static readonly HashSet<string> TopicIsNoise = new HashSet<string> { "Chitchat", "DeepTalk" };
        private static readonly HashSet<string> LongInteractions = new HashSet<string>
            { "Insult", "DeepTalk", "RomanceAttempt", "MarriageProposal", "Breakup", "ConvertIdeoAttempt", "Counsel" };

        /// <summary>The line request for a vanilla interaction: what kind of thing it was, the tone it asks for, how it landed.</summary>
        public override string VanillaRequest(JObject info, string speaker, string listener)
        {
            string sentence = (string)info["sentence"], kind = (string)info["kind"];
            string key = info["key"] == null ? "" : (string)info["key"];
            string tone;
            bool known = InteractionTone.TryGetValue(key, out tone);
            string s = "";
            if (!TopicIsNoise.Contains(key) && sentence.Length > 0)
                s = key == "Insult" || key == "Slight"
                    ? " The game's own log reads: \"" + sentence + "\" Use its subject only if it fits what is really going on here."
                    : " The game's own log reads: \"" + sentence + "\"";
            var outcome = info["outcome"] as JArray;
            string landed = outcome != null && outcome.Count > 0 ? " How it landed (the game's own note): " + string.Join("; ", outcome.Select(x => (string)x)) + "." : "";
            return "=== REQUEST ===\n" +
                   speaker + " has just done something to " + listener + ": it is " + kind + "." + s + landed + "\n" +
                   (known ? "Tone: " + tone + ".\n" : "") +
                   "Write " + speaker + "'s line of speech to " + listener + " that goes with it, so it reads as " + kind.Split(new[] { " about " }, StringSplitOptions.None)[0] + ". " +
                   PromptBlocks.LengthHint(LongInteractions.Contains(key)) + " " + NoMetaRule;
        }

        /// <summary>A chitchat or deep talk carries no usable sentence: the scene only says they were talking.</summary>
        public override string VanillaTrigger(JObject info, string speaker, string listener)
        {
            string key = info["key"] == null ? "" : (string)info["key"];
            string sentence = (string)info["sentence"];
            if (key == "Chitchat") return speaker + " and " + listener + " are making small talk while going about their day.";
            if (key == "DeepTalk") return speaker + " is having a deep, personal talk with " + listener + ".";
            return sentence.Length > 0 ? sentence : speaker + " and " + listener + " had a " + Passing + ".";
        }

        public override List<SalientFact> SalientFacts(JObject snap, JObject scene, JObject listenerSnap, int listenerId, IEnumerable<JObject> events, long nowGame)
        { return SalienceFacts.Build(snap, scene, listenerSnap, listenerId, events, nowGame); }

        public override List<string> ListenerCard(JObject snap, JObject listenerSnap, int listenerId) { return SalienceFacts.ListenerCard(snap, listenerSnap, listenerId); }

        private static readonly HashSet<string> LongEvents = new HashSet<string> { "death", "injury", "mental_break", "raid_start", "funeral", "wedding" };

        public override bool EarnsLength(JObject ev)
        {
            if (ev == null) return false;
            string t = Js.StrOr(ev, "type", "");
            if (LongEvents.Contains(t)) return true;
            if (t == "social") return LongInteractions.Contains(Js.StrOr(DataOf(ev), "interaction", ""));
            return SeverityOf(ev, 0.0) >= 0.7;
        }

        /// <summary>True for a thought or line that is only meta-talk about chatting ("I wish people would cut this chitchat")
        /// when its scene (memory or trigger texts in context) was a passing chitchat. Any other scene keeps it.</summary>
        public override bool DropsMeta(string text, IEnumerable<string> context)
        {
            if (!MetaTalk.IsMatch(text ?? "")) return false;
            if (context == null) return false;
            return context.Any(c => (c ?? "").ToLowerInvariant().Contains(Passing));
        }

        // Routine social contact has no outcome: a chat, a deep talk, a passing exchange. An insult, a rebuff, romance, a breakup, a fight
        // or a told secret has one and stays.
        private static readonly Regex RoutineTalk = new Regex(
            @"passing exchange|chit[\s-]?chat|small[\s-]?talk|deep (talk|conversation)|\bdeeptalk\b|\bhad a (little |quick |short |nice |long |brief )?(chat|talk|conversation)\b|" +
            @"\btalked (with|to)\b|\bwere talking\b|\bchatted\b|\bkind words\b|\breassur|\bcounsel",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex TalkOutcome = new Regex(
            @"insult|slight|rebuff|reject|romanc|romantic|break ?up|broke up|propos|kiss|fight|quarrel|argu|secret|gossip|confid|\btold me\b|\bsnapp|\bangry|\bhurt my",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly HashSet<string> RoutineInteractions = new HashSet<string> { "Chitchat", "DeepTalk", "KindWords", "Reassure", "Counsel" };

        public override bool RoutineSocial(string etype, string text)
        {
            if (etype == "conversation") return true;                    // the template or model summary of a spoken talk: the relation carries its outcome
            if (etype != "social") return false;
            string t = text ?? "";
            return RoutineTalk.IsMatch(t) && !TalkOutcome.IsMatch(t);
        }

        public override bool RoutineSocialEvent(JObject ev)
        {
            if (Js.StrOr(ev, "type", "") != "social") return false;
            string inter = Js.StrOr(DataOf(ev), "interaction", "");
            if (inter.Length > 0) return RoutineInteractions.Contains(inter);
            return RoutineSocial("social", Js.StrOr(ev, "summary", ""));
        }

        /// <summary>The idle text filler core/idle runs last: fn(host, slot) -> texts stored.</summary>
        public override Func<ITextFillHost, int, Task<int>> IdleFill() { return TextFill.FillOnceAsync; }

        // ---- ongoing concerns and grudges (core/concerns, core/grudges)
        public override List<JObject> ConcernBirths(JObject ev, IList<int> holders, Func<int, int, bool> loved) { return ConcernRules.Births(ev, holders, loved); }
        public override List<(string Key, string Text)> ConcernResolutions(JObject ev) { return ConcernRules.Resolutions(ev, IsLedgerEnd); }
        public override (int Offender, int Victim, string Verb)? SocialOffence(JObject ev) { return ConcernRules.SocialOffence(ev); }
        public override ISet<string> OffenceIntents() { return new HashSet<string> { "insult" }; }
        public override string GrudgeStyle(JObject snap) { return ConcernRules.Style(snap); }

        public override bool IsUrgentVisual(JObject ev) { return new[] { "injury", "death", "raid_start", "mental_break" }.Contains(Js.StrOr(ev, "type", null)); }
        public override bool IsLedgerEnd(JObject ev) { return Js.StrOr(ev, "type", "").EndsWith("_end", StringComparison.Ordinal); }

        public override bool IsPrivate(JObject ev, int actorId)
        {
            var data = DataOf(ev);
            if (Js.Truthy(Js.Get(data, "secret"))) return true;
            if (Js.Truthy(Js.Get(data, "betrayal")))
                return Js.ItemsGet(ev, "actors").Any(a => Js.Int(a["id"]) == actorId && new[] { "perpetrator", "attacker", "speaker" }.Contains(Js.StrOr(a, "role", "\u0000")));
            return false;
        }

        public override bool IsMajorThreat(JObject ev)
        {
            return new[] { "death", "raid_start", "mental_break" }.Contains(Js.StrOr(ev, "type", null)) || SeverityOf(ev, 0) >= 0.9;
        }

        public override bool ChronicleWorthy(JObject ev)
        {
            string t = Js.StrOr(ev, "type", null);
            if (new[] { "death", "raid_start", "raid_end", "mental_break", "quest", "funeral", "lodger_arrive", "prisoner", "wedding" }.Contains(t)) return true;
            return SeverityOf(ev, 0) >= 0.5 && !new[] { "tend", "thought", "player_talk", "gathering", "gathering_end" }.Contains(t);
        }

        public override bool ChronicleTrigger(JObject ev) { return new[] { "death", "raid_end" }.Contains(Js.StrOr(ev, "type", null)); }

        public override ISet<string> AboutIntents() { return new HashSet<string> { "gossip" }; }

        public override IReadOnlyDictionary<string, string> ToldIntents()
        {
            return new Dictionary<string, string> { { "confide", "private" }, { "gossip", "public" } };
        }

        // ---- roster, snapshots, scenes
        public override List<(int Id, JToken RawName)> Roster(JObject resp)
        {
            return Js.ItemsGet(resp, "colonists").Select(c => (Js.Int(c["id"]), Js.Get(c, "name"))).ToList();
        }

        public override List<JObject> RosterLedger(JObject resp) { return Js.ItemsGet(resp, "ledger").Select(x => (JObject)x).ToList(); }

        public override NameInfo NameInfo(JToken rawName)
        {
            var p = new JObject { { "name", rawName == null ? JValue.CreateNull() : rawName.DeepClone() } };
            return new NameInfo(Builders.ShortName(p), Sanitize.NameTokens(Builders.DisplayName(p)));
        }

        public override JToken SnapshotName(JObject snap) { return Js.Get(snap, "name"); }

        /// <summary>Biological age in years, from the pawn snapshot (the mod sends AgeBiologicalYears); null when absent.</summary>
        public override double? SnapshotAge(JObject snap)
        {
            var a = Js.Get(snap, "age");
            return Js.IsNum(a, noBool: true) ? Js.Dbl(a) : (double?)null;
        }

        /// <summary>The job as text: the pawn route sends a string or {def, label} (main c16670a); both read here.</summary>
        public override string SnapshotJob(JObject snap) { return Js.JobText(Js.Get(snap, "job")); }

        public override List<(int Id, int? Opinion)> Relations(JObject snap)
        {
            var outp = new List<(int, int?)>();
            foreach (var r in Js.ItemsGet(snap, "relations"))
            {
                var who = Js.Get(r, "pawn");
                if (who == null || who.Type != JTokenType.Integer) continue;
                var op = Js.Get(r, "opinion");
                outp.Add(((int)(long)who, op == null ? (int?)null : Js.Int(op)));
            }
            return outp;
        }

        public override List<(int Id, bool CanHear)> ScenePresent(JObject scene)
        {
            var outp = new List<(int, bool)>();
            foreach (var q in Js.ItemsGet(scene, "present"))
            {
                var qo = q as JObject;
                if (qo == null || !qo.ContainsKey("pawn")) continue;
                JToken ch;
                bool hear = !qo.TryGetValue("can_hear", out ch) || PyJson.Truthy(ch);
                outp.Add((Js.Int(qo["pawn"]), hear));
            }
            return outp;
        }

        public override Scene BuildScene(JObject snap, JObject scene, string trigger, IDictionary<int, string> names, ISet<int> known, ISet<int> minors = null)
        {
            var room = Js.Get(scene, "room") as JObject ?? new JObject();
            string where = Js.Truthy(Js.Get(room, "role")) ? PyJson.Str(Js.Get(room, "role")) : "outdoors";
            var extras = new List<string>();
            var cl = Js.Get(room, "clean");
            if (Js.IsNum(cl, noBool: true)) { double c = Js.Dbl(cl); if (c < -3) extras.Add("filthy"); else if (c < -1) extras.Add("dirty"); else if (c > 1.5) extras.Add("spotless"); }
            else if (Js.Truthy(cl) && cl.Type == JTokenType.String && (string)cl != "clean") extras.Add((string)cl);   // a plain word from the mod: kept
            var tp = Js.Get(room, "temp");
            if (Js.IsNum(tp, noBool: true)) { double t = Js.Dbl(tp); if (t <= -15) extras.Add("freezing"); else if (t <= 0) extras.Add("cold"); else if (t >= 45) extras.Add("dangerously hot"); else if (t >= 33) extras.Add("hot"); }
            else if (Js.Truthy(tp) && tp.Type == JTokenType.String) extras.Add((string)tp);
            if (extras.Count > 0) where += " (" + string.Join(", ", extras) + ")";
            var todTok = Js.Get(scene, "time_of_day");
            if (todTok is JObject) todTok = Js.Get(todTok, "label");
            string when = string.Join(", ", new[] { todTok, Js.Get(scene, "weather") }.Where(x => Js.Truthy(x)).Select(x => PyJson.Str(x)));
            var present = new List<string>();
            foreach (var q in Js.ItemsGet(scene, "present"))
            {
                var pid = Js.Get(q, "pawn");
                if (pid == null || pid.Type != JTokenType.Integer || !known.Contains((int)(long)pid)) continue;
                string n;
                if (names.TryGetValue((int)(long)pid, out n) && !string.IsNullOrEmpty(n))
                {
                    string tag = minors != null && minors.Contains((int)(long)pid) ? "minor, " : "";
                    string job = Js.JobText(Js.Get(q, "job"));
                    present.Add(n + " (" + tag + (job.Length > 0 ? job : "idle") + ")");
                }
            }
            var mood = Js.Get(snap, "mood");
            var needs = Js.Get(snap, "needs");
            var h = Js.Get(snap, "health");
            var st = new List<string>();
            var nm = Js.Get(needs, "mood");
            if (nm != null && Js.IsNum(nm, noBool: true))
            {
                double mv = Js.Dbl(nm); if (mv > 1) mv /= 100;
                st.Add(mv < 0.2 ? "mood very low" : mv < 0.35 ? "mood low" : mv < 0.65 ? "mood middling" : mv < 0.85 ? "mood good" : "mood high");
            }
            if (Js.Truthy(Js.Get(h, "downed"))) st.Add("downed");
            var pain = Js.Get(h, "pain");
            if (Js.Truthy(pain) && Js.Dbl(pain) > 0.3) st.Add("in pain");
            var trends = Js.Get(scene, "trends") as JObject;
            if (trends != null)
                foreach (var kv in trends)
                {
                    var v = kv.Value as JArray;
                    if (v != null && v.Count >= 2 && Js.IsNum(v[0], noBool: true) && Js.IsNum(v[v.Count - 1], noBool: true))
                    {
                        double d = Js.Dbl(v[v.Count - 1]) - Js.Dbl(v[0]);
                        if (Math.Abs(d) > 1e-9) st.Add(kv.Key.Replace("_", " ") + (d < 0 ? " falling" : " rising"));
                    }
                }
            var ths = new List<string>();
            foreach (var t in Js.ItemsGet(mood, "thoughts").Take(4))
            {
                var off = Js.Get(t, "offset");
                long r = off == null ? 0 : PyFmt.RoundInt(Js.Float(off));
                ths.Add(Js.Str(Js.Get(t, "label")) + " (" + (r >= 0 ? "+" : "-") + Math.Abs(r).ToString(CultureInfo.InvariantCulture) + ")");
            }
            var (now, shortPhrase) = Register.SpeechNow(snap);
            string sjob = Js.JobText(Js.Get(snap, "job"));
            double? age = SnapshotAge(snap);
            return new Scene
            {
                Where = where, When = when.Length > 0 ? when : "daytime", Present = present, State = st.Count > 0 ? string.Join(", ", st) : "ordinary",
                SpeechNow = now, SpeechShort = shortPhrase, Job = sjob.Length > 0 ? sjob : "idle", Thoughts = ths, Trigger = trigger,
                MinorSelf = AgeGate.IsMinor(age.HasValue ? (object)age.Value : null),
            };
        }

        // ---- minds
        public override string FactsHash(JObject snap) { return Builders.FactsHash(snap); }
        public override FixedBio BuildFixedBio(JObject snap, IDictionary<int, string> names) { return Builders.BuildFixedBio(snap, names); }
        public override VoiceCard BuildVoiceCard(JObject snap) { return Builders.BuildVoiceCard(snap); }

        /// <summary>The effective register for the inline prompts (thought, diary, dream, bystander); a temporary state is named,
        /// so a drunk pawn's thought reads drunk and not merely simple.</summary>
        public override string SpeechRegister(JObject snap)
        {
            var states = Register.TempStates(snap);
            string words = Register.EffectiveWords(snap);
            return states.Count > 0 ? "right now " + string.Join("; ", states.Select(t => t.Phrase)) + ", so " + words : words;
        }

        public override double TalkWeight(JObject snap) { return Builders.TalkWeight(snap); }
        public override string VoiceExamplePrompt(string bio, string registerLine, string tics) { return Builders.VoicePrompt(bio, registerLine, tics); }
    }
}
