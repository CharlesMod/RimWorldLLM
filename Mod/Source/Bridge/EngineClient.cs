using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // ------------------------------------------------------------------------------------ DTOs (docs/ENGINE.md §7)

    public sealed class EngineProgress
    {
        [JsonProperty("done_bytes")] public long DoneBytes;
        [JsonProperty("total_bytes")] public long TotalBytes;
        [JsonProperty("rate_bps")] public double RateBps;
        [JsonProperty("eta_s")] public double EtaSeconds;
        [JsonProperty("step")] public string Step;
        public double Fraction { get { return TotalBytes > 0 ? Math.Min(1.0, (double)DoneBytes / TotalBytes) : 0.0; } }
        /// <summary>The checksum pass after a download: the byte count starts again from zero.</summary>
        public bool Verifying { get { return Step != null && Step.StartsWith("verifying", StringComparison.Ordinal); } }
    }

    public sealed class EngineShape
    {
        [JsonProperty("slots")] public int Slots;
        [JsonProperty("ctx_per_slot")] public int CtxPerSlot;
        [JsonProperty("n_cpu_moe")] public int NCpuMoe;
        [JsonProperty("kv_type")] public string KvType;
        [JsonProperty("vram_mib")] public int VramMib;
    }

    public sealed class EngineAgent
    {
        [JsonProperty("phase")] public string Phase;
        [JsonProperty("pid")] public int Pid;
        [JsonProperty("restarts")] public int Restarts;
        [JsonProperty("message")] public string Message;
    }

    public sealed class EngineParent
    {
        [JsonProperty("pid")] public int Pid;
        [JsonProperty("alive")] public bool Alive;
    }

    public sealed class EngineStatus
    {
        [JsonProperty("version")] public string Version;
        [JsonProperty("port")] public int Port;
        [JsonProperty("mode")] public string Mode;
        [JsonProperty("phase")] public string Phase;
        [JsonProperty("message")] public string Message;
        [JsonProperty("progress")] public EngineProgress Progress;
        [JsonProperty("model")] public string Model;
        [JsonProperty("backend")] public string Backend;
        [JsonProperty("shape")] public EngineShape Shape;
        [JsonProperty("server_url")] public string ServerUrl;
        [JsonProperty("tok_s")] public double? TokS;
        [JsonProperty("agent")] public EngineAgent Agent;
        [JsonProperty("parents")] public List<EngineParent> Parents;
        [JsonProperty("errors")] public List<string> Errors;
        /// <summary>True when the selected local model is the catalog's fallback (the smallest one).</summary>
        [JsonProperty("model_fallback")] public bool ModelFallback;
        /// <summary>What to expect from the fallback model, one sentence from the engine (empty otherwise).</summary>
        [JsonProperty("model_note")] public string ModelNote;
        /// <summary>"starter" while a stand-in model answers and the chosen one downloads; "full" for the chosen model.</summary>
        [JsonProperty("model_tier")] public string ModelTier;
        /// <summary>starter_downloading, target_downloading, target_retrying, target_no_space, swapping, starter_serving, target_serving.</summary>
        [JsonProperty("model_stage")] public string ModelStage;
        [JsonProperty("upgrade")] public EngineUpgrade Upgrade;
        /// <summary>The custom GGUF file in use (or remembered), null when none.</summary>
        [JsonProperty("custom")] public EngineCustom Custom;
        /// <summary>Human name of the serving model.</summary>
        [JsonProperty("model_name")] public string ModelName;
        /// <summary>The model kept on disk after a switch, null when none.</summary>
        [JsonProperty("previous")] public EnginePrevious Previous;
        /// <summary>Graphics picture and the room left for the model; null from an older engine.</summary>
        [JsonProperty("gpu")] public GpuSummary Gpu;
    }

    public sealed class EngineCustom
    {
        [JsonProperty("path")] public string Path;
        [JsonProperty("name")] public string Name;
        [JsonProperty("size")] public long Size;
        [JsonProperty("missing")] public bool Missing;
    }

    public sealed class EnginePrevious
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("file")] public string File;
        [JsonProperty("bytes")] public long Bytes;
    }

    /// <summary>Verdict of POST custom/check (and plan.custom) for one GGUF file.</summary>
    public sealed class CustomCheck
    {
        [JsonProperty("ok")] public bool Ok;
        [JsonProperty("error")] public string Error;
        [JsonProperty("path")] public string Path;
        [JsonProperty("name")] public string Name;
        [JsonProperty("size")] public long Size;
        [JsonProperty("arch")] public string Arch;
        [JsonProperty("params")] public long Params;
        [JsonProperty("ctx_train")] public int CtxTrain;
        [JsonProperty("quant")] public string Quant;
        [JsonProperty("moe")] public bool Moe;
        [JsonProperty("known")] public bool Known;
        [JsonProperty("estimated")] public bool Estimated;
        [JsonProperty("fits")] public string Fits;        // yes | tight | no
        [JsonProperty("why")] public string Why;
        [JsonProperty("shape")] public EngineShape Shape;
        [JsonProperty("est_vram_mib")] public int EstVramMib;
        [JsonProperty("est_ram_mib")] public int EstRamMib;
        [JsonProperty("est_decode_tps")] public double EstDecodeTps;
        [JsonProperty("chat_template")] public bool ChatTemplate;
    }

    public sealed class EngineStoragePart
    {
        [JsonProperty("name")] public string Name;        // models | backends | runtime | downloads | logs
        [JsonProperty("path")] public string Path;
        [JsonProperty("bytes")] public long Bytes;
    }

    public sealed class EngineStorageModel
    {
        [JsonProperty("file")] public string File;
        [JsonProperty("id")] public string Id;            // "" when not a catalog model
        [JsonProperty("bytes")] public long Bytes;
        [JsonProperty("in_use")] public bool InUse;
        [JsonProperty("partial")] public bool Partial;
    }

    public sealed class EngineStorage
    {
        [JsonProperty("data_dir")] public string DataDir;
        [JsonProperty("total_bytes")] public long TotalBytes;
        [JsonProperty("free_bytes")] public long FreeBytes;   // -1 unknown
        [JsonProperty("parts")] public List<EngineStoragePart> Parts;
        [JsonProperty("models")] public List<EngineStorageModel> Models;
    }

    public sealed class DeleteModelResult
    {
        [JsonProperty("freed_bytes")] public long FreedBytes;
    }

    /// <summary>The background download of the chosen model while a starter model serves (engine ENGINE.md §7a).</summary>
    public sealed class EngineUpgrade
    {
        [JsonProperty("target")] public string Target;
        [JsonProperty("target_name")] public string TargetName;
        [JsonProperty("state")] public string State;
        [JsonProperty("progress")] public EngineProgress Progress;
        [JsonProperty("error")] public string Error;
        [JsonProperty("retry_in_s")] public double RetryInSeconds;
    }

    public sealed class EngineGpu
    {
        [JsonProperty("index")] public int Index;
        [JsonProperty("vendor")] public string Vendor;
        [JsonProperty("name")] public string Name;
        [JsonProperty("vram_total_mib")] public int VramTotalMiB;
        [JsonProperty("vram_used_mib")] public int VramUsedMiB;
        [JsonProperty("unified_memory")] public bool UnifiedMemory;
        [JsonProperty("source")] public string Source;
    }

    public sealed class EngineHardware
    {
        [JsonProperty("os")] public string OS;
        [JsonProperty("arch")] public string Arch;
        [JsonProperty("cpu_name")] public string CpuName;
        [JsonProperty("cores")] public int Cores;
        [JsonProperty("avx2")] public bool Avx2;
        [JsonProperty("ram_total_mib")] public int RamTotalMiB;
        [JsonProperty("ram_avail_mib")] public int RamAvailMiB;
        [JsonProperty("disk_free_mib")] public int DiskFreeMiB;
        [JsonProperty("gpus")] public List<EngineGpu> Gpus;
    }

    public sealed class EnginePlanModel
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("size")] public long Size;
        [JsonProperty("fits")] public string Fits;        // yes | tight | no
        [JsonProperty("why")] public string Why;
        [JsonProperty("shape")] public EngineShape Shape;
        [JsonProperty("est_vram_mib")] public int EstVramMib;
        [JsonProperty("est_ram_mib")] public int EstRamMib;
        [JsonProperty("installed")] public bool Installed;
        /// <summary>True when the catalog has no download URL (import-only).</summary>
        [JsonProperty("import_only")] public bool ImportOnly;
        /// <summary>True for a model kept as a last resort; the UI warns about it.</summary>
        [JsonProperty("fallback")] public bool Fallback;
        [JsonProperty("fallback_note")] public string FallbackNote;
        /// <summary>Bytes still to download for this model (0 if installed).</summary>
        [JsonProperty("disk_need_bytes")] public long DiskNeedBytes;
    }

    public sealed class EnginePlan
    {
        [JsonProperty("recommended")] public string Recommended;
        [JsonProperty("models")] public List<EnginePlanModel> Models;
        [JsonProperty("backend")] public string Backend;
        /// <summary>Verdict of the current custom file, null when none.</summary>
        [JsonProperty("custom")] public CustomCheck Custom;
        [JsonProperty("disk_free_bytes")] public long DiskFreeBytes;
    }

    public sealed class EngineTestResult
    {
        [JsonProperty("light")] public string Light;      // green | amber | red
        [JsonProperty("message")] public string Message;
        [JsonProperty("latency_ms")] public int LatencyMs;
        [JsonProperty("model")] public string Model;
    }

    public sealed class EngineExternal
    {
        [JsonProperty("base_url")] public string BaseUrl = "";
        [JsonProperty("model")] public string Model = "";
        [JsonProperty("api_key")] public string ApiKey = "";
    }

    public sealed class EngineSettings
    {
        [JsonProperty("mode")] public string Mode = "external";
        [JsonProperty("external")] public EngineExternal External;
        [JsonProperty("linger_s", NullValueHandling = NullValueHandling.Ignore)] public int? LingerSeconds;
        [JsonProperty("kv_type", NullValueHandling = NullValueHandling.Ignore)] public string KvType;
    }

    /// <summary>Phase names of /engine/v1/status.</summary>
    public static class EnginePhase
    {
        public const string NotInstalled = "not_installed", Downloading = "downloading", Installing = "installing",
            Starting = "starting", Loading = "loading", Ready = "ready", Resizing = "resizing",
            Sleeping = "sleeping", External = "external", Error = "error";

        public static bool IsBusy(string p)
        {
            return p == Downloading || p == Installing || p == Starting || p == Loading || p == Resizing;
        }
    }

    /// <summary>Outcome of one engine call; never throws.</summary>
    public sealed class EngineResult<T>
    {
        public bool Ok;
        public T Value;
        public int HttpStatus;
        public string Error;     // short reason when !Ok
        public bool TimedOut;
        public bool Unreachable;
    }

    /// <summary>
    /// Client for the engine's control API (<c>/engine/v1/*</c>, docs/ENGINE.md §7). No game references.
    /// Every call is async with a short timeout and returns an <see cref="EngineResult{T}"/>; non-GET calls carry
    /// the anti-CSRF header <c>X-Easy-Engine: 1</c>.
    /// </summary>
    public sealed class EngineClient
    {
        public const int FirstPort = 47480, LastPort = 47489;
        public const string CsrfHeader = "X-Easy-Engine";

        static readonly HttpClient Http = MakeHttp();
        static HttpClient MakeHttp()
        {
            var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // per-call timeouts below
            return c;
        }

        public string BaseUrl { get; private set; }
        public int ShortTimeoutMs = 3000;      // status, plan, cancel, attach...
        public int CheckTimeoutMs = 10000;     // custom/check (reads a GGUF header)
        public int StorageTimeoutMs = 5000;    // storage (directory walk)
        public int LongTimeoutMs = 60000;      // test (an 8-token chat), import (hash a big file)

        public EngineClient(string baseUrl) { BaseUrl = (baseUrl ?? "").TrimEnd('/'); }
        public EngineClient(int port) : this("http://127.0.0.1:" + port) { }

        /// <summary>The OpenAI base URL clients should use against the engine's proxy.</summary>
        public string OpenAiBase { get { return BaseUrl + "/v1"; } }

        // ---------------------------------------------------------------------------------- calls
        public Task<EngineResult<EngineStatus>> GetStatusAsync() { return Send<EngineStatus>("GET", "status", null, ShortTimeoutMs); }
        public Task<EngineResult<EngineHardware>> GetHardwareAsync(bool refresh = false) { return Send<EngineHardware>("GET", refresh ? "hardware?refresh=1" : "hardware", null, refresh ? LongTimeoutMs : ShortTimeoutMs); }
        public Task<EngineResult<EnginePlan>> GetPlanAsync() { return Send<EnginePlan>("GET", "plan", null, ShortTimeoutMs * 2); }
        public Task<EngineResult<JObject>> InstallAsync(string model, string backend = null)
        {
            var o = new JObject { ["model"] = model };
            if (!string.IsNullOrEmpty(backend)) o["backend"] = backend;
            return Send<JObject>("POST", "install", o, ShortTimeoutMs);
        }
        public Task<EngineResult<JObject>> CancelAsync() { return Send<JObject>("POST", "cancel", null, ShortTimeoutMs); }
        public Task<EngineResult<JObject>> ImportAsync(string path) { return Send<JObject>("POST", "import", new JObject { ["path"] = path }, LongTimeoutMs); }
        public Task<EngineResult<JObject>> StartAsync() { return Send<JObject>("POST", "start", null, ShortTimeoutMs); }
        public Task<EngineResult<JObject>> StopAsync() { return Send<JObject>("POST", "stop", null, ShortTimeoutMs); }
        public Task<EngineResult<JObject>> SleepAsync() { return Send<JObject>("POST", "sleep", null, ShortTimeoutMs); }
        public Task<EngineResult<JObject>> WakeAsync() { return Send<JObject>("POST", "wake", null, ShortTimeoutMs); }
        public Task<EngineResult<JObject>> PutSettingsAsync(EngineSettings s) { return Send<JObject>("PUT", "settings", JObject.FromObject(s), ShortTimeoutMs); }
        public Task<EngineResult<EngineTestResult>> TestAsync() { return Send<EngineTestResult>("POST", "test", null, LongTimeoutMs); }
        public Task<EngineResult<JObject>> AttachAsync(int pid) { return Send<JObject>("POST", "attach", new JObject { ["pid"] = pid }, ShortTimeoutMs); }
        /// <summary>Tells the engine which process is the game and which card it renders on (POST host; an older engine answers 404, which callers ignore).</summary>
        public Task<EngineResult<JObject>> PostHostAsync(int pid, HostAdapterInfo adapter, int reserveMib)
        {
            var o = new JObject { ["pid"] = pid, ["reserve_mib"] = reserveMib };
            if (adapter != null && adapter.Known) o["adapter"] = adapter.ToJson();
            return Send<JObject>("POST", "host", o, ShortTimeoutMs);
        }
        public Task<EngineResult<CustomCheck>> CheckCustomAsync(string path) { return Send<CustomCheck>("POST", "custom/check", new JObject { ["path"] = path }, CheckTimeoutMs); }
        public Task<EngineResult<JObject>> UseCustomAsync(string path) { return Send<JObject>("POST", "custom/use", new JObject { ["path"] = path }, ShortTimeoutMs); }
        public Task<EngineResult<JObject>> ClearCustomAsync() { return Send<JObject>("POST", "custom/clear", null, ShortTimeoutMs); }
        public Task<EngineResult<EngineStorage>> GetStorageAsync(bool refresh = false) { return Send<EngineStorage>("GET", refresh ? "storage?refresh=1" : "storage", null, StorageTimeoutMs); }
        public Task<EngineResult<JObject>> RemoveDownloadsAsync() { return Send<JObject>("POST", "remove-downloads", new JObject { ["confirm"] = true }, ShortTimeoutMs); }
        /// <summary>Deletes one downloaded model file; <paramref name="id"/> (catalog id) or <paramref name="file"/>, only the non-empty one is sent (id wins).</summary>
        public Task<EngineResult<DeleteModelResult>> DeleteModelAsync(string id, string file = null)
        {
            var o = new JObject();
            if (!string.IsNullOrEmpty(id)) o["id"] = id;
            else if (!string.IsNullOrEmpty(file)) o["file"] = file;
            return Send<DeleteModelResult>("POST", "delete-model", o, ShortTimeoutMs);
        }
        public Task<EngineResult<string>> GetLogsAsync(string name, int tail = 200)
        {
            return Send<string>("GET", "logs?name=" + Uri.EscapeDataString(name ?? "engine") + "&tail=" + tail, null, ShortTimeoutMs);
        }

        // ---------------------------------------------------------------------------------- transport
        async Task<EngineResult<T>> Send<T>(string method, string path, JObject body, int timeoutMs)
        {
            var res = new EngineResult<T>();
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    var req = new HttpRequestMessage(new HttpMethod(method), BaseUrl + "/engine/v1/" + path);
                    if (method != "GET")
                    {
                        req.Headers.TryAddWithoutValidation(CsrfHeader, "1");
                        req.Content = new StringContent(body == null ? "{}" : body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    }
                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false))
                    {
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        res.HttpStatus = (int)resp.StatusCode;
                        if (!resp.IsSuccessStatusCode)
                        {
                            res.Error = ErrorText(text, res.HttpStatus);
                            return res;
                        }
                        try
                        {
                            if (typeof(T) == typeof(string)) res.Value = (T)(object)text;
                            else if (string.IsNullOrWhiteSpace(text)) res.Value = default(T);
                            else res.Value = JsonConvert.DeserializeObject<T>(text);
                            res.Ok = true;
                        }
                        catch (JsonException e) { res.Error = "bad reply: " + e.Message; }
                    }
                }
                catch (OperationCanceledException) { res.TimedOut = true; res.Error = "timeout"; }
                catch (HttpRequestException e) { res.Unreachable = true; res.Error = e.InnerException != null ? e.InnerException.Message : e.Message; }
                catch (Exception e) { res.Error = e.Message; }
            }
            return res;
        }

        /// <summary>The Error of a result whose reply was the engine's 503 "yielded" (another program on that machine wants its graphics card).</summary>
        public const string YieldedError = "yielded";

        static string ErrorText(string body, int status)
        {
            try
            {
                var j = JObject.Parse(body);
                var e = j["error"];
                if (e is JObject && (string)e["code"] == "yielded") return YieldedError;
                if (e is JObject) return (string)e["message"] ?? ("HTTP " + status);
                if (e != null) return e.ToString();
                if (j["message"] != null) return (string)j["message"];
            }
            catch { }
            return "HTTP " + status;
        }

        // ---------------------------------------------------------------------------------- discovery
        /// <summary>The engine data dir (docs/ENGINE.md §2). <paramref name="env"/> reads environment variables (injectable).</summary>
        public static string DataDir(string os, Func<string, string> env, string home)
        {
            string over = env("EASY_ENGINE_HOME");
            if (!string.IsNullOrWhiteSpace(over)) return over;
            switch (os)
            {
                case "windows":
                    {
                        string la = env("LOCALAPPDATA");
                        if (string.IsNullOrWhiteSpace(la)) la = Path.Combine(home ?? "", "AppData", "Local");
                        return Path.Combine(la, "EasyEngine");
                    }
                case "darwin":
                    return Path.Combine(home ?? "", "Library", "Application Support", "EasyEngine");
                default:
                    {
                        string x = env("XDG_DATA_HOME");
                        if (string.IsNullOrWhiteSpace(x)) x = Path.Combine(home ?? "", ".local", "share");
                        return Path.Combine(x, "easy-engine");
                    }
            }
        }

        /// <summary>The engine data dir under its old name (hermes-engine / HermesEngine, HERMES_ENGINE_HOME). The engine moves it
        /// to <see cref="DataDir"/> on start; the mod looks here only as a fallback for the lock on that first run.</summary>
        public static string LegacyDataDir(string os, Func<string, string> env, string home)
        {
            string over = env("HERMES_ENGINE_HOME");
            if (!string.IsNullOrWhiteSpace(over)) return over;
            switch (os)
            {
                case "windows":
                    {
                        string la = env("LOCALAPPDATA");
                        if (string.IsNullOrWhiteSpace(la)) la = Path.Combine(home ?? "", "AppData", "Local");
                        return Path.Combine(la, "HermesEngine");
                    }
                case "darwin":
                    return Path.Combine(home ?? "", "Library", "Application Support", "HermesEngine");
                default:
                    {
                        string x = env("XDG_DATA_HOME");
                        if (string.IsNullOrWhiteSpace(x)) x = Path.Combine(home ?? "", ".local", "share");
                        return Path.Combine(x, "hermes-engine");
                    }
            }
        }

        /// <summary>The port in engine.lock: the data dir's first, then the old-named dir's; 0 when neither is readable.</summary>
        public static int LockPort(string os, Func<string, string> env, string home)
        {
            int port = ReadLockPort(Path.Combine(DataDir(os, env, home), "engine.lock"));
            if (port > 0) return port;
            return ReadLockPort(Path.Combine(LegacyDataDir(os, env, home), "engine.lock"));
        }

        /// <summary>Reads the port from engine.lock. Accepts JSON ({"pid":1,"port":47480}) or "pid port" / "port=N" text. 0 when unreadable.</summary>
        public static int ReadLockPort(string lockPath)
        {
            try
            {
                if (!File.Exists(lockPath)) return 0;
                string t = File.ReadAllText(lockPath).Trim();
                if (t.Length == 0) return 0;
                if (t[0] == '{')
                {
                    var j = JObject.Parse(t);
                    int p = (int?)j["port"] ?? 0;
                    return p >= 1 && p <= 65535 ? p : 0;
                }
                var m = System.Text.RegularExpressions.Regex.Match(t, @"port\D{0,3}(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return int.Parse(m.Groups[1].Value);
                var parts = t.Split(new[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);
                int q;
                if (parts.Length >= 2 && int.TryParse(parts[1], out q) && q >= 1 && q <= 65535) return q;
            }
            catch { }
            return 0;
        }

        /// <summary>Ports to try, in order: the one in engine.lock (when given) first, then 47480-47489.</summary>
        public static List<int> CandidatePorts(int lockPort)
        {
            var l = new List<int>();
            if (lockPort > 0) l.Add(lockPort);
            for (int p = FirstPort; p <= LastPort; p++) if (p != lockPort) l.Add(p);
            return l;
        }

        /// <summary>Finds a running engine: tries each candidate port's status with a short timeout. Null when none answers.</summary>
        public static async Task<EngineClient> DiscoverAsync(int lockPort, int perPortTimeoutMs = 600, IEnumerable<int> ports = null)
        {
            foreach (int p in ports ?? CandidatePorts(lockPort))
            {
                var c = new EngineClient(p) { ShortTimeoutMs = perPortTimeoutMs };
                var r = await c.GetStatusAsync().ConfigureAwait(false);
                if (r.Ok && r.Value != null && !string.IsNullOrEmpty(r.Value.Phase))
                {
                    c.ShortTimeoutMs = 3000;
                    return c;
                }
            }
            return null;
        }
    }
}
