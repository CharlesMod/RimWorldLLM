using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>The "place" tool's arguments (POST /act/place): buildings in a room, power, climate and ship parts.
    /// op: furnish (what = a building word, at = a room), generator (what = kind), battery, connect (at = room or "#id"),
    /// switch, heater, cooler, target (at = room, temp = word), ship (what = part).
    /// at: a room label, "room N", a room role, or "#&lt;thingId&gt;". temp: a word from PlacePure.Temps.</summary>
    public sealed class PlaceArgs
    {
        public string Op, What, At, Temp;
        public int? Degrees;   // temp resolved to degrees C by the route
    }

    /// <summary>Own hands interface for the place tool (game thread). Placements return HandResult.Placed with marks
    /// (blueprints) so caps and undo work; target returns Done (not undoable).</summary>
    public interface IPlaceHands
    {
        HandResult Place(PlaceArgs a, BuildBudget left);
    }

    public static partial class PlacePure
    {
        public static readonly string[] Ops = { "furnish", "generator", "battery", "connect", "switch", "heater", "cooler", "target", "ship" };
        public static readonly string[] GeneratorKinds = { "wood", "chemfuel", "solar", "wind", "geothermal" };
        public static readonly string[] ShipParts = { "beam", "reactor", "engine", "casket", "sensor", "core" };
        /// <summary>Temperature words and degrees C.</summary>
        public static readonly string[] TempWords = { "freezing", "chilled", "cool", "comfy", "warm" };
        public static readonly int[] TempDegrees = { -10, 3, 15, 21, 25 };

        public static int? Degrees(string word)
        {
            int i = Array.IndexOf(TempWords, (word ?? "").Trim().ToLowerInvariant());
            return i < 0 ? (int?)null : TempDegrees[i];
        }

        /// <summary>Supplied by the game assembly (Routes.cs); null in tests that don't set it.</summary>
        public static Func<IPlaceHands> Hands;
    }
}
