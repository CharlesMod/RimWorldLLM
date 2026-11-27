using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    public class Bubble
    {
        public Pawn Pawn;
        public string Text;
        public int StartTick;
        public bool Active;

        // Comic style (see SpeechBubbles.DrawComic). Set at creation.
        public BubbleKind Kind;
        public string Clean;          // text without kind markers
        public float AnimT;           // seconds of pop-in played (frozen while the game is paused)
        public bool Ambient;          // a pawn thought from /thought (fainter, yields to speech), not a spoken line

        // Layout cache: rebuilt only when the text, kind, lettering, font or UI scale changes.
        internal bool CacheValid;
        internal bool CacheCaps;
        internal float CacheUiScale;
        internal string Display;      // text with explicit line breaks, centred when drawn
        internal float TextW, TextH, W, H;
    }

    /// <summary>
    /// Overhead dialogue bubbles. Drawn via a Harmony postfix on the map OnGUI (before the main
    /// tabs), which is where overhead labels are drawn, so the z-order is correct.
    /// </summary>
    public static partial class SpeechBubbles
    {
        const int MaxBubbles = 24;
        static readonly List<Bubble> Active = new List<Bubble>();

        /// <param name="kind">optional explicit look ("thought", "narrator", "shout", "whisper")</param>
        /// <param name="intent">the /say intent, used to pick the look</param>
        public static void Add(Pawn pawn, string text, string kind = null, string intent = null, bool combat = false)
        {
            if (pawn == null || string.IsNullOrWhiteSpace(text)) return;
            string clean;
            var look = BubbleLayout.ChooseKind(kind, intent, text, combat, out clean);
            for (int i = Active.Count - 1; i >= 0; i--)
                if (Active[i].Pawn == pawn) Active.RemoveAt(i);
            if (Active.Count >= MaxBubbles) Active.RemoveAt(0);
            Active.Add(new Bubble
            {
                Pawn = pawn, Text = text, StartTick = Find.TickManager.TicksGame, Active = true,
                Kind = look, Clean = string.IsNullOrWhiteSpace(clean) ? text : clean,
                // Born while paused: skip the pop so it does not sit frozen at 85%.
                AnimT = Find.TickManager.Paused ? BubbleMetrics.PopSeconds : 0f
            });
            DirectorCamera.OnSpeech(pawn, look, intent);
        }

        public static void Clear() { Active.Clear(); Pacing.Clear(); }

        internal static readonly ThoughtPacing Pacing = new ThoughtPacing();
        static readonly List<System.Collections.Generic.KeyValuePair<int, string>> _ready = new List<System.Collections.Generic.KeyValuePair<int, string>>();

        static bool SpeechVisible(Pawn pawn, int now, int durationTicks)
        {
            for (int i = 0; i < Active.Count; i++)
                if (Active[i].Pawn == pawn && !Active[i].Ambient && now - Active[i].StartTick <= durationTicks) return true;
            return false;
        }

        /// <summary>
        /// A pawn thought as a cloud over the pawn (kind "thought" only; diaries never come here). Subject to
        /// <see cref="ThoughtPacing"/>: one per pawn, per-pawn and global gaps, and never over a visible speech balloon.
        /// </summary>
        public static void AddThought(Pawn pawn, string surface, string text)
        {
            var s = RimWorldLLMMod.Settings;
            if (pawn == null || s == null || !s.showBubbles || !s.showThoughts || s.bubbleStyle == "classic") return;
            string line = ThoughtPacing.Surface(surface, text);
            if (line == null) return;
            int now = Find.TickManager.TicksGame, dur = ThoughtPacing.GapTicks(Mathf.Max(1, s.bubbleDurationSec), TicksPerRealSec());   // real seconds at any game speed (never shorter than the 1x count)
            bool thoughtActive = false;
            for (int i = 0; i < Active.Count; i++)
                if (Active[i].Pawn == pawn && Active[i].Ambient && now - Active[i].StartTick <= dur) thoughtActive = true;
            var v = Pacing.Offer(pawn.thingIDNumber, line, now, thoughtActive, SpeechVisible(pawn, now, dur),
                                 ThoughtPacing.GapTicks(s.thoughtPerPawnSec, TicksPerRealSec()), ThoughtPacing.GapTicks(s.thoughtGlobalSec, TicksPerRealSec()));
            if (v == ThoughtPacing.Verdict.Show) ShowThought(pawn, line);
        }

        // Game ticks per real second now (60 at 1x, 180, 360, 900): the pacing gaps are real seconds, not game seconds.
        static float TicksPerRealSec() { return 60f * Find.TickManager.TickRateMultiplier; }

        static void ShowThought(Pawn pawn, string line)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
                if (Active[i].Pawn == pawn) Active.RemoveAt(i);
            if (Active.Count >= MaxBubbles) Active.RemoveAt(0);
            Active.Add(new Bubble
            {
                Pawn = pawn, Text = line, Clean = line, StartTick = Find.TickManager.TicksGame, Active = true,
                Kind = BubbleKind.Thought, Ambient = true,
                AnimT = Find.TickManager.Paused ? BubbleMetrics.PopSeconds : 0f
            });
            DirectorCamera.OnThought(pawn);
        }

        // Release waiting thoughts whose pawn has stopped speaking (called once per frame by the comic draw path).
        static void PromoteThoughts(Map map, RimWorldLLMSettings s, int now, int durationTicks)
        {
            if (Pacing.PendingCount == 0) return;
            _ready.Clear();
            var pawns = map.mapPawns.AllPawnsSpawned;
            Pacing.Promote(now, id =>
            {
                for (int i = 0; i < pawns.Count; i++)
                    if (pawns[i].thingIDNumber == id) return SpeechVisible(pawns[i], now, durationTicks);
                return false;
            }, ThoughtPacing.GapTicks(s.thoughtGlobalSec, TicksPerRealSec()), _ready, ThoughtPacing.StaleFor(TicksPerRealSec()));
            for (int k = 0; k < _ready.Count; k++)
                for (int i = 0; i < pawns.Count; i++)
                    if (pawns[i].thingIDNumber == _ready[k].Key) { ShowThought(pawns[i], _ready[k].Value); break; }
        }

        /// <summary>Fill, border and text colours for a pawn: one stable hue, dark fill, mid border, pale text.</summary>
        public static void PawnColors(int pawnId, out Color bg, out Color border, out Color text)
        {
            float h = Bridge.PawnHue.HueFor(pawnId), r, g, b;
            Bridge.PawnHue.Hsv(h, 0.6f, 0.22f, out r, out g, out b); bg = new Color(r, g, b, 0.92f);
            Bridge.PawnHue.Hsv(h, 0.7f, 0.95f, out r, out g, out b); border = new Color(r, g, b, 1f);
            Bridge.PawnHue.Hsv(h, 0.3f, 1f, out r, out g, out b); text = new Color(r, g, b, 1f);
        }

        public static void DrawBubbles()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var map = Find.CurrentMap;
            var s = RimWorldLLMMod.Settings;
            if (map == null || s == null) return;
            if (s.bubbleStyle == "classic") DrawClassic(map, s); else DrawComic(map, s);
        }

        /// <summary>The original rectangular boxes, kept for the "Classic" bubble style.</summary>
        static void DrawClassic(Map map, RimWorldLLMSettings s)
        {
            bool draw = s.showBubbles;

            int now = Find.TickManager.TicksGame;
            int durationTicks = ThoughtPacing.GapTicks(Mathf.Max(1, s.bubbleDurationSec), TicksPerRealSec());   // readable at 3x: the setting is real seconds of watching

            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var b = Active[i];
                var pawn = b.Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Map != map || now - b.StartTick > durationTicks)
                {
                    b.Active = false;
                    Active.RemoveAt(i);
                    continue;
                }
                if (!draw) continue;

                Vector2 screenPos = GenMapUI.LabelDrawPosFor(pawn, 1.0f);
                // Word-wrap: max width ~260px, up to 4 lines, "…" only past that.
                const float maxW = 260f, pad = 6f;
                var prevFont = Text.Font;
                var prevWrap = Text.WordWrap;
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                float innerW = maxW - pad * 2f;
                float lineH = Text.LineHeight;
                float maxInnerH = lineH * 4f + 1f;
                string display = b.Text;
                float h = Text.CalcHeight(display, innerW);
                if (h > maxInnerH)
                {
                    int lo = 0, hi = display.Length;
                    while (lo < hi)
                    {
                        int mid = (lo + hi + 1) / 2;
                        if (Text.CalcHeight(display.Substring(0, mid).TrimEnd() + "\u2026", innerW) <= maxInnerH) lo = mid; else hi = mid - 1;
                    }
                    display = display.Substring(0, lo).TrimEnd() + "\u2026";
                    h = Text.CalcHeight(display, innerW);
                }
                float w = maxW;
                if (h <= lineH + 1f)
                    w = Mathf.Clamp(Text.CalcSize(display).x + pad * 2f + 4f, 80f, maxW);
                float boxH = h + pad * 2f;
                Rect boxRect = new Rect(screenPos.x - w * 0.5f, screenPos.y - boxH - 8f, w, boxH);

                Color bg = new Color(0.05f, 0.06f, 0.09f, 0.9f), border = new Color(0.4f, 0.42f, 0.5f, 1f), text = Color.white;
                if (s.colorBubbles) PawnColors(pawn.thingIDNumber, out bg, out border, out text);
                // The box carries its own dark fill, so the text reads on both dark and light terrain.
                float opacity = Mathf.Clamp01(s.bubbleOpacity);
                Widgets.DrawBoxSolid(boxRect, new Color(bg.r, bg.g, bg.b, bg.a * opacity));
                GUI.color = new Color(border.r, border.g, border.b, border.a * opacity);
                Widgets.DrawBox(boxRect, 2, null);
                GUI.color = new Color(text.r, text.g, text.b, text.a * opacity);

                Widgets.Label(boxRect.ContractedBy(pad), display);
                Text.Font = prevFont;
                Text.WordWrap = prevWrap;
                GUI.color = Color.white;
            }
        }

        // Leaving a game (to the main menu, or on the way to loading another save).
        [HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
        public static class GameEndPatch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                RimWorldLLMGameComponent.OnGameEnd();
            }
        }

        [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs))]
        public static class BubbleOnGuiPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                DrawBubbles();
            }
        }
    }
}
