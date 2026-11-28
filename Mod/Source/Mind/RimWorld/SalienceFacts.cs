// Turns the speaker's snapshot, the listener's snapshot, the scene and the recent events into SalientFacts for the salience picker.
// Pure (JObject in, list out). Only facts the snapshot actually has; nothing is invented. Unremarkable states get a low weight or are skipped.
// Texts are plain words: no numbers the model is told never to say (opinion, mood percent, temperature degrees stay as words).
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind.RimWorld
{
    public static class SalienceFacts
    {
        public const double TicksPerHour = 2500.0;
        private const int TextCap = 160;

        /// <summary>Everything that could stand out. listenerSnap / listenerId may be null / -1 (no listener); events are
        /// ledger entries or events: { type, summary, game, data }.</summary>
        public static List<SalientFact> Build(JObject snap, JObject scene, JObject listenerSnap, int listenerId, IEnumerable<JObject> events, long nowGame)
        {
            var facts = new List<SalientFact>();
            Room(facts, scene);
            WeatherTimeSeason(facts, scene);
            Job(facts, snap);
            Nearby(facts, scene, listenerId);
            Events(facts, events, nowGame);
            Injuries(facts, snap);
            Needs(facts, snap);
            Mood(facts, snap);
            Listener(facts, snap, listenerSnap, listenerId);
            History(facts, snap);
            return facts;
        }

        private static string Cap(string s) { s = (s ?? "").Trim(); return s.Length <= TextCap ? s : s.Substring(0, TextCap).TrimEnd(); }

        private static void Add(List<SalientFact> l, string kind, string key, string text, double w, double age = 0, bool listener = false, bool social = false)
        {
            if (string.IsNullOrWhiteSpace(text) || w <= 0) return;
            l.Add(new SalientFact(kind, key, Cap(text), Math.Min(1.0, w), age, listener, social));
        }

        // ---- scene
        private static void Room(List<SalientFact> l, JObject scene)
        {
            var room = Js.Get(scene, "room") as JObject;
            if (room == null) return;
            string role = Js.S(Js.Get(room, "role")).Trim();
            if (role.Length > 0)
                Add(l, FactKind.Room, "room:role", role == "outdoors" ? "You are outdoors." : "You are in the " + role + ".", 0.2);
            var clean = Js.Get(room, "clean");
            if (Js.IsNum(clean, true))
            {
                double c = Js.Dbl(clean);
                if (c < -3) Add(l, FactKind.Room, "room:clean", "The room is filthy.", 0.65);
                else if (c < -1) Add(l, FactKind.Room, "room:clean", "The room is dirty.", 0.45);
                else if (c > 1.5) Add(l, FactKind.Room, "room:clean", "The room is spotless.", 0.3);
            }
            var imp = Js.Get(room, "impressiveness");
            if (Js.IsNum(imp, true) && role != "outdoors")
            {
                double v = Js.Dbl(imp);
                if (v >= 50) Add(l, FactKind.Room, "room:impressive", "The room is impressive.", 0.45);
                else if (v >= 25) Add(l, FactKind.Room, "room:impressive", "The room is nice.", 0.25);
                else if (v <= 5) Add(l, FactKind.Room, "room:impressive", "The room is bare and grim.", 0.3);
            }
            var temp = Js.Get(room, "temp");
            if (Js.IsNum(temp, true))
            {
                double t = Js.Dbl(temp);
                if (t <= -15) Add(l, FactKind.Room, "room:temp", "It is freezing cold here.", 0.8);
                else if (t <= 0) Add(l, FactKind.Room, "room:temp", "It is cold here.", 0.5);
                else if (t >= 45) Add(l, FactKind.Room, "room:temp", "The heat here is dangerous.", 0.8);
                else if (t >= 33) Add(l, FactKind.Room, "room:temp", "It is hot here.", 0.5);
            }
        }

        private static void WeatherTimeSeason(List<SalientFact> l, JObject scene)
        {
            string w = Js.S(Js.Get(scene, "weather")).Trim();
            if (w.Length > 0)
            {
                string lw = w.ToLowerInvariant();
                double wt = lw.Contains("toxic") || lw.Contains("flare") || lw.Contains("eclipse") || lw.Contains("blizzard") || lw.Contains("fallout") ? 0.75
                    : lw.Contains("rain") || lw.Contains("snow") || lw.Contains("fog") || lw.Contains("storm") || lw.Contains("hail") || lw.Contains("thunder") || lw.Contains("drizzle") ? 0.5
                    : lw.Contains("clear") || lw.Contains("fair") ? 0.08 : 0.15;
                Add(l, FactKind.Weather, "weather", "The weather: " + lw + ".", wt);
            }
            var tod = Js.Get(scene, "time_of_day");
            string label = tod is JObject ? Js.S(Js.Get(tod, "label")) : Js.S(tod);
            label = label.Trim().ToLowerInvariant();
            if (label.Length > 0)
                Add(l, FactKind.Time, "time", "It is " + label + ".", label == "night" ? 0.35 : label == "dawn" || label == "evening" ? 0.25 : 0.1);
            string season = Js.S(Js.Get(scene, "season")).Trim().ToLowerInvariant();
            if (season.Length > 0)
                Add(l, FactKind.Season, "season", "The season: " + season + ".", season.Contains("winter") ? 0.3 : 0.12);
        }

        private static void Job(List<SalientFact> l, JObject snap)
        {
            string job = Js.JobText(Js.Get(snap, "job")).Trim();
            if (job.Length == 0) return;
            string lj = job.ToLowerInvariant();
            bool idle = lj.Contains("idle") || lj.Contains("wander") || lj.Contains("standing") || lj.Contains("waiting");
            Add(l, FactKind.Job, "job", "You are " + char.ToLowerInvariant(job[0]) + job.Substring(1) + ".", idle ? 0.15 : 0.4);
        }

        private static void Nearby(List<SalientFact> l, JObject scene, int listenerId)
        {
            var near = new List<(string Name, string Job, double Dist)>();
            foreach (var q in Js.ItemsGet(scene, "present"))
            {
                var pid = Js.Get(q, "pawn");
                if (pid != null && pid.Type == JTokenType.Integer && (int)(long)pid == listenerId) continue;
                string name = Js.S(Js.Get(q, "name")).Trim();
                if (name.Length == 0) continue;
                var d = Js.Get(q, "distance");
                near.Add((name, Js.JobText(Js.Get(q, "job")).Trim(), Js.IsNum(d, true) ? Js.Dbl(d) : 99));
            }
            foreach (var n in near.OrderBy(x => x.Dist).ThenBy(x => x.Name, StringComparer.Ordinal).Take(3))
                Add(l, FactKind.Nearby, "nearby:" + n.Name, n.Job.Length > 0 ? n.Name + " is nearby, " + char.ToLowerInvariant(n.Job[0]) + n.Job.Substring(1) + "." : n.Name + " is nearby.",
                    n.Job.Length > 0 ? 0.3 : 0.15);
        }

        // ---- events
        private static readonly Dictionary<string, double> EventWeight = new Dictionary<string, double>
        {
            { "death", 1.0 }, { "raid_start", 0.9 }, { "mental_break", 0.9 }, { "fire", 0.7 }, { "injury", 0.7 }, { "birth", 0.7 }, { "wedding", 0.7 },
            { "funeral", 0.6 }, { "raid_end", 0.6 }, { "lodger_arrive", 0.5 }, { "arrival", 0.5 }, { "prisoner", 0.5 }, { "quest", 0.45 },
            { "weather_extreme", 0.5 }, { "level_up", 0.3 }, { "first_cue", 0.5 }, { "gathering", 0.4 },
        };
        private static readonly HashSet<string> SilentTypes = new HashSet<string> { "letter", "alert", "dialog", "tend", "thought", "player_talk" };
        private static readonly string[] Outcomes = { "insult", "slight", "rebuff", "reject", "romance", "romantic", "fight", "marriage", "breakup", "proposal" };

        private static void Events(List<SalientFact> l, IEnumerable<JObject> events, long nowGame)
        {
            foreach (var e in events ?? Enumerable.Empty<JObject>())
            {
                if (e == null) continue;
                string type = Js.S(Js.Get(e, "type")).Trim();
                if (type.Length == 0) type = Js.S(Js.Get(e, "etype")).Trim();
                string summary = Js.S(Js.Get(e, "summary")).Trim();
                if (summary.Length == 0 || SilentTypes.Contains(type)) continue;
                long game = Js.IsNum(Js.Get(e, "game"), true) ? (long)Js.Dbl(Js.Get(e, "game")) : (Js.IsNum(Js.Get(e, "opened_time"), true) ? (long)Js.Dbl(Js.Get(e, "opened_time")) : nowGame);
                double age = Math.Max(0, (nowGame - game) / TicksPerHour);
                string key = "event:" + type + ":" + game;
                if (type == "social")
                {
                    string inter = Js.S(Js.Get(Js.Get(e, "data"), "interaction")).ToLowerInvariant() + " " + summary.ToLowerInvariant();
                    bool outcome = Outcomes.Any(o => inter.Contains(o));
                    Add(l, FactKind.Social, key, summary, outcome ? 0.8 : 0.3, age, false, true);
                    continue;
                }
                double w;
                if (!EventWeight.TryGetValue(type, out w)) w = 0.35;
                var sev = Js.Get(e, "severity");
                if (Js.IsNum(sev, true)) w = Math.Max(w, Math.Min(1.0, Js.Dbl(sev)));
                Add(l, FactKind.Event, key, summary, w, age);
            }
        }

        // ---- the speaker
        private static void Injuries(List<SalientFact> l, JObject snap)
        {
            var h = Js.Get(snap, "health");
            if (h == null) return;
            var conds = new List<(string Label, string Part, double Sev, bool Tended)>();
            foreach (var c in Js.ItemsGet(h, "conditions"))
            {
                string label = Js.S(Js.Get(c, "label")).Trim();
                if (label.Length == 0) continue;
                var sv = Js.Get(c, "severity");
                conds.Add((label, Js.S(Js.Get(c, "part")).Trim(), Js.IsNum(sv, true) ? Js.Dbl(sv) : 0, Js.Truthy(Js.Get(c, "tended"))));
            }
            foreach (var c in conds.OrderByDescending(x => x.Sev).ThenBy(x => x.Label, StringComparer.Ordinal).Take(2))
                Add(l, FactKind.Wound, "injury:" + c.Label + ":" + c.Part,
                    (c.Part.Length > 0 ? "Your " + c.Part.ToLowerInvariant() + ": " : "You have ") + c.Label.ToLowerInvariant() + (c.Tended ? " (tended)." : " (not tended yet)."),
                    c.Tended ? 0.35 : 0.65);
            var pain = Js.Get(h, "pain");
            if (Js.IsNum(pain, true) && Js.Dbl(pain) >= 0.6) Add(l, FactKind.Wound, "injury:pain", "You are in a lot of pain.", 0.8);
            else if (Js.IsNum(pain, true) && Js.Dbl(pain) > 0.3) Add(l, FactKind.Wound, "injury:pain", "You are in pain.", 0.5);
            if (Js.Truthy(Js.Get(h, "downed"))) Add(l, FactKind.Wound, "injury:downed", "You are down and cannot get up.", 0.9);
        }

        private static void Needs(List<SalientFact> l, JObject snap)
        {
            var n = Js.Get(snap, "needs");
            double v;
            if (Num(n, "food", out v)) { if (v < 0.15) Add(l, FactKind.Need, "need:food", "You are starving.", 0.9); else if (v < 0.3) Add(l, FactKind.Need, "need:food", "You are hungry.", 0.55); }
            if (Num(n, "rest", out v)) { if (v < 0.15) Add(l, FactKind.Need, "need:rest", "You are exhausted.", 0.8); else if (v < 0.3) Add(l, FactKind.Need, "need:rest", "You are tired.", 0.5); }
            if (Num(n, "comfort", out v) && v < 0.2) Add(l, FactKind.Need, "need:comfort", "You are uncomfortable.", 0.35);
            if (Num(n, "joy", out v) && v < 0.15) Add(l, FactKind.Need, "need:joy", "You are bored and have had no fun lately.", 0.4);
        }

        private static bool Num(JToken o, string key, out double v)
        {
            var t = Js.Get(o, key);
            v = Js.IsNum(t, true) ? Js.Dbl(t) : 0;
            return Js.IsNum(t, true);
        }

        private static readonly string[] ChatWords = { "chat", "talk", "conversation", "chitchat" };

        private static void Mood(List<SalientFact> l, JObject snap)
        {
            var mood = Js.Get(snap, "mood");
            var mv = Js.Get(mood, "value");
            if (Js.IsNum(mv, true))
            {
                double m = Js.Dbl(mv);
                if (m <= 1.0 + 1e-9)
                {
                    if (m < 0.2) Add(l, FactKind.Mood, "mood:overall", "Your mood is very low.", 0.8);
                    else if (m < 0.35) Add(l, FactKind.Mood, "mood:overall", "Your mood is low.", 0.5);
                    else if (m > 0.85) Add(l, FactKind.Mood, "mood:overall", "You are in good spirits.", 0.35);
                }
            }
            foreach (var t in Js.ItemsGet(mood, "thoughts").Select(x => new { L = Js.S(Js.Get(x, "label")).Trim(), O = Js.IsNum(Js.Get(x, "offset"), true) ? Js.Dbl(Js.Get(x, "offset")) : 0 })
                .Where(x => x.L.Length > 0 && Math.Abs(x.O) >= 3).OrderByDescending(x => Math.Abs(x.O)).ThenBy(x => x.L, StringComparer.Ordinal).Take(3))
            {
                bool chat = ChatWords.Any(w => t.L.ToLowerInvariant().Contains(w));
                double w = Math.Min(0.8, 0.2 + Math.Abs(t.O) / 20.0);
                Add(l, FactKind.Mood, "mood:" + t.L, (t.O < 0 ? "Weighing on you: " : "Lifting you: ") + t.L.ToLowerInvariant() + ".", w, 0, false, chat);
            }
        }

        private static void History(List<SalientFact> l, JObject snap)
        {
            string arrival = Js.S(Js.Get(Js.Get(snap, "history"), "arrival")).Trim();
            if (arrival.Length > 0) Add(l, FactKind.History, "history:arrival", "How you came to the colony: " + arrival, 0.12);
        }

        // ---- the listener
        /// <summary>An opinion as words, never a number.</summary>
        public static string OpinionWords(int opinion)
        {
            return opinion <= -60 ? "can't stand" : opinion <= -20 ? "dislike" : opinion < 20 ? "feel nothing much for" : opinion < 60 ? "like" : "are close to";
        }

        private static string Third(int opinion)
        {
            return opinion <= -60 ? "can't stand" : opinion <= -20 ? "dislikes" : opinion < 20 ? "feels nothing much for" : opinion < 60 ? "likes" : "is close to";
        }

        /// <summary>What a pawn shows to others, in words: hurt, exhausted, upset, ... Empty when nothing shows.</summary>
        public static List<string> VisibleState(JObject snap)
        {
            var s = new List<string>();
            if (snap == null) return s;
            var h = Js.Get(snap, "health");
            if (Js.Truthy(Js.Get(h, "downed"))) s.Add("down on the ground");
            var pain = Js.Get(h, "pain");
            if (Js.IsNum(pain, true) && Js.Dbl(pain) > 0.3) s.Add("in pain");
            if (Js.ItemsGet(h, "conditions").Any(c => !Js.Truthy(Js.Get(c, "tended")) && Js.IsNum(Js.Get(c, "severity"), true) && Js.Dbl(Js.Get(c, "severity")) > 0.3)) s.Add("hurt");
            double v;
            var n = Js.Get(snap, "needs");
            if (Num(n, "rest", out v) && v < 0.2) s.Add("exhausted");
            if (Num(n, "food", out v) && v < 0.2) s.Add("hungry");
            var mv = Js.Get(Js.Get(snap, "mood"), "value");
            if (Js.IsNum(mv, true) && Js.Dbl(mv) < 0.3) s.Add("upset");
            else if (Js.IsNum(mv, true) && Js.Dbl(mv) > 0.85) s.Add("cheerful");
            if (Js.Truthy(Js.Get(snap, "asleep"))) s.Add("asleep");
            return s;
        }

        /// <summary>The relation of one pawn to another from the first's snapshot: (kind, opinion), null when not listed.</summary>
        public static (string Kind, int? Opinion)? RelationTo(JObject snap, int otherId)
        {
            foreach (var r in Js.ItemsGet(snap, "relations"))
            {
                var p = Js.Get(r, "pawn");
                if (p == null || p.Type != JTokenType.Integer || (int)(long)p != otherId) continue;
                var op = Js.Get(r, "opinion");
                return (Js.S(Js.Get(r, "kind")).Trim(), Js.IsNum(op, true) ? (int?)(int)Math.Round(Js.Dbl(op)) : null);
            }
            return null;
        }

        public static int SelfId(JObject snap) { var i = Js.Get(snap, "id"); return i != null && i.Type == JTokenType.Integer ? (int)(long)i : -1; }

        /// <summary>The listener for a "who you are talking to" section: lines like "Doing: ...", "Looks: ...", "You and them: ...". In words, no numbers.</summary>
        public static List<string> ListenerCard(JObject snap, JObject lis, int listenerId)
        {
            var o = new List<string>();
            if (lis == null) return o;
            string job = Js.JobText(Js.Get(lis, "job")).Trim();
            if (job.Length > 0) o.Add("Doing: " + job);
            var state = VisibleState(lis);
            if (state.Count > 0) o.Add("Looks: " + string.Join(", ", state.Take(4)));
            var mine = RelationTo(snap, listenerId);
            if (mine.HasValue)
            {
                string kind = mine.Value.Kind;
                bool kin = kind.Length > 0 && kind != "acquaintance";
                string op = mine.Value.Opinion.HasValue ? "you " + OpinionWords(mine.Value.Opinion.Value) + " them" : "";
                if (kin || op.Length > 0) o.Add("You and them: " + (kin ? "your " + kind : "an acquaintance") + (op.Length > 0 ? "; " + op : ""));
            }
            int me = SelfId(snap);
            var theirs = me >= 0 ? RelationTo(lis, me) : null;
            if (theirs.HasValue && theirs.Value.Opinion.HasValue) o.Add("What they think of you: they " + OpinionWords(theirs.Value.Opinion.Value) + " you");
            return o;
        }

        private static void Listener(List<SalientFact> l, JObject snap, JObject lis, int listenerId)
        {
            if (lis == null) return;
            string name = Js.StrOr(Js.Get(lis, "name"), "first", "");
            if (name.Length == 0) name = Js.S(Js.Get(lis, "name")).Trim();
            if (name.Length == 0) name = "They";
            var state = VisibleState(lis);
            if (state.Count > 0)
                Add(l, FactKind.Listener, "listener:state", name + " looks " + string.Join(", ", state.Take(3)) + ".", state.Contains("down on the ground") ? 0.9 : state.Contains("hurt") || state.Contains("in pain") ? 0.7 : 0.5, 0, true);
            string job = Js.JobText(Js.Get(lis, "job")).Trim();
            if (job.Length > 0) Add(l, FactKind.Listener, "listener:job", name + " is " + char.ToLowerInvariant(job[0]) + job.Substring(1) + ".", 0.35, 0, true);
            var mine = RelationTo(snap, listenerId);
            if (mine.HasValue)
            {
                string kind = mine.Value.Kind;
                bool kin = kind.Length > 0 && kind != "acquaintance";
                string op = mine.Value.Opinion.HasValue ? "you " + OpinionWords(mine.Value.Opinion.Value) + " " + name : "";
                string txt = kin ? name + " is your " + kind + (op.Length > 0 ? "; " + op : "") + "." : op.Length > 0 ? "You " + OpinionWords(mine.Value.Opinion.Value) + " " + name + "." : "";
                double w = kin ? 0.5 : mine.Value.Opinion.HasValue && Math.Abs(mine.Value.Opinion.Value) >= 40 ? 0.5 : 0.25;
                Add(l, FactKind.Listener, "listener:relation", txt, w, 0, true);
            }
            int me = SelfId(snap);
            var theirs = me >= 0 ? RelationTo(lis, me) : null;
            if (theirs.HasValue && theirs.Value.Opinion.HasValue)
                Add(l, FactKind.Listener, "listener:their_opinion", name + " " + Third(theirs.Value.Opinion.Value) + " you.", Math.Abs(theirs.Value.Opinion.Value) >= 40 ? 0.45 : 0.2, 0, true);
        }
    }
}
