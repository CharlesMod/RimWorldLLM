// Director, part 5: the two per-listener sections of a line prompt, built with the salience picker:
//   === WHAT STANDS OUT RIGHT NOW ===   the 3 to 6 facts of the moment that a line may be about
//   === WHO YOU ARE TALKING TO ===      the listener in words, and the last few things said between the two
// They are fixed for a lease (the scene does not change under it), so they ride with the frozen prefix and stay cache-safe.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public sealed partial class Director
    {
        public const int StandsOutMin = 3, StandsOutMax = 6;
        public const int PairKeep = 6, PairShown = 3, SalientMemoryKeep = 3;
        /// <summary>Game time units a said line stays "recent" for the listener section (six game hours).</summary>
        public const long PairRecentTicks = 15000;

        private readonly Dictionary<int, List<List<string>>> _salientRecent = new Dictionary<int, List<List<string>>>();
        private readonly Dictionary<(int, int), List<(int Speaker, string Text, long Game)>> _pairLines = new Dictionary<(int, int), List<(int, string, long)>>();

        /// <summary>A line one speaker said to the other, kept so the next line between them can pick it up.</summary>
        public void RememberPair(int speaker, int listener, string text, long game)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var key = speaker < listener ? (speaker, listener) : (listener, speaker);
            List<(int, string, long)> l;
            if (!_pairLines.TryGetValue(key, out l)) { l = new List<(int, string, long)>(); _pairLines[key] = l; }
            l.Add((speaker, text, game));
            if (l.Count > PairKeep) l.RemoveRange(0, l.Count - PairKeep);
        }

        /// <summary>The last few lines between the two, oldest first, only the recent ones.</summary>
        public List<(int Speaker, string Text)> PairLines(int a, int b, long nowGame)
        {
            List<(int, string, long)> l;
            if (!_pairLines.TryGetValue(a < b ? (a, b) : (b, a), out l)) return new List<(int, string)>();
            return l.Where(x => nowGame - x.Item3 <= PairRecentTicks).Select(x => (x.Item1, x.Item2)).Reverse().Take(PairShown).Reverse().ToList();
        }

        private List<JObject> EventsForFacts(Mind mind, IEnumerable<LedgerEntry> led, long nowGame, long day)
        {
            var o = new List<JObject>();
            foreach (var e in led)
                o.Add(new JObject { ["type"] = e.Etype ?? "", ["summary"] = e.Summary ?? "", ["game"] = e.OpenedTime });
            foreach (var m in mind.Memories.Where(x => x.Visibility != "private" && x.Source != "told" && nowGame - x.Game <= day && !string.IsNullOrWhiteSpace(x.Text)
                                                           && !MemoryOps.IsRoutineSocial(x, Adapter))
                         .OrderByDescending(x => x.Game).Take(6))
                o.Add(new JObject { ["type"] = string.IsNullOrEmpty(m.Etype) || m.Etype == "conversation" ? (m.Etype == "conversation" ? "social" : "memory") : m.Etype,
                                    ["summary"] = m.Text, ["game"] = m.Game, ["severity"] = Math.Min(1.0, m.Importance) });
            return o;
        }

        /// <summary>Build the two sections for one speaker (listener may be null). "" when there is nothing to say.</summary>
        public async Task<string> LineContextAsync(int aid, int? listener, JObject snap, JObject scene, string listenerName)
        {
            Mind mind = Minds[aid];
            long nowGame = Now().Game;
            JObject lisSnap = null;
            if (listener != null)
            {
                try { lisSnap = await Bridge.ActorAsync(listener.Value); } catch (BridgeError) { lisSnap = null; }
            }
            var events = EventsForFacts(mind, Ledger.KnownTo(aid), nowGame, Day);
            var all = Adapter.SalientFacts(snap, scene, lisSnap, listener ?? -1, events, nowGame);
            // the listener's own state is in the second section; a fact that names the listener still counts "about them" for a line
            foreach (var f in all)
                if (!string.IsNullOrEmpty(listenerName) && f.Kind != FactKind.Listener && f.Text.IndexOf(listenerName, StringComparison.OrdinalIgnoreCase) >= 0) f.AboutListener = true;
            List<List<string>> recent;
            var recentKeys = new HashSet<string>();
            if (_salientRecent.TryGetValue(aid, out recent)) foreach (var r in recent) recentKeys.UnionWith(r);
            // the scene block already says where the character is and what it is doing, so those two facts are not repeated
            var picked = Salience.Pick(all.Where(f => f.Kind != FactKind.Listener && f.Key != "job" && f.Key != "room:role"),
                new SalienceAsk { Purpose = SaliencePurpose.Line, Min = StandsOutMin, Max = StandsOutMax, RecentKeys = recentKeys, ListenerName = listenerName ?? "" });
            if (!_salientRecent.TryGetValue(aid, out recent)) { recent = new List<List<string>>(); _salientRecent[aid] = recent; }
            recent.Add(picked.Select(f => f.Key).ToList());
            if (recent.Count > SalientMemoryKeep) recent.RemoveAt(0);
            string s = PromptBlocks.StandsOut(picked.Select(f => f.Text));
            if (listener != null)
            {
                var lines = Adapter.ListenerCard(snap, lisSnap, listener.Value);
                var said = PairLines(aid, listener.Value, nowGame).Select(x => (_names.TryGetValue(x.Speaker, out string n) ? n : "someone") + ": " + x.Text).ToList();
                s += PromptBlocks.ListenerSection(listenerName, lines, said);
            }
            return s;
        }
    }
}
