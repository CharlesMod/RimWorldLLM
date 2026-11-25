using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using HermesVoice.Bridge;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// The Social tab's opinion tooltip lists thoughts by label and offset only, so the quotes behind the mod's and the
    /// quoted vanilla opinion thoughts are appended below it. Targets a private-ish UI method: if it is missing in a game
    /// version the patch is skipped and only the Needs tab tooltip (Thought.Description) shows quotes.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SocialTooltipQuotes
    {
        static System.Reflection.MethodBase Target() { return AccessTools.Method(typeof(SocialCardUtility), "GetPawnRowTooltip"); }
        public static bool Prepare() { return Target() != null; }
        public static System.Reflection.MethodBase TargetMethod() { return Target(); }

        public static void Postfix(ref string __result, object[] __args)
        {
            try
            {
                if (!QuotedThought.Shown || __args == null || __args.Length < 2) return;
                var self = __args[1] as Pawn;
                var f = AccessTools.Field(__args[0].GetType(), "otherPawn");
                var other = f?.GetValue(__args[0]) as Pawn;
                string extra = OpinionQuotes(self, other);
                if (extra.Length > 0) __result = __result + "\n\n" + extra;
            }
            catch (Exception e) { Log.WarningOnce("[HermesVoice] social tooltip quotes: " + e.Message, 7710031); }
        }

        /// <summary>Up to three newest quotes behind self's opinion thoughts about other.</summary>
        public static string OpinionQuotes(Pawn self, Pawn other)
        {
            var mems = self?.needs?.mood?.thoughts?.memories?.Memories;
            if (mems == null || other == null) return "";
            var found = new List<KeyValuePair<string, QuoteEntry>>();
            foreach (var m in mems)
            {
                var hs = m as Thought_HVSocial;
                if (hs == null || hs.otherPawn != other) continue;
                foreach (var q in hs.quotes) found.Add(new KeyValuePair<string, QuoteEntry>(hs.LabelCap, q));
            }
            var top = found.GroupBy(k => k.Value.Tick + "|" + k.Value.Text).Select(g => g.First())
                           .OrderByDescending(k => k.Value.Tick).Take(Quotes.MaxShown).ToList();
            return Quotes.Tooltip(top.Select(k => k.Value).ToList(), q =>
            {
                string label = top.First(k => k.Value == q).Key;
                return string.IsNullOrEmpty(q.Speaker) ? UiUtil.T("HV_QuoteNoSpeaker", label, q.Text) : UiUtil.T("HV_QuoteThought", label, q.Speaker, q.Text);
            });
        }
    }
}
