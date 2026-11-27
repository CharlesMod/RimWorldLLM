// Port of agent/rimworldllm_agent/core/steward_board.py: the steward's goals-board tools (game-agnostic): goals, the plan,
// standing orders, a chat note, do nothing. The board lives in the mod (GET/PUT /goals, saved with the game). During a cycle the
// tools only record edits; Edits.Apply replays them on a board and gives the fields of the one PUT /goals that ends the cycle.
// A stale revision (409) is handled by fetching the board again and replaying the same edits.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    /// <summary>One recorded board edit: the Python's (op, ...) tuple. add_goal: A = text, B = horizon; set_goal: A = goal id, B = status;
    /// set_plan / add_order: A = text; drop_order: A = order id.</summary>
    public sealed class EditOp
    {
        public readonly string Kind, A, B;
        public EditOp(string kind, string a = null, string b = null) { Kind = kind; A = a; B = b; }
    }

    /// <summary>Recorded board edits, replayed on any board.</summary>
    public sealed class Edits
    {
        public List<EditOp> Ops = new List<EditOp>();
        public Edits() { }
        public Edits(IEnumerable<EditOp> ops) { Ops = ops.ToList(); }

        public bool Any { get { return Ops.Count > 0; } }

        private static List<JObject> Copy(JToken t)
        {
            return (t as JArray ?? new JArray()).Select(x => (JObject)((JObject)x).DeepClone()).ToList();
        }

        private static bool IdIs(JObject o, string id)
        {
            var t = o["id"];
            return t != null && t.Type == JTokenType.String && (string)t == id;
        }

        /// <summary>The PUT fields (goals / plan / orders, only the parts the edits touched) these edits make on board.
        /// Throws PyValueError for an edit the board cannot take (full, unknown id).</summary>
        public JObject Apply(JObject board)
        {
            var goals = Copy(PyJson.Truthy(board["goals"]) ? board["goals"] : null);
            var orders = Copy(PyJson.Truthy(board["orders"]) ? board["orders"] : null);
            string plan = null;
            var touched = new HashSet<string>();
            foreach (var op in Ops)
            {
                switch (op.Kind)
                {
                    case "add_goal":
                        if (goals.Count >= StewardBoard.MaxGoals) goals = StewardBoard.DropOldestFinished(goals);
                        if (goals.Count >= StewardBoard.MaxGoals)
                            throw new PyValueError("the goals board is full (" + StewardBoard.MaxGoals + " open goals): mark some done or dropped first");
                        goals.Add(new JObject { ["text"] = op.A, ["horizon"] = op.B, ["status"] = "open" });
                        touched.Add("goals");
                        break;
                    case "set_goal":
                        {
                            var g = goals.FirstOrDefault(x => IdIs(x, op.A));
                            if (g == null) throw new PyValueError("no goal " + op.A);
                            g["status"] = op.B;
                            touched.Add("goals");
                            break;
                        }
                    case "set_plan":
                        plan = op.A;
                        touched.Add("plan");
                        break;
                    case "add_order":
                        if (orders.Count >= StewardBoard.MaxOrders)
                            throw new PyValueError("there are already " + StewardBoard.MaxOrders + " standing orders: drop one first");
                        orders.Add(new JObject { ["text"] = op.A, ["by"] = "player" });
                        touched.Add("orders");
                        break;
                    case "drop_order":
                        if (!orders.Any(o => IdIs(o, op.A))) throw new PyValueError("no order " + op.A);
                        orders = orders.Where(o => !IdIs(o, op.A)).ToList();
                        touched.Add("orders");
                        break;
                }
            }
            var res = new JObject();
            if (touched.Contains("goals")) res["goals"] = new JArray(goals);
            if (touched.Contains("plan")) res["plan"] = plan;
            if (touched.Contains("orders")) res["orders"] = new JArray(orders);
            return res;
        }

        /// <summary>Record op if the board can take it on top of the earlier edits; else the reason (null when recorded).</summary>
        public string TryAdd(JObject board, EditOp op)
        {
            var trial = new Edits(Ops.Concat(new[] { op }));
            try { trial.Apply(board); }
            catch (PyValueError e) { return e.Message; }
            Ops.Add(op);
            return null;
        }
    }

    public static class StewardBoard
    {
        public const int MaxGoals = 20;
        public const int MaxOrders = 20;
        public const int TextMax = 300;
        public static readonly string[] BoardToolNames = { "add_goal", "set_goal", "set_plan", "add_order", "drop_order", "note", "do_nothing" };

        private static readonly Regex Control = new Regex("[\u0000-\u001f\u007f-\u009f]", RegexOptions.CultureInvariant);

        /// <summary>Player or steward text for a prompt or the board: control characters out, markup characters softened, one line.
        /// Not the game-data filter: this is the player's own voice and the steward's own notes.</summary>
        public static string Plain(object text, int cap = TextMax)
        {
            string s;
            if (text == null || (text is string ts && ts.Length == 0) || (text is bool tb && !tb)
                || (text is long tl && tl == 0) || (text is int ti && ti == 0) || (text is double td && td == 0)) s = "";
            else if (text is JToken tok) s = PyJson.Truthy(tok) ? PyJson.Str(tok) : "";
            else s = PyStr.StrOf(text);
            string t = Control.Replace(s, " ").Replace('<', '(').Replace('>', ')').Replace('{', '(').Replace('}', ')');
            t = PyStr.CollapseSpaces(t);
            return PyStr.Len(t) <= cap ? t : PyStr.Head(t, cap - 1).TrimEnd(PyWhite) + "…";
        }

        private static readonly char[] PyWhite = Enumerable.Range(0, 0x10000).Select(i => (char)i).Where(PyStr.IsSpace).ToArray();

        private static List<string> Ids(JToken arr, Func<JObject, bool> keep)
        {
            var o = new List<string>();
            if (arr is JArray a)
                foreach (var x in a.OfType<JObject>())
                    if (keep(x) && PyJson.Truthy(x["id"])) o.Add(PyJson.Str(x["id"]));
            return o;
        }

        public static List<ToolSpec> BoardTools(JObject board, bool advisor = false)
        {
            var openGoals = Ids(PyJson.Truthy(board["goals"]) ? board["goals"] : null,
                g => (g["status"] == null ? "open" : PyJson.Str(g["status"])) == "open");
            var orders = Ids(PyJson.Truthy(board["orders"]) ? board["orders"] : null, o => true);
            var o2 = new List<ToolSpec>
            {
                new ToolSpec("add_goal", "Add a goal to your goals board: a long-term one or a short-term one.",
                    new[] { new Param("text", "text", "the goal, one sentence", maxLen: TextMax), new Param("horizon", "enum", "long or short term", choices: new[] { "short", "long" }) }),
            };
            if (openGoals.Count > 0)
                o2.Add(new ToolSpec("set_goal", "Mark one of your open goals done or dropped.",
                    new[] { new Param("goal_id", "enum", "the goal's id", choices: openGoals), new Param("status", "enum", "done or dropped", choices: new[] { "done", "dropped" }) }));
            o2.Add(new ToolSpec("set_plan", "Replace your current plan, a few sentences on what you will do next.", new[] { new Param("text", "text", "the plan", maxLen: TextMax) }));
            o2.Add(new ToolSpec("add_order", "Record a standing order the player gave you, in their words, so it is kept and obeyed.",
                new[] { new Param("text", "text", "the standing order", maxLen: TextMax) }));
            if (orders.Count > 0)
                o2.Add(new ToolSpec("drop_order", "Remove a standing order the player has withdrawn.", new[] { new Param("order_id", "enum", "the order's id", choices: orders) }));
            o2.Add(new ToolSpec("note", "Post a short action note in the chat now, such as 'set the cook to priority 1'. At most three per turn.",
                new[] { new Param("text", "text", "the note, a few words", maxLen: 120) }));
            o2.Add(new ToolSpec("do_nothing", "Do nothing this turn.", new Param[0], endsTurn: true));
            return o2;
        }

        internal static List<JObject> DropOldestFinished(List<JObject> goals)
        {
            for (int i = 0; i < goals.Count; i++)
            {
                var st = goals[i]["status"];
                if (st != null && st.Type == JTokenType.String && ((string)st == "done" || (string)st == "dropped"))
                {
                    var r = goals.Take(i).ToList();
                    r.AddRange(goals.Skip(i + 1));
                    return r;
                }
            }
            return goals;
        }

        private static string G(JObject o, string key, string dflt)
        {
            var t = o[key];
            return t == null ? dflt : PyJson.Str(t);
        }

        public static string RenderBoard(JObject board, int logLines = 6)
        {
            var o = new List<string>();
            var goals = (PyJson.Truthy(board["goals"]) ? board["goals"] : null) as JArray ?? new JArray();
            o.Add("Goals: " + (goals.Count > 0 ? "" : "none yet"));
            foreach (JObject g in goals)
                o.Add("- " + G(g, "id", "?") + " [" + G(g, "horizon", "short") + ", " + G(g, "status", "open") + "] " + Plain(g["text"] ?? new JValue("")));
            string plan = Plain(board["plan"]);
            o.Add("Plan: " + (plan.Length > 0 ? plan : "none yet"));
            var orders = (PyJson.Truthy(board["orders"]) ? board["orders"] : null) as JArray ?? new JArray();
            o.Add("Standing orders from the player (they override your own plans): " + (orders.Count > 0 ? "" : "none"));
            foreach (JObject x in orders) o.Add("- " + G(x, "id", "?") + ": " + Plain(x["text"]));
            var log = (PyJson.Truthy(board["log"]) ? board["log"] : null) as JArray ?? new JArray();
            var tail = Grounding.Tail(log.ToList(), logLines);
            if (tail.Count > 0)
            {
                o.Add("What you did lately:");
                foreach (var x in tail) o.Add("- " + Plain(((JObject)x)["text"], 200));
            }
            return string.Join("\n", o);
        }
    }
}
