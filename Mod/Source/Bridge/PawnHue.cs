using System;

namespace RimWorldLLM.Bridge
{
    /// <summary>Stable per-id colours: the same id always gets the same hue, and nearby ids land far apart.</summary>
    public static class PawnHue
    {
        /// <summary>Hue in [0,1) from an id (golden-ratio spread of a mixed hash).</summary>
        public static float HueFor(int id)
        {
            unchecked
            {
                uint x = (uint)id;
                x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
                double h = (x / 4294967296.0) ;
                return (float)(h - Math.Floor(h));
            }
        }

        /// <summary>HSV (all 0..1) to RGB (0..1).</summary>
        public static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h = h - (float)Math.Floor(h);
            float c = v * s;
            float hp = h * 6f;
            float x = c * (1f - Math.Abs(hp % 2f - 1f));
            float r1 = 0, g1 = 0, b1 = 0;
            switch ((int)hp)
            {
                case 0: r1 = c; g1 = x; break;
                case 1: r1 = x; g1 = c; break;
                case 2: g1 = c; b1 = x; break;
                case 3: g1 = x; b1 = c; break;
                case 4: r1 = x; b1 = c; break;
                default: r1 = c; b1 = x; break;
            }
            float m = v - c;
            r = r1 + m; g = g1 + m; b = b1 + m;
        }

        /// <summary>Relative luminance (sRGB weights, no gamma; enough for a readability check).</summary>
        public static float Luma(float r, float g, float b) { return 0.2126f * r + 0.7152f * g + 0.0722f * b; }
    }
}
