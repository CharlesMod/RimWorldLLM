using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The Vivarium's two event kinds: `letter` (a letter arrived) and `alert` (an alert of interest became active).
    /// They bypass the per-kind EventGate toggles (the steward needs them), but only publish while a game is live.</summary>
    public static class VivariumEvents
    {
        // Besides every critical alert: the ones the steward must hear about even when the game rates them lower.
        static readonly HashSet<string> Interesting = new HashSet<string>
        {
            "Alert_FireInHomeArea", "Alert_StarvationColonists", "Alert_LowFood", "Alert_ColonistNeedsRescuing",
            "Alert_ColonistNeedsTend", "Alert_LifeThreateningHediff"
        };

        static readonly HashSet<string> _active = new HashSet<string>();
        static bool _primed;

        public static void Reset() { _active.Clear(); _primed = false; }

        /// <summary>Whether an alert class is worth an event (pure, for tests of the rule).</summary>
        public static bool Wanted(string name, bool critical) { return critical || (name != null && Interesting.Contains(name)); }

        /// <summary>Every 250 ticks from the game component: one `alert` event per activation.</summary>
        public static void PollAlerts()
        {
            if (Current.Game == null || Find.TickManager == null || Find.CurrentMap == null) return;
            var now = new HashSet<string>();
            foreach (var a in VivariumDigest.ActiveAlerts())
            {
                if (!Wanted(a.Name, a.Critical)) continue;
                now.Add(a.Name);
                if (!_primed || _active.Contains(a.Name)) continue;   // the first poll after a load only learns what is already up
                EventBus.Publish(new EventSpec
                {
                    Type = "alert", Severity = a.Critical ? 0.8f : 0.6f, Summary = "Alert: " + a.Label,
                    Data = new JObject { ["alert"] = a.Name, ["label"] = a.Label, ["critical"] = a.Critical }
                });
            }
            _active.Clear();
            foreach (var n in now) _active.Add(n);
            _primed = true;
        }

        public static void OnLetter(Letter let)
        {
            if (let == null || Current.Game == null || Find.TickManager == null) return;
            var choices = new JArray();
            var cl = let as ChoiceLetter;
            if (cl != null)
                foreach (var opt in cl.Choices)
                {
                    string t = VivariumDigest.OptText(opt);
                    if (!string.IsNullOrEmpty(t)) choices.Add(t);
                }
            string label = let.Label.Resolve();
            EventBus.Publish(new EventSpec
            {
                Type = "letter", Severity = choices.Count > 0 ? 0.6f : 0.3f, Summary = "Letter: " + label,
                Data = new JObject { ["letter"] = let.ID, ["label"] = label, ["choices"] = choices, ["quest"] = cl != null && cl.quest != null }
            });
        }
    }

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_VivariumLetter
    {
        [HarmonyPostfix]
        public static void Postfix(Letter let, int delayTicks)
        {
            if (delayTicks > 0) return;   // a delayed letter is not in the stack yet
            EventPayload.Safe("letter", () => VivariumEvents.OnLetter(let));
        }
    }
}
