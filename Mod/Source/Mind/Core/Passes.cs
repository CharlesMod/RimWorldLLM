// Port of agent/rimworldllm_agent/core/passes.py: line pass and intent pass: a line (stop at newline, 48-token cap), then the
// GBNF-constrained intent in the same slot. A failed line is dropped, never shown.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RimWorldLLM.Mind
{
    public sealed class SanitizeCtx
    {
        public ISet<string> SpeakerNames = new HashSet<string>();
        public ISet<string> KnownNames = new HashSet<string>();
        public ISet<string> AllNames = new HashSet<string>();
        public ISet<string> PromptWords = new HashSet<string>();
        public ISet<string> CommonWords = new HashSet<string>();
        /// <summary>private memory texts the listener must not hear quoted</summary>
        public List<string> Secrets = new List<string>();
        /// <summary>the speaker or the listener is under 18 (or of unknown age)</summary>
        public bool MinorInvolved;
        /// <summary>name tokens of every minor in the world</summary>
        public ISet<string> MinorNames = new HashSet<string>();
        /// <summary>the speaker's voice card speaks of itself by name</summary>
        public bool ThirdPerson;
        /// <summary>cut a label the model leaks after the line ("... kind")</summary>
        public IList<string> IntentLabels = new string[0];
    }

    public class Spoken
    {
        public string Text;
        public string Intent;
        /// <summary>"normal" | "shout" | "whisper"; null = the pass did not say (the mod guesses)</summary>
        public string Delivery;
        /// <summary>the private aim (sanitised, age-gated); null = off or dropped (never drops the line)</summary>
        public string Aim;
        /// <summary>the age gate touched the aim (it was dropped for it)</summary>
        public bool AimGated;
        public Spoken(string text, string intent, string delivery = null, string aim = null, bool aimGated = false)
        { Text = text; Intent = intent; Delivery = delivery; Aim = aim; AimGated = aimGated; }
    }

    public class Dropped
    {
        public string Reason;
        public Dropped(string reason) { Reason = reason; }
    }

    /// <summary>A further validation of a sanitised line: null when fine, else (reason code, request text for the retry).</summary>
    public delegate (string Reason, string Request)? LineCheck(string text);

    /// <summary>One event per check, for the on_check callback.</summary>
    public sealed class CheckEvent
    {
        public int Attempt;
        public string Text;
        public bool Refused;
        public string Reason;
    }

    public static class Passes
    {
        public static readonly Regex JustWant = new Regex(@"^\W*i\s+just\s+want", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static List<LlmMessage> Msgs(IEnumerable<(string, string)> m)
        {
            return m.Select(t => new LlmMessage(t.Item1, t.Item2)).ToList();
        }

        public static async Task<(Sanitized, List<(string, string)>)> LinePassAsync(ILlmClient llm, int slot, Mind mind, string name, string prefix,
            IList<(string, string)> turns, int turnNo, SanitizeCtx ctx, int maxTokens = 48, string request = null,
            string aim = null, string speech = null)
        {
            var msgs = PromptBlocks.LineMessages(prefix, turns, mind, name, turnNo, request, aim, speech);
            Completion c;
            try
            {
                c = await llm.CompleteAsync(Msgs(msgs), slot, "line", maxTokens, new[] { "\n" }, actor: mind.ActorId).ConfigureAwait(false);
            }
            catch (LlmError e)
            {
                return (new Sanitized(null, "llm_error:" + e.Cause), msgs);
            }
            var s = Sanitize.SanitizeLine(c.Text, ctx.SpeakerNames, ctx.KnownNames, ctx.AllNames, ctx.PromptWords,
                truncated: c.Stats.PredictedN >= maxTokens, commonWords: ctx.CommonWords, secrets: ctx.Secrets,
                narrationGuard: !ctx.ThirdPerson, involvesMinor: ctx.MinorInvolved, minorNames: ctx.MinorNames,
                intentLabels: ctx.IntentLabels);
            return (s, msgs);
        }

        public static string ParseIntent(string text, IList<string> labels)
        {
            string t = Sanitize.PyStrip(text).ToLowerInvariant().Trim('.', ':', '"', '\'', ' ');
            if (labels.Contains(t)) return t;
            var parts = Sanitize.PySplit(t);
            string first = parts.Length > 0 ? parts[0] : "";
            return labels.Contains(first) ? first : labels[0];     // unparseable means the "nothing" label
        }

        public static string ParseDelivery(string text)
        {
            var words = Sanitize.PySplit(Sanitize.PyStrip(text).ToLowerInvariant().Replace(",", " "));
            foreach (var w in words.Skip(1))
            {
                string s = w.Trim('.', ':', '"', '\'');
                if (PromptBlocks.Deliveries.Contains(s)) return s;
            }
            return null;
        }

        /// <summary>"label delivery aim words...": the label, the delivery (null if absent) and the rest as the raw aim.</summary>
        public static (string Label, string Delivery, string Aim) ParseIntentAim(string text, IList<string> labels)
        {
            var words = Sanitize.PySplit(Sanitize.PyStrip(text)).ToList();
            string label = labels[0];
            if (words.Count > 0 && labels.Contains(words[0].ToLowerInvariant().Trim('.', ':', '"', '\'', ' ')))
            {
                label = words[0].ToLowerInvariant().Trim('.', ':', '"', '\'', ' ');
                words.RemoveAt(0);
            }
            else if (words.Count > 0) words.RemoveAt(0);
            string delivery = null;
            if (words.Count > 0 && PromptBlocks.Deliveries.Contains(words[0].ToLowerInvariant().Trim('.', ':', '"', '\'', ',')))
            {
                delivery = words[0].ToLowerInvariant().Trim('.', ':', '"', '\'', ',');
                words.RemoveAt(0);
            }
            return (label, delivery, string.Join(" ", words));
        }

        /// <summary>The intent pass, optionally with the private aim in the same call (aim=true). Returns (label, delivery, raw aim);
        /// the raw aim is "" when off or not asked. With no listener there is no call, unless aim and soloAim: then an
        /// aim-only call (root ::= aim).</summary>
        public static async Task<(string Label, string Delivery, string Aim)> IntentPassAimAsync(ILlmClient llm, int slot,
            IList<(string, string)> msgs, string line, string listenerName, IList<string> labels, int? actor = null, bool aim = false,
            bool soloAim = true, string aimExtra = null)
        {
            if (listenerName == null)
            {
                if (!(aim && soloAim)) return (labels[0], null, "");
                try
                {
                    var c = await llm.CompleteAsync(Msgs(PromptBlocks.AimMessages(msgs, line, aim ? aimExtra : null)), slot, "intent",
                        Inner.AimExtraTokens + 4, new string[0], PromptBlocks.AimOnlyGrammar, 0.0, actor).ConfigureAwait(false);
                    return (labels[0], null, c.Text);
                }
                catch (LlmError)
                {
                    return (labels[0], null, "");
                }
            }
            Completion c2;
            try
            {
                c2 = await llm.CompleteAsync(Msgs(PromptBlocks.IntentMessages(msgs, line, listenerName, PromptBlocks.Deliveries, aim, aim ? aimExtra : null)),
                    slot, "intent", 8 + (aim ? Inner.AimExtraTokens : 0), new string[0], PromptBlocks.GrammarFor(labels, PromptBlocks.Deliveries, aim),
                    0.0, actor).ConfigureAwait(false);
            }
            catch (LlmError)
            {
                return (labels[0], null, "");
            }
            if (aim) return ParseIntentAim(c2.Text, labels);
            return (ParseIntent(c2.Text, labels), ParseDelivery(c2.Text), "");
        }

        /// <summary>Same warm slot: prefix is byte-identical to the line pass, so only the tail is prefilled.
        /// One constrained completion gives the social label and the delivery.</summary>
        public static async Task<(string Label, string Delivery)> IntentPassAsync(ILlmClient llm, int slot, IList<(string, string)> msgs,
            string line, string listenerName, IList<string> labels, int? actor = null)
        {
            var r = await IntentPassAimAsync(llm, slot, msgs, line, listenerName, labels, actor).ConfigureAwait(false);
            return (r.Label, r.Delivery);
        }

        /// <summary>A line, then its intent. check(text) is a further validation of a sanitised line: it returns null when
        /// the line is fine, or (reason code, request text for the retry). A refused line is retried up to retries
        /// times with that request, then the result is Dropped(reason code). onCheck gets one event per check.
        /// Returns a Spoken or a Dropped.</summary>
        public static async Task<object> SpeakAsync(ILlmClient llm, int slot, Mind mind, string name, string prefix,
            IList<(string, string)> turns, int turnNo, SanitizeCtx ctx, string listenerName, IList<string> labels, int maxTokens = 48,
            string request = null, bool aim = false, bool soloAim = true, string freshAim = null, LineCheck check = null,
            int retries = 0, Action<CheckEvent> onCheck = null, string speech = null, string aimExtra = null,
            ILlmClient intentLlm = null, bool dropJustWant = false)
        {
            var (s, msgs) = await LinePassAsync(llm, slot, mind, name, prefix, turns, turnNo, ctx, maxTokens, request, freshAim, speech).ConfigureAwait(false);
            int attempt = 0;
            while (s.Ok && check != null)
            {
                var bad = check(s.Text);
                onCheck?.Invoke(new CheckEvent { Attempt = attempt, Text = s.Text, Refused = bad != null, Reason = bad != null ? bad.Value.Reason : "" });
                if (bad == null) break;
                if (attempt >= retries) return new Dropped(bad.Value.Reason);
                attempt += 1;
                (s, msgs) = await LinePassAsync(llm, slot, mind, name, prefix, turns, turnNo, ctx, maxTokens, bad.Value.Request, freshAim, speech).ConfigureAwait(false);
            }
            if (!s.Ok) return new Dropped(s.Reason);
            // aim_ground: the intent pass may go through another client (aim_sampler) and carry the scene's tension
            var (intent, delivery, rawAim) = await IntentPassAimAsync(aim && intentLlm != null ? intentLlm : llm, slot, msgs, s.Text,
                listenerName, labels, mind.ActorId, aim, soloAim, aimExtra).ConfigureAwait(false);
            var (aimText, gated) = Inner.SanitizeAim(rawAim, ctx);
            if (dropJustWant && aimText != null && aimText.Length > 0 && JustWant.IsMatch(aimText))
                aimText = null;                                   // a dropped aim never drops the line
            return new Spoken(s.Text, intent, delivery, aimText, gated);
        }
    }
}
