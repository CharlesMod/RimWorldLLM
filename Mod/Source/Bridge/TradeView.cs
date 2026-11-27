using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    public sealed class NeedsInput
    {
        public int Components;
        public bool FabricationDone;
        public int Medicine, KeepMedicine = 10;
        public int Steel;
        public double FoodDays; public int KeepFoodDays = 10;
        public bool ShipResearchDone;
        public int Plasteel, PlasteelNeed, Uranium, UraniumNeed, AdvancedComponents, AdvancedComponentsNeed;   // need 0 = not asked for
    }

    /// <summary>The digest's `trade` helpers. Pure.</summary>
    public static class TradeView
    {
        public const int MaxNeeds = 8;

        /// <summary>What the colony should buy, most pressing first, at most 8.</summary>
        public static List<string> Needs(NeedsInput n)
        {
            var l = new List<string>();
            if (!n.FabricationDone && n.Components < 10) l.Add("components (" + n.Components + " left, cannot make them)");
            if (n.KeepMedicine > 0 && n.Medicine < n.KeepMedicine) l.Add("medicine (" + n.Medicine + ", keep " + n.KeepMedicine + ")");
            if (n.KeepFoodDays > 0 && n.FoodDays < n.KeepFoodDays) l.Add("food (" + TradeRules.Num(n.FoodDays) + " days, keep " + n.KeepFoodDays + ")");
            if (n.Steel < 100) l.Add("steel (" + n.Steel + ")");
            if (n.ShipResearchDone)
            {
                if (n.PlasteelNeed > n.Plasteel) l.Add("plasteel (" + n.Plasteel + ", ship needs " + n.PlasteelNeed + ")");
                if (n.UraniumNeed > n.Uranium) l.Add("uranium (" + n.Uranium + ", ship needs " + n.UraniumNeed + ")");
                if (n.AdvancedComponentsNeed > n.AdvancedComponents) l.Add("advanced components (" + n.AdvancedComponents + ", ship needs " + n.AdvancedComponentsNeed + ")");
            }
            return Cap(l, MaxNeeds);
        }

        public static List<T> Cap<T>(IEnumerable<T> items, int n) { return items.Take(Math.Max(0, n)).ToList(); }

        /// <summary>The n items with the highest key.</summary>
        public static List<T> Top<T>(IEnumerable<T> items, Func<T, double> key, int n) { return Cap(items.OrderByDescending(key), n); }
    }
}
