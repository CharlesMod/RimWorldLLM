using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldLLM.Bridge
{
    /// <summary>One easyEngine seen on the network by mDNS (docs/ENGINE.md §7f in the easyEngine repo).</summary>
    public sealed class MdnsEngine
    {
        public string Instance = "";   // "den-pc [9f2c4e]._easy-engine._tcp.local."
        public string Id = "";         // TXT id= (the engine_id)
        public string Name = "";       // TXT name=
        public int Api;                // TXT api=
        public string Host = "";       // SRV target
        public int Port;               // SRV port
        public readonly List<IPAddress> Addrs = new List<IPAddress>();
        public IPAddress From;         // the packet's source address (a fallback when no A record came)

        /// <summary>The address to call, "host:port": an IPv4 address first, then IPv6 (in brackets), then the source address.</summary>
        public string Address
        {
            get
            {
                if (Port <= 0) return "";
                var v4 = Addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) return v4 + ":" + Port;
                var v6 = Addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal)
                         ?? Addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                if (v6 != null) return "[" + (v6.IsIPv6LinkLocal ? new IPAddress(v6.GetAddressBytes()).ToString() : v6.ToString()) + "]:" + Port;
                if (From != null) return (From.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + From + "]" : From.ToString()) + ":" + Port;
                return "";
            }
        }
    }

    /// <summary>
    /// A small, dependency-free mDNS/DNS-SD browser for <c>_easy-engine._tcp.local.</c>: one PTR query sent from an
    /// ephemeral port (so engines answer by unicast, RFC 6762 §6.7) and a parser for the PTR/SRV/TXT/A/AAAA records of
    /// the replies. Parsing never throws: a malformed packet yields nothing.
    /// </summary>
    public static class Mdns
    {
        public const string Service = "_easy-engine._tcp.local.";
        public static readonly IPAddress Group4 = IPAddress.Parse("224.0.0.251");
        public static readonly IPAddress Group6 = IPAddress.Parse("ff02::fb");
        public const int Port = 5353;

        const ushort TypeA = 1, TypePTR = 12, TypeTXT = 16, TypeAAAA = 28, TypeSRV = 33;

        /// <summary>The PTR question for the service (id 0, no flags, class IN), byte for byte what <c>easy-engine find</c> sends.</summary>
        public static byte[] BuildQuery()
        {
            var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in Service.TrimEnd('.').Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                b.Add((byte)bytes.Length);
                b.AddRange(bytes);
            }
            b.Add(0);
            b.Add(0); b.Add((byte)TypePTR);
            b.Add(0); b.Add(1);
            return b.ToArray();
        }

        sealed class Rec
        {
            public string Name; public ushort Type; public uint Ttl;
            public string Target; public int Port; public List<string> Txt; public IPAddress Ip;
        }

        /// <summary>Parses one reply into the engines it names. Records with TTL 0 (a goodbye) are dropped.</summary>
        public static List<MdnsEngine> Parse(byte[] msg, IPAddress from = null)
        {
            var outList = new List<MdnsEngine>();
            if (msg == null || msg.Length < 12) return outList;
            try
            {
                int flags = (msg[2] << 8) | msg[3];
                if ((flags & 0x8000) == 0) return outList;    // a query, not a response
                int qd = U16(msg, 4), an = U16(msg, 6), ns = U16(msg, 8), ar = U16(msg, 10);
                int p = 12;
                for (int i = 0; i < qd; i++) { ReadName(msg, ref p); p += 4; if (p > msg.Length) return outList; }
                var recs = new List<Rec>();
                int total = an + ns + ar;
                for (int i = 0; i < total; i++)
                {
                    var r = ReadRecord(msg, ref p);
                    if (r == null) break;
                    if (r.Ttl == 0) continue;
                    recs.Add(r);
                }
                var instances = new List<string>();
                foreach (var r in recs)
                    if (r.Type == TypePTR && Eq(r.Name, Service) && r.Target != null && !instances.Any(x => Eq(x, r.Target))) instances.Add(r.Target);
                // A reply may carry SRV/TXT for an instance without its PTR (an answer to a direct query): take those too.
                foreach (var r in recs)
                    if ((r.Type == TypeSRV || r.Type == TypeTXT) && r.Name.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase) && !instances.Any(x => Eq(x, r.Name))) instances.Add(r.Name);
                foreach (var inst in instances)
                {
                    var e = new MdnsEngine { Instance = inst, From = from };
                    foreach (var r in recs)
                    {
                        if (!Eq(r.Name, inst)) continue;
                        if (r.Type == TypeSRV) { e.Host = r.Target ?? ""; e.Port = r.Port; }
                        else if (r.Type == TypeTXT && r.Txt != null)
                            foreach (var kv in r.Txt)
                            {
                                int eq = kv.IndexOf('=');
                                if (eq <= 0) continue;
                                string k = kv.Substring(0, eq).ToLowerInvariant(), v = kv.Substring(eq + 1);
                                if (k == "id") e.Id = v.Trim().ToLowerInvariant();
                                else if (k == "name") e.Name = v;
                                else if (k == "api") int.TryParse(v, out e.Api);
                            }
                    }
                    if (e.Host.Length > 0)
                        foreach (var r in recs)
                            if ((r.Type == TypeA || r.Type == TypeAAAA) && Eq(r.Name, e.Host) && r.Ip != null && !e.Addrs.Contains(r.Ip)) e.Addrs.Add(r.Ip);
                    if (e.Id.Length == 0 || e.Port <= 0) continue;    // not enough to call it
                    if (e.Name.Length == 0) e.Name = InstanceLabel(inst);
                    outList.Add(e);
                }
            }
            catch { outList.Clear(); }
            return outList;
        }

        /// <summary>Merges engines into <paramref name="into"/> by engine id (addresses added, first-seen fields kept).</summary>
        public static void Merge(List<MdnsEngine> into, IEnumerable<MdnsEngine> found)
        {
            foreach (var f in found)
            {
                var have = into.FirstOrDefault(x => x.Id == f.Id);
                if (have == null) { into.Add(f); continue; }
                foreach (var a in f.Addrs) if (!have.Addrs.Contains(a)) have.Addrs.Add(a);
                if (have.Port <= 0) have.Port = f.Port;
            }
        }

        static string InstanceLabel(string inst)
        {
            int i = inst.IndexOf("." + Service, StringComparison.OrdinalIgnoreCase);
            return i > 0 ? inst.Substring(0, i) : inst;
        }

        static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        static int U16(byte[] m, int p) { if (p + 2 > m.Length) throw new IndexOutOfRangeException(); return (m[p] << 8) | m[p + 1]; }

        static Rec ReadRecord(byte[] m, ref int p)
        {
            var r = new Rec { Name = ReadName(m, ref p) };
            if (p + 10 > m.Length) return null;
            r.Type = (ushort)U16(m, p);
            r.Ttl = (uint)((m[p + 4] << 24) | (m[p + 5] << 16) | (m[p + 6] << 8) | m[p + 7]);
            int len = U16(m, p + 8);
            p += 10;
            int end = p + len;
            if (end > m.Length) return null;
            switch (r.Type)
            {
                case TypePTR: { int q = p; r.Target = ReadName(m, ref q); break; }
                case TypeSRV:
                    {
                        if (len < 7) break;
                        r.Port = U16(m, p + 4);
                        int q = p + 6; r.Target = ReadName(m, ref q);
                        break;
                    }
                case TypeTXT:
                    {
                        r.Txt = new List<string>();
                        int q = p;
                        while (q < end)
                        {
                            int l = m[q++];
                            if (q + l > end) break;
                            r.Txt.Add(Encoding.UTF8.GetString(m, q, l));
                            q += l;
                        }
                        break;
                    }
                case TypeA: if (len == 4) r.Ip = new IPAddress(new[] { m[p], m[p + 1], m[p + 2], m[p + 3] }); break;
                case TypeAAAA: if (len == 16) { var b = new byte[16]; Array.Copy(m, p, b, 0, 16); r.Ip = new IPAddress(b); } break;
            }
            p = end;
            return r;
        }

        /// <summary>Reads a (possibly compressed) DNS name; bounded against pointer loops.</summary>
        static string ReadName(byte[] m, ref int p)
        {
            var sb = new StringBuilder();
            int q = p, jumps = 0;
            bool jumped = false;
            while (true)
            {
                if (q >= m.Length) throw new IndexOutOfRangeException();
                int l = m[q];
                if (l == 0) { q++; break; }
                if ((l & 0xC0) == 0xC0)
                {
                    if (q + 1 >= m.Length || ++jumps > 32) throw new IndexOutOfRangeException();
                    int ptr = ((l & 0x3F) << 8) | m[q + 1];
                    if (!jumped) { p = q + 2; jumped = true; }
                    q = ptr;
                    continue;
                }
                if ((l & 0xC0) != 0) throw new FormatException("bad label");
                if (q + 1 + l > m.Length) throw new IndexOutOfRangeException();
                sb.Append(Encoding.UTF8.GetString(m, q + 1, l)).Append('.');
                q += 1 + l;
                if (sb.Length > 1024) throw new FormatException("name too long");
            }
            if (!jumped) p = q;
            if (sb.Length == 0) sb.Append('.');
            return sb.ToString();
        }

        // ---- the network part ----------------------------------------------------------------------------------

        /// <summary>
        /// Sends the query on every up, multicast-capable interface (IPv4 first, then IPv6) and collects the answers
        /// that arrive within <paramref name="waitMs"/>. Never throws; an interface that refuses is skipped.
        /// </summary>
        public static async Task<List<MdnsEngine>> BrowseAsync(int waitMs = 2000, CancellationToken ct = default(CancellationToken))
        {
            var found = new List<MdnsEngine>();
            var sockets = new List<UdpClient>();
            var query = BuildQuery();
            try
            {
                foreach (var (addr, index) in Interfaces())
                {
                    UdpClient u = null;
                    try
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            u = new UdpClient(new IPEndPoint(addr, 0));
                            u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                            u.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                            await u.SendAsync(query, query.Length, new IPEndPoint(Group4, Port)).ConfigureAwait(false);
                        }
                        else
                        {
                            u = new UdpClient(new IPEndPoint(addr, 0));
                            u.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, index);
                            await u.SendAsync(query, query.Length, new IPEndPoint(new IPAddress(Group6.GetAddressBytes(), index), Port)).ConfigureAwait(false);
                        }
                        sockets.Add(u);
                    }
                    catch { try { if (u != null) u.Dispose(); } catch { } }
                }
                if (sockets.Count == 0) return found;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(waitMs);
                    var readers = sockets.Select(u => ReadAll(u, found, cts.Token)).ToArray();
                    try { await Task.WhenAll(readers).ConfigureAwait(false); } catch { }
                }
            }
            finally { foreach (var u in sockets) try { u.Dispose(); } catch { } }
            lock (found) return found.ToList();
        }

        static async Task ReadAll(UdpClient u, List<MdnsEngine> found, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await u.ReceiveAsync(ct).ConfigureAwait(false); }
                catch { return; }
                var got = Parse(r.Buffer, r.RemoteEndPoint.Address);
                lock (found) Merge(found, got);
            }
        }

        static IEnumerable<(IPAddress, int)> Interfaces()
        {
            var v4 = new List<(IPAddress, int)>();
            var v6 = new List<(IPAddress, int)>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return v4; }
            foreach (var ni in all)
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (!ni.SupportsMulticast) continue;
                    var props = ni.GetIPProperties();
                    int idx6 = -1;
                    try { var p6 = props.GetIPv6Properties(); if (p6 != null) idx6 = p6.Index; } catch { }
                    foreach (var ua in props.UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (IPAddress.IsLoopback(a)) continue;
                        if (a.AddressFamily == AddressFamily.InterNetwork) v4.Add((a, 0));
                        else if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal && idx6 >= 0) v6.Add((a, idx6));
                    }
                }
                catch { }
            }
            return v4.Concat(v6);
        }
    }
}
