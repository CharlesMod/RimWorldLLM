using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>Scrollable log of every spoken line, newest first, filterable by pawn; click a name to jump to the pawn.</summary>
    public class MainTabWindow_HermesLog : MainTabWindow
    {
        Vector2 scroll;
        int filterPawn;               // 0 = everyone
        string filterName = "";
        int tab;                      // 0 = conversation log, 1 = chronicle
        Vector2 chronScroll;

        public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(UI.screenWidth - 40f, 820f), Mathf.Min(UI.screenHeight - 200f, 640f)); } }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), UiUtil.T(tab == 0 ? "HV_LogTitle" : "HV_ChronTitle"));
            Text.Font = GameFont.Small;
            if (DirectorCamera.Allowed)
            {
                var camBtn = new Rect(inRect.xMax - 210f, inRect.y + 2f, 210f, 30f);
                if (Widgets.ButtonText(camBtn, UiUtil.T(DirectorCamera.Enabled ? "HV_Camera_ButtonOn" : "HV_Camera_ButtonOff"))) DirectorCamera.Toggle();
                TooltipHandler.TipRegion(camBtn, UiUtil.T("HV_Camera_Tip"));
            }

            var body = new Rect(inRect.x, inRect.y + 74f, inRect.width, inRect.height - 74f);
            var tabs = new List<TabRecord>
            {
                new TabRecord(UiUtil.T("HV_TabConversation"), () => tab = 0, tab == 0),
                new TabRecord(UiUtil.T("HV_TabChronicle"), () => tab = 1, tab == 1)
            };
            TabDrawer.DrawTabs(body, tabs);
            if (tab == 0) DrawLog(body); else DrawChronicle(body);
        }

        void DrawChronicle(Rect inRect)
        {
            var entries = HermesBridge.Chronicle.History();
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(inRect.x, inRect.y + 6f, inRect.width, 28f), UiUtil.T("HV_ChronCount", entries.Count, Bridge.ChronicleBook.MaxEntries));
            Text.Anchor = TextAnchor.UpperLeft;
            var outRect = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 40f);
            if (entries.Count == 0) { Widgets.Label(outRect, UiUtil.T("HV_ChronEmpty")); return; }

            float w = outRect.width - 18f;
            var heights = new float[entries.Count];
            float total = 0f;
            for (int i = 0; i < entries.Count; i++)           // i = 0 is the newest
            {
                heights[i] = Text.LineHeight + Text.CalcHeight(entries[entries.Count - 1 - i].Text, w - 8f) + 12f;
                total += heights[i];
            }
            Widgets.BeginScrollView(outRect, ref chronScroll, new Rect(0f, 0f, w, total));
            float cy = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                if (cy + heights[i] >= chronScroll.y && cy <= chronScroll.y + outRect.height)
                {
                    var e = entries[entries.Count - 1 - i];
                    var r = new Rect(0f, cy, w, heights[i]);
                    bool major = e.Kind == Bridge.ChronicleBook.KindMajor;
                    if (major) { GUI.color = new Color(0.9f, 0.55f, 0.35f, 0.25f); GUI.DrawTexture(r, BaseContent.WhiteTex); GUI.color = Color.white; }
                    else if (i % 2 == 0) Widgets.DrawLightHighlight(r);
                    string head = UiUtil.DateOfDay(e.Day) + (major ? "  |  " + UiUtil.T("HV_ChronMajor") : "");
                    GUI.color = major ? new Color(1f, 0.8f, 0.55f) : Color.gray;
                    Widgets.Label(new Rect(r.x + 4f, r.y + 2f, r.width - 8f, Text.LineHeight), head);
                    GUI.color = Color.white;
                    Widgets.Label(new Rect(r.x + 4f, r.y + 2f + Text.LineHeight, r.width - 8f, r.height - Text.LineHeight - 4f), e.Text);
                }
                cy += heights[i];
            }
            Widgets.EndScrollView();
        }

        void DrawLog(Rect inRect)
        {
            var comp = HermesVoiceGameComponent.Instance;
            var entries = comp != null ? comp.ConversationLog.Entries : new List<ConversationEntry>();

            float y = inRect.y + 6f;
            Widgets.Label(new Rect(inRect.x, y + 3f, 60f, 26f), UiUtil.T("HV_LogFilterLabel"));
            string current = filterPawn == 0 ? UiUtil.T("HV_LogFilterAll") : filterName;
            if (Widgets.ButtonText(new Rect(inRect.x + 64f, y, 220f, 28f), current))
            {
                var opts = new List<FloatMenuOption> { new FloatMenuOption(UiUtil.T("HV_LogFilterAll"), () => { filterPawn = 0; filterName = ""; scroll = Vector2.zero; }) };
                foreach (var kv in Participants(entries))
                {
                    int id = kv.Key; string nm = kv.Value;
                    opts.Add(new FloatMenuOption(nm, () => { filterPawn = id; filterName = nm; scroll = Vector2.zero; }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            var shown = entries.Where(e => filterPawn == 0 || e.PawnId == filterPawn || e.ListenerId == filterPawn).Reverse().ToList();
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(inRect.x + 290f, y, inRect.width - 290f, 28f), UiUtil.T("HV_LogCount", shown.Count, entries.Count, ConversationLog.MaxEntries));
            Text.Anchor = TextAnchor.UpperLeft;

            var outRect = new Rect(inRect.x, y + 34f, inRect.width, inRect.height - 40f);
            if (shown.Count == 0)
            {
                Widgets.Label(outRect, UiUtil.T("HV_LogEmpty"));
                return;
            }

            float w = outRect.width - 18f;
            var heights = new float[shown.Count];
            float total = 0f;
            for (int i = 0; i < shown.Count; i++)
            {
                heights[i] = Text.LineHeight * 2f + Text.CalcHeight(shown[i].Text, w - 8f) + 12f;
                total += heights[i];
            }
            var view = new Rect(0f, 0f, w, total);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float cy = 0f;
            for (int i = 0; i < shown.Count; i++)
            {
                // Skip rows that are scrolled out of sight.
                if (cy + heights[i] >= scroll.y && cy <= scroll.y + outRect.height)
                    DrawRow(new Rect(0f, cy, w, heights[i]), shown[i], i);
                cy += heights[i];
            }
            Widgets.EndScrollView();
        }

        static IEnumerable<KeyValuePair<int, string>> Participants(List<ConversationEntry> entries)
        {
            var d = new Dictionary<int, string>();
            foreach (var e in entries)
            {
                d[e.PawnId] = e.PawnName;
                if (e.ListenerId != 0) d[e.ListenerId] = e.ListenerName;
            }
            return d.OrderBy(kv => kv.Value);
        }

        static string RefusedText(string code)
        {
            string key = "HV_Refused_" + code;
            return key.CanTranslate() ? UiUtil.T(key) : code;
        }

        void DrawRow(Rect r, ConversationEntry e, int index)
        {
            if (index % 2 == 0) Widgets.DrawLightHighlight(r);
            float x = r.x + 4f, y = r.y + 2f;
            float lh = Text.LineHeight;

            // Line 1: date, speaker, listener (names are links).
            string date = UiUtil.DateOf(e.Tick);
            Widgets.Label(new Rect(x, y, 230f, lh), date);
            float nx = x + 236f;
            nx = NameLink(nx, y, e.PawnId, e.PawnName);
            if (e.ListenerId != 0)
            {
                Widgets.Label(new Rect(nx, y, 24f, lh), "→");
                nx += 24f;
                nx = NameLink(nx, y, e.ListenerId, e.ListenerName);
            }

            // Line 2: trigger, intent, effect.
            string trig = UiUtil.T(e.Trigger == "player" ? "HV_LogTriggerPlayer" : "HV_LogTriggerAgent");
            string eff;
            Color col;
            if (!string.IsNullOrEmpty(e.Effect)) { eff = UiUtil.T("HV_LogEffectApplied", e.Effect); col = new Color(0.55f, 0.85f, 0.55f); }
            else if (!string.IsNullOrEmpty(e.Refused)) { eff = UiUtil.T("HV_LogEffectRefused", RefusedText(e.Refused)); col = new Color(0.95f, 0.75f, 0.4f); }
            else { eff = UiUtil.T("HV_LogNoEffect"); col = Color.gray; }
            GUI.color = Color.gray;
            Widgets.Label(new Rect(x, y + lh, 230f, lh), trig + (string.IsNullOrEmpty(e.Intent) || e.Intent == "none" ? "" : "  |  " + UiUtil.T("HV_LogIntent", e.Intent)));
            GUI.color = col;
            Widgets.Label(new Rect(x + 236f, y + lh, r.width - 244f, lh), eff);
            GUI.color = Color.white;

            // Text.
            Widgets.Label(new Rect(x, y + lh * 2f, r.width - 8f, r.height - lh * 2f - 4f), e.Text);
        }

        float NameLink(float x, float y, int id, string name)
        {
            float w = Text.CalcSize(name).x + 4f;
            var r = new Rect(x, y, w, Text.LineHeight);
            Widgets.DrawHighlightIfMouseover(r);
            UiUtil.Tip(r, UiUtil.T("HV_LogJumpTip", name));
            if (Widgets.ButtonInvisible(r)) UiUtil.Jump(id, name);
            GUI.color = new Color(0.75f, 0.9f, 1f);
            Widgets.Label(r, name);
            GUI.color = Color.white;
            return x + w + 4f;
        }
    }
}
