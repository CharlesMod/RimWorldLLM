// Port of games/rimworld/steward_tools.py: the RimWorld side of the steward (core/steward.py): which events wake it, its
// prompt files, the digest renderer, and the game tools. Each game tool maps to one POST /act/<verb> of the bridge
// (docs/BRIDGE-API.md, "Vivarium routes"); names from the model become ids through the digest, and anything unknown comes
// back as a tool error the model reads.
//
// The core steward calls StewardKit.Session(digest, bridge, advisor) once per cycle and gets a StewardSession: the tools
// for this digest (enums built from the colonists, beds, letters and policies in it), the set of acting tools, and RunAsync.
//
// Differences from Python: tool values arrive as a JObject of the values ToolSpec.Check produced (JValue strings and
// integers). str.lower() is ToLowerInvariant. The prompt files are the embedded copies (StewardResources).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>What a game tool did: the object the model reads back, and for a success the verb and the bridge's short note.</summary>
    public sealed class Outcome
    {
        public JObject Result;
        public string Verb = "";
        public string Note = "";
        public bool Ok => Result.ContainsKey("done");

        public Outcome(JObject result, string verb = "", string note = "") { Result = result; Verb = verb; Note = note; }
    }

    /// <summary>One cycle's game tools, built from one digest.</summary>
    public sealed class StewardSession
    {
        public readonly JObject Digest;
        public readonly IBridge Bridge;
        public readonly bool Advisor;
        /// <summary>Cleaned colonist name -> pawn id, in the digest's order.</summary>
        public readonly Dictionary<string, int> Pawns;
        public readonly Dictionary<string, bool> IsColonist = new Dictionary<string, bool>(StringComparer.Ordinal);
        public readonly Dictionary<string, JObject> Beds = new Dictionary<string, JObject>(StringComparer.Ordinal);
        public readonly Dictionary<string, JObject> Letters = new Dictionary<string, JObject>(StringComparer.Ordinal);
        public readonly Dictionary<string, JObject> Dialogs = new Dictionary<string, JObject>(StringComparer.Ordinal);
        public readonly List<string> Work;
        public readonly Dictionary<string, List<string>> Policies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public readonly List<JObject> Research;
        public readonly List<ToolSpec> Tools;
        public readonly HashSet<string> Acting;
        /// <summary>The builder tools; they exist only when the digest has a build block.</summary>
        public readonly BuildKit Build;
        public readonly PlaceKit Place;
        /// <summary>The rezone tool; it exists only when the digest lists a zone or a hydroponics basin.</summary>
        public readonly ZonesKit Zones;
        /// <summary>The colonists the digest marks incapable of violence (they stay out of "draft all").</summary>
        public readonly HashSet<string> CannotFight = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The combat tools (squad, defend): squad needs a combat view, defend also a build block.</summary>
        public readonly CombatKit Combat;
        /// <summary>The trade tools (trade_offer, trade); they exist only when the digest has a trade block.</summary>
        public readonly TradeKit Trade;
        /// <summary>The Biotech tools (mechs, genes, children); they exist only when the digest has a bio block.</summary>
        public readonly BioKit Bio;
        /// <summary>The read tools inspect, find, survey (offered when the digest has pawns); the 4-reads-per-turn counter lives here, one session per turn.</summary>
        public readonly InspectKit Inspect;
        /// <summary>The command tools (job, medical, prisoner, animal, social); they exist only when the digest has a cmd block.</summary>
        public readonly CommandKit Cmd;
        /// <summary>Cleaned colonist name -> age, for the pawns whose digest entry has a numeric age.</summary>
        public readonly Dictionary<string, double> Ages = new Dictionary<string, double>(StringComparer.Ordinal);
        public const int ChildAge = 13;
        public const string ChildRefusal = "children stay out of fights";

        public StewardSession(JObject digest, IBridge bridge, bool advisor, bool afterFight = false, bool catalog = false)
        {
            Digest = digest ?? new JObject(); Bridge = bridge; Advisor = advisor;
            Pawns = StewardDigest.PawnNames(Digest);
            foreach (var p in DigestJson.Items(DigestJson.G(Digest, "pawns")))
            {
                string name = GameData.Name(DigestJson.G(p, "name"));
                var jo = p as JObject;
                bool colonist = jo == null || !jo.TryGetValue("role", out JToken role) || (role.Type == JTokenType.String && (string)role == "colonist");
                IsColonist[name] = colonist;
            }
            foreach (var b in DigestJson.Items(DigestJson.G(Digest, "beds")))
                if (b is JObject bo && DigestJson.Truthy(DigestJson.G(bo, "id"))) Beds[PyJson.Str(bo["id"])] = bo;
            foreach (var l in DigestJson.Items(DigestJson.G(Digest, "letters")))
                if (l is JObject lo && DigestJson.Truthy(DigestJson.G(lo, "id"))) Letters[PyJson.Str(lo["id"])] = lo;
            foreach (var x in DigestJson.Items(DigestJson.G(Digest, "dialogs")))
                if (x is JObject xo && DigestJson.Truthy(DigestJson.G(xo, "id"))) Dialogs[PyJson.Str(xo["id"])] = xo;
            Work = DigestJson.Items(DigestJson.G(Digest, "work_types")).Select(w => PyJson.Str(w)).ToList();
            if (Work.Count == 0) Work = new List<string>(StewardKit.WorkFallback);
            if (DigestJson.G(Digest, "policies") is JObject pol)
                foreach (var kv in pol.Properties())
                    if (kv.Value is JArray arr) Policies[kv.Name] = arr.Select(x => PyJson.Str(x)).ToList();
            Research = DigestJson.Items(DigestJson.G(DigestJson.G(Digest, "research"), "available")).OfType<JObject>().ToList();
            foreach (var p in DigestJson.Items(DigestJson.G(Digest, "pawns")))
                if (p is JObject po && IncapableOfViolence(DigestJson.G(po, "incapable"))) CannotFight.Add(GameData.Name(DigestJson.G(po, "name")));
            Place = new PlaceKit(Digest);
            Build = new BuildKit(Digest);                        // the builder tools exist only when the digest has a build block
            Cmd = new CommandKit(Digest);
            Zones = new ZonesKit(Digest);
            Bio = new BioKit(Digest);                            // the Biotech tools exist only when the digest has a bio block
            foreach (var p in DigestJson.Items(DigestJson.G(Digest, "pawns")))
                if (p is JObject ao && DigestJson.IsNumberish(DigestJson.G(ao, "age"))) Ages[GameData.Name(DigestJson.G(ao, "name"))] = DigestJson.PyFloat(DigestJson.G(ao, "age"));
            Combat = new CombatKit(DigestJson.G(Digest, "combat"), Pawns, Build, afterFight);   // squad needs a combat view; defend also a build block
            Trade = new TradeKit(Digest);                        // trade_offer, trade: only with a trade block (caravans are off)
            Inspect = new InspectKit(Digest);
            var tools = BuildTools();
            if (!catalog && Combat.Tools.Count > 0 && Combat.Tools.Any(t => t.Name == "squad"))
                tools = tools.Where(t => t.Name != "order").ToList();   // in a fight squad orders replace single orders (cells by name, not x/z)
            Tools = tools.Concat(Build.Tools).Concat(Place.Tools).Concat(Zones.Tools).Concat(Combat.Tools).Concat(Trade.Tools).Concat(Bio.Tools).Concat(Cmd.Tools).Concat(Inspect.Tools).ToList();
            Acting = new HashSet<string>(Tools.Select(t => t.Name).Where(n => StewardKit.Acting.Contains(n) || Build.Names.Contains(n) || Place.Names.Contains(n) || Zones.Names.Contains(n) || Combat.Names.Contains(n) || Trade.Acting.Contains(n) || Bio.Names.Contains(n) || Cmd.Names.Contains(n)), StringComparer.Ordinal);
        }

        /// <summary>"Violent" in (p.get("incapable") or []): a list's item, a string's substring, a dict's key.</summary>
        private static bool IncapableOfViolence(JToken t)
        {
            if (!DigestJson.Truthy(t)) return false;
            if (t.Type == JTokenType.String) return ((string)t).Contains("Violent");
            return DigestJson.Items(t).Any(x => x.Type == JTokenType.String && (string)x == "Violent");
        }

        // ---- the tool list for this digest ----------------------------------------------
        public const int WhyMax = 80;
        private static readonly Param WhyParam = new Param("why", "text", StewardKit.WhyText, maxLen: WhyMax, required: false, sameCall: true);

        private List<ToolSpec> BuildTools()
        {
            var names = Pawns.Keys.ToList();
            var outl = new List<ToolSpec>
            {
                new ToolSpec("set_time", "Pause or unpause the game, or set its speed. Only what the settings allow; pausing is for real emergencies.",
                    new[]
                    {
                        new Param("pause", "enum", "pause the game, unpause it, or leave it", choices: new[] { "leave", "yes", "no" }),
                        new Param("speed", "enum", "game speed 1 to 4, or leave", choices: new[] { "leave", "1", "2", "3", "4" }),
                        new Param("emergency", "enum", "yes only for a real emergency pause", choices: new[] { "no", "yes" }),
                        WhyParam,
                    }),
            };
            if (names.Count > 0)
            {
                Func<Param> pawn = () => new Param("pawn", "enum", "which colonist, by name", choices: names);
                outl.Add(new ToolSpec("set_work", "Set one colonist's priority for one kind of work: 1 is done first, 4 last, 0 is off.",
                    new[] { pawn(), new Param("work", "enum", "work type", choices: Work), new Param("priority", "int", "0 off, 1 highest, 4 lowest", lo: 0, hi: 4), WhyParam }));
                outl.Add(new ToolSpec("set_schedule", "Set one colonist's schedule for the hours from_hour up to to_hour (0 to 24).",
                    new[]
                    {
                        pawn(), new Param("from_hour", "int", "first hour, 0 to 23", lo: 0, hi: 23), new Param("to_hour", "int", "end hour, 0 to 24", lo: 0, hi: 24),
                        new Param("assignment", "enum", "what they do in those hours", choices: new[] { "anything", "work", "joy", "sleep", "meditate" }), WhyParam,
                    }));
                outl.Add(new ToolSpec("draft", "Draft or undraft colonists. pawns is a comma-separated list of names, or all.",
                    new[]
                    {
                        new Param("pawns", "text", "names separated by commas, or all", maxLen: 160),
                        new Param("drafted", "enum", "draft or undraft", choices: new[] { "yes", "no" }), WhyParam,
                    }));
                outl.Add(new ToolSpec("order", "Give one drafted colonist an order: move to a cell, attack a target (by id), or hold position.",
                    new[]
                    {
                        pawn(), new Param("order", "enum", "the order", choices: new[] { "move", "attack", "hold" }),
                        new Param("x", "int", "move: cell x (0 if unused)", lo: 0, hi: 999), new Param("z", "int", "move: cell z (0 if unused)", lo: 0, hi: 999),
                        new Param("target_id", "int", "attack: the target's id (0 if unused)", lo: 0, hi: 999999),
                        new Param("hold", "enum", "hold: yes to hold, no to stop holding", choices: new[] { "yes", "no" }), WhyParam,
                    }));
                if (new[] { "outfit", "food", "drug" }.Any(k => Policies.TryGetValue(k, out var l) && l.Count > 0))
                    outl.Add(new ToolSpec("set_policy", "Give one colonist an outfit, food or drug policy by name (see POLICIES).",
                        new[]
                        {
                            pawn(), new Param("kind", "enum", "which policy", choices: new[] { "outfit", "food", "drug" }),
                            new Param("name", "text", "the policy's name", maxLen: 60), WhyParam,
                        }));
                if (Beds.Count > 0)
                    outl.Add(new ToolSpec("assign_bed", "Assign one colonist to a bed (by the bed's id in BEDS).",
                        new[] { pawn(), new Param("bed_id", "enum", "the bed's id", choices: Beds.Keys.ToList()), WhyParam }));
            }
            outl.Add(new ToolSpec("set_research", "Choose the research project to work on, by name (see RESEARCH).",
                new[] { new Param("project", "text", "the project's name", maxLen: 80), WhyParam }));
            if (Letters.Count > 0)
                outl.Add(new ToolSpec("answer_letter", "Answer a letter that needs a choice (an offer, a quest) with one of its choices, or dismiss it.",
                    new[]
                    {
                        new Param("letter_id", "enum", "the letter's id", choices: Letters.Keys.ToList()),
                        new Param("choice", "text", "one of the letter's choices, or dismiss", maxLen: 80), WhyParam,
                    }));
            if (Dialogs.Count > 0 && !Advisor)
                outl.Add(new ToolSpec("choose", "Answer an open dialog or choice letter (see OPEN DIALOGS) with one of its options; for a naming "
                    + "dialog give text instead: pick a fitting short name yourself (for the colony, a plain evocative place or family name; "
                    + "for two names write faction | settlement).",
                    new[]
                    {
                        new Param("dialog", "enum", "the dialog's id", choices: Dialogs.Keys.ToList()),
                        new Param("option", "text", "the option's number or label; leave empty for a naming dialog", maxLen: 120, required: false),
                        new Param("text", "text", "naming dialogs only: the name", maxLen: 80, required: false),
                        new Param("passions", "text", "growth moments only: the skills to take passions in, separated by commas", maxLen: 120, required: false),
                        new Param("trait", "text", "growth moments only: the trait's label", maxLen: 80, required: false), WhyParam,
                    }));
            return outl;
        }

        // ---- running a tool ---------------------------------------------------------------
        public string Validate(string tool, JObject values) => null;

        /// <summary>Run one tool: its values become one POST /act/&lt;verb&gt;. Unknown names, policies and the like come back as
        /// {"error": ...}; a refusal by the game as {"refused": ...}; a success as {"done": note, ...}.</summary>
        public async Task<Outcome> RunAsync(string tool, JObject v)
        {
            if (Array.IndexOf(TradeKit.Looks, tool) >= 0)        // a look tool GETs a route and returns text; it is not an action
            {
                try { return new Outcome(new JObject { ["result"] = await Trade.LookAsync(tool, v, Bridge).ConfigureAwait(false) }); }
                catch (PyValueError e) { return new Outcome(new JObject { ["error"] = e.Message }); }
                catch (BridgeError e)
                {
                    if (e.Code == "refused") return new Outcome(new JObject { ["refused"] = e.Detail.Length > 0 ? e.Detail : e.Code });
                    return new Outcome(new JObject { ["error"] = PyStr.Head((e.Code + ": " + e.Detail).Trim(':', ' '), 160) });
                }
            }
            if (Array.IndexOf(InspectKit.Names, tool) >= 0) return new Outcome(await Inspect.RunAsync(tool, v, Bridge).ConfigureAwait(false));
            if (tool == "undo" && !Zones.TakeUndo()) return new Outcome(new JObject { ["refused"] = ZonesKit.UndoRefusal });   // one undo a turn
            string verb;
            JObject body;
            try { (verb, body) = Body(tool, v); }
            catch (PyValueError e) { return new Outcome(new JObject { ["error"] = e.Message }); }
            JObject r;
            try { r = await Bridge.ActAsync(verb, body).ConfigureAwait(false); }
            catch (BridgeError e)
            {
                if (e.Code == "refused") return new Outcome(new JObject { ["refused"] = e.Detail.Length > 0 ? e.Detail : "the game refused it" });
                string msg = PyStr.Head((e.Code + ": " + e.Detail).Trim(':', ' '), 160);
                return new Outcome(new JObject { ["error"] = msg });
            }
            string note = r.TryGetValue("done", out JToken dt) ? PyJson.Str(dt) : "";
            var result = new JObject { ["done"] = note };
            foreach (var p in r.Properties())
                if (p.Name != "ok" && p.Name != "tick" && p.Name != "save_epoch" && p.Name != "paused" && p.Name != "speed" && p.Name != "done")
                    result[p.Name] = p.Value.DeepClone();
            return new Outcome(result, verb, note);
        }

        private static string Ci(IEnumerable<string> options, string wanted)
        {
            string w = PyStr.Strip(wanted).ToLowerInvariant();
            foreach (var o in options) if (o.ToLowerInvariant() == w) return o;
            return null;
        }

        private static string Opt(JObject v, string k)
            => PyStr.Strip(v.TryGetValue(k, out JToken t) && PyJson.Truthy(t) ? PyJson.Str(t) : "");

        private JObject Choose(JObject v)
        {
            string did = S(v, "dialog");
            if (!Dialogs.TryGetValue(did, out JObject dlg)) throw new PyValueError("no such dialog; the open dialogs are: " + string.Join(", ", Dialogs.Keys));
            var body = new JObject { ["dialog"] = did };
            string text = Opt(v, "text"), opt = Opt(v, "option");
            var fields = DigestJson.Items(DigestJson.G(dlg, "fields")).Where(f => DigestJson.Truthy(DigestJson.G(f, "name"))).Select(f => PyJson.Str(DigestJson.G(f, "name"))).ToList();
            var g = DigestJson.G(dlg, "growth") as JObject;
            if (g != null && g.Count > 0 && opt.Length == 0)     // a growth moment: pick passions and a trait from what is offered
            {
                var offered = DigestJson.Items(DigestJson.G(g, "passions")).Select(x => PyJson.Str(x)).ToList();
                var traits = DigestJson.Items(DigestJson.G(g, "trait_choices")).OfType<JObject>().Select(t => DigestJson.Str(t, "label", "None")).ToList();
                var picks = (v.TryGetValue("passions", out JToken pt) && PyJson.Truthy(pt) ? PyJson.Str(pt) : "").Split(',').Select(x => PyStr.Strip(x)).Where(x => x.Length > 0).ToList();
                string trait = Opt(v, "trait");
                if (offered.Count > 0 && picks.Count == 0)
                {
                    JToken mp = DigestJson.G(g, "max_passions");
                    throw new PyValueError("give passions (up to " + (DigestJson.Truthy(mp) ? DigestJson.PyInt(mp) : 1) + ", comma separated) from: " + string.Join(", ", offered));
                }
                if (traits.Count > 0 && trait.Length == 0) throw new PyValueError("give trait, one of: " + string.Join(", ", traits));
                if (picks.Count > 0) body["passions"] = new JArray(picks.Cast<object>().ToArray());
                if (trait.Length > 0) body["trait"] = trait;
                return body;
            }
            if (fields.Count > 0)                                // a naming dialog
            {
                if (text.Length == 0) text = opt;
                if (text.Length == 0) throw new PyValueError("this dialog asks for a name: give text");
                var parts = text.Split('|').Select(x => PyStr.Strip(x)).Where(x => x.Length > 0).ToList();
                if (fields.Count == 1) body["text"] = parts.Count > 0 ? parts[0] : text;
                else if (parts.Count == fields.Count)
                {
                    var o = new JObject();
                    for (int i = 0; i < fields.Count; i++) o[fields[i]] = parts[i];
                    body["text"] = o;
                }
                else throw new PyValueError("this dialog needs " + string.Join(" | ", fields) + " (write the names separated by |)");
                return body;
            }
            var live = DigestJson.Items(DigestJson.G(dlg, "options")).Where(o => !DigestJson.Truthy(DigestJson.G(o, "disabled"))).ToList();
            Func<string> optionList = () => string.Join(", ", live.Select(x => PyJson.Str(DigestJson.G(x, "index")) + " " + PyJson.Str(DigestJson.G(x, "label"))));
            if (opt.Length == 0) throw new PyValueError("give option: " + optionList());
            var labels = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (var x in live)
            {
                JToken lb = DigestJson.G(x, "label");
                labels[(lb == null ? "" : PyJson.Str(lb)).ToLowerInvariant()] = DigestJson.G(x, "index");
            }
            var idx = new HashSet<string>(live.Select(x => PyJson.Str(DigestJson.G(x, "index"))), StringComparer.Ordinal);
            string ostr = PyStr.Strip(opt.TrimStart('#'));
            if (idx.Contains(ostr))
            {
                if (!long.TryParse(ostr, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long n))
                    throw new PyValueError("invalid literal for int() with base 10: " + PyJson.StrRepr(ostr));
                body["option"] = n;
            }
            else if (labels.TryGetValue(opt.ToLowerInvariant(), out JToken li)) body["option"] = DigestJson.PyInt(li);
            else throw new PyValueError("that dialog's options are: " + optionList());
            return body;
        }

        /// <summary>Under 13 by the digest's age: vanilla cannot draft them, and children stay out of fights.</summary>
        private bool IsChild(string name) => (Ages.TryGetValue(name, out double a) ? a : 99) < ChildAge;

        private int PawnId(string name)
        {
            if (!Pawns.TryGetValue(name, out int id))
                throw new PyValueError("no colonist named " + name + "; the colonists are: " + string.Join(", ", Pawns.Keys));
            return id;
        }

        private static string S(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken t)) throw new PyKeyError(k);
            return PyJson.Str(t);
        }

        private static long I(JObject v, string k)
        {
            if (!v.TryGetValue(k, out JToken t)) throw new PyKeyError(k);
            return DigestJson.PyInt(t);
        }

        private (string verb, JObject body) Body(string tool, JObject v)
        {
            var (verb, body) = VerbBody(tool, v);
            JToken wt;
            string why = PyStr.Strip(GameData.Clean(v.TryGetValue("why", out wt) && PyJson.Truthy(wt) ? PyJson.Str(wt) : "", "why", WhyMax));
            if (why.Length > 0) body["why"] = why;
            return (verb, body);
        }

        internal (string verb, JObject body) VerbBody(string tool, JObject v)
        {
            switch (tool)
            {
                case "set_time":
                    {
                        var b = new JObject();
                        if (S(v, "pause") != "leave") b["pause"] = S(v, "pause") == "yes";
                        if (S(v, "speed") != "leave") b["speed"] = I(v, "speed");
                        if (b.Count == 0) throw new PyValueError("nothing to change: set pause or speed");
                        b["emergency"] = S(v, "emergency") == "yes";
                        return ("time", b);
                    }
                case "set_work":
                    return ("work", new JObject { ["pawn"] = PawnId(S(v, "pawn")), ["priorities"] = new JObject { [S(v, "work")] = v["priority"].DeepClone() } });
                case "set_schedule":
                    {
                        if (I(v, "to_hour") == I(v, "from_hour")) throw new PyValueError("from_hour and to_hour are the same");
                        return ("schedule", new JObject
                        {
                            ["pawn"] = PawnId(S(v, "pawn")), ["from_hour"] = v["from_hour"].DeepClone(), ["to_hour"] = v["to_hour"].DeepClone(),
                            ["assignment"] = S(v, "assignment"),
                        });
                    }
                case "set_policy":
                    {
                        string kind = S(v, "kind");
                        var opts = Policies.TryGetValue(kind, out var l) ? l : new List<string>();
                        string name = Ci(opts, S(v, "name"));
                        if (name == null)
                            throw new PyValueError("no " + kind + " policy named " + S(v, "name") + "; the " + kind + " policies are: " + string.Join(", ", opts));
                        return ("policy", new JObject { ["pawn"] = PawnId(S(v, "pawn")), ["kind"] = kind, ["name"] = name });
                    }
                case "assign_bed":
                    {
                        if (!Beds.ContainsKey(S(v, "bed_id"))) throw new PyValueError("no such bed; the beds are: " + string.Join(", ", Beds.Keys));
                        return ("bed", new JObject { ["pawn"] = PawnId(S(v, "pawn")), ["bed"] = I(v, "bed_id") });
                    }
                case "set_research":
                    {
                        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var r in Research)
                        {
                            JToken def = DigestJson.G(r, "def"), label = DigestJson.G(r, "label");
                            string val = PyJson.Str(DigestJson.Truthy(def) ? def : label);
                            labels[DigestJson.Str(r, "label", "").ToLowerInvariant()] = val;
                            labels[DigestJson.Str(r, "def", "").ToLowerInvariant()] = val;
                        }
                        string project = S(v, "project");
                        labels.TryGetValue(PyStr.Strip(project).ToLowerInvariant(), out string hit);
                        if (Research.Count > 0 && hit == null)
                            throw new PyValueError("no available project named " + project + "; available: "
                                + string.Join(", ", Research.Select(r => PyJson.Str(DigestJson.G(r, "label")))));
                        return ("research", new JObject { ["project"] = string.IsNullOrEmpty(hit) ? project : hit });
                    }
                case "answer_letter":
                    {
                        string lid = S(v, "letter_id");
                        if (!Letters.TryGetValue(lid, out JObject let)) throw new PyValueError("no such letter; the letters are: " + string.Join(", ", Letters.Keys));
                        var ch = DigestJson.Items(DigestJson.G(let, "choices")).Select(c => PyJson.Str(c)).ToList();
                        string choice = S(v, "choice");
                        string pick = PyStr.Strip(choice).ToLowerInvariant() == "dismiss" ? "dismiss" : Ci(ch, choice);
                        if (pick == null)
                        {
                            var all = new List<string>(ch) { "dismiss" };
                            throw new PyValueError("that letter's choices are: " + string.Join(", ", all));
                        }
                        return ("letter", new JObject { ["letter"] = I(v, "letter_id"), ["choice"] = pick });
                    }
                case "choose":
                    return ("choose", Choose(v));
                case "draft":
                    {
                        string raw = PyStr.Strip(S(v, "pawns"));
                        var ids = new List<int>();
                        if (raw.ToLowerInvariant() == "all")
                        {
                            foreach (var kv in Pawns) if ((!IsColonist.TryGetValue(kv.Key, out bool col) || col) && !CannotFight.Contains(kv.Key) && !IsChild(kv.Key)) ids.Add(kv.Value);
                        }
                        else
                        {
                            foreach (var part in raw.Split(',').Where(x => PyStr.Strip(x).Length > 0))
                            {
                                string n = Ci(Pawns.Keys, part);
                                if (n == null) throw new PyValueError("no colonist named " + PyStr.Strip(part) + "; the colonists are: " + string.Join(", ", Pawns.Keys));
                                if (IsChild(n)) throw new PyValueError(ChildRefusal);
                                ids.Add(Pawns[n]);
                            }
                        }
                        if (ids.Count == 0) throw new PyValueError("no one to draft");
                        return ("draft", new JObject { ["pawns"] = new JArray(ids.Take(30).Cast<object>().ToArray()), ["drafted"] = S(v, "drafted") == "yes" });
                    }
                case "order":
                    {
                        int pid = PawnId(S(v, "pawn"));
                        string kind = S(v, "order");
                        if (IsChild(S(v, "pawn"))) throw new PyValueError(ChildRefusal);
                        if (kind == "move")
                        {
                            if (I(v, "x") == 0 && I(v, "z") == 0) throw new PyValueError("move needs a real cell (x and z); in a fight use squad move with a named position");
                            return ("order", new JObject { ["pawn"] = pid, ["order"] = "move", ["x"] = v["x"].DeepClone(), ["z"] = v["z"].DeepClone() });
                        }
                        if (kind == "attack")
                        {
                            if (I(v, "target_id") <= 0) throw new PyValueError("attack needs target_id");
                            return ("order", new JObject { ["pawn"] = pid, ["order"] = "attack", ["target"] = v["target_id"].DeepClone() });
                        }
                        return ("order", new JObject { ["pawn"] = pid, ["order"] = "hold", ["hold"] = S(v, "hold") == "yes" });
                    }
                default:
                    if (Build.Names.Contains(tool)) return Build.Body(tool, v);
                    if (Place.Names.Contains(tool)) return Place.Body(tool, v);
                    if (Combat.Names.Contains(tool)) return Combat.Body(tool, v);
                    if (Trade.Acting.Contains(tool)) return Trade.Body(tool, v);
                    if (Bio.Names.Contains(tool)) return Bio.Body(tool, v);
                    if (Cmd.Names.Contains(tool)) return Cmd.Body(tool, v);
                    if (Zones.Names.Contains(tool)) return Zones.Body(tool, v);
                    throw new PyValueError("unknown tool " + tool);
            }
        }
    }

    /// <summary>What core/steward.py asks of the RimWorld adapter.</summary>
    public sealed class StewardKit
    {
        /// <summary>The why param's text on every acting tool, kept short: it repeats on ~20 tools; steward_system.txt explains it once with an example.</summary>
        public const string WhyText = "since or because ..., shown to the player";
        // events that wake the steward at once while the narrator plays
        private static readonly string[] WakeTypes = { "raid_start", "mental_break", "death", "dialog" };
        private static readonly string[] WakeAlertWords = { "fire", "starvation", "rescue" };     // beside any alert the game marks critical
        // events never listed in "since last time": chatter the colonists have among themselves
        private static readonly HashSet<string> QuietTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "social", "thought", "level_up", "first_cue", "gathering", "gathering_end", "player_talk", "art_created", "sleep_start", "tend",
        };

        public static readonly string[] WorkFallback =
        {
            "Firefighter", "Patient", "Doctor", "Cooking", "Hunting", "Construction", "Growing", "Mining",
            "PlantCutting", "Smithing", "Tailoring", "Art", "Crafting", "Hauling", "Cleaning", "Research",
        };

        /// <summary>COMBAT_TOOLS: what a combat turn may use (core/steward.py).</summary>
        public static readonly string[] CombatToolNames = { "squad", "defend", "draft", "inspect", "survey", "note", "do_nothing" };

        /// <summary>TRADE_TOOLS: what a trade turn may use (core/steward.py).</summary>
        public static readonly string[] TradeToolNames = { "trade_offer", "trade", "note", "do_nothing" };
        /// <summary>FOOD_CRITICAL_DAYS: the playbook's starvation rule (food_days &lt; 3): no trade turn.</summary>
        public const int FoodCriticalDays = 3;

        public static readonly string[] ActingNames = { "set_time", "set_work", "set_schedule", "set_policy", "assign_bed", "set_research", "answer_letter", "choose", "draft", "order" };
        public static readonly HashSet<string> Acting = new HashSet<string>(ActingNames, StringComparer.Ordinal);

        public const string AdvisorNote =
            "\n\nADVISOR MODE. The narrator is not playing right now: the player is only chatting with you. Act only on what the "
            + "player's message explicitly asks for, and do at most one task (one action tool call); then say plainly what you did. "
            + "If they ask for something you have no tool for, say you cannot yet. If they only "
            + "ask or talk, just answer. A question (\"should we take the quest?\", \"what do you think about...?\") is not a request: "
            + "answer it with your advice and do not act. Act only on a plain instruction such as \"put Ana on cooking\". "
            + "Do not change the goals board except to record a standing order they ask for.";

        /// <summary>The director never voices these; the steward reads them.</summary>
        public readonly HashSet<string> SilentEvents = new HashSet<string>(StringComparer.Ordinal) { "letter", "alert", "dialog" };

        /// <summary>A short reason when this event wakes the steward at once, else null.</summary>
        public string Wake(JObject e)
        {
            string t = DigestJson.Str(e, "type", "");
            JToken data = DigestJson.G(e, "data");
            if (WakeTypes.Contains(t))
                return t == "death" && DigestJson.Truthy(DigestJson.G(data, "animal")) ? null : t;
            if (t == "alert")
            {
                string text = (DigestJson.Str(data, "alert", "") + " " + DigestJson.Str(data, "label", "")).ToLowerInvariant();
                if (DigestJson.Truthy(DigestJson.G(data, "critical")) || WakeAlertWords.Any(w => text.Contains(w))) return "alert";
                return null;
            }
            if (t == "letter" && DigestJson.Truthy(DigestJson.G(data, "choices"))) return "letter";
            return null;
        }

        /// <summary>One line for the "since last time" list, or null for chatter that is not worth the steward's attention.</summary>
        public string EventLine(JObject e)
        {
            string t = DigestJson.Str(e, "type", "");
            JToken data = DigestJson.G(e, "data");
            if (QuietTypes.Contains(t)) return null;
            if (!(t == "letter" || t == "alert" || t == "quest" || t == "injury" || WakeTypes.Contains(t)))
            {
                double sev = e.TryGetValue("severity", out JToken st) ? DigestJson.PyFloat(st) : 0.0;
                if (sev < 0.4) return null;
            }
            JToken summary = DigestJson.G(e, "summary");
            JToken textTok = DigestJson.Truthy(summary) ? summary : DigestJson.G(data, "label");
            if (!DigestJson.Truthy(textTok)) textTok = null;
            string text = textTok == null ? "" : PyJson.Str(textTok);
            if (t == "letter" && DigestJson.Truthy(DigestJson.G(data, "choices")))
            {
                string label = data is JObject dobj && dobj.TryGetValue("label", out JToken lt) ? PyJson.Str(lt) : text;
                string choices = string.Join(", ", DigestJson.Items(DigestJson.G(data, "choices")).Select(c => PyJson.Str(c)));
                text = label + " (choices: " + choices + "; letter #" + DigestJson.Str(data, "letter", "None") + ")";
            }
            if (t == "dialog")
            {
                JToken ttl = DigestJson.G(data, "title");
                string title = data is JObject dd && dd.ContainsKey("title") ? PyJson.Str(ttl) : text;
                return "dialog " + PyJson.Str(DigestJson.G(data, "dialog")) + ": " + GameData.Clean(title, "event")
                    + " (options: " + string.Join(", ", DigestJson.Items(DigestJson.G(data, "options")).Select(c => PyJson.Str(c))) + ")";
            }
            return text.Length > 0 ? t + ": " + GameData.Clean(text, "event") : t;
        }

        public string SystemPrompt(bool advisor)
            => StewardResources.Text(StewardResources.SystemName) + (advisor ? AdvisorNote : "");

        public string DefaultStrategy() => StewardResources.Text(StewardResources.StrategyName);

        /// <summary>The Biotech section, always: the bio tools are always declared, and a fixed system text keeps the engine's prompt cache
        /// (the section is a few hundred characters; the digest's bio block decides only whether the tools are callable).</summary>
        public string StrategyExtra(JObject digest) => StewardResources.Text(StewardResources.BiotechName).Trim();

        /// <summary>The "=== PLAYBOOK ALERTS ===" block: playbook rules that fire on this digest's facts ("" when none).</summary>
        public string PlaybookAlerts(JObject digest) => StewardRules.AlertsSection(digest);

        public string RenderDigest(JObject digest) => StewardDigest.RenderDigest(digest);

        public StewardSession Session(JObject digest, IBridge bridge, bool advisor, bool afterFight = false) => new StewardSession(digest, bridge, advisor, afterFight);

        /// <summary>The tools of a combat turn: built from the combat view alone (the colonists in it), no digest.</summary>
        public StewardSession CombatSession(JObject view, IBridge bridge)
        {
            var pawns = new JArray();
            foreach (var kv in new[] { ("fighters", false), ("others", true) })
                foreach (var f in CombatKit.OrList(DigestJson.G(view, kv.Item1)))
                    if (f is JObject fo && DigestJson.Truthy(DigestJson.G(fo, "id")) && DigestJson.Truthy(DigestJson.G(fo, "name")))
                    {
                        var p = new JObject { ["id"] = fo["id"].DeepClone(), ["name"] = fo["name"].DeepClone(), ["role"] = "colonist" };
                        if (kv.Item2) p["incapable"] = new JArray("Violent");
                        pawns.Add(p);
                    }
            foreach (var c in CombatKit.OrList(DigestJson.G(DigestJson.G(view, "downed") is JObject dn ? dn : new JObject(), "colonists")))
                if (c is JObject co && DigestJson.Truthy(DigestJson.G(co, "id")) && DigestJson.Truthy(DigestJson.G(co, "name")))
                    pawns.Add(new JObject { ["id"] = co["id"].DeepClone(), ["name"] = co["name"].DeepClone(), ["role"] = "colonist", ["incapable"] = new JArray("Violent") });
            return new StewardSession(new JObject { ["pawns"] = pawns, ["combat"] = view }, bridge, false);
        }

        public string CombatSystemPrompt() => StewardCombat.SystemPrompt();

        public StewardSession TradeSession(JObject digest, IBridge bridge) => new StewardSession(digest, bridge, false);

        public string TradeSystemPrompt() => StewardResources.Text(StewardResources.TradeName);

        /// <summary>Why a trade turn is not worth running now ("" when it is): the colony is starving (food_days under 3, or a
        /// critical starvation alert), so the routine cycle handles food first.</summary>
        public string TradeSkip(JObject digest)
        {
            if (StewardRules.DeriveFacts(digest).TryGetValue("food_days", out object fd) && IsRealNumber(fd) && Convert.ToDouble(fd, CultureInfo.InvariantCulture) < FoodCriticalDays)
                return "food is critical (" + DigestJson.G6(Convert.ToDouble(fd, CultureInfo.InvariantCulture)) + " days)";
            foreach (var a in DigestJson.Items(DigestJson.G(digest, "alerts")).OfType<JObject>())
                if (DigestJson.Truthy(DigestJson.G(a, "critical"))
                    && (DigestJson.Str(a, "alert", "") + " " + DigestJson.Str(a, "label", "")).ToLowerInvariant().Contains("starv"))
                    return "a critical starvation alert";
            return "";
        }

        private static bool IsRealNumber(object o) => o is long || o is int || o is double || o is System.Numerics.BigInteger;

        /// <summary>(the trade view naming the arrivals, the food line).</summary>
        public (string view, string food) RenderTrade(JObject digest, IList<string> arrived)
        {
            StewardRules.DeriveFacts(digest).TryGetValue("food_days", out object fd);
            return (StewardDigest.TradeView(digest, arrived),
                IsRealNumber(fd) ? "FOOD: about " + DigestJson.G6(Convert.ToDouble(fd, CultureInfo.InvariantCulture)) + " days of food" : "");
        }

        public string RenderCombat(JObject view) => StewardCombat.RenderCombat(view);
    }
}
