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
    /// <summary>
    /// The chat: an always-on translucent panel at the side (MOBA style) with the log and the live "is thinking / is typing"
    /// lines, and the input bar (Enter opens it) at the panel's bottom edge. Drawn from the map OnGUI postfix.
    /// </summary>
    public static class VivariumChat
    {
        const string Control = "RWL_ChatInput";
        const int MaxFresh = 64;

        static bool _focusPending;
        static string _text = "";
        static int _seen;
        static float _scroll;
        static readonly Dictionary<int, double> _arrived = new Dictionary<int, double>();   // seq -> real time first seen

        static double Now { get { return Time.realtimeSinceStartupAsDouble; } }

        /// <summary>
        /// The in-process streaming API for a C# mind: Begin / Progress / Typing / Partial / End / Cancel (thread-safe).
        /// Same object as RWLBridge.Runtime.Vivarium.Live; see docs/VIVARIUM.md.
        /// </summary>
        public static ChatLive Live { get { return VivariumGame.State.Live; } }

        public static bool IsOpen { get { return Find.WindowStack != null && Find.WindowStack.IsOpen<Window_RWLChat>(); } }

        public static void Reset() { Close(); _text = ""; _seen = 0; _scroll = 0f; _arrived.Clear(); }

        /// <summary>The game started or loaded: older lines are not highlighted as new.</summary>
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

        static RimWorldLLMSettings S { get { return RimWorldLLMMod.Settings ?? new RimWorldLLMSettings(); } }
        static GameFont Font { get { return S.chatPanelMedium ? GameFont.Medium : GameFont.Small; } }

        static ChatLayout.Result Layout()
        {
            var s = S;
            var old = Text.Font;
            Text.Font = Font;
            float lh = Text.LineHeight;
            Text.Font = old;
            return ChatLayout.Compute(new ChatLayout.Input
            {
                ScreenW = UI.screenWidth, ScreenH = UI.screenHeight, Width = s.chatPanelWidth,
                Lines = s.chatPanelLines, LineH = lh, Right = s.chatPanelRight
            });
        }

        static Rect R(ChatRect c) { return new Rect(c.X, c.Y, c.W, c.H); }

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
                if (chat.LastSeq > _seen)
                    foreach (var c in chat.Entries) if (c.Seq > _seen && !_arrived.ContainsKey(c.Seq)) _arrived[c.Seq] = Now;
                _seen = chat.LastSeq;
                if (_arrived.Count > MaxFresh) _arrived.Clear();
            }

            // shown while typing; otherwise when on and no main tab window (architect, work, ...) is open over that corner
            bool panel = open || (S.chatPanelShow && (Find.MainTabsRoot == null || Find.MainTabsRoot.OpenTab == null));
            if (!panel) return;

            var lay = Layout();
            bool hover = R(lay.Whole).Contains(e.mousePosition);
            if (!hover) _scroll = 0f;   // back to the newest line when the mouse leaves
            if (hover && e.type == EventType.ScrollWheel)
            {
                Text.Font = Font;
                _scroll -= e.delta.y * Text.LineHeight;
                Text.Font = GameFont.Small;
                e.Use();   // the wheel reads history instead of zooming; clicks are never taken
            }
            if (e.type == EventType.Repaint) DrawPanel(lay, hover);
        }

        /// <summary>The input line. A real window, so the camera, the vanilla hotkeys and Esc leave it alone while it is open.</summary>
        public class Window_RWLChat : Window
        {
            public override Vector2 InitialSize { get { var r = Layout().Bar; return new Vector2(r.W, r.H); } }
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

            protected override void SetInitialSizeAndPosition() { windowRect = R(Layout().Bar); }

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
            int undo;
            if (ChatParse.TryUndo(text, out undo)) { UndoNarrator(undo); return; }
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

        /// <summary>The player's undo (chat "/undo [n]" and the settings button): takes back the narrator's last n placing
        /// actions (blueprints, designations, zones, bills) and says what happened in the chat. Game thread.</summary>
        public static void UndoNarrator(int n)
        {
            if (Current.Game == null) return;
            var v = VivariumGame.State;
            int tick = VivariumGame.NowTick;
            var r = VivariumRoutes.UndoLast(v, new VivariumHands(), n, tick, "player");
            v.Chat.Add("note", r.Ok ? r.Note : UiUtil.T("RWL_Viv_UndoNone", r.Reason), tick);
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

        // ------------------------------------------------------------------ the panel
        static Color ColorOf(string who, int to)
        {
            switch (who)
            {
                case "player": return to > 0 ? new Color(0.6f, 0.95f, 0.9f) : new Color(0.75f, 0.88f, 1f);
                case "narrator": return new Color(1f, 0.85f, 0.5f);
                case "colonist": return new Color(0.65f, 0.95f, 0.6f);
                default: return new Color(0.72f, 0.72f, 0.72f);   // notes ("→ ...")
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

        static string SpeakerName(LiveLine l)
        {
            if (l.Who == "narrator") return UiUtil.T("RWL_Viv_NarratorName");
            if (!string.IsNullOrEmpty(l.Name)) return l.Name;
            var map = Find.CurrentMap;
            if (map != null)
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                    if (p.thingIDNumber == l.To) return p.LabelShort;
            return "?";
        }

        sealed class Row { public string Text; public Color Col; public float Fresh; public float Progress = -1f; public float H; }

        static Row LiveRow(LiveLine l, double now)
        {
            string name = SpeakerName(l);
            string dots = new string('.', ChatLayout.Dots(now));
            var col = ColorOf(l.Who == "narrator" ? "narrator" : "colonist", l.To);
            if (!l.Typing || string.IsNullOrEmpty(l.Text))
            {
                string t = UiUtil.T(l.Typing ? "RWL_Viv_Typing" : "RWL_Viv_Thinking", name) + dots.PadRight(3);
                string pct = l.Typing ? "" : ChatLayout.ProgressText(l.Progress);
                if (pct.Length > 0) t += " " + pct;
                return new Row { Text = t, Col = new Color(col.r, col.g, col.b, 0.75f), Progress = l.Typing ? -1f : l.Progress };
            }
            string line = l.Who == "narrator" ? UiUtil.T("RWL_Viv_LineNarrator", l.Text) : name + ": " + l.Text;
            return new Row { Text = line + " " + dots, Col = col };
        }

        static void DrawPanel(ChatLayout.Result lay, bool hover)
        {
            var s = S;
            double now = Now;
            var entries = VivariumGame.State.Chat.Entries;
            var live = Live.Snapshot();
            var box = R(lay.Box);
            float inner = box.width - ChatLayout.Pad * 2f;
            float innerH = box.height - ChatLayout.Pad * 2f;

            Text.Font = Font;
            Text.WordWrap = true;
            var rows = new List<Row>();
            float freshest = 0f, need = innerH + Mathf.Max(0f, _scroll);
            // newest first until the box (plus the scroll) is filled, then flip
            for (int i = live.Count - 1; i >= 0; i--) rows.Add(LiveRow(live[i], now));
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                double at;
                float fresh = _arrived.TryGetValue(entries[i].Seq, out at) ? ChatLayout.Freshness(now - at) : 0f;
                rows.Add(new Row { Text = Line(entries[i]), Col = ColorOf(entries[i].Who, entries[i].To), Fresh = fresh });
                if (rows.Count > 200) break;
            }
            float sum = 0f;
            int keep = 0;
            foreach (var r in rows)
            {
                r.H = Text.CalcHeight(r.Text, inner);
                freshest = Mathf.Max(freshest, r.Fresh);
                sum += r.H; keep++;
                if (sum > need + 400f) break;   // enough history for this scroll; the wheel asks for more next frame
            }
            rows.RemoveRange(keep, rows.Count - keep);
            rows.Reverse();

            float bg = ChatLayout.BackgroundAlpha(s.chatPanelOpacity, hover, freshest);
            if (bg > 0.001f) Widgets.DrawBoxSolid(box, new Color(0f, 0f, 0f, bg));
            if (hover) { GUI.color = new Color(1f, 1f, 1f, 0.15f); Widgets.DrawBox(box, 1, null); GUI.color = Color.white; }
            if (rows.Count == 0) { Text.Font = GameFont.Small; return; }

            var hs = new List<float>(rows.Count);
            foreach (var r in rows) hs.Add(r.H);
            var vis = ChatLayout.Visible(hs, innerH, ref _scroll);
            Widgets.BeginGroup(new Rect(box.x + ChatLayout.Pad, box.y + ChatLayout.Pad, inner, innerH));
            foreach (var kv in vis)
            {
                var r = rows[kv.Key];
                var lr = new Rect(0f, kv.Value, inner, r.H);
                if (r.Fresh > 0f) Widgets.DrawBoxSolid(lr, new Color(1f, 0.9f, 0.6f, 0.12f * r.Fresh));
                ShadowLabel(lr, r.Text, r.Col);
                if (r.Progress >= 0f)
                {
                    var track = new Rect(0f, lr.yMax - 2f, inner * 0.5f, 2f);
                    Widgets.DrawBoxSolid(track, new Color(1f, 1f, 1f, 0.12f));
                    Widgets.DrawBoxSolid(new Rect(track.x, track.y, track.width * r.Progress, 2f), new Color(r.Col.r, r.Col.g, r.Col.b, 0.8f));
                }
            }
            Widgets.EndGroup();
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        /// <summary>Text with a soft dark outline so it reads over bright and dark ground alike.</summary>
        static void ShadowLabel(Rect r, string text, Color col)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.55f * col.a);
            Widgets.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text);
            Widgets.Label(new Rect(r.x - 1f, r.y + 1f, r.width, r.height), text);
            GUI.color = new Color(0f, 0f, 0f, 0.35f * col.a);
            Widgets.Label(new Rect(r.x + 1f, r.y - 1f, r.width, r.height), text);
            GUI.color = col;
            Widgets.Label(r, text);
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
