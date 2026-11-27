// Python number formatting and arithmetic semantics. All invariant-culture.
// Repr(double)        str()/repr() of a float ("1.0", "0.1", "1e-05", "1e+16", "inf", "nan")
// Fixed(double, n)    f"{x:.nf}" (exact decimal value, ties-to-even like CPython)
// Round(double, n)    round(x, n) -> double (correctly rounded, half-even on exact ties)
// RoundInt(double)    round(x) -> long (banker's rounding)
// FloorDiv / Mod      Python // and % for integers (floor semantics, result of Mod has the divisor's sign)
// FloorDiv(double..)  Python // and % for floats
using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RimWorldLLM.Mind.Py
{
    public static class PyFmt
    {
        public static string Repr(double x)
        {
            if (double.IsNaN(x)) return "nan";
            if (double.IsPositiveInfinity(x)) return "inf";
            if (double.IsNegativeInfinity(x)) return "-inf";
            if (x == 0) return (1 / x < 0) ? "-0.0" : "0.0";
            // shortest round-trip digits
            string s = x.ToString("R", CultureInfo.InvariantCulture);
            bool neg = s.StartsWith("-");
            if (neg) s = s.Substring(1);
            string mant = s; int exp10 = 0;
            int e = s.IndexOfAny(new[] { 'E', 'e' });
            if (e >= 0) { mant = s.Substring(0, e); exp10 = int.Parse(s.Substring(e + 1), CultureInfo.InvariantCulture); }
            int dot = mant.IndexOf('.');
            string intPart = dot >= 0 ? mant.Substring(0, dot) : mant;
            string frac = dot >= 0 ? mant.Substring(dot + 1) : "";
            string digits = (intPart + frac);
            int pointPos = intPart.Length + exp10; // decimal point position relative to digits start
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; }
            digits = digits.Substring(lead); pointPos -= lead;
            digits = digits.TrimEnd('0'); if (digits.Length == 0) digits = "0";
            int decpt = pointPos; // value = 0.DIGITS * 10^decpt
            var sb = new StringBuilder();
            if (neg) sb.Append('-');
            int sciExp = decpt - 1;
            if (sciExp < -4 || sciExp >= 16)
            {
                sb.Append(digits[0]);
                if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
                sb.Append('e'); sb.Append(sciExp < 0 ? '-' : '+');
                int ae = Math.Abs(sciExp);
                sb.Append(ae < 10 ? "0" + ae : ae.ToString(CultureInfo.InvariantCulture));
            }
            else if (decpt <= 0)
            {
                sb.Append("0."); sb.Append('0', -decpt); sb.Append(digits);
            }
            else if (decpt >= digits.Length)
            {
                sb.Append(digits); sb.Append('0', decpt - digits.Length); sb.Append(".0");
            }
            else
            {
                sb.Append(digits, 0, decpt); sb.Append('.'); sb.Append(digits, decpt, digits.Length - decpt);
            }
            return sb.ToString();
        }

        /// <summary>Exact decomposition: |x| = num / 2^shift (shift >= 0) or num * 2^-shift.</summary>
        private static void Decompose(double x, out BigInteger num, out int exp2)
        {
            long bits = BitConverter.DoubleToInt64Bits(x);
            int e = (int)((bits >> 52) & 0x7FF);
            long m = bits & 0xFFFFFFFFFFFFFL;
            if (e == 0) e = 1; else m |= 1L << 52;
            num = m; exp2 = e - 1075; // value = num * 2^exp2
        }

        /// <summary>Rounds |x| * 10^n to an integer, half-even, exactly.</summary>
        private static BigInteger ScaledRound(double x, int n)
        {
            Decompose(Math.Abs(x), out var num, out int e2);
            BigInteger numer = num, denom = BigInteger.One;
            if (e2 >= 0) numer <<= e2; else denom <<= -e2;
            if (n >= 0) numer *= BigInteger.Pow(10, n); else denom *= BigInteger.Pow(10, -n);
            BigInteger q = BigInteger.DivRem(numer, denom, out BigInteger rem);
            BigInteger twice = rem * 2;
            int c = twice.CompareTo(denom);
            if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
            return q;
        }

        /// <summary>f"{x:.nf}"</summary>
        public static string Fixed(double x, int n)
        {
            if (double.IsNaN(x)) return "nan";
            if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
            BigInteger q = ScaledRound(x, n);
            string s = q.ToString(CultureInfo.InvariantCulture);
            if (n > 0)
            {
                if (s.Length <= n) s = new string('0', n - s.Length + 1) + s;
                s = s.Substring(0, s.Length - n) + "." + s.Substring(s.Length - n);
            }
            bool neg = x < 0 || (x == 0 && 1 / x < 0);
            return neg ? "-" + s : s;
        }

        /// <summary>round(x, n) for a float: correctly rounded, half-even on exact ties.</summary>
        public static double Round(double x, int n)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
            string s = Fixed(x, Math.Max(n, 0));
            if (n < 0)
            {
                BigInteger q = ScaledRound(x, n);
                double v = (double)(q * BigInteger.Pow(10, -n));
                return x < 0 ? -v : v;
            }
            return double.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>round(x) -> int (banker's rounding).</summary>
        public static long RoundInt(double x) { return (long)Math.Round(x, MidpointRounding.ToEven); }

        public static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        public static long Mod(long a, long b)
        {
            long r = a % b;
            if (r != 0 && ((r < 0) != (b < 0))) r += b;
            return r;
        }

        public static double FloorDiv(double a, double b) { return Math.Floor(a / b); }

        public static double Mod(double a, double b)
        {
            double r = a % b;
            if (r != 0 && ((r < 0) != (b < 0))) r += b;
            return r;
        }
    }
}
