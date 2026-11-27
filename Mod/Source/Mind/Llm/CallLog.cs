// Port of agent/rimworldllm_agent/core/calllog.py: per-call stats and the JSONL call log.
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public sealed class CallStats
    {
        public string Kind = "";            // line | intent | reflect | voice_card | narrator | probe ...
        public int? Slot;
        public int? Actor;
        public long PromptN;
        public long CacheN;
        public double PromptMs;
        public long PredictedN;
        public double PredictedMs;
        public double WallS;
        public double? TtftS;
        public bool Ok = true;
        public string Error = "";
        public double Ts = NowS();

        /// <summary>time.time(): seconds since the Unix epoch.</summary>
        public static double NowS() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public double RawPrefillTps => PromptMs > 0 ? PromptN / (PromptMs / 1000) : 0.0;

        /// <summary>(prompt_n + cache_n) / prompt_s. Equal to raw means a cache miss.</summary>
        public double EffectivePrefillTps => PromptMs > 0 ? (PromptN + CacheN) / (PromptMs / 1000) : 0.0;

        /// <summary>asdict order, then the two derived rates rounded to one place (Python round, half-even on the exact value).</summary>
        public JObject ToDict()
        {
            var d = new JObject
            {
                ["kind"] = Kind,
                ["slot"] = Slot.HasValue ? new JValue(Slot.Value) : JValue.CreateNull(),
                ["actor"] = Actor.HasValue ? new JValue(Actor.Value) : JValue.CreateNull(),
                ["prompt_n"] = PromptN,
                ["cache_n"] = CacheN,
                ["prompt_ms"] = PromptMs,
                ["predicted_n"] = PredictedN,
                ["predicted_ms"] = PredictedMs,
                ["wall_s"] = WallS,
                ["ttft_s"] = TtftS.HasValue ? new JValue(TtftS.Value) : JValue.CreateNull(),
                ["ok"] = Ok,
                ["error"] = Error,
                ["ts"] = Ts,
            };
            d["raw_prefill_tps"] = PyRound.Round(RawPrefillTps, 1);
            d["effective_prefill_tps"] = PyRound.Round(EffectivePrefillTps, 1);
            return d;
        }
    }

    /// <summary>Python's round(x, ndigits) for finite doubles: the exact binary value rounded half to even.</summary>
    public static class PyRound
    {
        public static double Round(double x, int ndigits)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || x == 0) return x;
            long bits = BitConverter.DoubleToInt64Bits(x);
            bool neg = bits < 0;
            int exp = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            BigInteger m; int e;
            if (exp == 0) { m = frac; e = -1074; } else { m = frac | (1L << 52); e = exp - 1075; }
            // value = m * 2^e ; scaled = value * 10^ndigits as num/den
            BigInteger num = m, den = BigInteger.One;
            if (e >= 0) num <<= e; else den <<= -e;
            if (ndigits >= 0) num *= BigInteger.Pow(10, ndigits); else den *= BigInteger.Pow(10, -ndigits);
            BigInteger q = BigInteger.DivRem(num, den, out BigInteger rem);
            BigInteger twice = rem * 2;
            int c = twice.CompareTo(den);
            if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
            // back to a double via the decimal string, which parses correctly rounded
            string s = q.ToString() + "e" + (-ndigits).ToString();
            double r = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            return neg ? -r : r;
        }
    }

    public sealed class CallLog
    {
        public const long MaxBytesDefault = 20L * 1024 * 1024;   // rotate calls.jsonl before it would pass this
        public const int KeepDefault = 3;                        // calls.jsonl.1 .. .3 are kept; the oldest is dropped

        public readonly string Path;
        public readonly long MaxBytes;
        public readonly int Keep;
        /// <summary>In-memory tail (last 500) for the debug view and tests. Guarded by the log's lock; copy with Snapshot().</summary>
        public readonly List<JObject> Entries = new List<JObject>();
        private readonly object _lock = new object();

        public CallLog(string path, long maxBytes = MaxBytesDefault, int keep = KeepDefault)
        {
            Path = string.IsNullOrEmpty(path) ? null : path;
            MaxBytes = maxBytes; Keep = keep;
            if (Path != null)
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
        }

        public List<JObject> Snapshot() { lock (_lock) return new List<JObject>(Entries); }

        /// <summary>path -> path.1 -> ... -> path.keep; the oldest is dropped. Best effort.</summary>
        public static void RotateFile(string path, int keep)
        {
            try
            {
                string Named(int i) => path + "." + i;
                if (File.Exists(Named(keep))) File.Delete(Named(keep));
                for (int i = keep - 1; i > 0; i--)
                    if (File.Exists(Named(i))) File.Move(Named(i), Named(i + 1), true);
                if (keep > 0) File.Move(path, Named(1), true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>stats.to_dict() | extra; appended to memory and (when a path is set) to the JSONL file.</summary>
        public JObject Record(CallStats stats, JObject extra = null)
        {
            JObject d = stats.ToDict();
            if (extra != null) foreach (var p in extra.Properties()) d[p.Name] = p.Value.DeepClone();
            lock (_lock)
            {
                Entries.Add(d);
                if (Entries.Count > 500) Entries.RemoveRange(0, Entries.Count - 500);
                if (Path != null)
                {
                    byte[] line = new UTF8Encoding(false).GetBytes(PyJson.Dumps(d, ensureAscii: false) + "\n");
                    long size = 0;
                    try { var fi = new FileInfo(Path); if (fi.Exists) size = fi.Length; } catch (IOException) { }
                    if (size > 0 && size + line.Length > MaxBytes) RotateFile(Path, Keep);
                    using var f = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    f.Write(line, 0, line.Length);
                }
            }
            return d;
        }
    }
}
