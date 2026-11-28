using System;
using System.Collections.Generic;
using System.Text;

namespace RimWorldLLM.Bridge
{
    /// <summary>QR error-correction level.</summary>
    public enum QrEcc { Low = 0, Medium = 1, Quartile = 2, High = 3 }

    /// <summary>
    /// A small QR Code (model 2) encoder: byte mode, UTF-8, versions 1..40, Reed-Solomon, all 8 masks with the
    /// standard penalty rules. Verse-free and Unity-free. Structure follows the spirit of Nayuki's MIT-licensed
    /// QR Code generator (https://www.nayuki.io/page/qr-code-generator-library); the code here is an independent
    /// rewrite, with the finder-like penalty rule (N3) done as in the ISO text: 1:1:3:1:1 pattern with 4 light modules (or the symbol edge) on either side, 40 per occurrence.
    /// </summary>
    public sealed class QrCode
    {
        /// <summary>Modules per side: 17 + 4 * Version.</summary>
        public int Size;
        public int Version;
        public int Mask;
        public QrEcc Ecc;
        bool[] modules;

        /// <summary>True for a dark module. x is the column, y the row, 0,0 top-left. Out of range is light.</summary>
        public bool Get(int x, int y)
        {
            return x >= 0 && x < Size && y >= 0 && y < Size && modules[y * Size + x];
        }

        public static QrCode Encode(string text, QrEcc ecc = QrEcc.Medium) { return Encode(text, ecc, 0, -1); }

        /// <summary>version 0 = smallest that fits (else forced 1..40, throws if too small); mask -1 = best by penalty, else 0..7.</summary>
        public static QrCode Encode(string text, QrEcc ecc, int version, int mask)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (version < 0 || version > 40) throw new ArgumentException("version must be 0 (auto) or 1..40");
            if (mask < -1 || mask > 7) throw new ArgumentException("mask must be -1 (auto) or 0..7");
            byte[] data = new UTF8Encoding(false).GetBytes(text);
            int lo = version == 0 ? 1 : version, hi = version == 0 ? 40 : version;
            int ver = 0;
            for (int v = lo; v <= hi; v++)
            {
                int cap = DataCodewords(v, ecc) * 8;
                int need = 4 + (v <= 9 ? 8 : 16) + data.Length * 8;
                if (need <= cap) { ver = v; break; }
            }
            if (ver == 0) throw new ArgumentException("text does not fit in a QR code at this error-correction level");

            var bits = new List<bool>();
            Append(bits, 4, 4);
            Append(bits, data.Length, ver <= 9 ? 8 : 16);
            foreach (byte b in data) Append(bits, b, 8);
            int capBits = DataCodewords(ver, ecc) * 8;
            for (int i = 0; i < 4 && bits.Count < capBits; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            var cw = new byte[capBits / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) cw[i >> 3] |= (byte)(0x80 >> (i & 7));
            for (int i = bits.Count / 8, pad = 0; i < cw.Length; i++, pad ^= 1) cw[i] = pad == 0 ? (byte)0xEC : (byte)0x11;

            return new QrCode(ver, ecc, Interleave(cw, ver, ecc), mask);
        }

        /// <summary>
        /// RGBA32 pixels of the code with a quiet zone, each module scale x scale pixels. Dark = 0,0,0,255; light =
        /// 255,255,255,255. Rows run BOTTOM-UP (row 0 of the array is the bottom row of the picture), the order
        /// Unity's Texture2D.SetPixels32 expects. Width = height = (Size + 2*quiet) * scale; 4 bytes per pixel.
        /// </summary>
        public static byte[] ToRgba(QrCode q, int scale, int quiet = 4)
        {
            if (q == null) throw new ArgumentNullException("q");
            if (scale < 1) throw new ArgumentException("scale must be >= 1");
            if (quiet < 0) throw new ArgumentException("quiet must be >= 0");
            int dim = (q.Size + 2 * quiet) * scale;
            var px = new byte[dim * dim * 4];
            for (int row = 0; row < dim; row++)
            {
                int top = dim - 1 - row;          // picture row counted from the top
                int my = top / scale - quiet;
                for (int col = 0; col < dim; col++)
                {
                    int mx = col / scale - quiet;
                    byte v = q.Get(mx, my) ? (byte)0 : (byte)255;
                    int o = (row * dim + col) * 4;
                    px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = 255;
                }
            }
            return px;
        }

        // ---------------------------------------------------------------- construction

        bool[] isFn;

        QrCode(int version, QrEcc ecc, byte[] codewords, int mask)
        {
            Version = version; Ecc = ecc; Size = version * 4 + 17;
            modules = new bool[Size * Size];
            isFn = new bool[Size * Size];
            DrawFunctionPatterns();
            DrawCodewords(codewords);
            if (mask < 0)
            {
                int best = int.MaxValue;
                for (int m = 0; m < 8; m++)
                {
                    ApplyMask(m); DrawFormat(m);
                    int p = Penalty();
                    if (p < best) { best = p; mask = m; }
                    ApplyMask(m);   // undo
                }
            }
            ApplyMask(mask); DrawFormat(mask);
            Mask = mask;
            isFn = null;
        }

        void SetFn(int x, int y, bool dark)
        {
            if (x < 0 || y < 0 || x >= Size || y >= Size) return;
            modules[y * Size + x] = dark; isFn[y * Size + x] = true;
        }

        void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++) { SetFn(6, i, i % 2 == 0); SetFn(i, 6, i % 2 == 0); }
            DrawFinder(3, 3); DrawFinder(Size - 4, 3); DrawFinder(3, Size - 4);
            int[] al = AlignPositions(Version);
            for (int i = 0; i < al.Length; i++)
                for (int j = 0; j < al.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == al.Length - 1) || (i == al.Length - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            SetFn(al[i] + dx, al[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            DrawFormat(0);
            if (Version >= 7)
            {
                int rem = Version;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = Version << 12 | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool b = ((bits >> i) & 1) != 0;
                    int a = Size - 11 + i % 3, c = i / 3;
                    SetFn(a, c, b); SetFn(c, a, b);
                }
            }
        }

        void DrawFinder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    SetFn(cx + dx, cy + dy, d != 2 && d != 4);
                }
        }

        static readonly int[] FormatBits = { 1, 0, 3, 2 };   // L, M, Q, H

        void DrawFormat(int mask)
        {
            int data = FormatBits[(int)Ecc] << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            for (int i = 0; i <= 5; i++) SetFn(8, i, Bit(bits, i));
            SetFn(8, 7, Bit(bits, 6)); SetFn(8, 8, Bit(bits, 7)); SetFn(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetFn(14 - i, 8, Bit(bits, i));
            for (int i = 0; i < 8; i++) SetFn(Size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetFn(8, Size - 15 + i, Bit(bits, i));
            SetFn(8, Size - 8, true);
        }

        static bool Bit(int v, int i) { return ((v >> i) & 1) != 0; }

        void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool up = ((right + 1) & 2) == 0;
                        int y = up ? Size - 1 - vert : vert;
                        if (!isFn[y * Size + x] && i < data.Length * 8)
                        {
                            modules[y * Size + x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        void ApplyMask(int m)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool inv;
                    switch (m)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv && !isFn[y * Size + x]) modules[y * Size + x] ^= true;
                }
        }

        int Penalty()
        {
            int n = Size, total = 0;
            // N1: runs of 5+ in rows and columns
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < n; a++)
                {
                    int run = 1;
                    for (int b = 1; b < n; b++)
                    {
                        bool cur = pass == 0 ? modules[a * n + b] : modules[b * n + a];
                        bool prev = pass == 0 ? modules[a * n + b - 1] : modules[(b - 1) * n + a];
                        if (cur == prev) run++;
                        else { if (run >= 5) total += 3 + run - 5; run = 1; }
                    }
                    if (run >= 5) total += 3 + run - 5;
                }
            // N2: 2x2 blocks
            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                {
                    bool c = modules[y * n + x];
                    if (c == modules[y * n + x + 1] && c == modules[(y + 1) * n + x] && c == modules[(y + 1) * n + x + 1]) total += 3;
                }
            // N3: 1011101 with a light run of 4 (or the symbol edge) on at least one side; each occurrence counts 40 once
            var line = new bool[n];
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < n; a++)
                {
                    for (int b = 0; b < n; b++) line[b] = pass == 0 ? modules[a * n + b] : modules[b * n + a];
                    total += FinderLike(line);
                }
            // N4: balance
            int dark = 0;
            for (int i = 0; i < modules.Length; i++) if (modules[i]) dark++;
            int all = n * n;
            int k = (Math.Abs(dark * 20 - all * 10) + all - 1) / all - 1;
            total += k * 10;
            return total;
        }

        static readonly bool[] Core = { true, false, true, true, true, false, true };

        static int FinderLike(bool[] line)
        {
            int n = line.Length, count = 0, i = 0;
            while (i + 7 <= n)
            {
                bool hit = true;
                for (int k = 0; k < 7 && hit; k++) hit = line[i + k] == Core[k];
                if (!hit) { i++; continue; }
                bool leftLight = true, rightLight = true;
                for (int k = Math.Max(0, i - 4); k < i; k++) if (line[k]) leftLight = false;
                for (int k = i + 7; k < Math.Min(n, i + 11); k++) if (line[k]) rightLight = false;
                if (leftLight || rightLight) { count += 40; i += 7; } else i += 4;
            }
            return count;
        }

        // ---------------------------------------------------------------- tables and Reed-Solomon

        static void Append(List<bool> bits, int val, int len)
        {
            for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0);
        }

        static int[] AlignPositions(int ver)
        {
            if (ver == 1) return new int[0];
            int num = ver / 7 + 2;
            int step = ver == 32 ? 26 : (ver * 4 + num * 2 + 1) / (num * 2 - 2) * 2;
            var r = new int[num];
            r[0] = 6;
            for (int i = num - 1, pos = ver * 4 + 10; i >= 1; i--, pos -= step) r[i] = pos;
            return r;
        }

        static int RawModules(int ver)
        {
            int r = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int num = ver / 7 + 2;
                r -= (25 * num - 10) * num - 55;
                if (ver >= 7) r -= 36;
            }
            return r;
        }

        static readonly int[][] EccPerBlock =
        {
            new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
            new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        };

        static readonly int[][] NumBlocks =
        {
            new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
            new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
            new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
        };

        static int DataCodewords(int ver, QrEcc ecc)
        {
            int e = (int)ecc;
            return RawModules(ver) / 8 - EccPerBlock[e][ver] * NumBlocks[e][ver];
        }

        static byte[] Interleave(byte[] data, int ver, QrEcc ecc)
        {
            int e = (int)ecc;
            int blocks = NumBlocks[e][ver], eccLen = EccPerBlock[e][ver];
            int raw = RawModules(ver) / 8;
            int shortBlocks = blocks - raw % blocks;
            int shortLen = raw / blocks;
            var gen = Divisor(eccLen);
            var dataParts = new byte[blocks][];
            var eccParts = new byte[blocks][];
            for (int i = 0, k = 0; i < blocks; i++)
            {
                int len = shortLen - eccLen + (i < shortBlocks ? 0 : 1);
                dataParts[i] = new byte[len];
                Array.Copy(data, k, dataParts[i], 0, len);
                k += len;
                eccParts[i] = Remainder(dataParts[i], gen);
            }
            var result = new byte[raw];
            int p = 0;
            for (int i = 0; i <= shortLen - eccLen; i++)
                for (int j = 0; j < blocks; j++)
                    if (i < dataParts[j].Length) result[p++] = dataParts[j][i];
            for (int i = 0; i < eccLen; i++)
                for (int j = 0; j < blocks; j++) result[p++] = eccParts[j][i];
            return result;
        }

        static byte Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        static byte[] Divisor(int degree)
        {
            var r = new byte[degree];
            r[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    r[j] = Mul(r[j], root);
                    if (j + 1 < degree) r[j] ^= r[j + 1];
                }
                root = Mul(root, 0x02);
            }
            return r;
        }

        static byte[] Remainder(byte[] data, byte[] gen)
        {
            var r = new byte[gen.Length];
            foreach (byte b in data)
            {
                int f = b ^ r[0];
                Array.Copy(r, 1, r, 0, r.Length - 1);
                r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= Mul(gen[i], f);
            }
            return r;
        }
    }
}
