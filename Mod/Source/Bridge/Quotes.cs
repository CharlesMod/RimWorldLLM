using System;
using System.Collections.Generic;
using System.Text;

namespace HermesVoice.Bridge
{
    /// <summary>One spoken line that caused a thought or log entry.</summary>
    public sealed class QuoteEntry
    {
        public string Text;
        public string Speaker;
        public int Tick;
        public QuoteEntry() { }
        public QuoteEntry(string text, string speaker, int tick) { Text = text; Speaker = speaker; Tick = tick; }
    }

    /// <summary>Quote text rules and the pure text assembly for thoughts and log entries (no game references).</summary>
    public static class Quotes
    {
        public const int DefaultMaxChars = 140;
        public const int MaxShown = 3;
        public const string Ellipsis = "…";

        /// <summary>The sanitised line, collapsed to one line of whitespace and cut to at most max characters (ellipsis included),
        /// ending on a word when it can. Empty in, empty out.</summary>
        public static string Trim(string line, int max = DefaultMaxChars)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            var sb = new StringBuilder(line.Length);
            bool space = false;
            foreach (char c in line.Trim())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            string s = sb.ToString();
            if (max < 4) max = 4;
            if (s.Length <= max) return s;
            int budget = max - Ellipsis.Length;
            int cut = s.LastIndexOf(' ', budget);          // last space at or before the budget
            if (cut < budget / 2) cut = budget;            // no usable word break: hard cut
            string head = s.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '-');
            if (head.Length == 0) head = s.Substring(0, budget);
            return head + Ellipsis;
        }

        /// <summary>Adds a quote to a newest-first list: the same line from the same tick is not added twice; the list keeps MaxShown.</summary>
        public static void Push(List<QuoteEntry> list, QuoteEntry q)
        {
            if (list == null || q == null || string.IsNullOrEmpty(q.Text)) return;
            foreach (var e in list) if (e.Tick == q.Tick && e.Text == q.Text && e.Speaker == q.Speaker) return;
            list.Insert(0, q);
            if (list.Count > MaxShown) list.RemoveRange(MaxShown, list.Count - MaxShown);
        }

        /// <summary>Tooltip lines, newest first, at most MaxShown, one per quote, formatted by the caller (keyed string).</summary>
        public static string Tooltip(IList<QuoteEntry> quotes, Func<QuoteEntry, string> format)
        {
            if (quotes == null || quotes.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < quotes.Count && i < MaxShown; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(format(quotes[i]));
            }
            return sb.ToString();
        }

        /// <summary>The vanilla sentence as a lead-in, then the quote: "Bram insulted Hana's cooking: "line"".</summary>
        public static string LeadIn(string vanillaSentence, string quote, string template)
        {
            string lead = (vanillaSentence ?? "").Trim();
            while (lead.EndsWith(".") || lead.EndsWith(":")) lead = lead.Substring(0, lead.Length - 1).TrimEnd();
            if (string.IsNullOrEmpty(quote)) return vanillaSentence ?? "";
            return template.Replace("{0}", lead).Replace("{1}", quote);
        }
    }

    /// <summary>Which log text a spoken line gets: one merged entry when an effect was applied, else the plain line.</summary>
    public static class EntryKinds
    {
        /// <summary>Intents whose applied effect has a "who did what" log wording (key HV_LogEffect_x).</summary>
        public static readonly string[] Wording = { "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain" };

        /// <summary>The one entry for a line: the effect wording when the game applied the effect, else the plain say entry.
        /// Never both, so the quote is not shown twice in a row.</summary>
        public static string EffectWording(string appliedEffect)
        {
            return appliedEffect != null && Array.IndexOf(Wording, appliedEffect) >= 0 ? appliedEffect : null;
        }
    }

    /// <summary>Which of the game's own interactions the mod voices, and the hard rules. Def names are data, from settings.</summary>
    public static class InteractionPolicy
    {
        public enum Tier { Skip, Idle, High }

        public const string DefaultHigh =
            "Insult,Slight,KindWords,DeepTalk,RomanceAttempt,MarriageProposal,Breakup,ConvertIdeoAttempt,Reassure,Counsel";
        public const string DefaultIdle = "Chitchat";

        static readonly string[] RomanceMarks = { "romance", "marriage", "breakup", "lovin", "flirt", "propos", "spouse", "fianc", "seduc" };

        public static HashSet<string> ParseList(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in (csv ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        public static Tier Classify(string defName, ISet<string> high, ISet<string> idle)
        {
            if (string.IsNullOrEmpty(defName)) return Tier.Skip;
            if (high != null && high.Contains(defName)) return Tier.High;
            if (idle != null && idle.Contains(defName)) return Tier.Idle;
            return Tier.Skip;
        }

        /// <summary>Romance-type by name. Built in, not a setting, so the age rule cannot be configured away.</summary>
        public static bool IsRomance(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return false;
            string l = defName.ToLowerInvariant();
            foreach (var m in RomanceMarks) if (l.Contains(m)) return true;
            return false;
        }

        /// <summary>The decision to voice one vanilla interaction. Romance never when either biological age is under 18.</summary>
        public static bool Voice(bool settingOn, string defName, Tier tier, bool bothVoiced, bool bothHumanlike,
                                 float initiatorBioAge, float recipientBioAge)
        {
            if (!settingOn || tier == Tier.Skip || !bothVoiced || !bothHumanlike) return false;
            if (IsRomance(defName) && (initiatorBioAge < 18f || recipientBioAge < 18f)) return false;
            return true;
        }
    }

    /// <summary>
    /// Lines waiting for the vanilla interaction they were generated for. Keyed by the reference the event carried, so
    /// lookups are O(1). T is whatever the game side holds (thought and log entry references).
    /// </summary>
    public sealed class PendingTable<T>
    {
        public enum Result { Attached, Unknown, Expired, AlreadyAttached, WrongPair }

        sealed class Slot
        {
            public int A, B, Tick;
            public bool Done;
            public T Payload;
        }

        readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>();
        readonly Queue<string> _order = new Queue<string>();
        readonly int _capacity;

        public PendingTable(int capacity = 64) { _capacity = capacity; }

        public int Count { get { return _slots.Count; } }

        public void Register(string key, int initiatorId, int recipientId, int tick, T payload)
        {
            if (string.IsNullOrEmpty(key) || _slots.ContainsKey(key)) return;
            _slots[key] = new Slot { A = initiatorId, B = recipientId, Tick = tick, Payload = payload };
            _order.Enqueue(key);
            while (_slots.Count > _capacity && _order.Count > 0) _slots.Remove(_order.Dequeue());
        }

        /// <summary>The line is from speakerId to listenerId at tick now. The pair must match the interaction's pair exactly
        /// (speaker is the initiator, listener the recipient). An attached slot never attaches again.</summary>
        public Result TryAttach(string key, int speakerId, int listenerId, int now, int windowTicks, out T payload)
        {
            payload = default(T);
            Slot s;
            if (string.IsNullOrEmpty(key) || !_slots.TryGetValue(key, out s)) return Result.Unknown;
            if (s.A != speakerId || s.B != listenerId) return Result.WrongPair;
            if (s.Done) return Result.AlreadyAttached;
            if (now - s.Tick > windowTicks || now < s.Tick) { _slots.Remove(key); return Result.Expired; }
            s.Done = true;
            payload = s.Payload;
            return Result.Attached;
        }

        /// <summary>Drops slots older than the window (called now and then from the game's tick).</summary>
        public int Sweep(int now, int windowTicks)
        {
            int n = 0;
            while (_order.Count > 0)
            {
                string k = _order.Peek();
                Slot s;
                if (!_slots.TryGetValue(k, out s)) { _order.Dequeue(); continue; }
                if (now - s.Tick <= windowTicks && now >= s.Tick) break;
                _order.Dequeue();
                _slots.Remove(k);
                n++;
            }
            return n;
        }
    }

    /// <summary>Whether a line may produce game effects.</summary>
    public static class EffectGuard
    {
        /// <summary>Lines generated for one of the game's own interactions (vanilla_ref set) never add an effect:
        /// the game already applied its own.</summary>
        public static bool MayApply(string vanillaRef) { return string.IsNullOrEmpty(vanillaRef); }
    }
}
