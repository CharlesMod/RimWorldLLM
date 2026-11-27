// agent/rimworldllm_agent/core/config.py. The field list (MindConfig.Fields.cs) is generated from the Python by
// csmind/llm/gen_config.py; this file holds the logic: sources and precedence, data dir defaults on each OS, the
// legacy-folder migration, bridge.json's llm block, and the creative_*_for lookups as an explicit map.
//
// Precedence for a setting (highest first): environment (the RIMWORLDLLM_* variables in EnvMap), then the config
// file (TOML when the path ends in ".toml", else JSON), then, for the llm endpoint only, bridge.json's "llm" block
// (when the file and the environment left llm.base_url empty), then the defaults. RIMWORLDLLM_DATA_DIR is NOT an
// override of a file's data_dir: it only feeds the default (Python behaviour, kept).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    /// <summary>pydantic ValidationError (a ValueError): a config value of the wrong type.</summary>
    public sealed class ConfigError : Exception
    {
        public ConfigError(string message) : base(message) { }
    }

    /// <summary>The slice of the process environment the config reads (os.environ, os.name, Path.home()),
    /// injectable so tests never touch the real one.</summary>
    public sealed class MindEnv
    {
        private readonly Func<string, string> _get;
        public bool IsWindows { get; }
        public string Home { get; }

        public MindEnv(Func<string, string> get, bool isWindows, string home)
        {
            _get = get; IsWindows = isWindows; Home = home;
        }

        public MindEnv(IDictionary<string, string> vars, bool isWindows, string home)
            : this(k => vars != null && vars.TryGetValue(k, out var v) ? v : null, isWindows, home) { }

        public static MindEnv Process => new MindEnv(Environment.GetEnvironmentVariable,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        /// <summary>null when the variable is not set (an empty value is "set" and returns "").</summary>
        public string Get(string name) => _get(name);
        public bool Has(string name) => _get(name) != null;
    }

    /// <summary>pydantic's lax-mode coercions for the value types config.py uses.</summary>
    internal static class ConfigCoerce
    {
        private static ConfigError Bad(string field, string want, JToken v)
            => new ConfigError("validation error for Config: " + field + ": Input should be " + want + " (got " + PyJson.Repr(v) + ")");

        public static int Int(JToken v, string field)
        {
            switch (v.Type)
            {
                case JTokenType.Integer:
                    {
                        object o = ((JValue)v).Value;
                        BigInteger b = o is BigInteger bi ? bi : new BigInteger(Convert.ToInt64(o));
                        if (b < int.MinValue || b > int.MaxValue) throw Bad(field, "a valid integer (out of range)", v);
                        return (int)b;
                    }
                case JTokenType.Float:
                    {
                        double d = (double)v;
                        if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) <= int.MaxValue) return (int)d;
                        throw Bad(field, "a valid integer, got a number with a fractional part", v);
                    }
                case JTokenType.Boolean: return (bool)v ? 1 : 0;
                case JTokenType.String:
                    {
                        string s = ((string)v).Trim();
                        if (int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int r)) return r;
                        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
                            && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue) return (int)d;
                        throw Bad(field, "a valid integer, unable to parse string as an integer", v);
                    }
                default: throw Bad(field, "a valid integer", v);
            }
        }

        public static double Float(JToken v, string field)
        {
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return Convert.ToDouble(((JValue)v).Value is BigInteger bi ? (object)(double)bi : ((JValue)v).Value, System.Globalization.CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)v ? 1.0 : 0.0;
                case JTokenType.String:
                    {
                        string s = ((string)v).Trim();
                        string low = s.ToLowerInvariant();
                        if (low == "inf" || low == "+inf" || low == "infinity" || low == "+infinity") return double.PositiveInfinity;
                        if (low == "-inf" || low == "-infinity") return double.NegativeInfinity;
                        if (low == "nan") return double.NaN;
                        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
                        throw Bad(field, "a valid number, unable to parse string as a number", v);
                    }
                default: throw Bad(field, "a valid number", v);
            }
        }

        public static bool Bool(JToken v, string field)
        {
            switch (v.Type)
            {
                case JTokenType.Boolean: return (bool)v;
                case JTokenType.Integer:
                    {
                        object o = ((JValue)v).Value;
                        if (!(o is BigInteger) && Convert.ToInt64(o) == 0) return false;
                        if (!(o is BigInteger) && Convert.ToInt64(o) == 1) return true;
                        throw Bad(field, "a valid boolean, unable to interpret input", v);
                    }
                case JTokenType.Float:
                    {
                        double d = (double)v;
                        if (d == 0) return false;
                        if (d == 1) return true;
                        throw Bad(field, "a valid boolean, unable to interpret input", v);
                    }
                case JTokenType.String:
                    switch (((string)v).Trim().ToLowerInvariant())
                    {
                        case "0": case "off": case "f": case "false": case "n": case "no": return false;
                        case "1": case "on": case "t": case "true": case "y": case "yes": return true;
                    }
                    throw Bad(field, "a valid boolean, unable to interpret input", v);
                default: throw Bad(field, "a valid boolean", v);
            }
        }

        public static string Str(JToken v, string field)
        {
            if (v.Type == JTokenType.String) return (string)v;
            throw Bad(field, "a valid string", v);
        }

        public static JObject Obj(JToken v, string field)
        {
            if (v is JObject o) return o;
            throw Bad(field, "a valid dictionary or instance", v);
        }

        /// <summary>A bare `tuple` field: any elements; the config only compares them as strings.</summary>
        public static string[] StrList(JToken v, string field)
        {
            if (v is JArray a) return a.Select(x => PyJson.Str(x)).ToArray();
            throw Bad(field, "a valid tuple", v);
        }

        public static List<int> IntList(JToken v, string field)
        {
            if (v is JArray a) return a.Select(x => Int(x, field)).ToList();
            throw Bad(field, "a valid list", v);
        }

        public static JArray ToArray<T>(IEnumerable<T> xs)
        {
            var a = new JArray();
            foreach (var x in xs) a.Add(new JValue(x));
            return a;
        }
    }

    public sealed partial class MindConfig
    {
        public const string LegacyDataLeaf = "hermesvoice-agent";   // the folder's name before the mod became RimWorldLLM

        /// <summary>Environment variable -> path of keys in the config dict (the Python _ENV).</summary>
        public static readonly (string Env, string[] Keys)[] EnvMap =
        {
            ("RIMWORLDLLM_LLM_URL", new[] { "llm", "base_url" }),
            ("RIMWORLDLLM_LLM_KEY", new[] { "llm", "api_key" }),
            ("RIMWORLDLLM_LLM_MODEL", new[] { "llm", "model" }),
            ("RIMWORLDLLM_LLM_MODE", new[] { "llm", "mode" }),
            ("RIMWORLDLLM_BRIDGE_URL", new[] { "bridge_url" }),
            ("RIMWORLDLLM_BRIDGE_TOKEN", new[] { "bridge_token" }),
            ("RIMWORLDLLM_WINDOWS", new[] { "windows" }),
        };

        public MindConfig() : this(MindEnv.Process) { }

        public MindConfig(MindEnv env)
        {
            DataDir = DefaultDataDir(env);
        }

        public List<int> ActorSlots
        {
            get
            {
                var l = new List<int>();
                for (int i = 1; i <= Windows; i++) l.Add(i);
                return l;
            }
        }

        // ---- the creative_* lookups core/creative.py builds with getattr("creative_" + feature + ...), as an explicit map

        /// <summary>creative_&lt;feature&gt; as a bool for "ground" and "plan" (false for any other name).</summary>
        public bool CreativeFlag(string feature)
        {
            switch (feature)
            {
                case "ground": return CreativeGround;
                case "plan": return CreativePlan;
                default: return false;
            }
        }

        /// <summary>creative_&lt;feature&gt;_for for "ground", "plan" and "best_of" (empty for any other name).</summary>
        public string[] CreativeFor(string feature)
        {
            switch (feature)
            {
                case "ground": return CreativeGroundFor ?? new string[0];
                case "plan": return CreativePlanFor ?? new string[0];
                case "best_of": return CreativeBestOfFor ?? new string[0];
                default: return new string[0];
            }
        }

        // ---------------------------------------------------------------- data dir

        public static string DefaultDataDir(MindEnv env)
        {
            string ov = env.Get("RIMWORLDLLM_DATA_DIR");
            if (!string.IsNullOrEmpty(ov)) return ov;
            if (env.IsWindows)
            {
                // Windows: %LOCALAPPDATA%\rimworldllm-agent, beside the engine's %LOCALAPPDATA%\EasyEngine
                string local = env.Get("LOCALAPPDATA");
                string root = !string.IsNullOrEmpty(local) ? local : Path.Combine(env.Home, "AppData", "Local");
                return Path.Combine(root, "rimworldllm-agent");
            }
            string xdg = env.Get("XDG_DATA_HOME");
            string share = xdg != null ? (xdg.Length == 0 ? "." : xdg) : Path.Combine(env.Home, ".local", "share");
            return Path.Combine(share, "rimworldllm-agent");
        }

        private static string NormPath(string p, bool windows)
        {
            if (p == null) return null;
            var sb = new StringBuilder();
            char prev = '\0';
            foreach (char c in p)
            {
                char d = (windows && c == '\\') ? '/' : c;
                if (d == '/' && prev == '/') continue;
                sb.Append(d); prev = d;
            }
            string s = sb.ToString();
            if (s.Length > 1 && s.EndsWith("/")) s = s.TrimEnd('/');
            return windows ? s.ToLowerInvariant() : s;
        }

        /// <summary>One-time move of the old-named data folder (hermesvoice-agent) to <paramref name="target"/> when it is the
        /// default location and does not exist yet. Never deletes: when both exist both stay (logged). Returns "override",
        /// "custom", "none", "both", "moved" or "failed".</summary>
        public static string MigrateLegacyDataDir(string target, MindEnv env, LogSink log = null)
        {
            log = log ?? LlmLog.Sink;
            void Info(string m) => log?.Invoke("INFO", "rimworldllm.config", m);
            void Warn(string m) => log?.Invoke("WARNING", "rimworldllm.config", m);
            if (!string.IsNullOrEmpty(env.Get("RIMWORLDLLM_DATA_DIR"))) return "override";
            if (NormPath(target, env.IsWindows) != NormPath(DefaultDataDir(env), env.IsWindows)) return "custom";
            string parent = Path.GetDirectoryName(target.TrimEnd('/', '\\'));
            string old = Path.Combine(parent ?? "", LegacyDataLeaf);
            bool isLink = false;
            try { isLink = new DirectoryInfo(old).LinkTarget != null; } catch (IOException) { }
            if (isLink || !Directory.Exists(old)) return "none";
            if (Directory.Exists(target) || File.Exists(target))
            {
                Info("both " + target + " and the old " + old + " exist; using the new one and leaving the old one alone");
                return "both";
            }
            try { Directory.Move(old, target); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Warn("could not move the old data folder " + old + " to " + target + ": " + e.Message);
                return "failed";
            }
            Info("moved the old data folder " + old + " to " + target);
            return "moved";
        }

        // ---------------------------------------------------------------- loading

        /// <summary>load_config(path): defaults, then the file (path, or $RIMWORLDLLM_CONFIG), then the environment, then
        /// bridge.json's llm block when no llm URL was set. A missing file throws IOException; a bad value ConfigError.</summary>
        public static MindConfig Load(string path = null, MindEnv env = null)
        {
            env = env ?? MindEnv.Process;
            if (string.IsNullOrEmpty(path)) path = env.Get("RIMWORLDLLM_CONFIG");
            JObject data = new JObject();
            if (!string.IsNullOrEmpty(path))
            {
                byte[] raw = File.ReadAllBytes(path);
                JToken parsed;
                if (path.EndsWith(".toml", StringComparison.Ordinal))
                    parsed = TomlLite.Parse(new UTF8Encoding(false, true).GetString(raw));
                else
                    parsed = PyJson.Loads(raw);
                data = parsed as JObject ?? throw new ConfigError("config file must hold an object at the top level");
            }
            foreach (var (name, keys) in EnvMap)
            {
                string val = env.Get(name);
                if (val == null) continue;
                JObject d = data;
                for (int i = 0; i < keys.Length - 1; i++)
                {
                    if (d[keys[i]] == null) d[keys[i]] = new JObject();
                    d = d[keys[i]] as JObject ?? throw new ConfigError(keys[i] + " must be a table");
                }
                d[keys[keys.Length - 1]] = new JValue(val);
            }
            string bj = env.Get("RIMWORLDLLM_BRIDGE_JSON");
            if (bj != null) data["bridge_json"] = new JValue(bj);
            LlmFromBridgeJson(data);
            return FromDict(data, env);
        }

        /// <summary>Config(**data): validate and build. Unknown keys are ignored.</summary>
        public static MindConfig FromDict(JObject data, MindEnv env = null)
        {
            var c = new MindConfig(env ?? MindEnv.Process);
            c.Apply(data);
            return c;
        }

        internal static bool Truthy(JToken t)
        {
            if (t == null) return false;
            switch (t.Type)
            {
                case JTokenType.Null: case JTokenType.Undefined: return false;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.String: return ((string)t).Length > 0;
                case JTokenType.Integer: { object o = ((JValue)t).Value; return o is BigInteger b ? !b.IsZero : Convert.ToInt64(o) != 0; }
                case JTokenType.Float: return (double)t != 0;
                case JTokenType.Array: return ((JArray)t).Count > 0;
                case JTokenType.Object: return ((JObject)t).Count > 0;
                default: return true;
            }
        }

        /// <summary>Fill llm.* from bridge.json's "llm" block when no LLM URL was set by the file or the environment.</summary>
        private static void LlmFromBridgeJson(JObject data)
        {
            JToken llmTok = data["llm"];
            if (llmTok != null && llmTok.Type != JTokenType.Object && llmTok.Type != JTokenType.Null)
                throw new ConfigError("llm must be a table");
            if (Truthy((llmTok as JObject)?["base_url"]) || !Truthy(data["bridge_json"])) return;
            JObject doc;
            try { doc = ReadBridgeJson((string)data["bridge_json"]); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException) { return; }
            JObject block = doc["llm"] as JObject;
            if (block == null || string.IsNullOrWhiteSpace(StrOf(block["base_url"]))) return;
            if (!(data["llm"] is JObject llm)) { llm = new JObject(); data["llm"] = llm; }
            foreach (string key in new[] { "base_url", "model", "api_key" })
            {
                string val = StrOf(block[key]).Trim();
                if (val.Length > 0 && !Truthy(llm[key])) llm[key] = new JValue(val);
            }
        }

        private static string StrOf(JToken t) => Truthy(t) ? PyJson.Str(t) : "";

        /// <summary>bridge.json: {"port", "bind", "token", "pid", "version", "epoch", "llm"}. IOException when missing,
        /// PyJsonException (a FormatException) when it is not JSON.</summary>
        public static JObject ReadBridgeJson(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            string text = new UTF8Encoding(false, false).GetString(raw);
            return PyJson.Loads(text) as JObject ?? throw new FormatException("bridge.json is not an object");
        }
    }
}
