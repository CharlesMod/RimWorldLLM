using System;
using System.Collections.Generic;
using System.Text;

namespace HermesVoice
{
    // Pure layout maths for the comic speech balloons. No Verse / UnityEngine references: this file is compiled
    // straight into Mod.Tests and into the offline preview tool, so the preview and the game share these numbers.
    // All lengths are UI pixels (RimWorld's scaled UI space) unless a name says "Src" (texture source pixels).

    /// <summary>What a line of speech looks like. Chosen by <see cref="BubbleLayout.ChooseKind"/>.</summary>
    public enum BubbleKind { Say, Shout, Whisper, Thought, Narrator }

    public enum TailSide { None, Bottom, Top, Left, Right }

    public struct BRect
    {
        public float X, Y, W, H;
        public BRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public float Right { get { return X + W; } }
        public float Bottom { get { return Y + H; } }
        public float CenterX { get { return X + W * 0.5f; } }
        public float CenterY { get { return Y + H * 0.5f; } }
        public bool Contains(float px, float py) { return px >= X && px <= Right && py >= Y && py <= Bottom; }
        public bool Overlaps(BRect o, float gap)
        {
            return X < o.Right + gap && o.X < Right + gap && Y < o.Bottom + gap && o.Y < Bottom + gap;
        }
    }

    /// <summary>
    /// Where the tail attaches and how it points. <see cref="Angle"/> is the clockwise screen rotation, in degrees,
    /// to apply to a tail texture that points straight down; the pivot is (<see cref="AnchorX"/>, <see cref="AnchorY"/>).
    /// </summary>
    public struct TailInfo
    {
        public TailSide Side;
        public float AnchorX, AnchorY;   // on the balloon's body edge
        public float Angle;              // degrees, clockwise, relative to "down"
        public float Length;             // anchor to tip
        public float DirX, DirY;         // unit vector anchor -> tip
        public bool Has { get { return Side != TailSide.None; } }
    }

    /// <summary>
    /// Texture and shape numbers. Duplicated from Mod/Textures/src/bubble_metrics.json (which make_bubbles.py and
    /// preview.py read); BubbleMetricsTests fails if the two drift apart.
    /// </summary>
    public static class BubbleMetrics
    {
        public const float Unit = 0.5f;        // UI px per source px
        public const float BorderSrc = 40f;    // 9-slice corner size in source px
        public const float InkSrc = 5f;
        public const float RimSrc = 3f;
        public const float TailW = 64f, TailH = 96f, TailStubSrc = 10f, TailTipYSrc = 82f;
        public const float DotSize = 64f;

        // Middle strip of each atlas (one edge period for tiled shapes), the distance from the balloon rect edge to the
        // body edge, and whether the middle strip repeats.
        public static float MidSrc(BubbleKind k)
        {
            switch (k) { case BubbleKind.Shout: return 40f; case BubbleKind.Thought: return 56f; case BubbleKind.Whisper: return 27f; default: return 16f; }
        }
        public static float MarginSrc(BubbleKind k)
        {
            switch (k) { case BubbleKind.Shout: return 20f; case BubbleKind.Thought: return 7f; default: return 6f; }
        }
        public static bool Tiled(BubbleKind k) { return k == BubbleKind.Shout || k == BubbleKind.Thought || k == BubbleKind.Whisper; }

        public static float Margin(BubbleKind k) { return MarginSrc(k) * Unit; }
        public static float Corner { get { return BorderSrc * Unit; } }    // 20 UI px
        public static float Period(BubbleKind k) { return MidSrc(k) * Unit; }
        public static float TailBaseHalf { get { return 10f; } }           // half the tail's base width, UI px
        public static float TailStub { get { return TailStubSrc * Unit; } }

        // Inner padding between the text block and the body edge (UI px).
        public static float PadX(BubbleKind k)
        {
            switch (k) { case BubbleKind.Shout: return 8f; case BubbleKind.Thought: return 12f; case BubbleKind.Narrator: return 8f; default: return 11f; }
        }
        public static float PadY(BubbleKind k)
        {
            switch (k) { case BubbleKind.Shout: return 6f; case BubbleKind.Thought: return 9f; case BubbleKind.Narrator: return 5f; default: return 6f; }
        }

        public const float MaxTextWidth = 230f;
        public const int MaxLines = 4;
        public const float BelowOffset = 56f;     // balloon top below the target when it does not fit above (clears the pawn)
        public const float HeadDrop = 18f;        // when the balloon is below the label point, aim at the head top this far down
        public const float TailGap = 16f;         // default distance from balloon bottom to the target
        public const float MaxTailAngle = 70f;    // max deviation from the edge normal
        public const float PopSeconds = 0.12f;
        public const float PopFrom = 0.85f;
        public const int FadeTicks = 30;          // fade-out over the last half second of the bubble's life
    }

    public static class BubbleLayout
    {
        // ------------------------------------------------------------------------------ variant choice
        /// <summary>
        /// Picks the look of a line from data the mod already has. An explicit <paramref name="kindField"/>
        /// ("normal", "shout", "whisper"; also "thought", "narrator"; optional on /say) wins, then text markers (*..* or (..) = thought,
        /// [..] = narrator), then the intent (insult = shout; confide, gossip = whisper), then the text itself
        /// (a shouted exclamation, mostly capitals, or "!" in combat). <paramref name="clean"/> is the text without markers.
        /// </summary>
        public static BubbleKind ChooseKind(string kindField, string intent, string text, bool combat, out string clean)
        {
            clean = text ?? "";
            string t = clean.Trim();
            string k = (kindField ?? "").Trim().ToLowerInvariant();
            if (k == "thought") return BubbleKind.Thought;
            if (k == "narrator" || k == "caption") return BubbleKind.Narrator;
            if (k == "shout") return BubbleKind.Shout;
            if (k == "whisper") return BubbleKind.Whisper;
            bool plain = k == "normal";    // said at an ordinary volume: no intent or punctuation guess
            if (t.Length > 2)
            {
                if (t[0] == '*' && t[t.Length - 1] == '*') { clean = t.Substring(1, t.Length - 2).Trim(); return BubbleKind.Thought; }
                if (t[0] == '(' && t[t.Length - 1] == ')') { clean = t.Substring(1, t.Length - 2).Trim(); return BubbleKind.Thought; }
                if (t[0] == '[' && t[t.Length - 1] == ']') { clean = t.Substring(1, t.Length - 2).Trim(); return BubbleKind.Narrator; }
            }
            if (plain) return BubbleKind.Say;
            string i = (intent ?? "none").Trim().ToLowerInvariant();
            if (i == "insult") return BubbleKind.Shout;
            if (i == "confide" || i == "gossip") return BubbleKind.Whisper;
            if (LooksShouted(t, combat)) return BubbleKind.Shout;
            return BubbleKind.Say;
        }

        public static bool LooksShouted(string t, bool combat)
        {
            if (string.IsNullOrEmpty(t)) return false;
            int bang = 0, upper = 0, letters = 0;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '!') bang++;
                if (char.IsLetter(c)) { letters++; if (char.IsUpper(c)) upper++; }
            }
            if (bang >= 2) return true;
            if (combat && bang >= 1) return true;
            return letters >= 6 && upper * 10 >= letters * 8;
        }

        /// <summary>Whisper and aside lines use the smaller game font.</summary>
        public static bool SmallText(BubbleKind k) { return k == BubbleKind.Whisper; }

        // ------------------------------------------------------------------------------ wrapping
        /// <summary>
        /// Word-wraps <paramref name="text"/> to at most <paramref name="maxLines"/> lines of at most
        /// <paramref name="maxWidth"/>, then narrows the measure so the lines come out similar in length (no long line
        /// plus an orphan word). Past maxLines the text is cut with an ellipsis. <paramref name="measure"/> is the width
        /// of a string in the same units as maxWidth.
        /// </summary>
        public static List<string> Wrap(string text, Func<string, float> measure, float maxWidth, int maxLines)
        {
            var words = new List<string>();
            foreach (var w in (text ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                BreakLongWord(w, measure, maxWidth, words);
            if (words.Count == 0) return new List<string> { "" };

            var lines = Greedy(words, measure, maxWidth);
            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                string last = lines[maxLines - 1];
                while (last.Length > 0 && measure(last + "…") > maxWidth)
                {
                    int sp = last.LastIndexOf(' ');
                    last = sp > 0 ? last.Substring(0, sp) : last.Substring(0, last.Length - 1);
                }
                lines[maxLines - 1] = last.TrimEnd(' ', '.', ',', ';', ':') + "…";
                return lines;
            }
            if (lines.Count == 1) return lines;

            // Balance: the narrowest width that still wraps to the same number of lines.
            float lo = 0f;
            foreach (var w in words) lo = Math.Max(lo, measure(w));
            float hi = maxWidth;
            for (int it = 0; it < 24 && hi - lo > 0.25f; it++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Greedy(words, measure, mid).Count <= lines.Count) hi = mid; else lo = mid;
            }
            return Greedy(words, measure, hi);
        }

        static List<string> Greedy(List<string> words, Func<string, float> measure, float width)
        {
            var lines = new List<string>();
            string cur = null;
            foreach (var w in words)
            {
                if (cur == null) { cur = w; continue; }
                string tryLine = cur + " " + w;
                if (measure(tryLine) <= width) cur = tryLine;
                else { lines.Add(cur); cur = w; }
            }
            if (cur != null) lines.Add(cur);
            return lines;
        }

        static void BreakLongWord(string w, Func<string, float> measure, float maxWidth, List<string> into)
        {
            if (measure(w) <= maxWidth) { into.Add(w); return; }
            var sb = new StringBuilder();
            for (int i = 0; i < w.Length; i++)
            {
                if (sb.Length > 0 && measure(sb.ToString() + w[i]) > maxWidth) { into.Add(sb.ToString()); sb.Length = 0; }
                sb.Append(w[i]);
            }
            if (sb.Length > 0) into.Add(sb.ToString());
        }

        // ------------------------------------------------------------------------------ size
        /// <summary>
        /// Balloon rect size from the text block. Short lines get extra width so the body reads as an ellipse; tiled shapes
        /// (burst, cloud, dashed) snap to whole edge periods so the repeated strip never cuts a spike or bump in half.
        /// </summary>
        public static void BalloonSize(BubbleKind kind, float textW, float textH, out float w, out float h)
        {
            float m = BubbleMetrics.Margin(kind);
            w = textW + 2f * (m + BubbleMetrics.PadX(kind));
            h = textH + 2f * (m + BubbleMetrics.PadY(kind));
            if (kind == BubbleKind.Say || kind == BubbleKind.Thought)
                w += Math.Min(h * 0.35f, 14f);
            float minSide = 2f * BubbleMetrics.Corner;
            if (!BubbleMetrics.Tiled(kind)) { w = Math.Max(w, 36f); h = Math.Max(h, 26f); return; }
            float p = BubbleMetrics.Period(kind);
            w = minSide + (float)Math.Ceiling(Math.Max(0f, w - minSide) / p) * p;
            h = minSide + (float)Math.Ceiling(Math.Max(0f, h - minSide) / p) * p;
        }

        /// <summary>Corner size actually used for a 9-slice: the full 20 px, shrunk on short balloons so they become pills.</summary>
        public static float CornerScale(BubbleKind kind, float w, float h)
        {
            if (BubbleMetrics.Tiled(kind)) return BubbleMetrics.Unit;
            float s = Math.Min(BubbleMetrics.Unit, Math.Min(w, h) / (2f * BubbleMetrics.BorderSrc));
            return Math.Max(s, 0.05f);
        }

        /// <summary>
        /// Where a balloon of the given size sits before collision handling: centred above the target, or, when the
        /// pawn is so near the top of the screen that it would not fit, below the pawn so it never covers the speaker.
        /// </summary>
        public static BRect DefaultRect(float w, float h, float tx, float ty, BRect bounds)
        {
            float above = ty - BubbleMetrics.TailGap - h;
            if (above >= bounds.Y) return new BRect(tx - w * 0.5f, above, w, h);
            float below = ty + BubbleMetrics.BelowOffset;
            if (below + h <= bounds.Bottom) return new BRect(tx - w * 0.5f, below, w, h);
            return new BRect(tx - w * 0.5f, above, w, h);
        }

        // ------------------------------------------------------------------------------ tail
        /// <summary>
        /// Picks the edge, anchor and angle of the tail from the balloon rect and the point to aim at (above the speaker's
        /// head). The tail leaves the edge the target lies toward (so it flips to the side or top when the balloon has been
        /// pushed past the pawn by a screen edge or by other balloons), slides along that edge toward the target, and
        /// tilts to reach it, up to <see cref="BubbleMetrics.MaxTailAngle"/> off the edge normal.
        /// </summary>
        public static TailInfo ChooseTail(BubbleKind kind, BRect balloon, float tx, float ty)
        {
            var t = new TailInfo { Side = TailSide.None };
            if (kind == BubbleKind.Narrator) return t;
            if (balloon.Y > ty) ty += BubbleMetrics.HeadDrop;   // balloon below the label point: aim at the head, not above it
            float m = BubbleMetrics.Margin(kind);
            // Inside the body: nothing to point at.
            if (new BRect(balloon.X + m, balloon.Y + m, balloon.W - 2f * m, balloon.H - 2f * m).Contains(tx, ty)) return t;

            float hw = Math.Max(balloon.W * 0.5f, 1f), hh = Math.Max(balloon.H * 0.5f, 1f);
            float dxn = (tx - balloon.CenterX) / hw, dyn = (ty - balloon.CenterY) / hh;
            bool vertical = Math.Abs(dyn) >= Math.Abs(dxn);
            float inset = BubbleMetrics.Margin(kind) + Math.Min(BubbleMetrics.Corner, Math.Min(balloon.W, balloon.H) * 0.5f) + BubbleMetrics.TailBaseHalf * 0.6f;
            float baseAngle;
            if (vertical)
            {
                bool bottom = dyn > 0;
                t.Side = bottom ? TailSide.Bottom : TailSide.Top;
                float lo = balloon.X + inset, hi = balloon.Right - inset;
                t.AnchorX = lo < hi ? Clamp(tx, lo, hi) : balloon.CenterX;
                t.AnchorY = bottom ? balloon.Bottom - m : balloon.Y + m;
                baseAngle = bottom ? 0f : 180f;
            }
            else
            {
                bool right = dxn > 0;
                t.Side = right ? TailSide.Right : TailSide.Left;
                float lo = balloon.Y + inset, hi = balloon.Bottom - inset;
                t.AnchorY = lo < hi ? Clamp(ty, lo, hi) : balloon.CenterY;
                t.AnchorX = right ? balloon.Right - m : balloon.X + m;
                baseAngle = right ? -90f : 90f;
            }
            float dx = tx - t.AnchorX, dy = ty - t.AnchorY;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 4f) { t.Side = TailSide.None; return t; }
            float toward = (float)(Math.Atan2(-dx, dy) * 180.0 / Math.PI);   // 0 = down, clockwise positive
            float dev = Wrap180(toward - baseAngle);
            dev = Clamp(dev, -BubbleMetrics.MaxTailAngle, BubbleMetrics.MaxTailAngle);
            t.Angle = baseAngle + dev;
            t.Length = Clamp(dist - 3f, 14f, 70f);
            double rad = (t.Angle) * Math.PI / 180.0;
            t.DirX = (float)-Math.Sin(rad); t.DirY = (float)Math.Cos(rad);
            return t;
        }

        /// <summary>The three trailing circles of a thought balloon: centres and fill diameters, nearest the balloon first.</summary>
        public static int ThoughtDots(TailInfo tail, float[] xs, float[] ys, float[] diam)
        {
            if (!tail.Has) return 0;
            float[] frac = { 0.3f, 0.62f, 0.92f };
            float[] d = { 10f, 7.5f, 5f };
            float len = Math.Max(tail.Length, 20f);
            for (int i = 0; i < 3 && i < xs.Length; i++)
            {
                xs[i] = tail.AnchorX + tail.DirX * len * frac[i];
                ys[i] = tail.AnchorY + tail.DirY * len * frac[i];
                diam[i] = d[i];
            }
            return 3;
        }

        // ------------------------------------------------------------------------------ placement
        /// <summary>
        /// Clamps every balloon into <paramref name="bounds"/> and pushes overlapping ones apart vertically
        /// (earlier entries keep their place; later ones stack upward, or go below when there is no room above). Rects are mutated in place.
        /// </summary>
        public static void Separate(List<BRect> rects, BRect bounds, float gap)
        {
            for (int pass = 0; pass < 6; pass++)
            {
                bool moved = false;
                for (int i = 0; i < rects.Count; i++)
                {
                    var r = ClampInto(rects[i], bounds);
                    for (int j = 0; j < i; j++)
                    {
                        var o = rects[j];
                        if (!r.Overlaps(o, gap)) continue;
                        float up = o.Y - gap - r.H;                // r above o
                        float down = o.Bottom + gap;               // r below o
                        bool fitUp = up >= bounds.Y, fitDown = down + r.H <= bounds.Bottom;
                        float ny;
                        if (fitUp) ny = up;                       // comics stack upward
                        else if (fitDown) ny = down;
                        else ny = Clamp(up, bounds.Y, bounds.Bottom - r.H);
                        r.Y = ny; moved = true;
                    }
                    rects[i] = ClampInto(r, bounds);
                }
                if (!moved) break;
            }
        }

        // ------------------------------------------------------------------------------ arrangement (no tail crosses a balloon)
        /// <summary>True when the tail (its centre line and both base edges, anchor to tip) passes through <paramref name="r"/>.</summary>
        public static bool TailCrossesRect(TailInfo t, BRect r)
        {
            if (!t.Has) return false;
            float tipX = t.AnchorX + t.DirX * t.Length, tipY = t.AnchorY + t.DirY * t.Length;
            float px = -t.DirY * BubbleMetrics.TailBaseHalf * 0.8f, py = t.DirX * BubbleMetrics.TailBaseHalf * 0.8f;
            return SegmentHitsRect(t.AnchorX, t.AnchorY, tipX, tipY, r)
                || SegmentHitsRect(t.AnchorX + px, t.AnchorY + py, tipX, tipY, r)
                || SegmentHitsRect(t.AnchorX - px, t.AnchorY - py, tipX, tipY, r);
        }

        public static bool SegmentHitsRect(float x0, float y0, float x1, float y1, BRect r)
        {
            float t0 = 0f, t1 = 1f, dx = x1 - x0, dy = y1 - y0;
            return Clip(-dx, x0 - r.X, ref t0, ref t1) && Clip(dx, r.Right - x0, ref t0, ref t1)
                && Clip(-dy, y0 - r.Y, ref t0, ref t1) && Clip(dy, r.Bottom - y0, ref t0, ref t1);
        }

        static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Math.Abs(p) < 1e-6f) return q >= 0f;
            float t = q / p;
            if (p < 0f) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }

        static bool TailHitsOthers(TailInfo t, List<BRect> rects, int self)
        {
            for (int j = 0; j < rects.Count; j++)
                if (j != self && TailCrossesRect(t, rects[j])) return true;
            return false;
        }

        static bool FreeSpot(BRect r, List<BRect> rects, int self, int upTo, float gap)
        {
            for (int j = 0; j < upTo; j++)
                if (j != self && r.Overlaps(rects[j], gap)) return false;
            return true;
        }

        // Candidate x positions beside every other balloon (left of it, right of it), nearest the wanted centre first.
        static List<float> SideXs(List<BRect> rects, int self, int upTo, float w, float wantCx, float gap)
        {
            var xs = new List<float>();
            for (int j = 0; j < upTo; j++)
            {
                if (j == self) continue;
                xs.Add(rects[j].X - gap - w); xs.Add(rects[j].Right + gap);
            }
            xs.Sort((a, b) => Math.Abs(a + w * 0.5f - wantCx).CompareTo(Math.Abs(b + w * 0.5f - wantCx)));
            return xs;
        }

        /// <summary>
        /// Places all balloons and their tails. Speakers whose balloons would collide sit side by side above their
        /// own pawns; only when there is no room beside them do they stack upward. Afterwards no tail passes through
        /// another balloon: a crossing tail first moves its balloon sideways (beside the one in the way), and when
        /// even that is not possible the tail is shortened until it clears. Rects are mutated; tails are written.
        /// </summary>
        public static void Arrange(List<BRect> rects, IList<float> tx, IList<float> ty, IList<BubbleKind> kinds,
                                   BRect bounds, float gap, TailInfo[] tails)
        {
            int n = rects.Count;
            // 1. side by side: a later balloon that collides moves sideways, as near its own pawn as it can.
            for (int i = 1; i < n; i++)
            {
                if (FreeSpot(rects[i], rects, i, i, gap)) continue;
                var r = rects[i];
                foreach (float x in SideXs(rects, i, i, r.W, tx[i], gap))
                {
                    var c = new BRect(x, r.Y, r.W, r.H);
                    if (c.X < bounds.X || c.Right > bounds.Right) continue;
                    if (!FreeSpot(c, rects, i, i, gap)) continue;
                    rects[i] = c; break;
                }
            }
            // 2. whatever still collides stacks upward (or below).
            Separate(rects, bounds, gap);
            for (int i = 0; i < n; i++) tails[i] = ChooseTail(kinds[i], rects[i], tx[i], ty[i]);
            // 3. no tail through another balloon.
            for (int pass = 0; pass < 3; pass++)
            {
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    if (!TailHitsOthers(tails[i], rects, i)) continue;
                    any = true;
                    var cur = rects[i];
                    float homeY = DefaultRect(cur.W, cur.H, tx[i], ty[i], bounds).Y;
                    bool fixedIt = false;
                    foreach (float y in new[] { homeY, cur.Y })
                    {
                        foreach (float x in SideXs(rects, i, n, cur.W, tx[i], gap))
                        {
                            var c = new BRect(x, y, cur.W, cur.H);
                            if (c.X < bounds.X || c.Right > bounds.Right || c.Y < bounds.Y || c.Bottom > bounds.Bottom) continue;
                            if (!FreeSpot(c, rects, i, n, gap)) continue;
                            var t = ChooseTail(kinds[i], c, tx[i], ty[i]);
                            if (TailHitsOthers(t, rects, i)) continue;
                            rects[i] = c; tails[i] = t; fixedIt = true; break;
                        }
                        if (fixedIt) break;
                    }
                    if (fixedIt) continue;
                    var tail = tails[i];                       // last resort: shorten until it clears
                    while (tail.Length > 8f && TailCrossesAny(tail, rects, i)) tail.Length -= 4f;
                    if (TailCrossesAny(tail, rects, i)) tail.Side = TailSide.None;
                    tails[i] = tail;
                }
                if (!any) break;
            }
            for (int i = 0; i < n; i++)                        // guarantee: shorten or drop what still crosses
            {
                var tail = tails[i];
                while (tail.Length > 8f && TailCrossesAny(tail, rects, i)) tail.Length -= 4f;
                if (TailCrossesAny(tail, rects, i)) tail.Side = TailSide.None;
                tails[i] = tail;
            }
        }

        static bool TailCrossesAny(TailInfo t, List<BRect> rects, int self) { return TailHitsOthers(t, rects, self); }

        public static BRect ClampInto(BRect r, BRect bounds)
        {
            r.X = Clamp(r.X, bounds.X, Math.Max(bounds.X, bounds.Right - r.W));
            r.Y = Clamp(r.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - r.H));
            return r;
        }

        // ------------------------------------------------------------------------------ motion
        /// <summary>Pop-in scale: 85% to 100% with a slight overshoot over <see cref="BubbleMetrics.PopSeconds"/>.</summary>
        public static float PopScale(float seconds)
        {
            if (seconds >= BubbleMetrics.PopSeconds) return 1f;
            if (seconds <= 0f) return BubbleMetrics.PopFrom;
            float x = seconds / BubbleMetrics.PopSeconds;
            const float c1 = 3.2f, c3 = c1 + 1f;                 // easeOutBack, overshoots by ~ 13% of the 15% rise
            float e = 1f + c3 * (float)Math.Pow(x - 1f, 3) + c1 * (float)Math.Pow(x - 1f, 2);
            return BubbleMetrics.PopFrom + (1f - BubbleMetrics.PopFrom) * e;
        }

        /// <summary>Fade-out alpha from the ticks left in the bubble's life.</summary>
        public static float FadeAlpha(int ticksLeft)
        {
            if (ticksLeft >= BubbleMetrics.FadeTicks) return 1f;
            if (ticksLeft <= 0) return 0f;
            return (float)ticksLeft / BubbleMetrics.FadeTicks;
        }

        // ------------------------------------------------------------------------------ helpers
        public static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static float Wrap180(float a) { a %= 360f; if (a > 180f) a -= 360f; if (a < -180f) a += 360f; return a; }
    }
}
