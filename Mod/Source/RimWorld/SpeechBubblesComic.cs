using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    // The comic-balloon draw path. Layout maths lives in BubbleLayout (pure, tested); textures in BubbleTex.
    public static partial class SpeechBubbles
    {
        static readonly Color InkColor = new Color(0.10f, 0.09f, 0.12f, 1f);
        static readonly Color PaperColor = new Color(0.99f, 0.98f, 0.95f, 1f);
        static readonly Color CaptionColor = new Color(0.99f, 0.93f, 0.66f, 1f);
        const float ScreenMargin = 4f, BottomBar = 40f;

        // Reused per frame (no per-frame allocation in the draw path).
        static readonly List<Bubble> Live = new List<Bubble>();
        static readonly List<BRect> Rects = new List<BRect>();
        static readonly float[] DotX = new float[3], DotY = new float[3], DotD = new float[3];
        static Vector2[] _targets = new Vector2[32];

        static void DrawComic(Map map, HermesVoiceSettings s)
        {
            int now = Find.TickManager.TicksGame;
            int durationTicks = Mathf.Max(1, s.bubbleDurationSec) * 60;
            bool paused = Find.TickManager.Paused;
            float dt = paused ? 0f : Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float opacity = Mathf.Clamp01(s.bubbleOpacity);

            Live.Clear(); Rects.Clear();
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var b = Active[i];
                var pawn = b.Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Map != map || now - b.StartTick > durationTicks)
                {
                    b.Active = false;
                    Active.RemoveAt(i);
                }
            }
            if (!s.showBubbles) return;
            if (s.showThoughts) PromoteThoughts(map, s, now, durationTicks);

            float uiScale = Prefs.UIScale;
            var bounds = new BRect(ScreenMargin, ScreenMargin, UI.screenWidth - 2f * ScreenMargin, UI.screenHeight - 2f * ScreenMargin - BottomBar);
            int n = Active.Count;
            if (_targets.Length < n) _targets = new Vector2[n * 2];
            var prevFont = Text.Font; var prevAnchor = Text.Anchor; var prevWrap = Text.WordWrap; var prevColor = GUI.color;

            for (int i = 0; i < n; i++)
            {
                var b = Active[i];
                Vector2 pos = GenMapUI.LabelDrawPosFor(b.Pawn, 1.0f);
                if (!b.CacheValid || b.CacheCaps != s.comicLettering || !Mathf.Approximately(b.CacheUiScale, uiScale)) BuildCache(b, s.comicLettering, uiScale);
                float tx = pos.x, ty = pos.y - 4f;
                _targets[i] = new Vector2(tx, ty);
                Live.Add(b);
                Rects.Add(BubbleLayout.DefaultRect(b.W, b.H, tx, ty, bounds));
            }
            ArrangeInto(n, bounds);

            for (int i = 0; i < Live.Count; i++)
            {
                var b = Live[i];
                if (b.AnimT < BubbleMetrics.PopSeconds) b.AnimT += dt;
                float pop = BubbleLayout.PopScale(b.AnimT);
                float alpha = opacity * (b.Ambient ? Mathf.Clamp01(s.thoughtOpacity) : 1f) * BubbleLayout.FadeAlpha(durationTicks - (now - b.StartTick));
                if (alpha <= 0.003f) continue;
                DrawOne(b, Rects[i], _targets[i], _tails[i], pop, alpha, s);
            }
            Text.Font = prevFont; Text.Anchor = prevAnchor; Text.WordWrap = prevWrap; GUI.color = prevColor;
        }

        static readonly List<float> _tx = new List<float>(), _ty = new List<float>();
        static readonly List<BubbleKind> _kinds = new List<BubbleKind>();
        static TailInfo[] _tails = new TailInfo[32];

        static void ArrangeInto(int n, BRect bounds)
        {
            if (_tails.Length < n) _tails = new TailInfo[n * 2];
            _tx.Clear(); _ty.Clear(); _kinds.Clear();
            for (int i = 0; i < Live.Count; i++) { _tx.Add(_targets[i].x); _ty.Add(_targets[i].y); _kinds.Add(Live[i].Kind); }
            BubbleLayout.Arrange(Rects, _tx, _ty, _kinds, bounds, 3f, _tails);
        }

        static void BuildCache(Bubble b, bool caps, float uiScale)
        {
            Text.Font = BubbleLayout.SmallText(b.Kind) ? GameFont.Tiny : GameFont.Small;
            Text.WordWrap = false;
            string text = caps ? b.Clean.ToUpperInvariant() : b.Clean;
            var lines = BubbleLayout.Wrap(text, str => Text.CalcSize(str).x, BubbleMetrics.MaxTextWidth, BubbleMetrics.MaxLines);
            float widest = 0f;
            for (int i = 0; i < lines.Count; i++) widest = Mathf.Max(widest, Text.CalcSize(lines[i]).x);
            b.Display = string.Join("\n", lines);
            b.TextW = widest + 2f;
            b.TextH = lines.Count * Text.LineHeight;
            float w, h; BubbleLayout.BalloonSize(b.Kind, b.TextW, b.TextH, out w, out h);
            b.W = w; b.H = h;
            b.CacheCaps = caps; b.CacheUiScale = uiScale; b.CacheValid = true;
        }

        static void DrawOne(Bubble b, BRect br, Vector2 target, TailInfo tail, float pop, float alpha, HermesVoiceSettings s)
        {
            var kind = b.Kind;
            int ki = (int)kind;
            var balloon = new Rect(br.X, br.Y, br.W, br.H);
            float scale = BubbleLayout.CornerScale(kind, br.W, br.H);
            bool tiled = BubbleMetrics.Tiled(kind);
            float mid = BubbleMetrics.MidSrc(kind);

            Color ink = InkColor, fill = kind == BubbleKind.Narrator ? CaptionColor : PaperColor, rim = fill;
            bool useRim = s.colorBubbles;
            if (useRim) { Color bg, border, txt; PawnColors(b.Pawn.thingIDNumber, out bg, out border, out txt); rim = border; }
            ink.a = alpha; fill.a = alpha; rim.a = alpha;

            var prevMatrix = GUI.matrix;
            bool scaled = Mathf.Abs(pop - 1f) > 0.001f;
            if (scaled) GUIUtility.ScaleAroundPivot(new Vector2(pop, pop), target);

            GUI.color = ink;
            BubbleDraw.NineSlice(balloon, BubbleTex.Ink[ki], scale, tiled, mid);
            if (tail.Has && kind != BubbleKind.Thought) BubbleDraw.Tail(BubbleTex.TailInk, tail);
            GUI.color = fill;
            BubbleDraw.NineSlice(balloon, BubbleTex.Fill[ki], scale, tiled, mid);
            if (useRim) { GUI.color = rim; BubbleDraw.NineSlice(balloon, BubbleTex.Rim[ki], scale, tiled, mid); }
            if (tail.Has && kind != BubbleKind.Thought)
            {
                GUI.color = fill; BubbleDraw.Tail(BubbleTex.TailFill, tail);
                if (useRim) { GUI.color = rim; BubbleDraw.Tail(BubbleTex.TailRim, tail); }
            }
            else if (tail.Has)
            {
                int dots = BubbleLayout.ThoughtDots(tail, DotX, DotY, DotD);
                for (int d = 0; d < dots; d++)
                {
                    GUI.color = ink; BubbleDraw.Dot(BubbleTex.DotInk, DotX[d], DotY[d], DotD[d]);
                    GUI.color = fill; BubbleDraw.Dot(BubbleTex.DotFill, DotX[d], DotY[d], DotD[d]);
                    if (useRim) { GUI.color = rim; BubbleDraw.Dot(BubbleTex.DotRim, DotX[d], DotY[d], DotD[d]); }
                }
            }

            Text.Font = BubbleLayout.SmallText(kind) ? GameFont.Tiny : GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.WordWrap = false;
            GUI.color = new Color(InkColor.r, InkColor.g, InkColor.b, alpha);
            Widgets.Label(balloon, b.Display);
            Text.Anchor = TextAnchor.UpperLeft;

            if (scaled) GUI.matrix = prevMatrix;
            GUI.color = Color.white;
        }
    }
}
