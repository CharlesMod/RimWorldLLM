using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Guest side, "Use an engine on another PC on my network": one list from the LAN (mDNS, else the shipped
    /// binary's find) and the tailnet (tailscale status --json, a hello on each online peer), merged by engine_id,
    /// each row badged LAN and/or Tailscale. Run once per open of the list, never on a timer. Hellos carry no token.
    /// </summary>
    public static class GuestDiscovery
    {
        /// <summary>One row per engine: LAN rows keep their root; a tailnet-only engine gets its tailnet root.</summary>
        public static List<FoundEngine> Merge(IEnumerable<FoundEngine> lan, IEnumerable<ShareFound> tail, PairStore store)
        {
            var rows = new List<FoundEngine>();
            foreach (var f in lan ?? Enumerable.Empty<FoundEngine>())
            {
                if (f == null) continue;
                var k = NetKindOf.Of(f.Root);
                if (k == NetKind.Tailscale) f.ViaTailscale = true; else f.ViaLan = true;
                rows.Add(f);
            }
            foreach (var t in tail ?? Enumerable.Empty<ShareFound>())
            {
                if (t == null || t.Roots.Count == 0) continue;
                string id = (t.EngineId ?? "").ToLowerInvariant();
                var same = id.Length > 0 ? rows.FirstOrDefault(r => (r.EngineId ?? "").ToLowerInvariant() == id) : null;
                if (same != null)
                {
                    same.ViaTailscale = true;
                    if (!same.Answered)
                    {
                        // mDNS saw it, its LAN hello did not answer, the tailnet one did: use the tailnet root
                        same.Root = t.Roots[0]; same.Answered = true; same.ModelName = t.ModelName; same.NeedsToken = t.TokenRequired;
                        if (t.Name.Length > 0) same.Name = t.Name;
                    }
                    continue;
                }
                rows.Add(new FoundEngine
                {
                    EngineId = id, Name = t.Name, Root = t.Roots[0], ModelName = t.ModelName, Answered = true,
                    NeedsToken = t.TokenRequired, ViaTailscale = true, ViaLan = false,
                    HaveToken = store != null && id.Length > 0 && store.HasToken(id),
                });
            }
            return rows.OrderByDescending(f => f.Answered).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Both searches at once; neither can hold the other up past its own timeouts. Never throws.</summary>
        public static async Task<List<FoundEngine>> FindAllAsync(Func<Task<List<FoundEngine>>> lan, Func<Task<List<ShareFound>>> tail, PairStore store)
        {
            Task<List<FoundEngine>> a = null;
            Task<List<ShareFound>> b = null;
            try { a = lan != null ? lan() : null; } catch { }
            try { b = tail != null ? tail() : null; } catch { }
            List<FoundEngine> la = null;
            List<ShareFound> lb = null;
            try { if (a != null) la = await a.ConfigureAwait(false); } catch { }
            try { if (b != null) lb = await b.ConfigureAwait(false); } catch { }
            return Merge(la, lb, store);
        }

        /// <summary>The tailnet search with the real CLI and a token-free hello.</summary>
        public static Task<List<ShareFound>> TailnetAsync(string os, PairingApi api = null, CancellationToken ct = default(CancellationToken))
        {
            api = api ?? new PairingApi();
            string cli = TailscaleCli.Find(os, Environment.GetEnvironmentVariable, File.Exists);
            if (cli == null) return Task.FromResult(new List<ShareFound>());
            return TailnetDiscovery.FindAsync(
                (p, args, t) => EngineFinder.DefaultRun(p, string.Join(" ", args), t).GetAwaiter().GetResult(),
                cli,
                async (root, c) => { var (h, _) = await api.HelloAsync(root, null, c).ConfigureAwait(false); return h; },
                4, 1000, ct);
        }
    }
}
