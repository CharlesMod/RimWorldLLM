// Port of agent/rimworldllm_agent/games/rimworld/moments.py: which RimWorld events are a documentary moment, and how heavy
// (core/commentary.py asks the adapter).
//
// Weights (0..1; below 0.4 nothing is said): colony_lost 1.0, death 0.95, raid_start 0.9, ship_part 0.8, birth 0.75,
// legendary masterwork 0.7, wedding 0.7, a joiner or accepted quest 0.5, funeral 0.5, a social fight 0.5, any other mental
// break 0.6, masterwork 0.5, party 0.4. Urgent (skip the game-hour gap, keep the 30 s floor): colony_lost, death, raid_start, birth.
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind.RimWorld
{
    public static class Moments
    {
        private const RegexOptions Ro = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        private static readonly Regex Join = new Regex(@"\b(joins?|joined|wanderer|asks? to join|quest (?:accepted|begun|started)|accepted the quest|" +
                                                       @"refugee|new colonist)\b", Ro);
        private static readonly Regex Quest = new Regex(@"\bquest\b.*\b(accepted|begun|started)\b|\baccepted\b.*\bquest\b", Ro);

        private static string S(JObject o, string key, string dflt = "")
        {
            JToken t = o == null ? null : o[key];
            return t == null ? dflt : PyJson.Str(t);
        }

        private static JObject Obj(JObject o, string key)
        {
            JToken t = o == null ? null : o[key];
            return PyJson.Truthy(t) ? t as JObject : null;
        }

        private static List<JObject> Actors(JObject e)
        {
            var a = e["actors"] as JArray;
            return a == null ? new List<JObject>() : a.OfType<JObject>().ToList();
        }

        private static string Who(JObject e)
        {
            return string.Join(",", Actors(e).Take(3).Select(a => a["id"] == null ? "None" : PyJson.Str(a["id"])));
        }

        private static string Or(string a, string b) { return string.IsNullOrEmpty(a) ? b : a; }

        /// <summary>moments.moment(event, director): the documentary moment of an event, or null.</summary>
        public static Moment ForEvent(JObject e, ICommentaryHost d = null)
        {
            string t = S(e, "type");
            JObject data = Obj(e, "data") ?? new JObject();
            int game = e["game"] != null && PyJson.Truthy(e["game"]) ? (int)JobsJson.PyInt(e["game"]) : 0;
            string summary = S(e, "summary");
            string text = PyStr.Strip(Or(summary, t));
            bool minor = d != null && Actors(e).Any(a => a["id"] != null && d.IsMinor((int)JobsJson.PyInt(a["id"])));

            Moment Mk(string kind, double weight, bool urgent = false, string key = null, string text_ = null)
                => new Moment(kind, Or(key, kind + ":" + Who(e)), Or(text_, text), weight, urgent, game, minor);

            if (t == "colony_lost") return Mk("colony_lost", 1.0, true, "colony_lost", Or(text, "The last colonist is gone."));
            if (t == "death") return Mk("death", 0.95, true);
            if (t == "raid_start") return Mk("raid_start", 0.9, true, "raid_start");           // one comment per raid wave window
            if (t == "ship_part")
                return Mk("ship_part", 0.8, false, "ship_part:" + S(data, "thing"),
                    "A ship part was finished: " + Or(PyJson.Truthy(data["label"]) ? S(data, "label") : "", text));
            if (t == "birth")
            {
                JObject p = Obj(data, "pawn") ?? new JObject();
                return Mk("birth", 0.75, true, "birth:" + (p["id"] == null ? Who(e) : PyJson.Str(p["id"])));
            }
            if (t == "gathering")
            {
                string k = S(data, "kind").ToLowerInvariant();
                if (k == "wedding") return Mk("wedding", 0.7, false, "wedding:" + Who(e));
                if (k == "party") return Mk("party", 0.4, false, "party");
                return null;
            }
            if (t == "funeral") return Mk("funeral", 0.5);
            if (t == "mental_break")
            {
                bool fight = S(data, "state") == "SocialFighting";
                return Mk(fight ? "social_fight" : "mental_break", fight ? 0.5 : 0.6, false);
            }
            if (t == "masterwork")
            {
                bool legendary = S(data, "quality") == "legendary";
                string label = PyJson.Truthy(data["label"]) ? S(data, "label") : PyJson.Truthy(data["thing"]) ? S(data, "thing") : "a work";
                string maker = S(Obj(data, "maker"), "name");
                string words = PyStr.Strip(maker + " made a " + (legendary ? "legendary" : "masterwork") + " " + label);
                return Mk("masterwork", legendary ? 0.7 : 0.5, false, "masterwork:" + S(data, "thing"), words);
            }
            if (t == "letter" || t == "dialog" || t == "quest")
            {
                var parts = new List<string>();
                foreach (JToken x in new[] { e["summary"], data["title"], data["label"], data["kind"] })
                    if (PyJson.Truthy(x)) parts.Add(PyJson.Str(x));
                string blob = string.Join(" ", parts);
                if (Quest.IsMatch(blob) || t == "quest") return Mk("quest", 0.5, false, "quest:" + PyStr.Head(blob, 40));
                if (Join.IsMatch(blob)) return Mk("joiner", 0.5, false, "joiner:" + PyStr.Head(blob, 40));
            }
            return null;
        }
    }
}
