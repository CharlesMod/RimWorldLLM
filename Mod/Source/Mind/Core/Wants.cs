// Port of agent/rimworldllm_agent/core/wants.py: wants with follow-through: in an actor's idle window the model may pick at most
// one small social action. Tools: seek_out(target, topic), avoid(target), remember(fact, importance), do_nothing. The tool loop
// constrains the choice; nothing the model writes is executed unvalidated. Execution goes through the bridge only:
// social_action (a PROPOSED op) for seek_out and avoid, and the mind blob for remember. A failure of any kind ends in do_nothing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members run_wants reads (the Python takes the whole director as d).</summary>
    public interface IWantsHost
    {
        WantsStats Wants { get; }
        Dictionary<int, Mind> Minds { get; }
        IBridge Bridge { get; }
        IGameAdapter Adapter { get; }
        IDictionary<int, string> Names { get; }
        ISet<int> Dirty { get; }
        ConcernEngine Concerns { get; }
        bool IsMinor(int actorId);
        ISet<string> MinorTokens();
        /// <summary>d.tools_allowed(names, minor_involved): the tool names the age gate lets through.</summary>
        ISet<string> ToolsAllowed(IEnumerable<string> names, bool minorInvolved);
    }

    public sealed class WantsStats
    {
        public int Runs, Acted, Remembered, Nothing, Fallbacks;
        /// <summary>actions refused by validation (age gate, unknown target, second action)</summary>
        public int Refused;
        /// <summary>the mod does not implement the proposed op</summary>
        public int Unsupported;
        public List<WantsOutcome> Trace = new List<WantsOutcome>();
    }

    /// <summary>The summary dict run_wants returns (also appended to the trace).</summary>
    public sealed class WantsOutcome
    {
        public int Actor;
        public string Outcome;
        public List<string> Allowed;
        public List<(string Tool, JObject Args, string Error)> Executed;
        public bool Unsupported;
        public bool MinorInScene;
    }

    public static class WantsOps
    {
        public static readonly string[] ToolNames = { "seek_out", "avoid", "remember", "do_nothing" };
        public static readonly HashSet<string> Social = new HashSet<string> { "seek_out", "avoid" };

        public static readonly string SystemText =
            "You are the quiet part of one character, deciding what they do with a free moment. Choose at most one small " +
            "social action, or nothing. seek_out walks to someone to talk about a topic. avoid keeps clear of someone for a " +
            "while. remember keeps one fact worth holding on to. do_nothing is always fine and often right. Only use what the " +
            "scene below supports.\n\n" + GameData.DataRule;

        public static List<ToolSpec> Tools(IList<string> names)
        {
            Param who = names != null && names.Count > 0 ? new Param("target", "enum", "who", choices: names, required: true) : null;
            var seek = who != null ? new List<Param> { who, new Param("topic", "text", "what to talk about, a few words", maxLen: 80) } : new List<Param>();
            return new List<ToolSpec>
            {
                new ToolSpec("seek_out", "Walk over to someone to talk about something.", seek),
                new ToolSpec("avoid", "Keep clear of someone for a while.", who != null ? new List<Param> { who } : new List<Param>()),
                new ToolSpec("remember", "Keep one fact in mind.",
                    new List<Param> { new Param("fact", "text", "the fact", maxLen: 160), new Param("importance", "scale", "how much it matters", scale: Scale.Importance) }),
                new ToolSpec("do_nothing", "Do nothing special.", new List<Param>(), endsTurn: true),
            };
        }

        /// <summary>One decision for actor aid. Returns a summary (also appended to d.Wants.Trace).</summary>
        public static async Task<WantsOutcome> RunWantsAsync(IWantsHost d, int aid, IBackend backend, string mode = "auto")
        {
            var st = d.Wants;
            st.Runs++;
            var mind = d.Minds[aid];
            var scene = await d.Bridge.SceneAsync(aid).ConfigureAwait(false);
            var others = new List<KeyValuePair<int, string>>();
            foreach (var (id, _) in d.Adapter.ScenePresent(scene))
            {
                string nm;
                if (d.Names.TryGetValue(id, out nm) && id != aid && !others.Any(o => o.Key == id)) others.Add(new KeyValuePair<int, string>(id, nm));
            }
            var byName = new Dictionary<string, int>();
            foreach (var o in others) byName[o.Value] = o.Key;
            bool selfMinor = d.IsMinor(aid);
            Func<string, bool> targetMinor = n => d.IsMinor(byName[n]);
            bool minorInScene = selfMinor || others.Any(o => d.IsMinor(o.Key));
            var wanted = others.Count > 0 ? ToolNames.ToList() : new List<string> { "remember", "do_nothing" };
            var allowed = d.ToolsAllowed(wanted, minorInScene);
            var mems = mind.Memories.OrderBy(m => -m.Game).Take(5).ToList();
            string worryText = d.Concerns.WantsLines(mind);
            string wantsText = string.Join("\n", mind.Wants.Select(w => "- " + GameData.Clean(w, "label")));
            string presentText = string.Join("\n", others.Select(o => "- " + GameData.Name(o.Value)));
            string memText = string.Join("\n", mems.Select(m => "- " + GameData.Clean(m.Text, "event")));
            string user = "=== YOUR WANTS ===\n" + GameData.DataSection("wants", wantsText.Length > 0 ? wantsText : "- none") +
                "=== WHO IS HERE ===\n" + GameData.DataSection("present", presentText.Length > 0 ? presentText : "- nobody") +
                (worryText.Length > 0 ? "=== YOUR WORRIES ===\n" + GameData.DataSection("worries", worryText) : "") +
                "=== RECENT MEMORIES ===\n" + GameData.DataSection("memories", memText.Length > 0 ? memText : "- nothing") +
                "Pick at most one action.";
            int doneSocial = 0, doneRemember = 0;
            bool unsupported = false;

            Func<string, JObject, string> validator = (tool, args) =>
            {
                string text = string.Join(" ", args.Properties().Where(p => p.Value.Type == JTokenType.String).Select(p => (string)p.Value));
                string tgt = args["target"] != null && args["target"].Type == JTokenType.String ? (string)args["target"] : null;
                if (Social.Contains(tool))
                {
                    if (doneSocial > 0) return "only one social action per window";
                    if (tgt == null || !byName.ContainsKey(tgt)) return "target is not here";
                }
                if (tool == "remember" && doneRemember > 0) return "only one fact per window";
                if (AgeGate.Violation(text, selfMinor || (tgt != null && targetMinor(tgt)), d.MinorTokens()))
                {
                    st.Refused++;
                    return "not allowed: nothing romantic or sexual involving a minor";
                }
                return null;
            };

            Func<string, JObject, Task<JToken>> executor = async (tool, values) =>
            {
                if (tool == "do_nothing") return null;
                if (tool == "remember")
                {
                    double imp = (values["importance"].Value<double>() - Scale.Importance.Lo) / (double)(Scale.Importance.Hi - Scale.Importance.Lo);
                    mind.Memories.Add(new Memory
                    {
                        Id = mind.NextMemId(), Game = d.Bridge.Env.Game, Text = GameData.Clean(PyStrOf(values["fact"]), "event"),
                        Visibility = "private", Importance = Py.PyFmt.Round(imp, 2), Etype = "want",
                    });
                    mind.UnreflectedImportance += imp;
                    d.Dirty.Add(aid);
                    doneRemember++;
                    st.Remembered++;
                    return new JObject { ["ok"] = true };
                }
                int tid = byName[(string)values["target"]];
                try
                {
                    await d.Bridge.SocialActionAsync(aid, tool, tid, values["topic"] != null ? PyStrOf(values["topic"]) : "").ConfigureAwait(false);
                }
                catch (BridgeError e)
                {
                    if (e.Status == 404)
                    {
                        unsupported = true;
                        st.Unsupported++;
                    }
                    throw;
                }
                doneSocial++;
                st.Acted++;
                return new JObject { ["ok"] = true };
            };

            var harness = new ToolHarness(backend, Tools(others.Select(o => o.Value).OrderBy(x => x, StringComparer.Ordinal).ToList()), mode, maxSteps: 3);
            var allowedSorted = allowed.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var res = await harness.RunAsync(SystemText, user, executor, Loop.SyncCheck(validator),
                new LoopAction("call", "do_nothing").AsFallback(), o => o.Allowed = allowedSorted).ConfigureAwait(false);
            if (res.Fallbacks > 0) st.Fallbacks++;
            if (res.Executed.Count == 0 || res.Executed.All(e => e.Tool == "do_nothing")) st.Nothing++;
            var outc = new WantsOutcome
            {
                Actor = aid, Outcome = res.Outcome, Allowed = res.Allowed,
                Executed = res.Executed.Select(e => (e.Tool, e.Args, e.Error)).ToList(),
                Unsupported = unsupported, MinorInScene = minorInScene,
            };
            st.Trace.Add(outc);
            if (st.Trace.Count > 200) st.Trace.RemoveRange(0, st.Trace.Count - 200);
            return outc;
        }

        private static string PyStrOf(JToken t) { return Py.PyJson.Str(t); }
    }
}
