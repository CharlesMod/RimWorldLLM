using System;
using System.Collections.Generic;
using System.Linq;

// Vivarium Biotech: the hands interface (separate from IVivariumHands) and the pure helpers. Verse-free.
namespace RimWorldLLM.Bridge
{
    /// <summary>POST /act/xenogerm. Op: assemble or implant.</summary>
    public sealed class XenogermArgs
    {
        public string Op;                                   // assemble | implant
        public List<string> Genepacks = new List<string>(); // assemble: ids as strings, or labels
        public string Name;                                 // assemble: the xenogerm's name
        public int Xenogerm;                                // implant: the xenogerm's thing id
        public int Pawn;                                    // implant: who gets it
        public string Plan;                                 // both: why, in 10..200 chars, so the player can read it
    }

    /// <summary>The Biotech hands. Game thread. A hands object that does not implement this answers "Biotech hands are not available".</summary>
    public interface IVivariumBioHands
    {
        /// <summary>mechanitor 0 = the only/first one; group 1-based; mode work|escort|recharge|shutdown.</summary>
        HandResult MechMode(int mechanitor, int group, string mode);
        HandResult MechGroup(int mech, int group);
        /// <summary>gestator 0 = any; count 1..3; headroom = bandwidth that must stay free after.</summary>
        HandResult MechGestate(string kind, int gestator, int count, int headroom);
        /// <summary>genepack 0 = all unstored; bank 0 = any with space.</summary>
        HandResult Genepack(int genepack, int bank);
        HandResult Xenogerm(XenogermArgs a);
        /// <summary>op donate|stop.</summary>
        HandResult Hemogen(string op, int prisoner);
        /// <summary>preset school|play|default. hours is null from the route; the hand builds them with
        /// BioPure.ChildHours(preset, age) and checks with BioPure.CheckChildHours.</summary>
        HandResult ChildSchedule(int pawn, string preset, string[] hours);
        /// <summary>feeder 0 = clear; mode never|childcare|urgent.</summary>
        HandResult Feed(int baby, int feeder, string mode);
        /// <summary>priority 0..4.</summary>
        HandResult ChildCare(int pawn, int priority);
    }

    /// <summary>One active gene, as the game side hands it to <see cref="BioPure.GeneEffects"/>.</summary>
    public sealed class GeneInfo { public string DefName, Label, Chemical; public bool Hemogen; }

    public sealed class BioMissingInput
    {
        public int Mechs, Rechargers, Mechanitors, Gestators, Children, SchoolDesks, Babies, Cribs, Wastepacks,
                   HemogenCarriers, HemogenPacks, UnstoredGenepacks, GeneBanks;
        public bool WasteStockpile, BasicMechtech;
    }

    public static class BioPure
    {
        public const int DefaultHeadroom = 1;
        public const int MaxMissing = 6, MaxEffects = 6;
        public static readonly string[] Modes = { "work", "escort", "recharge", "shutdown" };
        public static readonly string[] ChildPresets = { "school", "play", "default" };

        // ---------------------------------------------------------------- bandwidth
        public static int HeadroomFor(int total) { return DefaultHeadroom; }

        public static int FreeBandwidth(int used, int gestating, int total) { return Math.Max(0, total - used - gestating); }

        /// <summary>ok when used + gestating + need*count + headroom &lt;= total.</summary>
        public static bool BandwidthOk(int used, int gestating, int total, int need, int count, int headroom, string kind, out string reason)
        {
            reason = null;
            if (count < 1) count = 1;
            if (used + gestating + need * count + headroom <= total) return true;
            string k = string.IsNullOrWhiteSpace(kind) ? "mech" : kind.Trim().ToLowerInvariant();
            string needs = count == 1 ? "a " + k + " needs " + need : count + " " + k + "s need " + (need * count);
            reason = "not enough bandwidth: " + (used + gestating) + " of " + total + " used, " + needs + " and " + headroom + " stays free";
            return false;
        }

        // ---------------------------------------------------------------- mech modes
        public static string ModeWord(string defName)
        {
            string d = (defName ?? "").Trim();
            switch (d.ToLowerInvariant())
            {
                case "work": return "work";
                case "escort": return "escort";
                case "recharge": return "recharge";
                case "selfshutdown": return "shutdown";
                default: return d.ToLowerInvariant();
            }
        }

        /// <summary>The MechWorkModeDef defName for a word; null when unknown.</summary>
        public static string ModeDef(string word)
        {
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "work": return "Work";
                case "escort": return "Escort";
                case "recharge": return "Recharge";
                case "shutdown": case "dormant": case "self-shutdown": case "selfshutdown": case "self shutdown": return "SelfShutdown";
                default: return null;
            }
        }

        // ---------------------------------------------------------------- gene effects
        // (match kind, defName, word), in priority order. "=" equals, "~" contains; case-insensitive.
        static readonly string[][] GeneTable =
        {
            new[] { "=", "Hemogenic", "needs hemogen" },
            new[] { "=", "Deathrest", "deathrest" },
            new[] { "=", "MoveSpeed_Quick", "fast" },
            new[] { "=", "MoveSpeed_VeryQuick", "very fast" },
            new[] { "=", "MoveSpeed_Slow", "slow" },
            new[] { "=", "Aggression_Aggressive", "aggressive" },
            new[] { "=", "Aggression_HyperAggressive", "very aggressive" },
            new[] { "=", "Aggression_DeadCalm", "calm" },
            new[] { "=", "KillThirst", "kill thirst" },
            new[] { "~", "Violence", "won't fight" },
            new[] { "=", "UVSensitivity_Intense", "burns in daylight" },
            new[] { "=", "UVSensitivity_Mild", "dislikes daylight" },
            new[] { "=", "Robust", "tough" },
            new[] { "=", "Delicate", "fragile" },
            new[] { "=", "StrongStomach", "strong stomach" },
            new[] { "=", "Sleepy", "sleeps a lot" },
            new[] { "~", "NeverSleep", "never sleeps" },
            new[] { "=", "Learning_Slow", "learns slowly" },
            new[] { "=", "Learning_Fast", "learns fast" },
            new[] { "=", "Immunity_Weak", "sickly" },
            new[] { "=", "Immunity_Strong", "rarely sick" },
            new[] { "=", "WoundHealing_Fast", "heals fast" },
            new[] { "=", "WoundHealing_SuperFast", "heals fast" },
            new[] { "=", "Ageless", "ageless" },
            new[] { "=", "Mood_Depressive", "gloomy" },
            new[] { "=", "Mood_Pessimist", "gloomy" },
            new[] { "=", "Mood_Optimist", "cheerful" },
            new[] { "=", "Mood_Sanguine", "cheerful" },
            new[] { "=", "FireTerror", "terrified of fire" },
        };

        static bool Matches(string[] row, GeneInfo g)
        {
            string d = g.DefName ?? "";
            if (row[0] == "=") return string.Equals(d, row[1], StringComparison.OrdinalIgnoreCase);
            return d.IndexOf(row[1], StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Short words for what a pawn's genes do: the known table first (in priority order), then
        /// "needs X" for chemical dependencies. Distinct, at most 6. Unknown genes are skipped.</summary>
        public static List<string> GeneEffects(IEnumerable<GeneInfo> genes)
        {
            var list = (genes ?? Enumerable.Empty<GeneInfo>()).Where(g => g != null).ToList();
            var outp = new List<string>();
            void Add(string w) { if (!string.IsNullOrWhiteSpace(w) && !outp.Contains(w)) outp.Add(w); }
            foreach (var row in GeneTable)
            {
                if (list.Any(g => Matches(row, g) || (row[2] == "needs hemogen" && g.Hemogen))) Add(row[2]);
            }
            foreach (var g in list)
            {
                if (g.Hemogen) { Add("needs hemogen"); continue; }
                if (!string.IsNullOrWhiteSpace(g.Chemical)) Add("needs " + g.Chemical.Trim().ToLowerInvariant());
            }
            return outp.Count > MaxEffects ? outp.GetRange(0, MaxEffects) : outp;
        }

        // ---------------------------------------------------------------- child hours
        /// <summary>24 letters: S sleep, A anything, J joy (recreation), W work. Null for age 13+.</summary>
        public static string[] ChildHours(string preset, int ageYears)
        {
            if (ageYears >= 13) return null;
            var h = new string[24];
            for (int i = 0; i < 24; i++) h[i] = "A";
            if (ageYears < 3)
            {
                for (int i = 0; i < 24; i++) if (i >= 19 || i < 6) h[i] = "S";
                return h;
            }
            for (int i = 0; i < 24; i++) if (i >= 21 || i < 6) h[i] = "S";
            switch ((preset ?? "default").Trim().ToLowerInvariant())
            {
                case "school":
                    Fill(h, 17, 19, "J");
                    break;
                case "play":
                    Fill(h, 9, 12, "J"); Fill(h, 15, 18, "J");
                    break;
                default:
                    Fill(h, 16, 18, "J");
                    if (ageYears >= 7) Fill(h, 13, 15, "W");
                    break;
            }
            return h;
        }

        static void Fill(string[] h, int from, int to, string c) { for (int i = from; i < to; i++) h[i] = c; }

        /// <summary>Null when the hours are fine for the age, else a reason in words.</summary>
        public static string CheckChildHours(int ageYears, string[] hours)
        {
            if (ageYears >= 13) return "not a child";
            if (hours == null || hours.Length != 24) return "a day has 24 hours";
            int s = 0, a = 0, j = 0, w = 0;
            foreach (var x in hours)
            {
                switch ((x ?? "").Trim().ToUpperInvariant())
                {
                    case "S": s++; break;
                    case "A": a++; break;
                    case "J": j++; break;
                    case "W": w++; break;
                    default: return "hours must be S, A, J or W";
                }
            }
            if (ageYears < 3)
            {
                if (w > 0) return "a baby can't have work hours";
                if (j > 0) return "a baby can't have joy hours";
                if (s < 10) return "babies need at least 10 hours of sleep";
                return null;
            }
            if (s < 9) return "children need at least 9 hours of sleep";
            if (w > 0 && ageYears < 7) return "a child of " + Math.Max(0, ageYears) + " can't have work hours";
            if (w > 3) return "a child can have at most 3 work hours";
            if (a + j < 4) return "children need at least 4 free hours";
            return null;
        }

        // ---------------------------------------------------------------- missing / trend
        public static List<string> Missing(BioMissingInput i)
        {
            var o = new List<string>();
            if (i == null) return o;
            if (i.Mechs > 0 && i.Rechargers == 0) o.Add("mech recharger");
            if (i.Mechanitors > 0 && i.Gestators == 0 && i.BasicMechtech) o.Add("mech gestator");
            if (i.Children > 0 && i.SchoolDesks == 0) o.Add("school desk");
            if (i.Babies > i.Cribs) o.Add("crib");
            if (i.Wastepacks > 0 && !i.WasteStockpile) o.Add("waste stockpile");
            if (i.HemogenCarriers > 0 && i.HemogenPacks == 0) o.Add("hemogen packs");
            if (i.UnstoredGenepacks > 0 && i.GeneBanks == 0) o.Add("gene bank");
            return o.Count > MaxMissing ? o.GetRange(0, MaxMissing) : o;
        }

        /// <summary>"growing" when the polluted count rose, else "steady".</summary>
        public static string Trend(int prevPolluted, int nowPolluted) { return nowPolluted > prevPolluted ? "growing" : "steady"; }
    }
}
