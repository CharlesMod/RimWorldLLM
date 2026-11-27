using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;
using Verse.AI.Group;

namespace RimWorldLLM
{
    /// <summary>Keeps the narrator's caravan plans true to the game: re-keys a plan from the forming lord to the formed caravan (matched by
    /// pawns), gives a caravan heading to a site its arrival action, notes arrivals once, prunes plans whose caravan is gone.
    /// Called from the game component every 120 ticks. Game thread only.</summary>
    public static class VivariumCaravanWatch
    {
        const int EveryTicks = 120;
        static readonly Dictionary<string, List<int>> forming = new Dictionary<string, List<int>>();
        static readonly HashSet<string> noted = new HashSet<string>();
        static bool warned;

        public static void Remember(string key, IEnumerable<int> pawnIds) { forming[key] = pawnIds.ToList(); }
        public static void Forget(string key) { forming.Remove(key); }

        public static void Tick()
        {
            if (Find.TickManager == null || Find.TickManager.TicksGame % EveryTicks != 0) return;
            if (!EventGate.GameLive()) return;
            try { Run(); }
            catch (Exception e) { if (!warned) { warned = true; Log.Warning("[RimWorldLLM] caravan watch failed: " + e.Message); } }
        }

        static void Run()
        {
            var v = VivariumGame.State;
            if (v == null) return;
            var plans = v.CaravanPlans;
            foreach (var e in plans.Items.ToList())
            {
                var lord = TradeUtil.FormingLordByKey(e.Key);
                List<int> ids;
                if (lord != null)
                {
                    forming[e.Key] = lord.ownedPawns.Select(p => p.thingIDNumber).ToList();   // pawns may join or leave while it forms
                    continue;
                }
                if (forming.TryGetValue(e.Key, out ids))
                {
                    var c = Find.WorldObjects.Caravans.FirstOrDefault(x => x.IsPlayerControlled && x.PawnsListForReading.Any(p => ids.Contains(p.thingIDNumber)));
                    if (c != null)
                    {
                        string nk = TradeUtil.CaravanKey(c);
                        plans.Remove(e.Key);
                        plans.Set(nk, e.Purpose, e.To, e.Quest, e.Formed, e.Note);
                        forming.Remove(e.Key);
                        continue;
                    }
                    plans.Remove(e.Key); forming.Remove(e.Key);   // cancelled, or the pawns are gone
                    continue;
                }
                if (TradeUtil.CaravanByKey(e.Key) == null) plans.Remove(e.Key);
            }
            foreach (var e in plans.Items.ToList())
            {
                var c = TradeUtil.CaravanByKey(e.Key);
                if (c == null) continue;
                EnsureArrivalAction(c, e);
                NoteArrival(c, e);
            }
        }

        static bool Where(string to, out PlanetTile tile, out Site site, out Settlement st)
        {
            tile = PlanetTile.Invalid; site = null; st = null;
            char k; int n;
            if (!TradeKeys.TryParse(to, out k, out n)) return false;
            if (k == 'T') { st = Find.WorldObjects.SettlementAt(n); if (st == null) return false; tile = st.Tile; return true; }
            if (k == 'W') { site = Find.WorldObjects.SiteAt(n); if (site == null) return false; tile = site.Tile; return true; }
            if (k == 'Q')
            {
                var q = QuestFacts.ById(n);
                if (q == null) return false;
                var f = QuestFacts.Of(q);
                site = f.Site; st = f.Settlement; tile = f.Tile;
                return f.Tile.Valid;
            }
            return false;
        }

        static void EnsureArrivalAction(Caravan c, CaravanPlans.Entry e)
        {
            PlanetTile tile; Site site; Settlement st;
            if (!Where(e.To, out tile, out site, out st) || site == null) return;
            var p = c.pather;
            if (!p.Moving || p.ArrivalAction != null || p.Destination != tile) return;
            if (!CaravanArrivalAction_VisitSite.CanVisit(c, site).Accepted) return;
            p.StartPath(tile, new CaravanArrivalAction_VisitSite(site), true);
        }

        static void NoteArrival(Caravan c, CaravanPlans.Entry e)
        {
            PlanetTile tile; Site site; Settlement st;
            if (!Where(e.To, out tile, out site, out st) || st == null) return;
            if (c.pather.Moving || c.Tile != tile) return;
            string id = c.ID + ":" + (int)tile;
            if (!noted.Add(id)) return;
            bool can = CaravanArrivalAction_Trade.CanTradeWith(c, st).Accepted;
            string text = c.LabelCap + " reached " + st.LabelCap + (can ? ": it can trade there" : ": it cannot trade there now");
            VivariumGame.State.Chat.Add("note", text, VivariumGame.NowTick);
        }
    }
}
