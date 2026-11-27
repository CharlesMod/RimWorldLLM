// Port of agent/rimworldllm_agent/core/inner.py: the inner monologue, each actor's private stream (Mind.Inner), made of aims
// (decided in the intent pass, after a spoken line, so an aim guides the NEXT line) and musings (the idle map thought).
// Later items (concerns, dreams, reflection, diaries) read the stream through AddInner / RecentInner.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RimWorldLLM.Mind
{
    public static class Inner
    {
        public const int AimMinWords = 3;
        public const int AimMaxWords = 16;
        /// <summary>decode room the aim needs beyond the label and delivery (8)</summary>
        public const int AimExtraTokens = 30;
        /// <summary>inner.AIM_RULE re-exports prompt.AIM_RULE</summary>
        public const string AimRule = PromptBlocks.AimRule;

        /// <summary>Append one thought to the stream and cap it at keep. The caller marks the mind dirty.</summary>
        public static InnerThought AddInner(Mind mind, string text, string kind, long game, int keep = Mind.InnerKeep)
        {
            var t = new InnerThought { Game = game, Text = text, Kind = kind };
            mind.Inner.Add(t);
            if (keep > 0) mind.Inner = Mind.Tail(mind.Inner, keep);
            return t;
        }

        /// <summary>The newest n entries, oldest first. kinds filters (null = every kind). Only this actor's own.</summary>
        public static List<InnerThought> RecentInner(Mind mind, int n = 3, ISet<string> kinds = null)
        {
            var es = mind.Inner.Where(t => kinds == null || kinds.Contains(t.Kind)).ToList();
            return n > 0 ? Mind.Tail(es, n) : new List<InnerThought>();
        }

        public static List<InnerThought> InnerOnDay(Mind mind, int dayNo, long dayUnits)
        {
            return mind.Inner.Where(t => PyFloorDiv(t.Game, dayUnits) == dayNo).ToList();
        }

        private static long PyFloorDiv(long a, long b) { return RimWorldLLM.Mind.Py.PyFmt.FloorDiv(a, b); }

        /// <summary>True when text repeats or closely rephrases one of the actor's last n aims.</summary>
        public static bool RepeatsRecentAim(Mind mind, string text, int n = 4)
        {
            var prev = RecentInner(mind, n, new HashSet<string> { "aim" }).Select(t => (0, t.Text)).ToList();
            return prev.Count > 0 && LoopGuard.FindRepeat(text, 0, prev) != null;
        }

        /// <summary>Game text of a stream slice, cleaned; the caller wraps it with data_section.</summary>
        public static string ThoughtsText(IEnumerable<InnerThought> entries)
        {
            return string.Join("\n", entries.Select(t => "- " + GameData.Clean(t.Text, "line")));
        }

        /// <summary>The speaker's newest aim if it was made within window game units.</summary>
        public static string FreshAim(Mind mind, long game, long window)
        {
            for (int i = mind.Inner.Count - 1; i >= 0; i--)
            {
                var t = mind.Inner[i];
                if (t.Kind == "aim")
                {
                    long age = game - t.Game;
                    return 0 <= age && age <= window ? t.Text : null;
                }
            }
            return null;
        }

        public static string AimLine(string aim)
        {
            return "Your private aim (never say it outright): " + GameData.Clean(aim, "line") + "\n";
        }

        /// <summary>(aim or null, touched_by_age_gate). A dropped aim never drops the line.</summary>
        public static (string Text, bool Gated) SanitizeAim(string raw, SanitizeCtx ctx)
        {
            if (string.IsNullOrEmpty(raw) || Sanitize.PyStrip(raw).Length == 0) return (null, false);
            Sanitized s = Sanitize.SanitizeProse(raw, ctx.KnownNames, ctx.AllNames, ctx.PromptWords, ctx.CommonWords,
                ctx.SpeakerNames, AimMinWords, AimMaxWords, ctx.MinorInvolved, ctx.MinorNames);
            if (!s.Ok) return (null, s.Reason == "minor_safety");
            if (AgeGate.Violation(s.Text, ctx.MinorInvolved, ctx.MinorNames)) return (null, true);
            if (ctx.Secrets != null && ctx.Secrets.Count > 0 && Sanitize.LeaksSecret(s.Text, ctx.Secrets)) return (null, false);
            return (s.Text, false);
        }

        // ---- aim_ground: the scene's tension for the aim request -----------------------------------------------
        public static readonly string[] SubtextKinds = { "fear", "want", "secret", "judgment" };
        private static readonly Dictionary<string, string> SubtextText = new Dictionary<string, string>
        {
            ["fear"] = "what you fear from this person or this moment",
            ["want"] = "what you want from them",
            ["secret"] = "something you are not saying",
            ["judgment"] = "a private judgment of them",
        };
        public const string AimRuleText = "The aim must complicate the spoken line, never restate it, and never open with \"I just want\".";

        /// <summary>One subtext kind per (speaker, lease count, game hour): deterministic, and different people in the same moment
        /// draw different kinds even with greedy decoding.</summary>
        public static string SubtextKind(int speaker, int count, int hour)
        {
            string key = "subtext:" + speaker.ToString(CultureInfo.InvariantCulture) + ":" + count.ToString(CultureInfo.InvariantCulture)
                + ":" + hour.ToString(CultureInfo.InvariantCulture);
            return SubtextKinds[Sha1Mod(key, SubtextKinds.Length)];
        }

        /// <summary>int(hashlib.sha1(key).hexdigest(), 16) % n</summary>
        internal static int Sha1Mod(string key, int n)
        {
            byte[] h;
            using (var sha = SHA1.Create()) h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var hex = new StringBuilder("0");
            foreach (var b in h) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            var v = BigInteger.Parse(hex.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (int)(v % n);
        }

        private static string MoodOf(int? opinion)
        {
            if (opinion == null) return "you hardly know where you stand with them";
            if (opinion >= 30) return "you like them and they likely like you";
            if (opinion <= -30) return "you and they have a grudge or dislike between you";
            return "you are neither close nor at odds";
        }

        /// <summary>The extra words for the aim request (about 50 words at most): where the speaker stands with the listener, one
        /// subtext kind, and the rule. The content of a private memory is never put here, only that something is kept back.</summary>
        public static string AimTension(Mind mind, int speaker, int? listener, string listenerName, string kind)
        {
            var parts = new List<string>();
            if (listener != null && !string.IsNullOrEmpty(listenerName))
            {
                Relation rel;
                mind.Relations.TryGetValue(listener.Value.ToString(CultureInfo.InvariantCulture), out rel);
                parts.Add("Scene tension: " + MoodOf(rel != null ? rel.Opinion : null) + ".");
                var shared = mind.Memories.Where(m => m.People.Contains(listener.Value) && m.Visibility != "private").ToList();
                if (shared.Count > 0)
                {
                    string t = GameData.Clean(shared[shared.Count - 1].Text, "line", 70);
                    parts.Add("A shared memory: " + t);
                }
                if (mind.Memories.Any(m => m.People.Contains(listener.Value) && m.Visibility == "private"))
                    parts.Add("You are keeping something from them.");
            }
            parts.Add("Subtext of this aim: " + SubtextText[kind] + ".");
            parts.Add(AimRuleText);
            return string.Join(" ", parts);
        }
    }

    /// <summary>Contrast cloud rate limit: at most one per actor per gap play-clock seconds, and a chance.</summary>
    public sealed class CloudGate
    {
        public Dictionary<int, double> Last = new Dictionary<int, double>();

        public bool Allow(int aid, double playNow, double gap, double chance, double roll)
        {
            double last;
            if (Last.TryGetValue(aid, out last) && playNow - last < gap) return false;
            if (roll >= chance) return false;
            Last[aid] = playNow;
            return true;
        }
    }
}
