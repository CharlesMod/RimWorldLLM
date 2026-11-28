using System;
using System.Linq;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Game-side owner of "Share this PC's engine on my network" (host side). Keeps the running local engine in step
    /// with the toggle (a live PUT, no restart), follows the engine's events stream while sharing for pairing requests,
    /// and opens the Allow/Deny window on the game thread. Same-PC is the default: with the toggle off nothing here
    /// talks to the engine except to turn a share made elsewhere back off.
    /// </summary>
    public static class EngineShareManager
    {
        public static readonly ShareHost Host = new ShareHost(() => EngineManager.LocalSource ? EngineManager.Client : null);
        static EngineEventWatch _watch;
        static readonly object _lk = new object();
        static volatile string _name = "";

        /// <summary>The name guests see (the engine's hello name; this PC's name until it answered).</summary>
        public static string EngineName { get { var n = _name; return n.Length > 0 ? n : Environment.MachineName; } }

        static bool Wanted
        {
            get { var s = RimWorldLLMMod.Settings; return s != null && s.shareEngine && EngineManager.LocalSource; }
        }

        /// <summary>After the mod attached to (or started) its local engine.</summary>
        public static void AfterAttach() { Apply(); }

        /// <summary>The toggle changed, or the LLM source did.</summary>
        public static void SettingsChanged() { Apply(); }

        static void Apply()
        {
            bool want = Wanted;
            SyncWatch(want);
            if (!EngineManager.LocalSource) { Host.Book.EndAll(); return; }
            if (EngineManager.Client == null) return;   // applied on the next attach
            Task.Run(async () =>
            {
                try
                {
                    await Host.ApplyAsync(want).ConfigureAwait(false);
                    var c = EngineManager.Client;
                    if (want && c != null)
                    {
                        var (h, _) = await new PairingApi().HelloAsync(c.BaseUrl, null).ConfigureAwait(false);
                        if (h != null && !string.IsNullOrEmpty(h.Name)) _name = h.Name;
                    }
                    if (want) await Host.RefreshClientsAsync().ConfigureAwait(false);
                }
                catch { }
            });
        }

        static void SyncWatch(bool want)
        {
            lock (_lk)
            {
                if (want)
                {
                    if (_watch == null)
                        _watch = new EngineEventWatch(() => { var c = EngineManager.Client; return c != null ? c.BaseUrl : null; },
                            () => Task.Run(() => Host.RefreshPendingAsync()),
                            () => Host.Book.EndAll());
                    _watch.Start();
                }
                else if (_watch != null)
                {
                    _watch.Stop();
                    _watch = null;
                    Host.Book.EndAll();
                }
            }
        }

        /// <summary>Status poll result (the settings page polls about every 2 s): addresses and whether the engine shares.</summary>
        public static void NoteStatus(EngineStatus st) { Host.Note(st); }

        public static void Answer(string key, bool allow) { Task.Run(() => Host.AnswerAsync(key, allow)); }
        public static void RefreshClients() { Task.Run(() => Host.RefreshClientsAsync()); }
        public static void Remove(string clientId) { Task.Run(() => Host.RemoveAsync(clientId)); }

        /// <summary>Every frame, game thread: a waiting request that is not quiet opens the Allow/Deny window.</summary>
        public static void Tick()
        {
            if (_watch == null) return;
            var cur = Host.Book.Current;
            if (cur == null || Find.WindowStack == null) return;
            if (Find.WindowStack.Windows.Any(w => w is Window_PairRequest)) return;
            Find.WindowStack.Add(new Window_PairRequest(cur.Key));
        }
    }
}
