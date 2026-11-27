using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Game side of the narrator's "why" views (docs: xlate contract sections 1-4): where loose piles could be
    /// stored, which blueprints, frames and bills are stuck and why, what a pawn cannot do, who is idle. The words are
    /// chosen by the pure <see cref="NarratorWork"/>. Each scan is cached per map per game tick and capped.</summary>
    public static class VivariumStuck
    {
        const int MaxPileJudge = 30, MaxSites = 300, MaxBenches = 12, MaxBillsPerBench = 6;

        static int Tick() { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; }

        // ---- loose piles ----

        static int pilesMap = -1, pilesTick = -1;
        static List<LoosePile> piles;

        /// <summary>Loose piles (biggest first, at most <see cref="MaxPileJudge"/>), each non-forbidden one judged for storage.</summary>
        public static List<LoosePile> LoosePiles(Map map, int[] home)
        {
            if (piles != null && pilesMap == map.uniqueID && pilesTick == Tick()) return piles;
            var scan = VivariumLoose.Get(map);
            var list = RimWorldLLM.BuildView.GroupLoose(scan.Items, home[0], home[1], RimWorldLLM.BuildView.PileCell, MaxPileJudge);
            List<IHaulDestination> dests = null;
            foreach (var p in list)
            {
                if (p.Forbidden || p.Ref < 0 || p.Ref >= scan.Things.Count) continue;
                try
                {
                    var t = scan.Things[p.Ref];
                    if (t == null || !t.Spawned) continue;
                    if (dests == null) dests = map.haulDestinationManager.AllHaulDestinationsListForReading.ToList();
                    bool any = false;
                    foreach (var d in dests)
                    {
                        var st = d.GetStoreSettings();
                        if (st != null && st.AllowedToAccept(t)) { any = true; break; }
                    }
                    IntVec3 c = IntVec3.Invalid;
                    bool found = any && StoreUtility.TryFindBestBetterStoreCellFor(t, null, map, StoragePriority.Unstored, Faction.OfPlayer, out c, false);
                    double dist = found ? Math.Sqrt((double)(c.x - t.Position.x) * (c.x - t.Position.x) + (double)(c.z - t.Position.z) * (c.z - t.Position.z)) : 0;
                    p.Store = NarratorWork.StoreVerdict(any, found, dist);
                }
                catch (Exception) { }
            }
            piles = list; pilesMap = map.uniqueID; pilesTick = Tick();
            return list;
        }

        // ---- stuck sites and bills ----

        static int stuckMap = -1, stuckTick = -1;
        static StuckSummary stuck;

        public static StuckSummary Stuck(Map map)
        {
            if (stuck != null && stuckMap == map.uniqueID && stuckTick == Tick()) return stuck;
            var home = VivariumBuildView.Home(map) ?? new[] { map.Size.x / 2, map.Size.z / 2 };
            var items = new List<StuckItem>();
            try { Sites(map, items); } catch (Exception e) { Log.Warning("[RimWorldLLM] stuck sites failed: " + e.Message); }
            try { Bills(map, items); } catch (Exception e) { Log.Warning("[RimWorldLLM] stuck bills failed: " + e.Message); }
            stuck = NarratorWork.GroupStuck(items, home[0], home[1]);
            stuckMap = map.uniqueID; stuckTick = Tick();
            return stuck;
        }

        /// <summary>Can a colonist do this work type: not disabled and priority above 0.</summary>
        static bool SomeoneDoes(Map map, WorkTypeDef w, Dictionary<WorkTypeDef, bool> memo)
        {
            if (w == null) return true;
            bool r;
            if (memo.TryGetValue(w, out r)) return r;
            r = false;
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.workSettings == null || p.WorkTypeIsDisabled(w)) continue;
                if (p.workSettings.GetPriority(w) > 0) { r = true; break; }
            }
            memo[w] = r;
            return r;
        }

        static void Sites(Map map, List<StuckItem> items)
        {
            var sites = new List<Thing>();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)) { if (sites.Count >= MaxSites) break; if (t is Blueprint_Build) sites.Add(t); }
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)) { if (sites.Count >= MaxSites * 2) break; if (t is Frame) sites.Add(t); }
            if (sites.Count == 0) return;

            var memo = new Dictionary<WorkTypeDef, bool>();
            bool noBuilder = !SomeoneDoes(map, WorkTypeDefOf.Construction, memo);
            var needs = new List<KeyValuePair<Thing, List<ThingDefCountClass>>>();
            var totalNeed = new Dictionary<ThingDef, int>();
            foreach (var t in sites)
            {
                var ic = t as IConstructible;
                if (ic == null) continue;
                var list = new List<ThingDefCountClass>();
                foreach (var m in ic.TotalMaterialCost())
                {
                    int left = ic.ThingCountNeeded(m.thingDef);
                    if (left > 0) list.Add(new ThingDefCountClass(m.thingDef, left));
                }
                needs.Add(new KeyValuePair<Thing, List<ThingDefCountClass>>(t, list));
                foreach (var m in list) { int n; totalNeed.TryGetValue(m.thingDef, out n); totalNeed[m.thingDef] = n + m.count; }
            }
            var loose = VivariumLoose.Get(map);
            foreach (var kv in needs)
            {
                var t = kv.Key;
                bool forb = t.IsForbidden(Faction.OfPlayer);
                bool unreach = !forb && !map.reachability.CanReachColony(t.Position);
                var mats = kv.Value.Select(m =>
                {
                    int f; loose.Forbidden.TryGetValue(m.thingDef, out f);
                    return new MaterialNeed(m.thingDef.label, totalNeed[m.thingDef], VivariumLoose.Usable(map, m.thingDef), f);
                });
                var r = NarratorWork.SiteReason(forb, unreach, noBuilder, mats);
                if (r.IsNone) continue;
                var bp = t as Blueprint_Build;
                var fr = t as Frame;
                var def = (bp != null ? bp.def.entityDefToBuild : fr != null ? fr.def.entityDefToBuild : null);
                if (def == null) continue;
                ThingDef stuff = bp != null ? bp.stuffToUse : fr != null ? fr.Stuff : null;
                items.Add(new StuckItem { Kind = fr != null ? "frame" : "blueprint", Thing = def.label, Stuff = stuff != null ? stuff.label : null, Reason = r, X = t.Position.x, Z = t.Position.z });
            }
        }

        static void Bills(Map map, List<StuckItem> items)
        {
            var memo = new Dictionary<WorkTypeDef, bool>();
            int benches = 0;
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var giver = b as IBillGiver;
                if (giver == null || giver.BillStack == null || giver.BillStack.Count == 0) continue;
                if (++benches > MaxBenches) break;
                var power = b.GetComp<CompPowerTrader>();
                bool noPower = power != null && !power.PowerOn;
                int n = 0;
                foreach (var bill in giver.BillStack.Bills)
                {
                    if (++n > MaxBillsPerBench) break;
                    if (bill.suspended || !bill.ShouldDoNow()) continue;
                    var wt = WorkTypeOf(bill.recipe, b.def);
                    bool noWorker = !SomeoneDoes(map, wt, memo);
                    string missing = noPower || noWorker ? null : FirstMissing(map, bill);
                    var r = NarratorWork.BillReason(noPower, noWorker, wt != null ? (wt.labelShort ?? wt.defName) : null, missing);
                    if (r.IsNone) continue;
                    items.Add(new StuckItem { Kind = "bill", Thing = "bill: " + bill.recipe.label + " at " + b.def.label, Reason = r, X = b.Position.x, Z = b.Position.z });
                }
            }
        }

        static WorkTypeDef WorkTypeOf(RecipeDef recipe, ThingDef bench)
        {
            if (recipe.requiredGiverWorkType != null) return recipe.requiredGiverWorkType;
            foreach (var g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                if (g.fixedBillGiverDefs != null && g.fixedBillGiverDefs.Contains(bench) && g.workType != null) return g.workType;
            return null;
        }

        /// <summary>The first recipe ingredient that no unforbidden thing on the map matches, or null.</summary>
        static string FirstMissing(Map map, Bill bill)
        {
            foreach (var ing in bill.recipe.ingredients)
            {
                bool have = false;
                foreach (var d in ing.filter.AllowedThingDefs)
                {
                    if (!bill.ingredientFilter.Allows(d)) continue;
                    foreach (var t in map.listerThings.ThingsOfDef(d))
                        if (t.Spawned && !t.IsForbidden(Faction.OfPlayer)) { have = true; break; }
                    if (have) break;
                }
                if (!have) return ing.filter.Summary;
            }
            return null;
        }

        // ---- pawns ----

        public static bool IsChild(Pawn p) { return p.DevelopmentalStage != DevelopmentalStage.None && !p.DevelopmentalStage.Adult(); }

        public static List<string> Cannot(Pawn p)
        {
            var c = p.health != null ? p.health.capacities : null;
            if (c == null) return new List<string>();
            return NarratorWork.CannotReasons(c.GetLevel(PawnCapacityDefOf.Manipulation), c.GetLevel(PawnCapacityDefOf.Moving),
                c.GetLevel(PawnCapacityDefOf.Sight), c.GetLevel(PawnCapacityDefOf.Talking));
        }

        public static bool IsIdle(Pawn p)
        {
            if (p.Drafted) return false;
            string job = null;
            try { job = p.CurJob != null ? p.CurJob.def.defName : null; } catch (Exception) { }
            return NarratorWork.IsIdleJob(job);
        }

        /// <summary>"Cooking: Ana can't (...)" for a work type the pawn cannot do.</summary>
        public static string Refusal(Pawn p, WorkTypeDef w, string label)
        {
            var why = new List<string>();
            try { foreach (var s in p.GetReasonsForDisabledWorkType(w)) why.Add(s); } catch (Exception) { }
            return NarratorWork.WorkRefusal(label, p.LabelShort, why);
        }
    }
}
