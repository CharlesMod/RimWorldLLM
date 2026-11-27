using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Mod-side rotating autosave for unattended play (Verse-free). The game's own Autosaver is used whenever it is on; this
    /// only runs when the game's autosave is off while the narrator plays. Slots are named RWL-Narrator-1..K; the mod never
    /// touches a file whose name does not match <see cref="IsOurs"/>.
    /// </summary>
    public static class AutosaveRotation
    {
        public const string Prefix = "RWL-Narrator-";
        public const int MaxKeep = 20;
        public const int TicksPerDay = 60000;
        /// <summary>A soft hold (a raid on, say) stops a due save for at most this long past its due time.</summary>
        public const int SoftGraceTicks = TicksPerDay / 2;
        static readonly Regex OursRx = new Regex("^" + Regex.Escape(Prefix) + "([0-9]{1,2})$", RegexOptions.CultureInvariant);

        public static string SlotName(int slot) { return Prefix + slot; }

        /// <summary>True for a name this mod made (and so may overwrite): RWL-Narrator-N with N in 1..MaxKeep.</summary>
        public static bool IsOurs(string name)
        {
            var m = OursRx.Match(name ?? "");
            if (!m.Success) return false;
            int n = int.Parse(m.Groups[1].Value);
            return n >= 1 && n <= MaxKeep;
        }

        /// <summary>Which slot to write next: the first free one, else the one written longest ago (ties: lowest number).</summary>
        public static int NextSlot(int keep, Func<int, DateTime?> lastWriteUtc)
        {
            keep = Math.Max(1, Math.Min(MaxKeep, keep));
            int best = 1; DateTime? bestT = null; bool any = false;
            for (int i = 1; i <= keep; i++)
            {
                var t = lastWriteUtc(i);
                if (t == null) return i;
                if (!any || t.Value < bestT.Value) { best = i; bestT = t; any = true; }
            }
            return best;
        }

        public enum Decision { Off, GameHandles, NotDue, Hold, Save }

        public struct Input
        {
            public bool Enabled;          // the mod setting
            public bool NarratorPlays;
            public bool GameAutosaveOn;   // Prefs.AutosaveIntervalDays > 0
            public int NowTick, LastSaveTick;
            public float EveryDays;
            public bool HardHold;         // a modal window, a long event, saving disabled: never save now
            public bool SoftHold;         // a raid or similar: wait, but only so long
        }

        public static Decision Decide(Input i)
        {
            if (!i.Enabled || !i.NarratorPlays) return Decision.Off;
            if (i.GameAutosaveOn) return Decision.GameHandles;
            int every = (int)Math.Max(1f, Math.Min(60f, i.EveryDays) * TicksPerDay);
            int late = i.NowTick - i.LastSaveTick - every;
            if (late < 0) return Decision.NotDue;
            if (i.HardHold) return Decision.Hold;
            if (i.SoftHold && late < SoftGraceTicks) return Decision.Hold;
            return Decision.Save;
        }
    }
}
