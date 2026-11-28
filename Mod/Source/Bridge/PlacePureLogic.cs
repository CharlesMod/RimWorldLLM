using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    public static partial class PlacePure
    {
        /// <summary>Plain English of what a place call attempts, for greentext ("furnish the lab with a research bench").</summary>
        public static string WhatText(PlaceArgs a)
        {
            string at = AtText(a.At);
            switch (a.Op)
            {
                case "furnish": return "furnish " + at + " with " + Article(a.What);
                case "generator": return "build " + Article((a.What ?? "") + " generator");
                case "battery": return a.At == null ? "build a battery" : "build a battery near " + at;
                case "connect": return "run power to " + at;
                case "switch": return a.At == null ? "put in a power switch" : "put a power switch at " + at;
                case "heater": return "put a heater in " + at;
                case "cooler": return "put a cooler in " + at;
                case "target": return "set " + at + " to " + (a.Temp ?? "a new temperature");
                case "ship": return "build " + Article("ship " + (a.What ?? "part"));
                default: return "place something";
            }
        }

        /// <summary>"the lab" for a label, "room 12" and "#123" stay as they are.</summary>
        public static string AtText(string at)
        {
            at = (at ?? "").Trim();
            if (at.Length == 0) return "the base";
            var p = PlaceWords.ParseAt(at);
            if (p.Item1 == "id") return "building " + p.Item2;
            if (p.Item1 == "room") return "room " + p.Item2;
            return at.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? at : "the " + at;
        }

        public static string Article(string noun)
        {
            noun = (noun ?? "").Trim();
            if (noun.Length == 0) return "something";
            return ("aeiou".IndexOf(char.ToLowerInvariant(noun[0])) >= 0 ? "an " : "a ") + noun;
        }
    }

    /// <summary>A conduit route: the new cells to build (start to goal) and how many there are.</summary>
    public sealed class RouteResult
    {
        public List<int[]> Cells = new List<int[]>();
        public int NewCells;
    }

    public static class ConduitRoute
    {
        /// <summary>4-connected cheapest route from any start to any goal. cost(x,z): -1 impassable, 0 existing conduit (free, may be
        /// passed through, not listed), 1 normal, higher for doorways. Null when no route or more than maxLen new cells.
        /// A start that is already a goal gives an empty route.</summary>
        public static RouteResult Find(IList<int[]> starts, ISet<long> goals, Func<int, int, int> cost, int maxLen)
        {
            if (starts == null || goals == null || goals.Count == 0 || cost == null) return null;
            var dist = new Dictionary<long, int>();
            var news = new Dictionary<long, int>();
            var prev = new Dictionary<long, long>();
            var cells = new Dictionary<long, int[]>();
            var open = new SortedSet<Node>();
            int seq = 0;
            foreach (var s in starts.OrderBy(s => s[0]).ThenBy(s => s[1]))
            {
                long k = BaseGrid.Key(s[0], s[1]);
                if (goals.Contains(k)) return new RouteResult();
                int c = cost(s[0], s[1]);
                if (c < 0 || dist.ContainsKey(k)) continue;
                dist[k] = c; news[k] = c == 0 ? 0 : 1; prev[k] = long.MinValue; cells[k] = new[] { s[0], s[1] };
                open.Add(new Node { D = c, X = s[0], Z = s[1], Seq = seq++ });
            }
            int[] dx = { 0, 1, 0, -1 }, dz = { -1, 0, 1, 0 };
            while (open.Count > 0)
            {
                var n = open.Min; open.Remove(n);
                long nk = BaseGrid.Key(n.X, n.Z);
                if (n.D != dist[nk]) continue;
                if (goals.Contains(nk))
                {
                    var r = new RouteResult();
                    for (long k = nk; k != long.MinValue; k = prev[k])
                        if (cost(cells[k][0], cells[k][1]) != 0) r.Cells.Add(cells[k]);
                    r.Cells.Reverse();
                    r.NewCells = r.Cells.Count;
                    return r.NewCells > maxLen ? null : r;
                }
                for (int i = 0; i < 4; i++)
                {
                    int x = n.X + dx[i], z = n.Z + dz[i];
                    int c = cost(x, z);
                    if (c < 0) continue;
                    int nn = news[nk] + (c == 0 ? 0 : 1);
                    if (nn > maxLen) continue;
                    long k = BaseGrid.Key(x, z);
                    int nd = n.D + c;
                    int od;
                    if (dist.TryGetValue(k, out od) && od <= nd) continue;
                    dist[k] = nd; news[k] = nn; prev[k] = nk; cells[k] = new[] { x, z };
                    open.Add(new Node { D = nd, X = x, Z = z, Seq = seq++ });
                }
            }
            return null;
        }

        sealed class Node : IComparable<Node>
        {
            public int D, X, Z, Seq;
            public int CompareTo(Node o)
            {
                int c = D.CompareTo(o.D); if (c != 0) return c;
                c = X.CompareTo(o.X); if (c != 0) return c;
                c = Z.CompareTo(o.Z); if (c != 0) return c;
                return Seq.CompareTo(o.Seq);
            }
        }
    }

    public static class PowerWords
    {
        public static string Summary(double madeW, double usedW, double storedWh, int batteries)
        {
            string made = madeW <= 0 ? "no power made yet" : N(madeW) + " W made";
            string s = "power: " + made + ", " + N(usedW) + " W used";
            if (batteries <= 0) return s + ", no batteries";
            return s + ", " + batteries + (batteries == 1 ? " battery" : " batteries") + " holding " + N(storedWh) + " Wh";
        }
        static string N(double d) { return Math.Round(d).ToString(CultureInfo.InvariantCulture); }
    }

    public static class ShipLayout
    {
        public static readonly string[] Order = { "beam", "core", "reactor", "sensor", "engine", "casket" };

        public static string DefName(string part)
        {
            switch ((part ?? "").Trim().ToLowerInvariant())
            {
                case "beam": return "Ship_Beam";
                case "reactor": return "Ship_Reactor";
                case "engine": return "Ship_Engine";
                case "casket": return "Ship_CryptosleepCasket";
                case "sensor": return "Ship_SensorCluster";
                case "core": return "Ship_ComputerCore";
                default: return null;
            }
        }

        public static string Hint(string part)
        {
            switch ((part ?? "").Trim().ToLowerInvariant())
            {
                case "beam": return "needs ShipBasics research; beams are the skeleton, build them first and keep them uncovered by any roof";
                case "core": return "needs ShipComputerCore research; set it against the beams";
                case "reactor": return "needs ShipReactor research; it is big (6 by 7), so leave room and keep it unroofed";
                case "sensor": return "needs ShipSensorCluster research and long-range mineral scanner research; place it against the beams";
                case "engine": return "needs ShipEngine research; it is 3 by 4 and wants open ground behind it, unroofed";
                case "casket": return "needs ShipCryptosleep research; a casket goes head-on to a beam";
                default: return "ship parts: " + string.Join(", ", Order);
            }
        }
    }

    public static class PlaceWords
    {
        public static string GeneratorDef(string kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "wood": return "WoodFiredGenerator";
                case "chemfuel": return "ChemfuelPoweredGenerator";
                case "solar": return "SolarGenerator";
                case "wind": return "WindTurbine";
                case "geothermal": return "GeothermalGenerator";
                default: return null;
            }
        }

        /// <summary>"#123" -> (id, "123"); "room 12" / "room #12" -> (room, "12"); anything else -> (label, text).</summary>
        public static Tuple<string, string> ParseAt(string at)
        {
            string s = (at ?? "").Trim();
            int n;
            if (s.StartsWith("#") && int.TryParse(s.Substring(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return Tuple.Create("id", n.ToString(CultureInfo.InvariantCulture));
            if (s.StartsWith("room ", StringComparison.OrdinalIgnoreCase))
            {
                string r = s.Substring(5).Trim().TrimStart('#');
                if (int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out n)) return Tuple.Create("room", n.ToString(CultureInfo.InvariantCulture));
            }
            return Tuple.Create("label", s);
        }

        public static string NoRoomMessage(string at, IEnumerable<string> labels)
        {
            var l = (labels ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).ToList();
            return "no room called " + at + (l.Count == 0 ? "; there are no rooms yet" : "; rooms: " + string.Join(", ", l));
        }
    }
}
