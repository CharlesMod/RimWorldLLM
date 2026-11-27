using System;
using System.Collections.Generic;

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
        }

        public struct Result
        {
            public ChatRect Box;      // the log box (always drawn)
            public ChatRect Bar;      // the input bar slot under it (drawn while typing)
            public ChatRect Whole;    // box + bar, the mouse-over area
            public int Lines;         // visible lines that fit
        }

        public static Result Compute(Input i)
        {
            float lineH = i.LineH > 1f ? i.LineH : 22f;
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
            return new Result { Box = box, Bar = bar, Whole = new ChatRect(x, box.Y, w, bar.Bottom - box.Y), Lines = lines };
        }

        /// <summary>
        /// Which entries to draw in a box of the given inner height, newest at the bottom, scrolled up by scroll pixels.
        /// heights: each entry's wrapped height, oldest first. Returns (index, y) pairs, y relative to the box's inner top,
        /// may be negative for an entry cut at the top (draw it clipped). The scroll is clamped into range.
        /// </summary>
        public static List<KeyValuePair<int, float>> Visible(IList<float> heights, float innerH, ref float scroll)
        {
            var res = new List<KeyValuePair<int, float>>();
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
            return res;
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
