using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    /// <summary>OS and CPU as the engine names them: linux|windows|darwin and amd64|arm64.</summary>
    public sealed class EnginePlatform
    {
        public string Os;
        public string Arch;
        public string Dir { get { return Os + "-" + Arch; } }
        public string BinaryName { get { return Os == "windows" ? "hermes-engine.exe" : "hermes-engine"; } }
        public bool IsUnix { get { return Os != "windows"; } }

        public EnginePlatform(string os, string arch) { Os = os; Arch = arch; }

        /// <summary>Detects the running platform. RuntimeInformation first; Environment/IntPtr fallbacks for old Mono.</summary>
        public static EnginePlatform Detect()
        {
            string os = null, arch = null;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) os = "windows";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) os = "darwin";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) os = "linux";
                switch (RuntimeInformation.OSArchitecture)
                {
                    case Architecture.Arm64: arch = "arm64"; break;
                    case Architecture.X64: arch = "amd64"; break;
                }
            }
            catch { }
            if (os == null)
            {
                switch (Environment.OSVersion.Platform)
                {
                    case PlatformID.Win32NT: case PlatformID.Win32S: case PlatformID.Win32Windows: case PlatformID.WinCE: os = "windows"; break;
                    case PlatformID.MacOSX: os = "darwin"; break;
                    default: os = Directory.Exists("/Applications") && Directory.Exists("/System") ? "darwin" : "linux"; break;
                }
            }
            if (arch == null) arch = IntPtr.Size == 8 ? "amd64" : "386";
            return new EnginePlatform(os, arch);
        }
    }

    /// <summary>Starts a child process with no window. Injectable so tests never spawn anything.</summary>
    public interface IProcessStarter
    {
        /// <summary>Starts <paramref name="exe"/>; throws on failure. Output is discarded.</summary>
        void Start(string exe, IList<string> args, IDictionary<string, string> env);
    }

    public sealed class SystemProcessStarter : IProcessStarter
    {
        public void Start(string exe, IList<string> args, IDictionary<string, string> env)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            // Quote by hand: ProcessStartInfo.ArgumentList is not in the game's Mono.
            var sb = new System.Text.StringBuilder();
            foreach (var a in args) { if (sb.Length > 0) sb.Append(' '); sb.Append(Quote(a)); }
            psi.Arguments = sb.ToString();
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("process did not start");
            // Drain and discard so a full pipe can never stall the engine's logging.
            p.OutputDataReceived += (s, e) => { };
            p.ErrorDataReceived += (s, e) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        /// <summary>
        /// Quotes one argument for a command line under the Windows (MSVCRT/CommandLineToArgvW) rules, which Mono's
        /// Unix argument splitter also reads the same way: backslashes are doubled only where they come before a quote
        /// or the closing quote, so "C:\Program Files\x" reaches the engine unchanged (doubling every backslash, as
        /// before, handed Windows paths to the engine with every separator doubled).
        /// </summary>
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in a)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', slashes); sb.Append(ch); }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }
    }

    public enum LaunchOutcome { Attached, Spawned, NoBinary, SpawnFailed }

    public sealed class LaunchResult
    {
        public LaunchOutcome Outcome;
        public string Error;
        public string BinaryPath;
        public string ProfilePath;
        /// <summary>The engine to talk to: the one we attached to, or the spawned one once it answers (null if it did not answer in time).</summary>
        public EngineClient Client;
        /// <summary>True when attach was refused or failed (the engine still runs, but will not watch our pid).</summary>
        public string AttachError;
    }

    public sealed class LaunchOptions
    {
        public string ModRoot;                // the mod's root dir (contains Engine/)
        public string BridgeJsonPath;         // full path of bridge.json
        public int Pid;                       // our process id
        public HostAdapterInfo HostAdapter;   // the card the game renders on, null when unknown
        public EnginePlatform Platform;
        public IProcessStarter Starter = new SystemProcessStarter();
        /// <summary>Finds a running engine, or null. Default: discovery through engine.lock and 47480-47489.</summary>
        public Func<Task<EngineClient>> Discover;
        public int SpawnWaitMs = 20000;       // how long to wait for a fresh engine to answer status
        public int SpawnPollMs = 250;
        public Func<int, Task> Delay = ms => Task.Delay(ms);
    }

    /// <summary>
    /// Picks the engine binary, writes the game profile (docs/ENGINE.md §8), and attaches to a running engine or spawns one
    /// with <c>serve --profile F --parent-pid P</c> (docs/ENGINE.md §6). No game references; call from a worker thread.
    /// </summary>
    public static class EngineLauncher
    {
        public const string ProfileFileName = "engine-profile.json";

        public static string BinaryPath(string modRoot, EnginePlatform p)
        {
            return Path.Combine(modRoot, "Engine", p.Dir, p.BinaryName);
        }

        /// <summary>The agent wheel under Engine/agent/*.whl (highest name when several), or null.</summary>
        public static string FindWheel(string modRoot)
        {
            try
            {
                string dir = Path.Combine(modRoot, "Engine", "agent");
                if (!Directory.Exists(dir)) return null;
                var files = Directory.GetFiles(dir, "*.whl");
                if (files.Length == 0) return null;
                Array.Sort(files, StringComparer.Ordinal);
                return files[files.Length - 1];
            }
            catch { return null; }
        }

        /// <summary>The game profile for HermesVoice (RimWorld). <paramref name="wheelPath"/> may be null (no agent installed).</summary>
        public const int HostReserveEstimateMib = 1500;

        public static JObject BuildProfile(string bridgeJsonPath, string wheelPath, int hostPid = 0, HostAdapterInfo hostAdapter = null)
        {
            var o = new JObject
            {
                ["game"] = "RimWorld",
                ["slots"] = 3,
                ["min_slots"] = 1,
                ["ctx_per_slot"] = new JObject { ["max"] = 65536, ["floor"] = 8192 },
                ["game_reserve_mib"] = HostReserveEstimateMib   // the host estimate; the engine measures the game when it can
            };
            if (hostPid > 0) o["host_pid"] = hostPid;
            if (hostAdapter != null && hostAdapter.Known) o["host_adapter"] = hostAdapter.ToJson();
            if (wheelPath != null)
            {
                o["agent"] = new JObject
                {
                    ["id"] = "hermesvoice-agent",
                    ["wheel"] = wheelPath,
                    ["python"] = "3.12",
                    ["args"] = new JArray("run"),
                    ["env"] = new JObject { ["HERMESVOICE_BRIDGE_JSON"] = bridgeJsonPath }
                };
            }
            return o;
        }

        public static string WriteProfile(string bridgeJsonPath, string wheelPath, int hostPid = 0, HostAdapterInfo hostAdapter = null)
        {
            string dir = Path.GetDirectoryName(bridgeJsonPath);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, ProfileFileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, BuildProfile(bridgeJsonPath, wheelPath, hostPid, hostAdapter).ToString(Newtonsoft.Json.Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        public static IList<string> ServeArgs(string profilePath, int pid)
        {
            return new List<string> { "serve", "--profile", profilePath, "--parent-pid", pid.ToString() };
        }

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, uint mode);

        static void MakeExecutable(string path)
        {
            try { chmod(path, 493); } catch { }   // 0755; P/Invoke because the game's Mono has no File.SetUnixFileMode
        }

        /// <summary>The default discovery: engine.lock in the data dir, then the port range.</summary>
        public static Task<EngineClient> DefaultDiscoverAsync(EnginePlatform p)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string data = EngineClient.DataDir(p.Os, n => Environment.GetEnvironmentVariable(n), home);
            return EngineClient.DiscoverAsync(EngineClient.ReadLockPort(Path.Combine(data, "engine.lock")));
        }

        /// <summary>POST /engine/v1/host; an older engine (404) or any failure is ignored.</summary>
        static async Task PostHostQuietAsync(EngineClient c, LaunchOptions o)
        {
            try { await c.PostHostAsync(o.Pid, o.HostAdapter, HostReserveEstimateMib).ConfigureAwait(false); }
            catch { }
        }

        /// <summary>Attach to a running engine, else spawn one. Never throws.</summary>
        public static async Task<LaunchResult> EnsureRunningAsync(LaunchOptions o)
        {
            var r = new LaunchResult();
            try
            {
                var plat = o.Platform ?? EnginePlatform.Detect();
                var discover = o.Discover ?? (() => DefaultDiscoverAsync(plat));

                var running = await discover().ConfigureAwait(false);
                if (running != null)
                {
                    r.Client = running;
                    r.Outcome = LaunchOutcome.Attached;
                    var a = await running.AttachAsync(o.Pid).ConfigureAwait(false);
                    if (!a.Ok) r.AttachError = a.Error;
                    await PostHostQuietAsync(running, o).ConfigureAwait(false);
                    return r;
                }

                string bin = BinaryPath(o.ModRoot, plat);
                r.BinaryPath = bin;
                if (!File.Exists(bin))
                {
                    r.Outcome = LaunchOutcome.NoBinary;
                    r.Error = "No engine binary for " + plat.Dir + " (expected " + bin + ")";
                    return r;
                }
                if (plat.IsUnix) MakeExecutable(bin);

                r.ProfilePath = WriteProfile(o.BridgeJsonPath, FindWheel(o.ModRoot), o.Pid, o.HostAdapter);
                var env = new Dictionary<string, string> { ["HERMESVOICE_BRIDGE_JSON"] = o.BridgeJsonPath };
                try { o.Starter.Start(bin, ServeArgs(r.ProfilePath, o.Pid), env); }
                catch (Exception e)
                {
                    r.Outcome = LaunchOutcome.SpawnFailed;
                    r.Error = "Could not start the engine: " + e.Message;
                    return r;
                }
                r.Outcome = LaunchOutcome.Spawned;
                for (int waited = 0; waited < o.SpawnWaitMs; waited += o.SpawnPollMs)
                {
                    await o.Delay(o.SpawnPollMs).ConfigureAwait(false);
                    var c = await discover().ConfigureAwait(false);
                    if (c != null) { r.Client = c; break; }
                }
                if (r.Client != null) await PostHostQuietAsync(r.Client, o).ConfigureAwait(false);   // refresh once the game is running
                return r;
            }
            catch (Exception e)
            {
                r.Outcome = LaunchOutcome.SpawnFailed;
                r.Error = e.Message;
                return r;
            }
        }
    }
}
