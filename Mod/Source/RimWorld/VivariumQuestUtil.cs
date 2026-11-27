using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>What a quest is and where it points, read from its def, parts and look targets. Game thread only.</summary>
    public sealed class QuestFacts
    {
        public Quest Quest;
        public string Kind = "other";
        public Site Site;
        public Settlement Settlement;       // a trade-request delivery goes here
        public TradeRequestComp Request;
        public PlanetTile Tile = PlanetTile.Invalid;

        public static QuestFacts Of(Quest q)
        {
            var f = new QuestFacts { Quest = q };
            try
            {
                foreach (var t in q.QuestLookTargets)
                {
                    if (!t.HasWorldObject) continue;
                    var wo = t.WorldObject;
                    var site = wo as Site;
                    if (site != null && f.Site == null) f.Site = site;
                    var st = wo as Settlement;
                    if (st != null)
                    {
                        TradeRequestComp rc;
                        if (wo.TryGetComponent(out rc) && rc.ActiveRequest && f.Request == null) { f.Request = rc; f.Settlement = st; }
                    }
                }
            }
            catch (Exception) { }
            var names = new List<string>();
            try { foreach (var p in q.PartsListForReading) names.Add(p.GetType().Name); } catch (Exception) { }
            f.Kind = QuestPure.Kind(q.root != null ? q.root.defName : "", names, f.Site != null, f.Request != null);
            if (f.Kind == "delivery" && f.Settlement != null) f.Tile = f.Settlement.Tile;
            else if (f.Site != null) f.Tile = f.Site.Tile;
            return f;
        }

        public static Quest ById(int id)
        {
            foreach (var q in Find.QuestManager.QuestsListForReading) if (q.id == id) return q;
            return null;
        }

        /// <summary>Quests the narrator may see: ongoing, or offered and not hidden.</summary>
        public static IEnumerable<Quest> Visible()
        {
            foreach (var q in Find.QuestManager.QuestsListForReading)
            {
                if (q.hidden || q.hiddenInUI || q.dismissed) continue;
                if (q.State == QuestState.Ongoing || q.State == QuestState.NotYetAccepted) yield return q;
            }
        }
    }
}
