using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Heater and cooler target temperatures waiting for their building (a blueprint cannot hold one). Every 250 ticks each
    /// pending entry is checked: the building stands, so its target is set; or nothing is left, or it is a day old, so it is dropped.
    /// The decisions are PlacePending (Verse-free); saved with the game as parallel lists.</summary>
    public sealed class PlaceGameComponent : GameComponent
    {
        readonly List<PendingTarget> pending = new List<PendingTarget>();

        public PlaceGameComponent(Game game) { }

        public static void Queue(Map map, int x, int z, string defName, int degrees)
        {
            var c = Current.Game == null ? null : Current.Game.GetComponent<PlaceGameComponent>();
            if (c == null) return;
            int now = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            PlacePending.Merge(c.pending, new PendingTarget { Map = map.uniqueID, X = x, Z = z, Def = defName, Degrees = degrees, Tick = now });
        }

        public override void GameComponentTick()
        {
            if (pending.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            if (!PlacePending.Due(now)) return;
            try
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var e = pending[i];
                    Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == e.Map);
                    if (map == null) { pending.RemoveAt(i); continue; }
                    var cell = new IntVec3(e.X, 0, e.Z);
                    ThingWithComps built = null; bool planned = false;
                    if (cell.InBounds(map))
                        foreach (var t in cell.GetThingList(map))
                        {
                            if (t is Blueprint || t is Frame)
                            {
                                var bd = t.def.entityDefToBuild as ThingDef;
                                if (bd != null && bd.defName == e.Def) planned = true;
                            }
                            else if (t.def.defName == e.Def && t is ThingWithComps) built = (ThingWithComps)t;
                        }
                    var v = PlacePending.Decide(e, now, built != null, planned);
                    if (v == PendingVerdict.Keep) continue;
                    if (v == PendingVerdict.Apply)
                    {
                        var tc = built.GetComp<CompTempControl>();
                        if (tc != null) tc.TargetTemperature = e.Degrees;
                    }
                    pending.RemoveAt(i);
                }
            }
            catch (Exception ex) { Log.Warning("[RimWorldLLM] pending temperature targets failed: " + ex.Message); pending.Clear(); }
        }

        public override void ExposeData()
        {
            List<int> maps = null, xs = null, zs = null, degs = null, ticks = null;
            List<string> defs = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                maps = pending.Select(p => p.Map).ToList(); xs = pending.Select(p => p.X).ToList(); zs = pending.Select(p => p.Z).ToList();
                degs = pending.Select(p => p.Degrees).ToList(); ticks = pending.Select(p => p.Tick).ToList(); defs = pending.Select(p => p.Def).ToList();
            }
            Scribe_Collections.Look(ref maps, "rwlPlaceMaps", LookMode.Value);
            Scribe_Collections.Look(ref xs, "rwlPlaceX", LookMode.Value);
            Scribe_Collections.Look(ref zs, "rwlPlaceZ", LookMode.Value);
            Scribe_Collections.Look(ref degs, "rwlPlaceDeg", LookMode.Value);
            Scribe_Collections.Look(ref ticks, "rwlPlaceTick", LookMode.Value);
            Scribe_Collections.Look(ref defs, "rwlPlaceDef", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                pending.Clear();
                if (maps != null && xs != null && zs != null && degs != null && ticks != null && defs != null)
                {
                    int n = new[] { maps.Count, xs.Count, zs.Count, degs.Count, ticks.Count, defs.Count }.Min();
                    for (int i = 0; i < n; i++)
                        pending.Add(new PendingTarget { Map = maps[i], X = xs[i], Z = zs[i], Degrees = degs[i], Tick = ticks[i], Def = defs[i] });
                }
            }
        }
    }
}
