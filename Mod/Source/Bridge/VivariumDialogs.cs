using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Unattended play: dialogs (docs/VIVARIUM.md "Unattended play: dialogs"). Verse-free. The game side
// (Mod/Source/RimWorld/VivariumDialogHost.cs) scans the open windows and choice letters into DialogRecords,
// the routes show them and take the narrator's answer, and the safe-default policy below answers what the
// narrator leaves open past the timeout.
namespace RimWorldLLM.Bridge
{
    public static class DialogKinds
    {
        public const string Joiner = "joiner";            // ChoiceLetter_AcceptJoiner: refugee, wanderer, pod crash survivor
        public const string CreepJoiner = "creep_joiner"; // ChoiceLetter_AcceptCreepJoiner
        public const string Visitors = "visitors";        // ChoiceLetter_AcceptVisitors
        public const string BetrayVisitors = "betray_visitors";
        public const string Ransom = "ransom";            // ChoiceLetter_RansomDemand
        public const string ChoosePawn = "choose_pawn";   // ChoiceLetter_ChoosePawn
        public const string BabyToChild = "baby_to_child";
        public const string Growth = "growth_moment";     // ChoiceLetter_GrowthMoment
        public const string Quest = "quest";              // a quest offer letter (accept / decline)
        public const string Letter = "letter";            // any other choice letter that needs an answer
        public const string NodeTree = "node_tree";       // Dialog_NodeTree: comms, event dialogs
        public const string MessageBox = "message_box";   // Dialog_MessageBox, Dialog_Confirm
        public const string Naming = "naming";            // Dialog_NamePlayer*, Dialog_NamePawn

        /// <summary>The roles the first options of a choice letter have, in vanilla order. Anything past the list is "other".</summary>
        public static string[] RolesFor(string kind)
        {
            switch (kind)
            {
                case Joiner: case Visitors: return new[] { DialogOption.Accept, DialogOption.Decline };
                case Ransom: return new[] { DialogOption.Accept, DialogOption.Decline };
                case BetrayVisitors: return new[] { DialogOption.Accept, DialogOption.Decline };
                case CreepJoiner: return new[] { DialogOption.Accept, DialogOption.Other, DialogOption.Decline };
                case Quest: return new[] { DialogOption.Accept, DialogOption.Decline };
                default: return new string[0];
            }
        }

        /// <summary>The risk the accept option of a kind carries, shown to the narrator.</summary>
        public static string AcceptRisk(string kind)
        {
            switch (kind)
            {
                case Ransom: return "costs silver";
                case BetrayVisitors: return "betrays guests";
                case CreepJoiner: return "anomaly risk";
                case Quest: return "risk unknown";
                default: return "";
            }
        }
    }

    public sealed class DialogOption
    {
        public const string Accept = "accept", Decline = "decline", Close = "close", Other = "other";
        public int Index;
        public string Label = "";
        public bool Disabled;
        public string DisabledReason = "";
        public string Risk = "";       // "" none; else a few words ("costs silver", "destructive")
        public string Role = Other;    // accept | decline | close | other
    }

    public sealed class DialogField
    {
        public string Name = "name";   // "name", "settlement"
        public string Label = "";
        public int MaxLen = 40;
    }

    public sealed class DialogRecord
    {
        public const int MaxText = 300;
        public string Id = "";
        public string Kind = DialogKinds.Letter;
        public string Title = "";
        public string Text = "";
        public List<DialogOption> Options = new List<DialogOption>();
        public List<DialogField> Fields = new List<DialogField>();
        public double OpenedReal;      // real seconds (modal windows pause the game, so game ticks cannot time them)
        public int Attempts;           // default answers tried and failed
        public bool IsLetter;          // a letter in the stack (does not pause the game)
    }

    public sealed class DialogChoice
    {
        public int Option = -1;                         // -1 for naming
        public Dictionary<string, string> Texts = new Dictionary<string, string>();
        public string Why = "";
    }

    public static class DialogPolicy
    {
        public static readonly string[] FallbackNames = { "Lantern Hollow", "Ashford", "Ferngrove", "Stonebrook", "Harrow", "Wren Landing", "Cinder Vale" };

        /// <summary>The safe default answer, or null when nothing can be answered safely (every option disabled).</summary>
        public static DialogChoice Default(DialogRecord d)
        {
            if (d == null) return null;
            if (d.Kind == DialogKinds.Naming)
            {
                var c = new DialogChoice { Why = "no answer in time: used a fallback name" };
                int i = 0;
                foreach (var f in d.Fields)
                {
                    c.Texts[f.Name] = Fit(FallbackNames[(Hash(d.Id) + i++) % FallbackNames.Length], f.MaxLen);
                }
                if (d.Fields.Count == 0) return null;
                return c;
            }
            var open = d.Options.Where(o => !o.Disabled).ToList();
            if (open.Count == 0) return null;
            DialogOption pick = null;
            switch (d.Kind)
            {
                case DialogKinds.Joiner:
                case DialogKinds.Visitors:
                    pick = Role(open, DialogOption.Accept) ?? Safe(open);
                    break;
                case DialogKinds.Ransom:
                case DialogKinds.Quest:
                case DialogKinds.BetrayVisitors:
                case DialogKinds.CreepJoiner:
                    pick = Role(open, DialogOption.Decline) ?? Role(open, DialogOption.Close) ?? Safe(open);
                    break;
                case DialogKinds.MessageBox:
                case DialogKinds.NodeTree:
                    {
                        var safe = open.Where(o => string.IsNullOrEmpty(o.Risk)).ToList();
                        var pool = safe.Count > 0 ? safe : open;
                        pick = Role(pool, DialogOption.Close) ?? Role(pool, DialogOption.Decline) ?? pool[0];
                        break;
                    }
                default:
                    pick = Safe(open);
                    break;
            }
            return new DialogChoice { Option = pick.Index, Why = "no answer in time: the safe default" };
        }

        static DialogOption Role(List<DialogOption> opts, string role)
        {
            return opts.FirstOrDefault(o => o.Role == role);
        }

        static DialogOption Safe(List<DialogOption> opts)
        {
            return opts.FirstOrDefault(o => string.IsNullOrEmpty(o.Risk)) ?? opts[0];
        }

        static int Hash(string s) { int h = 7; foreach (char c in s ?? "") h = h * 31 + c; return h & 0x7fffffff; }

        public static string Fit(string s, int max)
        {
            s = (s ?? "").Trim();
            return max > 0 && s.Length > max ? s.Substring(0, max).TrimEnd() : s;
        }

        /// <summary>The chat note after a default answer: "No answer in 90 s: accepted the refugee (default)".</summary>
        public static string Note(DialogRecord d, DialogChoice c, int timeoutS)
        {
            return "No answer in " + timeoutS + " s: " + Done(d, c) + " (default)";
        }

        /// <summary>Plain English of what an answer did: chose "Accept" on "Refugee" / named it "Ferngrove".</summary>
        public static string Done(DialogRecord d, DialogChoice c)
        {
            if (d.Kind == DialogKinds.Naming) return "named " + Short(d.Title) + " " + string.Join(" and ", c.Texts.Values.Select(v => "\"" + v + "\""));
            var o = d.Options.FirstOrDefault(x => x.Index == c.Option);
            return "chose \"" + (o != null ? o.Label : "#" + c.Option) + "\" on \"" + Short(d.Title) + "\"";
        }

        static string Short(string s) { s = (s ?? "").Trim(); return s.Length > 60 ? s.Substring(0, 60) + "…" : s; }
    }

    /// <summary>The open dialogs the game side last saw. Game thread. Never saved.</summary>
    public sealed class DialogBook
    {
        public const int DefaultTimeoutS = 90, MaxAttempts = 3;
        readonly Dictionary<string, DialogRecord> _open = new Dictionary<string, DialogRecord>();
        readonly List<string> _order = new List<string>();

        public int Count { get { return _open.Count; } }

        public DialogRecord Get(string id)
        {
            DialogRecord d;
            return id != null && _open.TryGetValue(id, out d) ? d : null;
        }

        public IEnumerable<DialogRecord> All { get { return _order.Select(i => _open[i]); } }

        /// <summary>One scan: the dialogs open right now. Returns the new ones (first seen); the ones gone are dropped.</summary>
        public List<DialogRecord> Sync(IList<DialogRecord> seen, double nowReal)
        {
            var added = new List<DialogRecord>();
            var keep = new HashSet<string>();
            foreach (var d in seen)
            {
                if (d == null || string.IsNullOrEmpty(d.Id)) continue;
                keep.Add(d.Id);
                DialogRecord old;
                if (_open.TryGetValue(d.Id, out old))
                {
                    d.OpenedReal = old.OpenedReal; d.Attempts = old.Attempts;   // content may refresh (a node tree moves on), the clock does not
                    _open[d.Id] = d;
                }
                else
                {
                    d.OpenedReal = nowReal;
                    Clean(d);
                    _open[d.Id] = d; _order.Add(d.Id); added.Add(d);
                }
            }
            foreach (var id in _order.Where(i => !keep.Contains(i)).ToList()) { _open.Remove(id); _order.Remove(id); }
            return added;
        }

        public void Remove(string id) { if (id != null && _open.Remove(id)) _order.Remove(id); }

        public void Clear() { _open.Clear(); _order.Clear(); }

        static void Clean(DialogRecord d)
        {
            d.Title = VivJson.Clean(d.Title, 120);
            d.Text = VivJson.Clean(d.Text, DialogRecord.MaxText);
            foreach (var o in d.Options) o.Label = VivJson.Clean(o.Label, 120);
        }

        /// <summary>Dialogs open longer than the timeout (real seconds) that still have attempts left.</summary>
        public List<DialogRecord> Due(double nowReal, int timeoutS)
        {
            return All.Where(d => d.Attempts < MaxAttempts && nowReal - d.OpenedReal >= timeoutS).ToList();
        }

        /// <summary>Resolves the narrator's pick: an index, "#2", or a label (case-insensitive). Null with the reason in err.</summary>
        public static DialogOption Resolve(DialogRecord d, JToken option, out string err)
        {
            err = null;
            if (option == null || option.Type == JTokenType.Null) { err = "option is required"; return null; }
            DialogOption hit = null;
            if (option.Type == JTokenType.Integer)
            {
                int i = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, option.Value<long>()));
                hit = d.Options.FirstOrDefault(o => o.Index == i);
            }
            else if (option.Type == JTokenType.String)
            {
                string s = option.Value<string>().Trim();
                int n;
                if (s.StartsWith("#") && int.TryParse(s.Substring(1), out n)) hit = d.Options.FirstOrDefault(o => o.Index == n);
                else hit = d.Options.FirstOrDefault(o => string.Equals(o.Label, s, StringComparison.OrdinalIgnoreCase));
            }
            else { err = "option must be an index or a label"; return null; }
            if (hit == null) { err = "no option " + option.ToString() + "; options: " + string.Join(", ", d.Options.Select(o => o.Index + " " + o.Label)); return null; }
            if (hit.Disabled) { err = "\"" + hit.Label + "\" is not available" + (string.IsNullOrEmpty(hit.DisabledReason) ? "" : ": " + hit.DisabledReason); return null; }
            return hit;
        }

        public JObject ToJson(int timeoutS, double nowReal)
        {
            var arr = new JArray();
            foreach (var d in All) arr.Add(DialogJson(d, timeoutS, nowReal));
            return new JObject { ["dialogs"] = arr, ["timeout_s"] = timeoutS };
        }

        public static JObject DialogJson(DialogRecord d, int timeoutS, double nowReal)
        {
            var opts = new JArray();
            foreach (var o in d.Options)
            {
                var j = new JObject { ["index"] = o.Index, ["label"] = o.Label, ["disabled"] = o.Disabled };
                if (o.Disabled && !string.IsNullOrEmpty(o.DisabledReason)) j["disabled_reason"] = o.DisabledReason;
                if (!string.IsNullOrEmpty(o.Risk)) j["risk"] = o.Risk;
                opts.Add(j);
            }
            var fields = new JArray();
            foreach (var f in d.Fields) fields.Add(new JObject { ["name"] = f.Name, ["label"] = f.Label, ["max_len"] = f.MaxLen });
            var def = DialogPolicy.Default(d);
            string defLabel = null;
            if (def != null)
            {
                if (d.Kind == DialogKinds.Naming) defLabel = string.Join(" / ", def.Texts.Values);
                else { var o = d.Options.FirstOrDefault(x => x.Index == def.Option); defLabel = o != null ? o.Label : null; }
            }
            return new JObject
            {
                ["id"] = d.Id, ["kind"] = d.Kind, ["title"] = d.Title, ["text"] = d.Text,
                ["options"] = opts, ["fields"] = fields,
                ["default"] = defLabel == null ? JValue.CreateNull() : (JToken)defLabel,
                ["timeout_s"] = timeoutS,
                ["open_s"] = (int)Math.Max(0, nowReal - d.OpenedReal),
                ["pauses_game"] = !d.IsLetter
            };
        }

        /// <summary>The `dialog` event data (shared contract).</summary>
        public static JObject EventData(DialogRecord d, int timeoutS)
        {
            var j = DialogJson(d, timeoutS, d.OpenedReal);
            return new JObject
            {
                ["dialog"] = d.Id, ["kind"] = d.Kind, ["title"] = d.Title,
                ["options"] = new JArray(d.Options.Where(o => !o.Disabled).Select(o => o.Label)),
                ["default"] = j["default"], ["timeout_s"] = timeoutS
            };
        }
    }
}
