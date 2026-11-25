using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Public API for other mods. Reference HermesVoice.dll and call these statics (or reflect on them).
    /// See docs/HOOK-API.md.
    /// </summary>
    public static class HermesVoiceAPI
    {
        static readonly Dictionary<string, Func<Pawn, string>> Contexts = new Dictionary<string, Func<Pawn, string>>();
        static readonly object Lock = new object();

        /// <summary>
        /// Adds extra context to every GET /pawn snapshot, under extra[key]. The function runs on the game
        /// thread on each snapshot, so keep it cheap. Registering the same key again replaces the function.
        /// Return null to leave the key out for that pawn.
        /// </summary>
        public static void RegisterPawnContext(string key, Func<Pawn, string> provider)
        {
            if (string.IsNullOrWhiteSpace(key) || provider == null) return;
            lock (Lock) Contexts[key] = provider;
        }

        /// <summary>Queues a talk request for the agent (GET /talk): "pawn should say something about text".</summary>
        public static void RequestTalk(Pawn pawn, string text)
        {
            if (pawn == null || string.IsNullOrWhiteSpace(text)) return;
            TalkQueue.Add(pawn.thingIDNumber, text, "mod");
        }

        internal static JObject CollectContext(Pawn p)
        {
            var o = new JObject();
            KeyValuePair<string, Func<Pawn, string>>[] copy;
            lock (Lock) { copy = new KeyValuePair<string, Func<Pawn, string>>[Contexts.Count]; int i = 0; foreach (var kv in Contexts) copy[i++] = kv; }
            foreach (var kv in copy)
            {
                try
                {
                    string v = kv.Value(p);
                    if (v != null) o[kv.Key] = v;
                }
                catch (Exception e) { Log.WarningOnce("[HermesVoice] pawn context '" + kv.Key + "' failed: " + e.Message, kv.Key.GetHashCode()); }
            }
            return o;
        }
    }
}
