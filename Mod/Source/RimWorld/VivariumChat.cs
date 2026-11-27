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

        public static void Reset() { Close(); _text = ""; _seen = 0; _scroll = 0f; _arrived.Clear(); _rows.Clear(); _liveSeen = -1; _live.Clear(); _layoutCache.Invalidate(); }

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

        // Layout is recomputed only when an input changed (OnGUI runs for every event type, several times a frame).
        static readonly ChatLayoutCache _layoutCache = new ChatLayoutCache();
        static readonly float[] _lineH = new float[4];   // per GameFont

        static ChatLayout.Result Layout()
        {
            var s = S;
            return _layoutCache.Get(new ChatLayout.Input
            {
                ScreenW = UI.screenWidth, ScreenH = UI.screenHeight, Width = s.chatPanelWidth,
                Lines = s.chatPanelLines, LineH = LineHeight(), Right = s.chatPanelRight,
                Custom = s.ChatCustom, FX = s.chatPanelX, FY = s.chatPanelY, FW = s.chatPanelW, FH = s.chatPanelH
            });
        }

        static float LineHeight()
        {
            int f = (int)Font;
            if (f < 0 || f >= _lineH.Length) f = 1;
            if (_lineH[f] <= 0f)
            {
                var old = Text.Font;
                Text.Font = Font;
                _lineH[f] = Text.LineHeight;
                Text.Font = old;
            }
            return _lineH[f];
        }

        // ---- drag (move / resize) state; the box lives in settings as screen fractions, so UI scale and resolution changes keep it
        const int DragNone = 0, DragMove = 1, DragResize = 2;
        static int _drag;
        static Vector2 _dragMouse;
        static ChatRect _dragStart;

        static void SaveSettings()
        {
            try { if (RimWorldLLMMod.Instance != null) RimWorldLLMMod.Instance.WriteSettings(); }
            catch (Exception ex) { Log.ErrorOnce("[RimWorldLLM] saving chat panel settings failed: " + ex.Message, 7340212); }
        }

        static void SetBox(ChatRect box)
        {
            var f = ChatLayout.ToFractions(box, UI.screenWidth, UI.screenHeight);
            var s = S;
            s.chatPanelX = f.X; s.chatPanelY = f.Y; s.chatPanelW = f.W; s.chatPanelH = f.H;
        }

        static readonly string[] GroupKeys = { "RWL_Chat_ChipTalk", "RWL_Chat_ChipThoughts", "RWL_Chat_ChipNarrator", "RWL_Chat_ChipActions", "RWL_Chat_ChipStory" };

        static bool GroupOn(int i)
        {
            var s = S;
            switch (i) { case 0: return s.chatShowTalk; case 1: return s.chatShowThoughts; case 2: return s.chatShowNarrator; case 3: return s.chatShowActions; default: return s.chatShowStory; }
        }

        static void ToggleGroup(int i)
        {
            var s = S;
            switch (i) { case 0: s.chatShowTalk = !s.chatShowTalk; break; case 1: s.chatShowThoughts = !s.chatShowThoughts; break; case 2: s.chatShowNarrator = !s.chatShowNarrator; break; case 3: s.chatShowActions = !s.chatShowActions; break; default: s.chatShowStory = !s.chatShowStory; break; }
        }

        static bool Shown(string who) { var s = S; return ChatLayout.Shown(who, s.chatShowTalk, s.chatShowThoughts, s.chatShowNarrator, s.chatShowActions, s.chatShowStory); }

        /// <summary>The chips in the strip, left to right after the grip; one that does not fit is left out (rect width 0).</summary>
        static Rect[] ChipRects(ChatLayout.Result lay)
        {
            if (_chipLabels == null)
            {
                _chipLabels = new string[GroupKeys.Length]; _chipW = new float[GroupKeys.Length];
                var f0 = Text.Font;
                Text.Font = GameFont.Tiny;
                for (int i = 0; i < GroupKeys.Length; i++) { _chipLabels[i] = UiUtil.T(GroupKeys[i]); _chipW[i] = Text.CalcSize(_chipLabels[i]).x + 10f; }
                Text.Font = f0;
                _chipTip = UiUtil.T("RWL_Chat_ChipTip");
            }
            for (int i = 0; i < _chipRects.Length; i++) _chipRects[i] = default(Rect);
            float x = lay.Grip.Right + 2f, y = lay.Strip.Y + 2f, h = lay.Strip.H - 4f, maxX = lay.Strip.Right - 3f;
            for (int i = 0; i < _chipRects.Length; i++)
            {
                float w = _chipW[i];
                if (x + w > maxX) break;
                _chipRects[i] = new Rect(x, y, w, h);
                x += w + 3f;
            }
            return _chipRects;
        }

        static string[] _chipLabels;
        static float[] _chipW;
        static string _chipTip;
        static readonly Rect[] _chipRects = new Rect[GroupKeys.Length];

        /// <summary>Presses on the grips, the strip and the chips; the drag itself. Returns true when it took the event.</summary>
        static void HandleMouse(Event e, ref ChatLayout.Result lay, bool hover)
        {
            float lh = LineHeight();
            if (_drag != DragNone)
            {
                if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp || e.type == EventType.Repaint)
                {
                    bool up = e.type == EventType.MouseUp || (e.type == EventType.Repaint && !Input.GetMouseButton(0));
                    float dx = e.mousePosition.x - _dragMouse.x, dy = e.mousePosition.y - _dragMouse.y;
                    if (e.type == EventType.MouseDrag || up)
                    {
                        float sw = UI.screenWidth, sh = UI.screenHeight;
                        var nb = _drag == DragMove ? ChatLayout.Move(_dragStart, dx, dy, sw, sh) : ChatLayout.Resize(_dragStart, dx, dy, sw, sh, lh);
                        SetBox(nb);
                        lay = Layout();
                    }
                    if (up) { _drag = DragNone; SaveSettings(); }
                    if (e.type != EventType.Repaint) e.Use();
                }
                return;
            }
            if (!hover || e.type != EventType.MouseDown || e.button != 0) return;
            var m = e.mousePosition;
            var chips = ChipRects(lay);
            for (int i = 0; i < chips.Length; i++)
                if (chips[i].width > 0f && chips[i].Contains(m)) { ToggleGroup(i); SaveSettings(); e.Use(); return; }
            if (R(lay.Resize).Contains(m)) { BeginDrag(DragResize, lay, m); e.Use(); return; }
            if (R(lay.Strip).Contains(m)) { BeginDrag(DragMove, lay, m); e.Use(); }
        }

        static void BeginDrag(int mode, ChatLayout.Result lay, Vector2 mouse)
        {
            _drag = mode; _dragMouse = mouse; _dragStart = lay.Box;
            SetBox(lay.Box);   // from now on the panel is customised, starting exactly where it was
        }

        static void DrawChrome(ChatLayout.Result lay)
        {
            var strip = R(lay.Strip);
            Widgets.DrawBoxSolid(strip, new Color(0.05f, 0.05f, 0.05f, 0.9f));
            // move grip: three short bars
            for (int k = 0; k < 3; k++)
                Widgets.DrawBoxSolid(new Rect(strip.x + 5f, strip.y + 5f + k * 4f, 10f, 2f), new Color(1f, 1f, 1f, _drag == DragMove ? 0.9f : 0.5f));
            var chips = ChipRects(lay);
            var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            var mouse = Event.current.mousePosition;
            for (int i = 0; i < chips.Length; i++)
            {
                if (chips[i].width <= 0f) continue;
                bool on = GroupOn(i), over = chips[i].Contains(mouse);
                Widgets.DrawBoxSolid(chips[i], on ? new Color(0.25f, 0.4f, 0.3f, over ? 1f : 0.9f) : new Color(0.15f, 0.15f, 0.15f, over ? 0.9f : 0.6f));
                GUI.color = on ? Color.white : new Color(1f, 1f, 1f, 0.35f);
                Widgets.Label(chips[i], _chipLabels[i]);
                if (over) TooltipHandler.TipRegion(chips[i], _chipTip);
            }
            Text.Anchor = oldAnchor;
            GUI.color = Color.white;
            // resize grip: a little staircase in the corner
            var rz = R(lay.Resize);
            for (int k = 0; k < 3; k++)
                Widgets.DrawBoxSolid(new Rect(rz.xMax - 3f - k * 4f - 2f, rz.yMax - 3f - (2 - k) * 4f - 2f + 0f, 2f + k * 4f, 2f), new Color(1f, 1f, 1f, _drag == DragResize ? 0.9f : 0.55f));
            Text.Font = GameFont.Small;
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
            bool hover = _drag != DragNone || R(lay.Whole).Contains(e.mousePosition);
            HandleMouse(e, ref lay, hover);
            if (open)
            {
                var bw = Find.WindowStack.WindowOfType<Window_RWLChat>();
                if (bw != null) bw.windowRect = R(lay.Bar);   // the input bar follows the panel
            }
            if (!hover) _scroll = 0f;   // back to the newest line when the mouse leaves
            if (hover && e.type == EventType.ScrollWheel)
            {
                Text.Font = Font;
                _scroll -= e.delta.y * Text.LineHeight;
                Text.Font = GameFont.Small;
                e.Use();   // the wheel reads history instead of zooming; clicks are never taken
            }
            if (e.type == EventType.Repaint) { DrawPanel(lay, hover); if (hover) DrawChrome(lay); }
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
                case "thought": return new Color(0.6f, 0.76f, 0.6f, 0.9f);    // dimmer green, italic
                case "dream": return new Color(0.7f, 0.66f, 0.92f, 0.9f);     // dim violet, italic
                case "diary": return new Color(0.7f, 0.64f, 0.5f, 0.9f);      // dim paper brown
                case "chronicle": case "recap": return new Color(0.86f, 0.76f, 0.54f);   // muted gold
                case "art": return new Color(0.82f, 0.7f, 0.86f);
                case "camera": return new Color(0.5f, 0.74f, 0.85f, 0.95f);    // dim cyan-grey: the director's caption
                case "comment": return new Color(0.95f, 0.8f, 0.45f, 0.92f);   // narrator gold, italic: documentary commentary
                case "action": return new Color(0.56f, 0.71f, 0.23f);   // #8FB43A: greentext #789922, lifted a touch for dark backgrounds
                case "refused": return new Color(0.5f, 0.5f, 0.5f);
                default: return new Color(0.72f, 0.72f, 0.72f);   // notes ("→ ...")
            }
        }

        static string Name(ChatEntry c) { return string.IsNullOrEmpty(c.Name) ? "?" : c.Name; }

        static string Line(ChatEntry c)
        {
            switch (c.Who)
            {
                case "player": return c.To > 0 ? UiUtil.T("RWL_Viv_LinePlayerTo", c.Name, c.Text) : UiUtil.T("RWL_Viv_LinePlayer", c.Text);
                case "narrator": return UiUtil.T("RWL_Viv_LineNarrator", c.Text);
                case "colonist":
                    return string.IsNullOrEmpty(c.Listener) ? UiUtil.T("RWL_Viv_LineColonist", Name(c), c.Text)
                        : UiUtil.T("RWL_Viv_LineColonistTo", Name(c), c.Listener, c.Text);
                case "thought": return "<i>" + UiUtil.T("RWL_Viv_LineThought", Name(c), c.Text) + "</i>";
                case "dream": return "<i>" + UiUtil.T("RWL_Viv_LineDream", Name(c), c.Text) + "</i>";
                case "diary": return UiUtil.T("RWL_Viv_LineDiary", Name(c), c.Text);
                case "chronicle":
                    return string.IsNullOrEmpty(c.Name) ? UiUtil.T("RWL_Viv_LineChronicle", c.Text) : UiUtil.T("RWL_Viv_LineChronicleDay", c.Name, c.Text);
                case "recap": return UiUtil.T("RWL_Viv_LineRecap", c.Text);
                case "camera": return ChatRowFormat.Camera(c.Text, UiUtil.T("RWL_Viv_CameraPrefix"));
                case "comment": return ChatRowFormat.Comment(c.Text, UiUtil.T("RWL_Viv_CommentPrefix"));
                case "action": case "refused": return "> " + c.Text;
                default: return c.Text;   // note, art (stored as a one-liner)
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

        static PanelRow LiveRow(LiveLine l, double now)
        {
            string name = SpeakerName(l);
            string dots = new string('.', ChatLayout.Dots(now));
            if (!l.Typing || string.IsNullOrEmpty(l.Text))
            {
                string t = UiUtil.T(l.Typing ? "RWL_Viv_Typing" : "RWL_Viv_Thinking", name) + dots.PadRight(3);
                string pct = l.Typing ? "" : ChatLayout.ProgressText(l.Progress);
                if (pct.Length > 0) t += " " + pct;
                return new PanelRow { Text = t, Who = l.Who == "narrator" ? "narrator" : "colonist", To = l.To, Alpha = 0.75f, Progress = l.Typing ? -1f : l.Progress };
            }
            string line = l.Who == "narrator" ? UiUtil.T("RWL_Viv_LineNarrator", l.Text) : name + ": " + l.Text;
            return new PanelRow { Text = line + " " + dots, Who = l.Who == "narrator" ? "narrator" : "colonist", To = l.To };
        }

        // The panel keeps its rows between frames: text, heights and the row list are rebuilt only when something changed.
        static readonly ChatPanelRows _rows = new ChatPanelRows();
        static readonly List<LiveLine> _live = new List<LiveLine>();
        static int _liveSeen = -1;
        static readonly List<KeyValuePair<int, float>> _vis = new List<KeyValuePair<int, float>>();
        static readonly Func<string, bool> _shownFn = Shown;
        static readonly Func<ChatEntry, string> _formatFn = Line;
        static float _measureW;
        static readonly Func<string, float> _heightFn = t => Text.CalcHeight(t, _measureW);
        static double _drawNow;
        static readonly Func<LiveLine, PanelRow> _liveFn = l => LiveRow(l, _drawNow);

        static int FilterBits()
        {
            var s = S;
            return (s.chatShowTalk ? 1 : 0) | (s.chatShowThoughts ? 2 : 0) | (s.chatShowNarrator ? 4 : 0) | (s.chatShowActions ? 8 : 0) | (s.chatShowStory ? 16 : 0);
        }

        static void DrawPanel(ChatLayout.Result lay, bool hover)
        {
            var s = S;
            double now = Now;
            var chat = VivariumGame.State.Chat;
            var box = R(lay.Box);
            float inner = box.width - ChatLayout.Pad * 2f;
            float innerH = box.height - ChatLayout.Pad * 2f;

            Text.Font = Font;
            Text.WordWrap = true;
            _measureW = inner; _drawNow = now;
            int widthKey = (int)Mathf.Round(inner), fontKey = (int)Font, bits = FilterBits();
            Live.Refresh(ref _liveSeen, _live);
            _rows.SyncChat(chat.Entries, chat.LastSeq, bits, widthKey, fontKey, _shownFn, _formatFn, _heightFn);
            _rows.SyncLive(_liveSeen, ChatLayout.Dots(now), bits, widthKey, fontKey, _live, _shownFn, _liveFn, _heightFn);
            var rows = _rows.Rows;

            float freshest = 0f;
            if (_arrived.Count > 0)
                for (int i = rows.Count - 1; i >= 0 && i >= rows.Count - 12; i--)
                {
                    double at;
                    if (!rows[i].Live && _arrived.TryGetValue(rows[i].Seq, out at)) freshest = Mathf.Max(freshest, ChatLayout.Freshness(now - at));
                }
            float bg = ChatLayout.BackgroundAlpha(s.chatPanelOpacity, hover, freshest);
            if (bg > 0.001f) Widgets.DrawBoxSolid(box, new Color(0f, 0f, 0f, bg));
            if (hover) { GUI.color = new Color(1f, 1f, 1f, 0.15f); Widgets.DrawBox(box, 1, null); GUI.color = Color.white; }
            if (rows.Count == 0) { Text.Font = GameFont.Small; return; }

            ChatLayout.VisibleInto(_rows.Heights, innerH, ref _scroll, _vis);
            Widgets.BeginGroup(new Rect(box.x + ChatLayout.Pad, box.y + ChatLayout.Pad, inner, innerH));
            for (int k = 0; k < _vis.Count; k++)
            {
                var r = rows[_vis[k].Key];
                var lr = new Rect(0f, _vis[k].Value, inner, r.H);
                var col = ColorOf(r.Who, r.To);
                if (r.Alpha < 1f) col = new Color(col.r, col.g, col.b, r.Alpha);
                double at;
                float fresh = !r.Live && _arrived.Count > 0 && _arrived.TryGetValue(r.Seq, out at) ? ChatLayout.Freshness(now - at) : 0f;
                if (fresh > 0f) Widgets.DrawBoxSolid(lr, new Color(1f, 0.9f, 0.6f, 0.12f * fresh));
                ShadowLabel(lr, r.Text, col);
                if (r.Progress >= 0f)
                {
                    var track = new Rect(0f, lr.yMax - 2f, inner * 0.5f, 2f);
                    Widgets.DrawBoxSolid(track, new Color(1f, 1f, 1f, 0.12f));
                    Widgets.DrawBoxSolid(new Rect(track.x, track.y, track.width * r.Progress, 2f), new Color(col.r, col.g, col.b, 0.8f));
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
