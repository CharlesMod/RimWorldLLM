using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorldLLM.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Builds ShareDocs from what the player already sees and opens the share window. Game thread only.</summary>
    public static class ShareService
    {
        public const int ExchangeGapTicks = 2500;     // about one in-game hour between lines of one exchange
        public const int ThoughtSlackTicks = 600;     // inner thoughts this close to the exchange are included

        public static bool FooterOn { get { var s = RimWorldLLMMod.Settings; return s == null || s.shareFooter; } }

        public static string ColonyName()
        {
            try { return Faction.OfPlayer != null ? Faction.OfPlayer.Name ?? "" : ""; } catch (Exception) { return ""; }
        }

        static ShareLine Line(ConversationEntry e)
        {
            return new ShareLine { PawnId = e.PawnId, Speaker = e.PawnName, Text = e.Text };
        }

        public static void ShareLineOf(ConversationEntry e)
        {
            var d = new ShareDoc { Colony = ColonyName(), Date = UiUtil.DateOf(e.Tick) };
            d.Lines.Add(Line(e));
            Open(d);
        }

        /// <summary>The whole exchange around entry e (same two pawns, close in time), with recorded inner thoughts in time order.</summary>
        public static void ShareExchangeOf(List<ConversationEntry> all, ConversationEntry e)
        {
            int idx = all.IndexOf(e);
            if (idx < 0) { ShareLineOf(e); return; }
            int from, to;
            ShareFormat.ExchangeRange(all.Select(x => x.PawnId).ToArray(), all.Select(x => x.ListenerId).ToArray(),
                all.Select(x => x.Tick).ToArray(), idx, ExchangeGapTicks, out from, out to);
            var items = new List<KeyValuePair<int, ShareLine>>();
            var ids = new HashSet<int>();
            for (int i = from; i <= to; i++)
            {
                items.Add(new KeyValuePair<int, ShareLine>(all[i].Tick, Line(all[i])));
                ids.Add(all[i].PawnId);
                if (all[i].ListenerId != 0) ids.Add(all[i].ListenerId);
            }
            int t0 = all[from].Tick - ThoughtSlackTicks, t1 = all[to].Tick + ThoughtSlackTicks;
            var names = new Dictionary<int, string>();
            for (int i = from; i <= to; i++)
            {
                names[all[i].PawnId] = all[i].PawnName;
                if (all[i].ListenerId != 0) names[all[i].ListenerId] = all[i].ListenerName;
            }
            MindRecord rec;
            foreach (int id in ids)
            {
                if (!RWLBridge.Minds.Records.TryGetValue(id, out rec)) continue;
                string nm; names.TryGetValue(id, out nm);
                foreach (var th in rec.Thoughts)
                    if (th.Key >= t0 && th.Key <= t1)
                        items.Add(new KeyValuePair<int, ShareLine>(th.Key, new ShareLine { PawnId = id, Speaker = nm ?? "", Text = th.Value, Thought = true }));
            }
            var d = new ShareDoc { Colony = ColonyName(), Date = UiUtil.DateOf(all[from].Tick) };
            // Stable sort: ties keep the spoken line before the thought.
            foreach (var kv in items.Select((x, n) => new { x, n }).OrderBy(a => a.x.Key).ThenBy(a => a.n)) d.Lines.Add(kv.x.Value);
            Open(d);
        }

        public static void ShareDiary(Pawn pawn, KeyValuePair<int, string> page)
        {
            var d = new ShareDoc { Colony = ColonyName(), Date = UiUtil.DateOf(page.Key), Title = UiUtil.T("RWL_Share_DiaryOf", pawn.LabelShortCap) };
            d.Lines.Add(new ShareLine { PawnId = pawn.thingIDNumber, Speaker = pawn.LabelShortCap, Text = page.Value });
            Open(d);
        }

        public static void ShareChronicle(ChronicleEntry e)
        {
            var d = new ShareDoc { Colony = ColonyName(), Date = UiUtil.DateOfDay(e.Day), Title = UiUtil.T("RWL_Share_Chronicle") };
            d.Lines.Add(new ShareLine { Text = e.Text });
            Open(d);
        }

        static void Open(ShareDoc d) { Find.WindowStack.Add(new Window_RWLShare(d, FooterOn)); }

        public static string SharesFolder()
        {
            return Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLLM", "Shares");
        }

        public static void OpenFolder(string dir)
        {
            try { Application.OpenURL(new Uri(Path.GetFullPath(dir) + Path.DirectorySeparatorChar).AbsoluteUri); }
            catch (Exception ex) { Log.Warning("[RimWorldLLM] could not open " + dir + ": " + ex.Message); }
        }
    }
}
