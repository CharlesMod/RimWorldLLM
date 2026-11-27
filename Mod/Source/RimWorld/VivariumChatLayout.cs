using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;

namespace RimWorldLLM
{
    /// <summary>A plain rect (UI units), so the layout math needs no UnityEngine.</summary>
    public struct ChatRect
    {
        public float X, Y, W, H;
        public ChatRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public float Right { get { return X + W; } }
        public float Bottom { get { return Y + H; } }
        public bool Contains(float px, float py) { return px >= X && px < X + W && py >= Y && py < Y + H; }
        public bool Overlaps(ChatRect o) { return X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom; }
        public override string ToString() { return "(" + X + ", " + Y + ", " + W + " x " + H + ")"; }
    }

    /// <summary>
    /// Where the always-on chat panel goes, MOBA style. Pure math over the screen size in UI units (UI.screenWidth/Height,
    /// i.e. pixels / UI scale) and the vanilla 1.x HUD anchors:
    ///  - the resource readout: a column at the top left, x 7..~140, down to screenHeight - 200;
    ///  - the inspect pane: bottom left, 432 wide, 165 tall, on top of the 35-tall main button bar (top edge at H - 200);
    ///  - the date/weather/time controls: bottom right, 200 wide; alerts (154 wide) and the letter stack on the right edge;
    ///  - the colonist bar along the top centre.
    /// Left: the panel starts just right of the resource readout and sits above the inspect pane.
    /// Right: it ends left of the controls/alerts column; it may go down to the main button bar when it is clear of the inspect pane.
    /// The input bar slot is the panel's bottom edge: the log box sits right above it, same width.
    /// </summary>
    public static class ChatLayout
    {
        public const float BarH = 28f;
        public const float Gap = 8f;
        public const float Pad = 5f;
        public const float BottomBarH = 35f;            // vanilla main button bar
        public const float InspectPaneW = 432f, InspectPaneH = 165f;
        public const float ReadoutRight = 140f;          // resource readout column's right edge (with its scrollbar)
        public const float RightColumnW = 200f;          // date/weather/time controls; alerts and letters are narrower
        public const float TopMin = 100f;                // keep clear of the colonist bar
        public const float MinW = 220f, MaxW = 900f;
        public const int MinLines = 2;
        public const float LeftX = ReadoutRight + Gap;   // 148

        public struct Input
        {
            public float ScreenW, ScreenH;
            public float Width;       // wanted panel width
            public int Lines;         // wanted number of visible lines
            public float LineH;       // one line at the chosen font (Text.LineHeight)
            public bool Right;        // position: false = left, true = right
            public bool Custom;       // true: the player placed it; FX/FY/FW/FH (fractions of the screen) give the log box
            public float FX, FY, FW, FH;
        }

        public struct Result
        {
            public ChatRect Box;      // the log box (always drawn)
            public ChatRect Bar;      // the input bar slot under it (drawn while typing)
            public ChatRect Whole;    // box + bar, the mouse-over area
            public int Lines;         // visible lines that fit
            public ChatRect Strip;    // the hover title strip: the top StripH of the box
            public ChatRect Grip;     // move grip in the strip's left end
            public ChatRect Resize;   // resize grip at the box's bottom-right corner
        }

        public const float StripH = 20f, GripSize = 16f, ResizeSize = 16f;
        public const float BarGap = 2f;
        public static float MinBoxH(float lineH) { return MinLines * (lineH > 1f ? lineH : 22f) + 2f * Pad; }

        public static Result Compute(Input i)
        {
            float lineH = i.LineH > 1f ? i.LineH : 22f;
            if (i.Custom) return ComputeCustom(i, lineH);
            int lines = Math.Max(MinLines, i.Lines);

            // width: the wanted one, within what the side leaves free
            float w = Clamp(i.Width, MinW, MaxW);
            float free = i.ScreenW - LeftX - RightColumnW - Gap;   // between the readout column and the right column, either side
            if (w > free) w = Math.Max(Math.Min(MinW, i.ScreenW - 2f * Gap), free);
            float x = i.Right ? i.ScreenW - RightColumnW - Gap - w : LeftX;
            if (x < Gap) x = Gap;

            // bottom: above the inspect pane unless clear of it horizontally
            bool overInspect = x < InspectPaneW + Gap;
            float bottom = overInspect ? i.ScreenH - BottomBarH - InspectPaneH - Gap : i.ScreenH - BottomBarH - Gap;

            var bar = new ChatRect(x, bottom - BarH, w, BarH);
            float boxBottom = bar.Y - 2f;
            float room = boxBottom - TopMin - 2f * Pad;
            int fit = (int)Math.Floor(room / lineH);
            if (fit < lines) lines = Math.Max(1, fit);   // a tiny screen (under ~400 tall) keeps one line even under the colonist bar
            float h = lines * lineH + 2f * Pad;
            var box = new ChatRect(x, boxBottom - h, w, h);
            return Finish(box, bar, lines);
        }

        static Result Finish(ChatRect box, ChatRect bar, int lines)
        {
            float top = Math.Min(box.Y, bar.Y), bottom = Math.Max(box.Bottom, bar.Bottom);
            return new Result
            {
                Box = box, Bar = bar, Whole = new ChatRect(box.X, top, box.W, bottom - top), Lines = lines,
                Strip = new ChatRect(box.X, box.Y, box.W, Math.Min(StripH, box.H)),
                Grip = new ChatRect(box.X, box.Y, GripSize + 4f, Math.Min(StripH, box.H)),
                Resize = new ChatRect(box.Right - ResizeSize, box.Bottom - ResizeSize, ResizeSize, ResizeSize)
            };
        }

        static Result ComputeCustom(Input i, float lineH)
        {
            var box = ClampBox(FromFractions(i.FX, i.FY, i.FW, i.FH, i.ScreenW, i.ScreenH), i.ScreenW, i.ScreenH, lineH);
            var bar = BarFor(box, i.ScreenH);
            int lines = Math.Max(1, (int)Math.Floor((box.H - 2f * Pad) / lineH + 0.0001f));
            return Finish(box, bar, lines);
        }

        /// <summary>The input bar follows the box: right below it, or right above it when there is no room below.</summary>
        public static ChatRect BarFor(ChatRect box, float screenH)
        {
            float below = box.Bottom + BarGap;
            if (below + BarH <= screenH) return new ChatRect(box.X, below, box.W, BarH);
            return new ChatRect(box.X, Math.Max(0f, box.Y - BarGap - BarH), box.W, BarH);
        }

        public static ChatRect FromFractions(float fx, float fy, float fw, float fh, float screenW, float screenH)
        {
            return new ChatRect(fx * screenW, fy * screenH, fw * screenW, fh * screenH);
        }

        /// <summary>The box as fractions of the screen (X, Y, W, H hold the fractions).</summary>
        public static ChatRect ToFractions(ChatRect r, float screenW, float screenH)
        {
            float sw = Math.Max(1f, screenW), sh = Math.Max(1f, screenH);
            return new ChatRect(r.X / sw, r.Y / sh, r.W / sw, r.H / sh);
        }

        /// <summary>Fully on screen, at least MinW x MinBoxH (or the whole screen if that is smaller).</summary>
        public static ChatRect ClampBox(ChatRect r, float screenW, float screenH, float lineH)
        {
            float w = Clamp(r.W, Math.Min(MinW, screenW), screenW);
            float h = Clamp(r.H, Math.Min(MinBoxH(lineH), screenH), screenH);
            return new ChatRect(Clamp(r.X, 0f, screenW - w), Clamp(r.Y, 0f, screenH - h), w, h);
        }

        /// <summary>Drag the whole box by (dx, dy) from where it was when the drag began; stays on screen.</summary>
        public static ChatRect Move(ChatRect start, float dx, float dy, float screenW, float screenH)
        {
            return new ChatRect(Clamp(start.X + dx, 0f, Math.Max(0f, screenW - start.W)), Clamp(start.Y + dy, 0f, Math.Max(0f, screenH - start.H)), start.W, start.H);
        }

        /// <summary>Drag the bottom-right corner by (dx, dy): the top-left stays; min size, never past the screen edge.</summary>
        public static ChatRect Resize(ChatRect start, float dx, float dy, float screenW, float screenH, float lineH)
        {
            float minW = Math.Min(MinW, Math.Max(1f, screenW - start.X)), minH = Math.Min(MinBoxH(lineH), Math.Max(1f, screenH - start.Y));
            float w = Clamp(start.W + dx, minW, Math.Max(minW, screenW - start.X));
            float h = Clamp(start.H + dy, minH, Math.Max(minH, screenH - start.Y));
            return new ChatRect(start.X, start.Y, w, h);
        }

        /// <summary>Does this line show with the five group filters? Unknown kinds count as narrator (see ChatKinds.Group).</summary>
        public static bool Shown(string who, bool talk, bool thoughts, bool narrator, bool actions, bool story)
        {
            switch (ChatKinds.Group(who))
            {
                case ChatKinds.Talk: return talk;
                case ChatKinds.Thoughts: return thoughts;
                case ChatKinds.Actions: return actions;
                case ChatKinds.Story: return story;
                default: return narrator;
            }
        }

        /// <summary>
        /// Which entries to draw in a box of the given inner height, newest at the bottom, scrolled up by scroll pixels.
        /// heights: each entry's wrapped height, oldest first. Returns (index, y) pairs, y relative to the box's inner top,
        /// may be negative for an entry cut at the top (draw it clipped). The scroll is clamped into range.
        /// </summary>
        public static List<KeyValuePair<int, float>> Visible(IList<float> heights, float innerH, ref float scroll)
        {
            var res = new List<KeyValuePair<int, float>>();
            VisibleInto(heights, innerH, ref scroll, res);
            return res;
        }

        /// <summary>Same as <see cref="Visible"/> but fills a list the caller reuses (no allocation per frame).</summary>
        public static void VisibleInto(IList<float> heights, float innerH, ref float scroll, List<KeyValuePair<int, float>> res)
        {
            res.Clear();
            float total = 0f;
            for (int k = 0; k < heights.Count; k++) total += heights[k];
            scroll = Clamp(scroll, 0f, Math.Max(0f, total - innerH));
            float y = innerH + scroll;   // bottom of the newest entry
            for (int k = heights.Count - 1; k >= 0; k--)
            {
                float top = y - heights[k];
                if (top < innerH && y > 0f) res.Add(new KeyValuePair<int, float>(k, top));
                if (top <= 0f) break;
                y = top;
            }
            res.Reverse();
        }

        /// <summary>Background alpha: the setting while idle, near opaque under the mouse, a touch more while lines are fresh.</summary>
        public static float BackgroundAlpha(float setting, bool hover, float freshness)
        {
            if (hover) return 0.85f;
            float a = Clamp(setting, 0f, 1f);
            return Clamp(a + 0.15f * Clamp(freshness, 0f, 1f), 0f, 1f);
        }

        /// <summary>A new line's highlight, 1 when just added, fading to 0 over HighlightSec.</summary>
        public const double HighlightSec = 4.0;
        public static float Freshness(double age) { return age < 0 ? 1f : age >= HighlightSec ? 0f : (float)(1.0 - age / HighlightSec); }

        /// <summary>"74%" for a known prefill progress, "" for unknown (negative).</summary>
        public static string ProgressText(float p) { return p < 0f ? "" : ((int)Math.Floor(Clamp(p, 0f, 1f) * 100f + 0.0001f)) + "%"; }

        /// <summary>Animated dots, 0..3, three steps a second.</summary>
        public static int Dots(double now) { return (int)(Math.Max(0.0, now) * 3.0) % 4; }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
