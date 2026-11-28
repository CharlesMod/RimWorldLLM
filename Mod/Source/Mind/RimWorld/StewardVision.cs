// NEW (the narrator can look at the screen): the look tool of a routine steward turn (docs/VIVARIUM.md, Vision).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind.RimWorld
{
    /// <summary>One turn's look tool: GET /vision, the per-turn limits, and the picture waiting to go into the next request. A new
    /// VisionKit per turn; nothing here outlives the turn.</summary>
    public sealed class VisionKit : IVisionLook
    {
        public const string Name = "look";
        public string ToolName => Name;
        public static readonly string[] Kinds = { "close", "base", "wide" };
        public const int PollMs = 250, PollTotalMs = 3000;
        public const string NoMore = "no more looks this turn";
        public const string Busy = "camera busy";
        /// <summary>The question an engine look gets when the steward gave no reason.</summary>
        public const string DefaultQuestion = "Describe what you see for planning the colony's buildings: walls, doors, rooms, open ground, obstacles, blueprints.";
        private static readonly Regex TargetRe = new Regex(
            @"^(room:\d{1,9}|thing:\d{1,9}|pawn:\d{1,9}|cell:\d{1,4},\d{1,4}|dir:(north|south|east|west|northeast|northwest|southeast|southwest):\d{1,3})$",
            RegexOptions.CultureInvariant);

        public ToolSpec Spec { get; } = Make();

        /// <summary>The look tool as declared to the model (the catalog; no turn needed).</summary>
        public static ToolSpec CatalogSpec() => Make();

        private static ToolSpec Make() => new ToolSpec(Name,
            "Look at a picture of the colony from above, north up (not an action). base = the whole base for layout; wide = the surroundings for fields, ore, "
            + "approaches and raids; close = a few cells around one target (target: room:<id>, thing:<id>, pawn:<id>, cell:x,z, dir:<side>:<cells>) for doors, "
            + "blueprints and missing walls.",
            new[]
            {
                new Param("kind", "enum", "base, wide or close", choices: Kinds),
                new Param("target", "text", "close only: room:<id>, thing:<id>, pawn:<id>, cell:x,z or dir:<side>:<cells>", maxLen: 40, required: false),
                new Param("why", "text", "a brief reason in your own words", maxLen: 80, required: false, sameCall: true),
            });

        private readonly IBridge _bridge;
        private readonly Func<int, Task> _delay;
        private readonly ILookClient _look;
        private JObject _pending;
        private readonly List<string> _kinds = new List<string>();
        /// <summary>The kinds looked at this turn, in order (for the turn trace; no pictures).</summary>
        public IReadOnlyList<string> Looked => _kinds;

        public VisionKit(IBridge bridge, Func<int, Task> delay = null, ILookClient look = null)
        {
            _bridge = bridge;
            _look = look ?? new DirectLookClient(null);
            _delay = delay ?? (ms => Task.Delay(ms));
        }

        /// <summary>The picture of the last successful look, once; the loop attaches it to the next request.</summary>
        public JObject TakeAttachment()
        {
            var p = _pending;
            _pending = null;
            return p;
        }

        private static JObject Err(string m) => new JObject { ["error"] = m };

        /// <summary>The look tool's result: a legend line (string) on success, else {"error": ...}. Never holds the image bytes.</summary>
        public async Task<JToken> RunAsync(JObject v)
        {
            string kind = v.TryGetValue("kind", out JToken kt) ? PyStr.Strip(PyJson.Str(kt)).ToLowerInvariant() : "";
            string target = v.TryGetValue("target", out JToken tt) && tt.Type == JTokenType.String ? PyStr.Strip((string)tt) : "";
            if (target.Equals("none", StringComparison.OrdinalIgnoreCase)) target = "";
            if (Array.IndexOf(Kinds, kind) < 0) return Err("kind is close, base or wide");
            if (_kinds.Count >= 2 || (_kinds.Count == 1 && !((_kinds[0] == "base" || _kinds[0] == "wide") && kind == "close")))
                return Err(NoMore);
            if (kind == "close")
            {
                if (target.Length == 0) return Err("close needs a target: room:<id>, thing:<id>, pawn:<id>, cell:x,z or dir:<side>:<cells>");
                target = target.ToLowerInvariant().Replace(" ", "");
                if (!TargetRe.IsMatch(target)) return Err("target is not room:<id>, thing:<id>, pawn:<id>, cell:x,z or dir:<side>:<cells>");
            }
            else target = "";
            var q = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("kind", kind) };
            if (target.Length > 0) q.Add(new KeyValuePair<string, string>("target", target));
            int waited = 0;
            while (true)
            {
                JObject r;
                try { r = await _bridge.LookAsync("/vision", q).ConfigureAwait(false); }
                catch (BridgeError e)
                {
                    string why = e.Detail.Length > 0 ? e.Detail : e.Code;
                    return Err(e.Code == "refused" ? "not now (" + why + ")" : PyStr.Head((e.Code + ": " + e.Detail).Trim(':', ' '), 160));
                }
                string status = r.TryGetValue("status", out JToken st) && st.Type == JTokenType.String ? (string)st : "";
                if (status == "pending")
                {
                    if (waited >= PollTotalMs) return Err(Busy);
                    await _delay(PollMs).ConfigureAwait(false);
                    waited += PollMs;
                    continue;
                }
                string b64 = r.TryGetValue("image_b64", out JToken bt) && bt.Type == JTokenType.String ? (string)bt : "";
                if (status != "ready" || b64.Length == 0) return Err("no picture came back");
                string mime = r.TryGetValue("mime", out JToken mt) && mt.Type == JTokenType.String ? (string)mt : "image/jpeg";
                string legend = Legend(r, kind, target);
                string question = v.TryGetValue("why", out JToken wt) && wt.Type == JTokenType.String ? PyStr.Strip((string)wt) : "";
                LookOutcome got = await _look.LookAsync(new LookRequest { Mime = mime, B64 = b64, Kind = kind, Label = legend, Question = question.Length > 0 ? question : DefaultQuestion }).ConfigureAwait(false);
                if (got == null || (got.Part == null && string.IsNullOrEmpty(got.Answer))) return Err("the picture could not be used");
                _pending = got.Part;
                _kinds.Add(kind);
                if (got.Part == null) return new JValue(legend.Replace("Picture attached: ", "Looked: ") + ". What the picture shows: " + PyStr.Head(got.Answer, 600));
                return new JValue(legend);
            }
        }

        private static string N(JObject o, string k) => o != null && o.TryGetValue(k, out JToken t) && t.Type != JTokenType.Null ? Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture) : "?";

        /// <summary>"Picture attached: base, cells x 100-140, z 80-110 (40x30 cells), north up, 19 px per cell". One line, no bytes.</summary>
        public static string Legend(JObject reply, string kind, string target)
        {
            var l = reply["legend"] as JObject;
            var sb = new System.Text.StringBuilder("Picture attached: " + kind);
            if (l != null)
            {
                sb.Append(", cells x " + N(l, "x0") + "-" + N(l, "x1") + ", z " + N(l, "z0") + "-" + N(l, "z1"));
                sb.Append(" (" + N(l, "cells_w") + "x" + N(l, "cells_h") + " cells), north up, " + N(l, "px_per_cell") + " px per cell");
                string name = l["target_name"]?.Type == JTokenType.String ? (string)l["target_name"] : "";
                if (name.Length > 0) sb.Append(", target " + GameData.Clean(name, "look", 60));
                else if (target.Length > 0) sb.Append(", target " + target);
                string note = l["note"]?.Type == JTokenType.String ? (string)l["note"] : "";
                if (note.Length > 0) sb.Append(". " + GameData.Clean(note, "look", 120));
            }
            else sb.Append(", north up");
            return PyStr.Head(sb.ToString(), 400);
        }
    }
}
