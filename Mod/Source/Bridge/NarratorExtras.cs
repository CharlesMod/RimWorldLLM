using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM
{
    /// <summary>One mood thought: label and its mood offset.</summary>
    public class MoodThought
    {
        public string Label; public double Offset;
        public MoodThought(string label, double offset) { Label = label; Offset = offset; }
    }

    /// <summary>Verse-free helpers of the narrator's extras: break risk, why a mood is low, dirty rooms.</summary>
    public static class NarratorExtras
    {
        public const int MaxWhy = 2;
        public const double NearMargin = 0.05, DirtyBelow = -1.5;

        /// <summary>"extreme", "major" or "minor" when mood (0..1) is below that mental-break threshold; "near" when within 0.05 above
        /// the minor threshold; null when safe.</summary>
        public static string BreakRisk(double mood, double minor, double major, double extreme)
        {
            if (mood < extreme) return "extreme";
            if (mood < major) return "major";
            if (mood < minor) return "minor";
            if (mood < minor + NearMargin) return "near";
            return null;
        }

        /// <summary>The worst negative thoughts, same-label thoughts added together, most negative first, at most <paramref name="max"/>.</summary>
        public static List<string> MoodWhy(IEnumerable<MoodThought> thoughts, int max = MaxWhy)
        {
            if (thoughts == null) return new List<string>();
            return thoughts.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Label))
                .GroupBy(t => t.Label.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Label = g.Key, Sum = g.Sum(t => t.Offset) })
                .Where(g => g.Sum < -0.5)
                .OrderBy(g => g.Sum).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
                .Take(max).Select(g => g.Label).ToList();
        }

        /// <summary>A room is dirty when its Cleanliness stat is below -1.5.</summary>
        public static bool IsDirty(double cleanliness) { return cleanliness < DirtyBelow; }
    }
}
