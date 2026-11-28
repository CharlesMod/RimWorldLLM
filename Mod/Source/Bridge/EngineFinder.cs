using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>One engine in the "Find engines on my network" list.</summary>
    public sealed class FoundEngine
    {
        public string EngineId = "";
        public string Name = "";
        public string Root = "";        // "http://host:port"
        public string ModelName = "";   // from hello ("" when hello did not answer)
        public string Phase = "";       // from hello
        public bool Answered;           // hello answered as an easyEngine
        public bool HaveToken;          // this game holds a token for its engine_id
        public bool NeedsToken;         // hello: token_required
        public bool ViaTailscale;       // found (also) on the tailnet
        public bool ViaLan;             // found (also) on the LAN (mDNS, find, or a typed LAN address)
        public string Address { get { return EngineAddress.Display(Root); } }
    }

    /// <summary>
    /// Finds easyEngines on the local network: an mDNS browse first (about 2 s); when it finds nothing, the shipped
    /// easy-engine binary's <c>find --json</c>, if present. Then a hello on each to read its model and phase.
    /// </summary>
    public static class EngineFinder
    {
        /// <summary>Runs a program and returns its stdout, or null. Injectable for tests.</summary>
        public delegate Task<string> RunProgram(string path, string args, int timeoutMs);

        /// <summary>Parses the output of <c>easy-engine find --json</c>: [{"name","engine_id","api","address","host"}].</summary>
        public static List<MdnsEngine> ParseFindJson(string json)
        {
            var list = new List<MdnsEngine>();
            if (string.IsNullOrWhiteSpace(json)) return list;
            try
            {
                var arr = JArray.Parse(json.Trim());
                foreach (var t in arr)
                {
                    if (!(t is JObject o)) continue;
                    string id = ((string)o["engine_id"] ?? "").Trim().ToLowerInvariant();
                    string addr = ((string)o["address"] ?? "").Trim();
                    if (id.Length == 0 || addr.Length == 0) continue;
                    string root = EngineAddress.ToRoot(addr);
                    if (root == null) continue;
                    Uri u = new Uri(root);
                    var e = new MdnsEngine { Id = id, Name = (string)o["name"] ?? "", Host = (string)o["host"] ?? "", Port = u.Port };
                    int api; if (int.TryParse(((string)o["api"]) ?? "", out api)) e.Api = api;
                    System.Net.IPAddress ip;
                    if (System.Net.IPAddress.TryParse(u.Host.Trim('[', ']'), out ip)) e.Addrs.Add(ip);
                    else e.Host = u.Host;
                    list.Add(e);
                }
            }
            catch { list.Clear(); }
            return list;
        }

        /// <summary>The root to call for an engine seen by mDNS or find: its address, else its host name.</summary>
        public static string RootOf(MdnsEngine e)
        {
            string a = e.Address;
            if (a.Length == 0 && e.Host.Length > 0 && e.Port > 0) a = e.Host.TrimEnd('.') + ":" + e.Port;
            return EngineAddress.ToRoot(a);
        }

        /// <summary>Every root worth trying for an engine, best first: IPv4 addresses, other IPv6, the packet's source, the SRV host.</summary>
        public static List<string> RootsOf(MdnsEngine e)
        {
            var outList = new List<string>();
            void Add(string hostPort) { var r = EngineAddress.ToRoot(hostPort); if (r != null && !outList.Contains(r)) outList.Add(r); }
            if (e.Port <= 0) return outList;
            string best = e.Address;
            if (best.Length > 0) Add(best);
            foreach (var a in e.Addrs)
            {
                if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) Add(a + ":" + e.Port);
                else if (!a.IsIPv6LinkLocal) Add("[" + a + "]:" + e.Port);
            }
            if (e.From != null) Add((e.From.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + e.From + "]" : e.From.ToString()) + ":" + e.Port);
            if (e.Host.Length > 0) Add(e.Host.TrimEnd('.') + ":" + e.Port);
            return outList;
        }

        /// <summary>Calls hello on each root of <paramref name="e"/> until one answers as that engine; (null, null) when none does.</summary>
        public static async Task<(string root, EngineHello hello)> FirstAnswering(MdnsEngine e, PairingApi api, string token, CancellationToken ct)
        {
            var (r, h, _) = await FirstAnsweringFor(e, api, token, ct).ConfigureAwait(false);
            return (r, h);
        }

        /// <summary>
        /// As <see cref="FirstAnswering"/>; the token (held for e.Id) is sent only to a root whose anonymous hello shows
        /// that engine_id (an mDNS id is unauthenticated). <c>sentToken</c>: the hello returned was asked with it.
        /// </summary>
        public static async Task<(string root, EngineHello hello, bool sentToken)> FirstAnsweringFor(MdnsEngine e, PairingApi api, string token, CancellationToken ct)
        {
            foreach (var r in RootsOf(e))
            {
                var (h, _, sent) = await api.HelloForAsync(r, e.Id, token, ct).ConfigureAwait(false);
                if (h != null && h.IsEasyEngine && h.EngineId == e.Id) return (r, h, sent);
            }
            return (null, null, false);
        }

        public static async Task<string> DefaultRun(string path, string args, int timeoutMs)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var psi = new ProcessStartInfo(path, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    var done = await Task.WhenAny(outTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (done != outTask) { try { p.Kill(); } catch { } return null; }
                    try { p.WaitForExit(1000); } catch { }
                    return await outTask.ConfigureAwait(false);
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// The whole search. <paramref name="binaryPath"/> is the shipped easy-engine (may be null or missing).
        /// Never throws.
        /// </summary>
        public static async Task<List<FoundEngine>> FindAsync(PairStore store, string binaryPath, PairingApi api = null,
            EngineReconnect.Browse browse = null, RunProgram run = null, int waitMs = 2000, CancellationToken ct = default(CancellationToken))
        {
            api = api ?? new PairingApi();
            browse = browse ?? ((w, c) => Mdns.BrowseAsync(w, c));
            run = run ?? DefaultRun;
            var seen = new List<MdnsEngine>();
            try { Mdns.Merge(seen, await browse(waitMs, ct).ConfigureAwait(false)); } catch { }
            if (seen.Count == 0 && !string.IsNullOrEmpty(binaryPath))
            {
                string outText = null;
                try { outText = await run(binaryPath, "find --json --wait " + Math.Max(1, waitMs / 1000) + "s", waitMs + 5000).ConfigureAwait(false); } catch { }
                Mdns.Merge(seen, ParseFindJson(outText));
            }
            var tasks = seen.Select(async m =>
            {
                var f = new FoundEngine { EngineId = m.Id, Name = m.Name, Root = RootOf(m) ?? "" };
                var pe = store != null ? store.Get(m.Id) : null;
                f.HaveToken = pe != null && pe.HasToken;
                var (root, h) = await FirstAnswering(m, api, null, ct).ConfigureAwait(false);
                if (h != null)
                {
                    f.Root = root;
                    f.Answered = true; f.ModelName = h.ModelName; f.Phase = h.Phase; f.NeedsToken = h.TokenRequired;
                    if (h.Name.Length > 0) f.Name = h.Name;
                }
                return f;
            }).ToList();
            var outList = new List<FoundEngine>();
            foreach (var t in tasks) { try { outList.Add(await t.ConfigureAwait(false)); } catch { } }
            return outList.Where(f => f.Root.Length > 0).OrderByDescending(f => f.Answered).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A typed address: hello there, as one list row (Answered false when it did not answer as an easyEngine).</summary>
        public static async Task<FoundEngine> HelloAtAsync(string typed, PairStore store, PairingApi api = null, CancellationToken ct = default(CancellationToken))
        {
            api = api ?? new PairingApi();
            string root = EngineAddress.ToRoot(typed);
            var f = new FoundEngine { Root = root ?? "", Name = typed ?? "" };
            if (root == null) return f;
            var (h, _) = await api.HelloAsync(root, null, ct).ConfigureAwait(false);
            if (h != null && h.IsEasyEngine)
            {
                f.Answered = true; f.EngineId = h.EngineId; f.Name = h.Name; f.ModelName = h.ModelName; f.Phase = h.Phase; f.NeedsToken = h.TokenRequired;
                f.HaveToken = store != null && store.HasToken(h.EngineId);
            }
            return f;
        }
    }
}
