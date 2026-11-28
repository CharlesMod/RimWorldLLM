// The steward's command tools (task rh-tools-command): job, medical, prisoner, animal, social.
//
// Built from the digest's small `cmd` block (the game side writes it; no block, no tools). Each tool is one POST /act/<verb>:
// job -> job, medical -> medical, prisoner -> prisoner, animal -> animal, social -> social. Targets are words (names, room
// labels or ids, "#id" from the find tool), never cells. A tool appears only when the digest says it can matter:
//   job      whenever there is a cmd block (colonists exist)
//   medical  someone is hurt or sick, or a surgery is possible
//   prisoner prisoners exist; convert/enslave only with Ideology; execute only when the player's setting allows it
//   animal   colony animals or tameable wild ones nearby
//   social   a gathering is possible (the game says so; routine turns, so rare)
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    public sealed class CommandKit
    {
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", "a brief reason in your own words, starting with since or because; it is shown to the player",
            maxLen: WhyMax, required: false, sameCall: true);

        public static readonly string[] ToolNames = { "job", "medical", "prisoner", "animal", "social" };
        /// <summary>The cmd block names this long at most (the game caps the lists; this is the mind's own guard).</summary>
        private const int MaxNames = 8;

        public readonly bool Has;
        public readonly IDictionary<string, int> Pawns;
        public readonly List<string> Patients, Prisoners, Animals, Wild;
        public readonly bool Surgery, SocialOk, Ideology, MayExecute;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Names;

        private static List<string> Strs(JObject c, string key)
        {
            var l = new List<string>();
            if (DigestJson.G(c, key) is JArray a)
                foreach (var t in a) if (t.Type == JTokenType.String) l.Add(GameData.Clean((string)t, key));
            return l.Take(MaxNames).ToList();
        }

        private static bool Flag(JObject c, string key) => DigestJson.G(c, key) is JValue v && v.Type == JTokenType.Boolean && (bool)v;

        public CommandKit(JObject digest)
        {
            var c = DigestJson.G(digest, "cmd") as JObject;
            Has = c != null;
            Pawns = StewardDigest.PawnNames(digest);
            if (!Has)
            {
                Patients = Prisoners = Animals = Wild = new List<string>();
                Tools = new List<ToolSpec>(); Names = new HashSet<string>(StringComparer.Ordinal);
                return;
            }
            Patients = Strs(c, "patients"); Prisoners = Strs(c, "prisoners"); Animals = Strs(c, "animals"); Wild = Strs(c, "wild");
            Surgery = Flag(c, "surgery"); SocialOk = Flag(c, "social"); Ideology = Flag(c, "ideo"); MayExecute = Flag(c, "may_execute");
            Tools = BuildTools();
            Names = new HashSet<string>(Tools.Select(t => t.Name), StringComparer.Ordinal);
        }

        private static Param Enum(string name, string desc, IEnumerable<string> choices, bool required = true)
            => new Param(name, "enum", desc, choices: choices, required: required);

        private static Param Text(string name, string desc, int max = 60, bool required = true)
            => new Param(name, "text", desc, maxLen: max, required: required);

        private List<ToolSpec> BuildTools()
        {
            var outl = new List<ToolSpec>();
            if (Pawns.Count > 0)
                outl.Add(new ToolSpec("job", "Order a colonist to do one thing now, like a right-click.", new List<Param>
                {
                    Enum("op", "haul item to storage, rescue a downed pawn, tend a patient, clean a room, repair/refuel a building, equip a weapon, wear apparel, drop an item, consume food or drug, go to a place, stay still (drafted)", CommandPure.JobOps),
                    Text("who", "the colonist"),
                    Text("target", "name, room label or id, or #id from find; none for stay", 80, required: false),
                    WhyParam,
                }));
            if (Patients.Count > 0 || Surgery)
                outl.Add(new ToolSpec("medical", "Medicine: plan an operation, bed rest, or a care level.", new List<Param>
                {
                    Enum("op", "operate adds a surgery bill (leave what empty to list the choices); rest sends them to a medical bed; care sets the level", CommandPure.MedicalOps),
                    Text("who", "the patient"),
                    Text("what", "surgery label, or care: " + string.Join(", ", CommandPure.CareLevels), 80, required: false),
                    WhyParam,
                }));
            if (Prisoners.Count > 0)
            {
                var modes = CommandPure.PrisonerModes.Where(m => (m != "execute" || MayExecute) && ((m != "convert" && m != "enslave") || Ideology)).ToList();
                outl.Add(new ToolSpec("prisoner", "Set what to do with a prisoner.", new List<Param>
                {
                    Enum("op", "mode sets the interaction; free lets them go now", CommandPure.PrisonerOps),
                    Text("who", "the prisoner"),
                    Enum("mode", "none, chat (lower resistance), recruit" + (Ideology ? ", convert, enslave" : "") + ", release" + (MayExecute ? ", execute" : ""), modes, required: false),
                    WhyParam,
                }));
            }
            if (Animals.Count > 0 || Wild.Count > 0)
                outl.Add(new ToolSpec("animal", "Animals: tame a wild one, train, slaughter, release, keep to an area.", new List<Param>
                {
                    Enum("op", "tame/slaughter mark it; train and area need what", CommandPure.AnimalOps),
                    Text("who", "the animal's name, or a wild kind to tame"),
                    Text("what", "training (obedience, release, rescue, haul) or area name", required: false),
                    WhyParam,
                }));
            if (SocialOk)
                outl.Add(new ToolSpec("social", "Start a party, a wedding or another gathering.", new List<Param>
                {
                    Enum("op", "party, wedding, or other (name it in what)", CommandPure.SocialOps),
                    Text("who", "the organiser, or one of the couple", required: false),
                    Text("what", "gathering label for other", required: false),
                    WhyParam,
                }));
            return outl;
        }

        private static string Val(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken t) || t.Type == JTokenType.Null) return "";
            string s = PyStr.Strip(PyJson.Str(t));
            return s == "none" || s == "None" ? "" : s;
        }

        private string Colonist(string name)
        {
            foreach (var k in Pawns.Keys) if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return k;
            throw new PyValueError("no colonist named " + name + "; the colonists are: " + string.Join(", ", Pawns.Keys));
        }

        public (string verb, JObject body) Body(string tool, JObject v)
        {
            var b = new JObject();
            string who = Val(v, "who");
            if (who.Length == 0 && tool != "social") throw new PyValueError("who is required");
            switch (tool)
            {
                case "job":
                    b["op"] = Val(v, "op"); b["who"] = Colonist(who);
                    if (Val(v, "target").Length > 0) b["target"] = Val(v, "target");
                    break;
                case "medical":
                    b["op"] = Val(v, "op"); b["who"] = who;
                    if (Val(v, "what").Length > 0) b["what"] = Val(v, "what");
                    break;
                case "prisoner":
                    b["op"] = Val(v, "op"); b["who"] = who;
                    if (Val(v, "op") == "mode")
                    {
                        if (Val(v, "mode").Length == 0) throw new PyValueError("mode is required for op mode");
                        b["mode"] = Val(v, "mode");
                    }
                    break;
                case "animal":
                    b["op"] = Val(v, "op"); b["who"] = who;
                    if (Val(v, "what").Length > 0) b["what"] = Val(v, "what");
                    break;
                case "social":
                    b["op"] = Val(v, "op");
                    if (who.Length > 0) b["who"] = who;
                    if (Val(v, "what").Length > 0) b["what"] = Val(v, "what");
                    break;
                default: throw new PyAttributeError("CommandKit has no attribute _" + tool);
            }
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out JToken wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) b["why"] = why;
            return (tool, b);
        }

        /// <summary>One digest line: who is hurt, held, kept, or wild nearby (the names the tools take). Empty when there is nothing.</summary>
        public static string Line(JObject d)
        {
            var c = DigestJson.G(d, "cmd") as JObject;
            if (c == null) return "";
            var parts = new List<string>();
            foreach (var (key, label) in new[] { ("patients", "hurt/sick"), ("prisoners", "prisoners"), ("animals", "animals"), ("wild", "tameable wild") })
            {
                var l = Strs(c, key);
                if (l.Count > 0) parts.Add(label + ": " + string.Join(", ", l));
            }
            return parts.Count == 0 ? "" : "PEOPLE: " + string.Join("; ", parts);
        }
    }
}
