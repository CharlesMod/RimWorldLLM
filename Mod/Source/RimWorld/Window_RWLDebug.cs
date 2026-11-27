using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Developer window: recent bridge requests, the event ring tail, op-id store size, the talk queue, and a box that queues a talk request.</summary>
    public class Window_RWLDebug : Window
    {
        Vector2 scroll;
        float viewHeight = 800f;
        int testPawn;
        string testPawnName = "";
        string testText = "";
        string sent = "";

        public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(UI.screenWidth - 40f, 760f), Mathf.Min(UI.screenHeight - 100f, 680f)); } }

        public Window_RWLDebug()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            optionalTitle = UiUtil.T("RWL_Debug_Title");
        }

        static void Head(Listing_Standard l, string text)
        {
            l.Gap(6f);
            Text.Font = GameFont.Medium;
            l.Label(text);
            Text.Font = GameFont.Small;
        }

        static void Dim(Listing_Standard l, string text)
        {
            GUI.color = Color.gray;
            l.Label(text);
            GUI.color = Color.white;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!Prefs.DevMode) { Close(); return; }
            var view = new Rect(0f, 0f, inRect.width - 18f, viewHeight);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            l.Begin(view);

            var rt = RWLBridge.Runtime;
            l.Label(UiUtil.T("RWL_Debug_State", rt.IsRunning ? UiUtil.T("RWL_Debug_Running", rt.Port) : UiUtil.T("RWL_Debug_Stopped"),
                rt.Server != null ? rt.Server.PendingCount : 0));
            l.Label(UiUtil.T("RWL_Debug_Stores", RWLBridge.OpIds.Count, RWLBridge.Events.Count, RWLBridge.Events.LastSeq, RWLBridge.Talk.Count));

            // Test line.
            Head(l, UiUtil.T("RWL_Debug_TestLine"));
            Dim(l, UiUtil.T("RWL_Debug_TestHelp"));
            var row = l.GetRect(28f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, 220f, 28f), testPawn == 0 ? UiUtil.T("RWL_Debug_PickPawn") : testPawnName))
            {
                var opts = new List<FloatMenuOption>();
                foreach (var p in Colonists())
                {
                    var pp = p;
                    opts.Add(new FloatMenuOption(pp.LabelShortCap, () => { testPawn = pp.thingIDNumber; testPawnName = pp.LabelShortCap; }));
                }
                if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
            }
            testText = Widgets.TextField(new Rect(row.x + 228f, row.y, row.width - 228f - 110f, 28f), testText ?? "");
            bool ready = testPawn != 0 && !string.IsNullOrWhiteSpace(testText) && RimWorldLLMGameComponent.Instance != null;
            if (Widgets.ButtonText(new Rect(row.xMax - 104f, row.y, 104f, 28f), UiUtil.T("RWL_Debug_Send"), true, true, ready))
            {
                string id = RWLBridge.Talk.Add(testPawn, testText.Trim(), "debug");
                sent = UiUtil.T("RWL_Debug_Queued", id, testPawnName);
            }
            if (sent.Length > 0) Dim(l, sent);

            // Talk queue.
            Head(l, UiUtil.T("RWL_Debug_TalkQueue"));
            var q = RWLBridge.Talk.ToJson();
            if (q.Count == 0) Dim(l, UiUtil.T("RWL_Debug_Empty"));
            foreach (var t in q.Reverse().Take(30))
                l.Label(UiUtil.T("RWL_Debug_TalkRow", (string)t["id"], (string)t["pawn"], (string)t["from"], (string)t["text"]));

            // Requests.
            Head(l, UiUtil.T("RWL_Debug_Requests"));
            var reqs = rt.Requests.Recent(40);
            if (reqs.Count == 0) Dim(l, UiUtil.T("RWL_Debug_Empty"));
            foreach (var r in reqs)
            {
                GUI.color = r.Status >= 500 ? new Color(0.9f, 0.4f, 0.4f) : (r.Status >= 400 ? new Color(0.95f, 0.75f, 0.35f) : Color.white);
                l.Label(UiUtil.T("RWL_Debug_ReqRow", r.Method, r.Path, r.Status, r.Ms.ToString("0.0")));
                GUI.color = Color.white;
            }

            // Events.
            Head(l, UiUtil.T("RWL_Debug_Events"));
            var ev = RWLBridge.Events.Tail(30);
            if (ev.Count == 0) Dim(l, UiUtil.T("RWL_Debug_Empty"));
            foreach (var e in ev)
                l.Label(UiUtil.T("RWL_Debug_EventRow", (string)e["seq"], (string)e["type"], (string)e["tick"], (string)e["summary"]));

            viewHeight = l.CurHeight + 12f;
            l.End();
            Widgets.EndScrollView();
        }

        static IEnumerable<Pawn> Colonists()
        {
            var map = Find.CurrentMap;
            if (map == null) return Enumerable.Empty<Pawn>();
            return map.mapPawns.FreeColonistsSpawned.Where(p => p.RaceProps.Humanlike);
        }
    }
}
