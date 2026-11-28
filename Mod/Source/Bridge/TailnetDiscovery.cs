using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Finding easyEngines on the player's Tailscale tailnet: read `tailscale status --json`, hello each online
    // peer on the engine port (no token), merge with the LAN list. No game references; nothing here runs on a timer.

    /// <summary>One node of the tailnet.</summary>
    public sealed class TailnetPeer
    {
        public string HostName = "";
        public string DnsName = "";
        public string Os = "";
        public bool Online;
        public string IPv4 = "";
        public string IPv6 = "";
    }

    /// <summary>The parts of <c>tailscale status --json</c> the engine search needs.</summary>
    public sealed class TailnetStatus
    {
        public const int MaxTargets = 32;
        public string BackendState = "";
        public bool Running { get { return BackendState == "Running"; } }
        public TailnetPeer Self = new TailnetPeer();
        public List<TailnetPeer> Peers = new List<TailnetPeer>();

        /// <summary>Parses the JSON; null when it is not a status object. Never throws.</summary>
        public static TailnetStatus Parse(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                var j = JToken.Parse(json) as JObject;
                if (j == null || j["BackendState"] == null) return null;
                var s = new TailnetStatus { BackendState = Str(j["BackendState"]) };
                s.Self = ReadPeer(j["Self"] as JObject) ?? new TailnetPeer();
                var peers = j["Peer"] as JObject;
                if (peers != null)
                    foreach (var kv in peers)
                    {
                        var p = ReadPeer(kv.Value as JObject);
                        if (p != null) s.Peers.Add(p);
                    }
                return s;
            }
            catch { return null; }
        }

        static string Str(JToken t) { return t != null && t.Type == JTokenType.String ? ((string)t ?? "") : ""; }

        static TailnetPeer ReadPeer(JObject o)
        {
            if (o == null) return null;
            var p = new TailnetPeer { HostName = Str(o["HostName"]), DnsName = Str(o["DNSName"]), Os = Str(o["OS"]) };
            var on = o["Online"];
            p.Online = on != null && on.Type == JTokenType.Boolean && (bool)on;
            var ips = o["TailscaleIPs"] as JArray;
            if (ips != null)
                foreach (var t in ips)
                {
                    IPAddress a;
                    if (t.Type != JTokenType.String || !IPAddress.TryParse((string)t, out a)) continue;
                    if (a.AddressFamily == AddressFamily.InterNetwork) { if (p.IPv4.Length == 0) p.IPv4 = a.ToString(); }
                    else if (a.AddressFamily == AddressFamily.InterNetworkV6) { if (p.IPv6.Length == 0) p.IPv6 = a.ToString(); }
                }
            return p;
        }

        /// <summary>Roots to hello: online peers (not self) with an address, IPv4 first, at most 32, by host name.</summary>
        public List<string> ProbeTargets()
        {
            var roots = new List<string>();
            if (!Running) return roots;
            foreach (var p in Peers.Where(x => x.Online && (x.IPv4.Length > 0 || x.IPv6.Length > 0))
                                   .OrderBy(x => x.HostName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DnsName, StringComparer.Ordinal))
            {
                if (SameNode(p, Self)) continue;
                string root = p.IPv4.Length > 0
                    ? "http://" + p.IPv4 + ":" + EngineAddress.DefaultPort
                    : "http://[" + p.IPv6 + "]:" + EngineAddress.DefaultPort;
                if (!roots.Contains(root)) roots.Add(root);
                if (roots.Count >= MaxTargets) break;
            }
            return roots;
        }

        static bool SameNode(TailnetPeer a, TailnetPeer b)
        {
            if (b == null) return false;
            if (a.IPv4.Length > 0 && a.IPv4 == b.IPv4) return true;
            if (a.IPv6.Length > 0 && a.IPv6 == b.IPv6) return true;
            return false;
        }
    }

    /// <summary>Where the tailscale command line tool lives.</summary>
    public static class TailscaleCli
    {
        /// <summary>
        /// The CLI path, or null when not installed. <paramref name="os"/> is "windows", "darwin" or "linux"
        /// (as EngineLauncher names them).
        /// </summary>
        public static string Find(string os, Func<string, string> env, Func<string, bool> fileExists)
        {
            if (env == null) env = _ => null;
            if (fileExists == null) return null;
            bool win = os == "windows";
            string path = env("PATH") ?? "";
            char sep = win ? ';' : ':';
            char dirSep = win ? '\\' : '/';
            string exe = win ? "tailscale.exe" : "tailscale";
            foreach (var d0 in path.Split(sep))
            {
                string d = d0.Trim().Trim('"');
                if (d.Length == 0) continue;
                string cand = d.TrimEnd('/', '\\') + dirSep + exe;
                if (fileExists(cand)) return cand;
            }
            if (os == "darwin")
            {
                const string app = "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
                if (fileExists(app)) return app;
            }
            if (win)
            {
                foreach (var v in new[] { "ProgramFiles", "ProgramW6432" })
                {
                    string pf = env(v);
                    if (string.IsNullOrWhiteSpace(pf)) continue;
                    string cand = pf.TrimEnd('/', '\\') + "\\Tailscale\\tailscale.exe";
                    if (fileExists(cand)) return cand;
                }
            }
            return null;
        }
    }

    public enum NetKind { Lan, Tailscale, Loopback }

    public static class NetKindOf
    {
        /// <summary>Classifies "host", "host:port", "[v6]:port", a bare IP or an http root.</summary>
        public static NetKind Of(string addressOrRoot)
        {
            string host = HostOf(addressOrRoot);
            if (host.Length == 0) return NetKind.Lan;
            IPAddress a;
            if (IPAddress.TryParse(host, out a))
            {
                if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
                if (IPAddress.IsLoopback(a)) return NetKind.Loopback;
                byte[] b = a.GetAddressBytes();
                if (b.Length == 4) return b[0] == 100 && (b[1] & 0xC0) == 64 ? NetKind.Tailscale : NetKind.Lan;
                bool ts = b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
                return ts ? NetKind.Tailscale : NetKind.Lan;
            }
            string h = host.TrimEnd('.').ToLowerInvariant();
            if (h == "localhost" || h.EndsWith(".localhost")) return NetKind.Loopback;
            if (h.EndsWith(".ts.net")) return NetKind.Tailscale;
            return NetKind.Lan;
        }

        static string HostOf(string s)
        {
            s = (s ?? "").Trim();
            int i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 3);
            int slash = s.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) s = s.Substring(0, slash);
            int at = s.LastIndexOf('@');
            if (at >= 0) s = s.Substring(at + 1);
            if (s.StartsWith("["))
            {
                int close = s.IndexOf(']');
                return close > 0 ? s.Substring(1, close - 1) : "";
            }
            IPAddress a;
            if (IPAddress.TryParse(s, out a)) return s;   // bare IPv6 without brackets, or IPv4
            int colon = s.LastIndexOf(':');
            if (colon >= 0) s = s.Substring(0, colon);
            return s;
        }
    }

    /// <summary>One engine in the share list, seen by LAN and/or tailnet.</summary>
    public sealed class ShareFound
    {
        public string EngineId = "";
        public string Name = "";
        public string ModelName = "";
        public string ModelId = "";
        public List<string> Roots = new List<string>();
        public bool ViaLan;
        public bool ViaTailscale;
        public bool Pairing;     // hello: pairing is on
        public bool TokenRequired;

        public static ShareFound From(FoundEngine f)
        {
            var s = new ShareFound { EngineId = (f.EngineId ?? "").Trim().ToLowerInvariant(), Name = f.Name ?? "", ModelName = f.ModelName ?? "", TokenRequired = f.NeedsToken };
            if (!string.IsNullOrEmpty(f.Root)) { s.Roots.Add(f.Root); s.Mark(f.Root); }
            if (s.Roots.Count == 0) s.ViaLan = true;
            return s;
        }

        public static ShareFound From(string root, EngineHello h)
        {
            var s = new ShareFound { EngineId = h.EngineId, Name = h.Name, ModelName = h.ModelName, ModelId = h.ModelId, Pairing = h.Pairing, TokenRequired = h.TokenRequired };
            s.Roots.Add(root); s.Mark(root);
            return s;
        }

        void Mark(string root) { if (NetKindOf.Of(root) == NetKind.Tailscale) ViaTailscale = true; else ViaLan = true; }

        /// <summary>One row per engine_id (lower case), LAN roots first, flags combined; rows without an id are kept by root.</summary>
        public static List<ShareFound> Merge(IEnumerable<FoundEngine> lan, IEnumerable<ShareFound> tail)
        {
            return Merge((lan ?? Enumerable.Empty<FoundEngine>()).Where(f => f != null).Select(From), tail);
        }

        public static List<ShareFound> Merge(IEnumerable<ShareFound> lan, IEnumerable<ShareFound> tail)
        {
            var rows = new List<ShareFound>();
            var byId = new Dictionary<string, ShareFound>();
            foreach (var src in (lan ?? Enumerable.Empty<ShareFound>()).Concat(tail ?? Enumerable.Empty<ShareFound>()))
            {
                if (src == null) continue;
                string id = (src.EngineId ?? "").Trim().ToLowerInvariant();
                ShareFound row = null;
                if (id.Length > 0) byId.TryGetValue(id, out row);
                else row = rows.FirstOrDefault(r => r.EngineId.Length == 0 && src.Roots.Any(x => r.Roots.Contains(x)));
                if (row == null)
                {
                    row = new ShareFound { EngineId = id };
                    rows.Add(row);
                    if (id.Length > 0) byId[id] = row;
                    row.Name = src.Name; row.ModelName = src.ModelName; row.ModelId = src.ModelId;
                }
                else
                {
                    if (string.IsNullOrEmpty(row.Name)) row.Name = src.Name;
                    if (string.IsNullOrEmpty(row.ModelName)) row.ModelName = src.ModelName;
                    if (string.IsNullOrEmpty(row.ModelId)) row.ModelId = src.ModelId;
                }
                foreach (var r in src.Roots) if (!row.Roots.Contains(r)) row.Roots.Add(r);
                row.ViaLan |= src.ViaLan; row.ViaTailscale |= src.ViaTailscale;
                row.Pairing |= src.Pairing; row.TokenRequired |= src.TokenRequired;
            }
            return rows;
        }
    }

    public static class TailnetDiscovery
    {
        /// <summary>
        /// Reads the tailnet status once, then hellos each online peer on the engine port without a token.
        /// <paramref name="run"/> runs a program (path, args, timeout ms) and returns stdout or null. Never throws;
        /// not installed, not running or no peers gives an empty list. Call once per open of the list.
        /// </summary>
        public static async Task<List<ShareFound>> FindAsync(Func<string, string[], int, string> run, string cliPath,
            Func<string, CancellationToken, Task<EngineHello>> hello, int maxParallel = 4, int timeoutMs = 1000, CancellationToken ct = default(CancellationToken))
        {
            var found = new List<ShareFound>();
            try
            {
                if (run == null || hello == null || string.IsNullOrEmpty(cliPath) || ct.IsCancellationRequested) return found;
                string json = null;
                try { json = await Task.Run(() => run(cliPath, new[] { "status", "--json" }, 3000)).ConfigureAwait(false); } catch { }
                var st = TailnetStatus.Parse(json);
                if (st == null || !st.Running) return found;
                var targets = st.ProbeTargets();
                if (targets.Count == 0) return found;
                int par = Math.Max(1, maxParallel);
                int limit = Math.Max(1, timeoutMs);
                var results = new ShareFound[targets.Count];
                using (var gate = new SemaphoreSlim(par))
                {
                    var tasks = targets.Select((root, i) => Task.Run(async () =>
                    {
                        try { await gate.WaitAsync(ct).ConfigureAwait(false); } catch { return; }
                        try { results[i] = await ProbeOne(root, hello, limit, ct).ConfigureAwait(false); }
                        finally { gate.Release(); }
                    })).ToArray();
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                }
                foreach (var r in results) if (r != null) found.Add(r);
                return ShareFound.Merge(new ShareFound[0], found);
            }
            catch { return found; }
        }

        static async Task<ShareFound> ProbeOne(string root, Func<string, CancellationToken, Task<EngineHello>> hello, int timeoutMs, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);
                try
                {
                    var t = Task.Run(() => hello(root, cts.Token));
                    var done = await Task.WhenAny(t, Task.Delay(timeoutMs + 200, ct)).ConfigureAwait(false);
                    if (done != t) { cts.Cancel(); var _ = t.ContinueWith(x => { var e = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted); return null; }
                    var h = await t.ConfigureAwait(false);
                    if (h == null || !h.IsEasyEngine) return null;
                    return ShareFound.From(root, h);
                }
                catch { return null; }
            }
        }
    }
}
