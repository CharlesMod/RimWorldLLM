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
    [StaticConstructorOnStartup]
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

        public static void DrawBubbles()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var map = Find.CurrentMap;
            if (map == null) return;
            var s = HermesVoiceMod.Settings;
            if (s == null || !s.showBubbles) return;

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

                Vector2 screenPos = GenMapUI.LabelDrawPosFor(pawn, 1.0f);
                float estW = Mathf.Clamp(b.Text.Length * 7.5f + 28f, 140f, 360f);
                float boxH = 30f;
                Rect boxRect = new Rect(screenPos.x - estW * 0.5f, screenPos.y - boxH - 8f, estW, boxH);

                GUI.color = new Color(0.05f, 0.06f, 0.09f, 0.9f);
                Widgets.DrawBox(boxRect, 1, null);
                GUI.color = Color.white;

                string display = b.Text.Length > 60 ? b.Text.Substring(0, 60) + "..." : b.Text;
                Rect textRect = boxRect.ContractedBy(6f);
                Widgets.Label(textRect, display);
                GUI.color = Color.white;
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
