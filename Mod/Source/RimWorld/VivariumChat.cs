using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The chat bar (Enter opens it) and the scrollback panel above it. Drawn from the map OnGUI postfix.</summary>
    public static class VivariumChat
    {
        const string Control = "RWL_ChatInput";
        const int ShownLines = 8;
        const double FadeSec = 10.0;
        const float BarH = 28f, Pad = 6f;

        static bool _focusPending;
        static string _text = "";
        static int _seen;
        static double _visibleUntil;

        static double Now { get { return Time.realtimeSinceStartupAsDouble; } }

        public static bool IsOpen { get { return Find.WindowStack != null && Find.WindowStack.IsOpen<Window_RWLChat>(); } }

        public static void Reset() { Close(); _text = ""; _seen = 0; _visibleUntil = 0; }

        /// <summary>The game started or loaded: older lines do not pop the panel.</summary>
        public static void OnGameStart() { Reset(); _seen = VivariumGame.State.Chat.LastSeq; }

        public static void Close()
        {
            var ws = Find.WindowStack;
            if (ws != null) ws.TryRemove(typeof(Window_RWLChat), false);
            if (GUI.GetNameOfFocusedControl() == Control) GUI.FocusControl(null);
        }

        static bool CanOpen()
        {
            if (Current.Game == null || Find.CurrentMap == null || WorldRendererUtility.WorldRendered) return false;
            if (!string.IsNullOrEmpty(GUI.GetNameOfFocusedControl())) return false;
            var ws = Find.WindowStack;
            if (ws == null) return false;
            foreach (var w in ws.Windows)
                if (w.absorbInputAroundWindow || w.forcePause || w.preventCameraMotion) return false;
            return !ws.AnySearchWidgetFocused;
        }

        // Called from the map OnGUI postfix on every event type.
        public static void OnGUI()
        {
            var e = Event.current;
            if (e == null || Current.Game == null || Find.TickManager == null) return;
            if (WorldRendererUtility.WorldRendered) { if (IsOpen) Close(); return; }
            var chat = VivariumGame.State.Chat;

            bool open = IsOpen;
            if (e.type == EventType.KeyDown && !open && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && CanOpen())
            {
                _text = "";
                Find.WindowStack.Add(new Window_RWLChat());
                open = true;
                e.Use();
            }

            if (chat.LastSeq != _seen)
            {
                if (chat.LastSeq > _seen) _visibleUntil = Now + FadeSec;
                _seen = chat.LastSeq;
            }
            bool panel = open || Now < _visibleUntil;
            if (!panel) return;

            var r = BarRect();
            if (e.type == EventType.Repaint)
                DrawScrollback(r.x, r.y - 4f, r.width);
        }

        static Rect BarRect()
        {
            float w = Mathf.Min(560f, UI.screenWidth - 460f - 20f);
            if (w < 240f) w = Mathf.Min(UI.screenWidth - 20f, 400f);
            float x = UI.screenWidth - 20f - w > 440f ? 440f : 10f;
            float barY = UI.screenHeight - 35f - 8f - BarH;
            return new Rect(x, barY, w, BarH);
        }

        /// <summary>The input line. A real window, so the camera, the vanilla hotkeys and Esc leave it alone while it is open.</summary>
        public class Window_RWLChat : Window
        {
            public override Vector2 InitialSize { get { var r = BarRect(); return new Vector2(r.width, r.height); } }
            protected override float Margin { get { return 0f; } }

            public Window_RWLChat()
            {
                _focusPending = true;
                layer = WindowLayer.GameUI;
                preventCameraMotion = true;
                absorbInputAroundWindow = false;
                forcePause = false;
                doCloseX = false;
                doCloseButton = false;
                doWindowBackground = false;
                closeOnClickedOutside = true;
                closeOnCancel = true;
                closeOnAccept = false;
                focusWhenOpened = true;
                drawShadow = false;
                draggable = false;
            }

            protected override void SetInitialSizeAndPosition() { windowRect = BarRect(); }

            public override void DoWindowContents(Rect inRect) { DrawBar(inRect); }

            public override void PostClose()
            {
                if (GUI.GetNameOfFocusedControl() == Control) GUI.FocusControl(null);
            }
        }

        static void DrawBar(Rect r)
        {
            var e = Event.current;
            // Our own key handling first, so a vanilla handler never sees the Enter or Esc that belongs to the bar.
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == Control)
                {
                    string t = _text;
                    _text = "";
                    Close();
                    e.Use();
                    Send(t);
                    return;
                }
            }
            Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.7f));
            GUI.color = new Color(0.9f, 0.8f, 0.5f);
            Widgets.DrawBox(r, 1, null);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            GUI.SetNextControlName(Control);
            _text = GUI.TextField(r.ContractedBy(3f), _text ?? "", 500);
            if (_focusPending && e.type == EventType.Repaint) { GUI.FocusControl(Control); _focusPending = false; }
            if (string.IsNullOrEmpty(_text) && e.type == EventType.Repaint)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(r.x + 8f, r.y + 3f, r.width - 12f, r.height), UiUtil.T("RWL_Viv_ChatHint"));
                GUI.color = Color.white;
            }
        }

        /// <summary>A typed line: "@Name text" goes to that colonist, anything else to the narrator.</summary>
        public static void Send(string raw)
        {
            string text = (raw ?? "").Trim();
            if (text.Length == 0) return;
            var chat = VivariumGame.State.Chat;
            int tick = VivariumGame.NowTick;
            string name, rest;
            if (ChatParse.TryAt(text, out name, out rest))
            {
                var pawn = FindColonist(name);
                if (pawn == null) { chat.Add("note", UiUtil.T("RWL_Viv_NoColonist", name), tick); return; }
                if (rest.Length == 0) return;
                RWLBridge.Talk.Add(pawn.thingIDNumber, rest, "chat");
                chat.Add("player", rest, tick, pawn.thingIDNumber, pawn.LabelShort);
                return;
            }
            chat.Add("player", text, tick);
        }

        static Pawn FindColonist(string name)
        {
            var map = Find.CurrentMap;
            if (map == null) return null;
            var all = map.mapPawns.FreeColonistsSpawned.ToList();
            var names = all.Select(p => p.LabelShort).ToList();
            int i = ChatParse.Match(name, names);
            return i >= 0 ? all[i] : null;
        }

        // ------------------------------------------------------------------ scrollback
        static Color ColorOf(string who)
        {
            switch (who)
            {
                case "player": return new Color(0.75f, 0.9f, 1f);
                case "narrator": return new Color(1f, 0.85f, 0.5f);
                case "colonist": return new Color(0.6f, 0.95f, 0.6f);
                default: return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        static string Line(ChatEntry c)
        {
            switch (c.Who)
            {
                case "player": return c.To > 0 ? UiUtil.T("RWL_Viv_LinePlayerTo", c.Name, c.Text) : UiUtil.T("RWL_Viv_LinePlayer", c.Text);
                case "narrator": return UiUtil.T("RWL_Viv_LineNarrator", c.Text);
                case "colonist": return (string.IsNullOrEmpty(c.Name) ? "?" : c.Name) + ": " + c.Text;
                default: return c.Text;
            }
        }

        static void DrawScrollback(float x, float bottom, float w)
        {
            var entries = VivariumGame.State.Chat.Entries;
            if (entries.Count == 0) return;
            int from = Math.Max(0, entries.Count - ShownLines);
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            var lines = new List<string>(); var cols = new List<Color>(); var hs = new List<float>();
            float inner = w - Pad * 2f, total = Pad * 2f;
            for (int i = from; i < entries.Count; i++)
            {
                string s = Line(entries[i]);
                float h = Text.CalcHeight(s, inner);
                lines.Add(s); cols.Add(ColorOf(entries[i].Who)); hs.Add(h); total += h;
            }
            float top = Mathf.Max(10f, bottom - total);
            var panel = new Rect(x, top, w, bottom - top);
            Widgets.DrawBoxSolid(panel, new Color(0f, 0f, 0f, 0.45f));
            float y = panel.y + Pad;
            for (int i = 0; i < lines.Count && y < bottom; i++)
            {
                GUI.color = cols[i];
                Widgets.Label(new Rect(panel.x + Pad, y, inner, hs[i]), lines[i]);
                y += hs[i];
            }
            GUI.color = Color.white;
        }
    }

    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs))]
    public static class Patch_VivariumChatGui
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try { VivariumChat.OnGUI(); }
            catch (Exception e) { Log.ErrorOnce("[RimWorldLLM] chat bar failed: " + e.Message, 7340211); }
        }
    }
}
