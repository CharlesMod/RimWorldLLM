using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>A heater/cooler target temperature waiting for its building: blueprints cannot hold one, so the game component
    /// sets it once the building stands (Verse-free decisions here, the Verse side in PlaceGameComponent).</summary>
    public sealed class PendingTarget
    {
        public int Map, X, Z, Degrees, Tick;
        public string Def;
        public PendingTarget Copy() { return new PendingTarget { Map = Map, X = X, Z = Z, Degrees = Degrees, Tick = Tick, Def = Def }; }
    }

    public enum PendingVerdict { Keep, Apply, Drop }

    public static class PlacePending
    {
        public const int Interval = 250;      // ticks between checks
        public const int MaxAge = 60000;      // one day: older entries are dropped
        public const int MaxEntries = 60;

        public static bool Due(int ticksGame) { return ticksGame % Interval == 0; }

        /// <summary>Adds an entry: the same map, cell and def replaces the old one (newest target wins); the oldest go when over the cap.</summary>
        public static void Merge(IList<PendingTarget> list, PendingTarget add)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Map == add.Map && list[i].X == add.X && list[i].Z == add.Z && string.Equals(list[i].Def, add.Def, StringComparison.Ordinal))
                    list.RemoveAt(i);
            list.Add(add);
            while (list.Count > MaxEntries) list.RemoveAt(0);
        }

        /// <summary>Apply once the thing stands; drop when too old or when nothing (no blueprint, frame or thing) is left at the cell.</summary>
        public static PendingVerdict Decide(PendingTarget e, int now, bool built, bool planned)
        {
            if (built) return PendingVerdict.Apply;
            int age = now - e.Tick;
            if (age > MaxAge) return PendingVerdict.Drop;
            if (!planned) return PendingVerdict.Drop;
            return PendingVerdict.Keep;
        }

        /// <summary>The target a new heater or cooler gets: the asked one; a cooler in a freezer gets -10; else none (the game default).</summary>
        public static int? DefaultDegrees(string op, int? asked, string roomLabel, string roomRole)
        {
            if (asked != null) return asked;
            if (op == "cooler" && ((roomLabel ?? "").IndexOf("freezer", StringComparison.OrdinalIgnoreCase) >= 0
                                   || (roomRole ?? "").IndexOf("freezer", StringComparison.OrdinalIgnoreCase) >= 0)) return -10;
            return null;
        }

        public static string PendingNote(int degrees) { return "target " + degrees + " C will be set once it is built"; }

        /// <summary>"set 2 heater/cooler in the freezer to -10 C" (+ queued ones).</summary>
        public static string TargetNote(int set, int queued, string roomLabel, int degrees)
        {
            var parts = new List<string>();
            if (set > 0) parts.Add("set " + set + " heater/cooler in " + The(roomLabel) + " to " + degrees + " C");
            if (queued > 0) parts.Add((set > 0 ? "and " : "") + "queued " + queued + " more to " + degrees + " C once built");
            return string.Join(" ", parts);
        }

        public static string NoClimateMessage(string roomLabel)
        {
            return "no heater or cooler in " + The(roomLabel) + "; place one with place cooler first";
        }

        public static string NoSpotMessage(string roomLabel, int w, int h, string thing, string whyDetail)
        {
            string s = The(roomLabel) + " has no free spot for a " + w + "x" + h + " " + thing + "; build a larger room or remove something";
            return string.IsNullOrWhiteSpace(whyDetail) ? s : s + " (" + whyDetail.Trim() + ")";
        }

        /// <summary>Is a build refusal the "no spot in that room" kind (so the advice above fits)?</summary>
        public static bool IsNoSpot(string reason)
        {
            reason = reason ?? "";
            return reason.StartsWith("no free spot for", StringComparison.Ordinal) || reason.StartsWith("no wall cell of that room", StringComparison.Ordinal);
        }

        /// <summary>The part of a build refusal after its first ": " (the game's own reason), or null.</summary>
        public static string Detail(string reason)
        {
            reason = reason ?? "";
            int i = reason.IndexOf(": ", StringComparison.Ordinal);
            return i < 0 ? null : reason.Substring(i + 2);
        }

        public static string The(string label)
        {
            label = (label ?? "").Trim();
            if (label.Length == 0) return "the room";
            if (label.StartsWith("the ", StringComparison.OrdinalIgnoreCase) || label.IndexOf("'s ", StringComparison.Ordinal) >= 0 || label.StartsWith("room ", StringComparison.OrdinalIgnoreCase)) return label;
            return "the " + label;
        }

        /// <summary>Order of room matching for a label: exact label, exact role word, then substring of either. -1 when none.</summary>
        public static int MatchRoom(IList<string> labels, IList<string> roles, string want)
        {
            string w = (want ?? "").Trim();
            if (w.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) w = w.Substring(4).Trim();
            if (w.Length == 0) return -1;
            for (int i = 0; i < labels.Count; i++) if (string.Equals(labels[i], w, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < roles.Count; i++) if (string.Equals(roles[i], w, StringComparison.OrdinalIgnoreCase)) return i;
            for (int i = 0; i < labels.Count; i++) if ((labels[i] ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            for (int i = 0; i < roles.Count; i++) if ((roles[i] ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return -1;
        }

        /// <summary>"the route needs 12 conduits, only 5 blueprints left this cycle; ask again next cycle".</summary>
        public static string OverCapMessage(int need, int left)
        {
            return "the route needs " + need + " conduits, only " + left + " blueprints left this cycle; ask again next cycle";
        }

        public static string NoRouteMessage(string near)
        {
            return "no powered net or generator near " + near + "; place a generator first";
        }

        /// <summary>"7 conduits" / "1 conduit".</summary>
        public static string Conduits(int n) { return n + (n == 1 ? " conduit" : " conduits"); }
    }

    /// <summary>Where the next ship part goes (Verse-free): rectangles are cell boxes (x, z of the south-west cell, width, height).</summary>
    public struct ShipRect
    {
        public int X, Z, W, H;
        public ShipRect(int x, int z, int w, int h) { X = x; Z = z; W = w; H = h; }
        public double Cx { get { return X + W / 2.0; } }
        public double Cz { get { return Z + H / 2.0; } }
    }

    public static class ShipPlan
    {
        /// <summary>The part word of a ship def ("Ship_Beam" gives "beam"), or null.</summary>
        public static string PartOfDef(string defName)
        {
            foreach (var p in ShipLayout.Order) if (string.Equals(ShipLayout.DefName(p), defName, StringComparison.Ordinal)) return p;
            return null;
        }

        /// <summary>Length of the shared border of two boxes that touch edge to edge without overlapping (0 when apart, overlapping or only corner to corner).</summary>
        public static int SharedEdge(ShipRect a, ShipRect b)
        {
            int ox = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
            int oz = Math.Min(a.Z + a.H, b.Z + b.H) - Math.Max(a.Z, b.Z);
            if (ox > 0 && oz > 0) return 0;                       // overlap
            if (oz > 0 && (a.X + a.W == b.X || b.X + b.W == a.X)) return oz;
            if (ox > 0 && (a.Z + a.H == b.Z || b.Z + b.H == a.Z)) return ox;
            return 0;
        }

        /// <summary>Score of a candidate box for a part next to the existing parts (part word, box); null when it touches none.
        /// Engines go on the far side from the other parts, caskets next to a beam, the rest tight against the group.</summary>
        public static double? Score(string part, ShipRect cand, IList<KeyValuePair<string, ShipRect>> existing)
        {
            int shared = 0; bool nextToBeam = false;
            foreach (var e in existing)
            {
                int s = SharedEdge(cand, e.Value);
                shared += s;
                if (s > 0 && e.Key == "beam") nextToBeam = true;
            }
            if (shared == 0) return null;
            var others = existing.Where(e => e.Key != part || part != "engine").Where(e => e.Key != "engine").ToList();
            if (others.Count == 0) others = existing.ToList();
            double gx = others.Average(e => e.Value.Cx), gz = others.Average(e => e.Value.Cz);
            double d2 = (cand.Cx - gx) * (cand.Cx - gx) + (cand.Cz - gz) * (cand.Cz - gz);
            switch (part)
            {
                case "engine": return d2 + shared;
                case "casket": return (nextToBeam ? 1000 : 0) + shared * 10 - d2 * 0.01;
                default: return shared * 10 - d2 * 0.01 + (nextToBeam ? 5 : 0);
            }
        }
    }
}
