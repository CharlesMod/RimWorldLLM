using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    public class Bubble
    {
        public Pawn Pawn;
        public string Text;
        public int StartTick;
        public bool Active;
    }

    /// <summary>
    /// Overhead dialogue bubbles. Drawn via a Harmony postfix on the map OnGUI (before the main
    /// tabs), which is where overhead labels are drawn, so the z-order is correct.
    /// </summary>
    public static class SpeechBubbles
    {
        const int MaxBubbles = 24;
        static readonly List<Bubble> Active = new List<Bubble>();

        public static void Add(Pawn pawn, string text)
        {
            if (pawn == null || string.IsNullOrWhiteSpace(text)) return;
            for (int i = Active.Count - 1; i >= 0; i--)
                if (Active[i].Pawn == pawn) Active.RemoveAt(i);
            if (Active.Count >= MaxBubbles) Active.RemoveAt(0);
            Active.Add(new Bubble { Pawn = pawn, Text = text, StartTick = Find.TickManager.TicksGame, Active = true });
        }

        public static void Clear() { Active.Clear(); }

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
            var s = HermesVoiceMod.Settings;
            if (map == null || s == null) return;
            bool draw = s.showBubbles;

            int now = Find.TickManager.TicksGame;
            int durationTicks = Mathf.Max(1, s.bubbleDurationSec) * 60;

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
                Widgets.DrawBoxSolid(boxRect, bg);
                GUI.color = border;
                Widgets.DrawBox(boxRect, 2, null);
                GUI.color = text;

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
                HermesVoiceGameComponent.OnGameEnd();
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
