using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Materials lying on the ground. The colony's resource counter counts only what is in storage, so the
    /// narrator and the hands add these to it. One scan per map per game tick, shared by every reader.</summary>
    public static class VivariumLoose
    {
        public class Scan
        {
            /// <summary>Loose (not forbidden) and forbidden counts by def.</summary>
            public readonly Dictionary<ThingDef, int> Loose = new Dictionary<ThingDef, int>();
            public readonly Dictionary<ThingDef, int> Forbidden = new Dictionary<ThingDef, int>();
            public readonly List<LooseItem> Items = new List<LooseItem>();
            public int LooseOf(ThingDef d) { int n; return Loose.TryGetValue(d, out n) ? n : 0; }
        }

        static int cachedMap = -1, cachedTick = -1;
        static Scan cached;

        public static Scan Get(Map map)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (cached != null && cachedMap == map.uniqueID && cachedTick == tick) return cached;
            var s = new Scan();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t == null || !t.Spawned || t.def == null || !t.def.CountAsResource) continue;
                if (t.IsInAnyStorage() || t.Fogged()) continue;
                bool forb = t.IsForbidden(Faction.OfPlayer);
                var d = forb ? s.Forbidden : s.Loose;
                int n; d.TryGetValue(t.def, out n); d[t.def] = n + t.stackCount;
                s.Items.Add(new LooseItem(t.def.label ?? t.def.defName, t.Position.x, t.Position.z, t.stackCount, forb));
            }
            cached = s; cachedMap = map.uniqueID; cachedTick = tick;
            return s;
        }

        /// <summary>Stored plus loose (not forbidden): what the colony can use, since builders haul from anywhere.</summary>
        public static int Usable(Map map, ThingDef d) { return map.resourceCounter.GetCount(d) + Get(map).LooseOf(d); }
    }
}
