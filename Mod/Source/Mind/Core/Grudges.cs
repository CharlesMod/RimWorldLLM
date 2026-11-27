// Port of agent/rimworldllm_agent/core/grudges.py: grudge and make-up loop. An insult (a spoken one the game applied, or a vanilla
// Insult/Slight or social fight) gives the victim a grudge against the offender. Phases run on game time, with a real-time floor
// (play clock) on each so 3x and 4x do not rush them:
//   avoid     the victim keeps clear of the offender (a direct /social_action avoid, no model call)
//   cooldown  nothing happens for grudge_cooldown_days
//   resolve   one of them walks over (seek_out, again a direct /social_action) to apologise, make up or confront
//   outcome   the talk that follows closes the loop: made up (memory both sides, chronicle note) or escalated
//             (renews once, then fades); a talk that says neither retries later, up to grudge_max_attempts
// Nothing romantic ever: the topics are fixed words and minors take part like anyone else.
//
// Python coroutines handed to d._spawn become Func<Task> (lazy: the work starts when the host runs it, as a coroutine does).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    /// <summary>The director members the grudge engine reads (the Python takes the whole director as d).</summary>
    public interface IGrudgesHost
    {
        MindConfig Cfg { get; }
        Dictionary<int, Mind> Minds { get; }
        IGameAdapter Adapter { get; }
        IBridge Bridge { get; }
        /// <summary>The mod's /config reply (d.bridge_cfg), a JObject (may be empty).</summary>
        JObject BridgeCfg { get; }
        IDictionary<int, string> Names { get; }
        ISet<int> Dirty { get; }
        Chronicle Chronicle { get; }
        ConcernEngine Concerns { get; }
        /// <summary>Game units in one day (d.day).</summary>
        long Day { get; }
        /// <summary>d.now(): the play clock (only .Real is read).</summary>
        Now Now();
        /// <summary>d._spawn(coro): run the work as a background task.</summary>
        void Spawn(Func<Task> work);
    }

    public sealed class Reunion
    {
        /// <summary>who holds the grudge</summary>
        public int Holder;
        public string Grudge;
        public int Mover;
        public int Other;
        public string Approach;
        public long Expires;
        public string Cause = "";
        public Reunion(int holder, string grudge, int mover, int other, string approach, long expires, string cause = "")
        { Holder = holder; Grudge = grudge; Mover = mover; Other = other; Approach = approach; Expires = expires; Cause = cause; }
    }

    public sealed class GrudgeEngine
    {
        public const string Apologise = "apologise", MakeUpApproach = "make up", Confront = "confront";
        /// <summary>a seek_out's chat is expected within this long, else the attempt came to nothing</summary>
        public const double ReunionWindowDays = 0.5;
        public const string GuiltText = "I wish I had not treated {who} that way.";
        public static readonly HashSet<string> Positive = new HashSet<string> { "kind", "comfort" };
        public static readonly HashSet<string> Negative = new HashSet<string> { "insult", "slight" };

        /// <summary>(who walks: "offender" | "holder", approach). Deterministic. A guilty, not abrasive offender apologises; else a kind
        /// holder, or one whose opinion of the offender recovered, makes up; otherwise the holder confronts (words only).</summary>
        public static (string Who, string Approach) Decide(int? opinion, string holderStyle, string offenderStyle, bool offenderGuilty, int recover)
        {
            if (offenderGuilty && offenderStyle != "abrasive") return ("offender", Apologise);
            if (holderStyle == "kind" || (opinion != null && opinion >= recover && holderStyle != "abrasive")) return ("holder", MakeUpApproach);
            return ("holder", Confront);
        }

        /// <summary>"escalated" if anyone insulted or slighted, else "made_up" if anyone was kind or comforting, else "neutral".</summary>
        public static string OutcomeOf(IEnumerable<string> intents)
        {
            var l = intents.ToList();
            if (l.Any(Negative.Contains)) return "escalated";
            if (l.Any(Positive.Contains)) return "made_up";
            return "neutral";
        }

        private readonly IGrudgesHost d;
        public Dictionary<(int, int), Reunion> Pending = new Dictionary<(int, int), Reunion>();
        public HashSet<(int, string)> Busy = new HashSet<(int, string)>();
        public int Started, MadeUp, Escalated, Faded, Seeks;
        public List<JObject> Trace = new List<JObject>();
        // Play-clock marks per (holder, grudge id): not saved. A grudge loaded from a save has none and is held by game
        // time only; every phase that starts in this session also waits for its real-time floor.
        public Dictionary<(int, string), double> RealBorn = new Dictionary<(int, string), double>();
        public Dictionary<(int, string), double> RealTry = new Dictionary<(int, string), double>();

        public GrudgeEngine(IGrudgesHost d) { this.d = d; }

        private static (int, int) Pair(int a, int b) { return a < b ? (a, b) : (b, a); }

        // ---- gates ----------------------------------------------------------
        public bool On { get { return d.Cfg.Grudges; } }

        /// <summary>Social actions need the mod's wants and effects on; this loop never goes through the tool backend.</summary>
        public bool CanAct()
        {
            if (!On) return false;
            var cfg = d.BridgeCfg;
            JToken w = cfg == null ? null : cfg["wants_enabled"];
            if (w != null && w.Type == JTokenType.Boolean && !w.Value<bool>()) return false;
            JToken ef = cfg == null ? null : cfg["effects"];
            return !(ef != null && ef.Type == JTokenType.String && (string)ef == "off");
        }

        private void Note(JObject o)
        {
            Trace.Add(o);
            if (Trace.Count > 100) Trace.RemoveRange(0, Trace.Count - 100);
        }

        private long Units(double days) { return (long)(days * d.Day); }

        private double Real() { return d.Now().Real; }

        private void Mark(int holder, Grudge g, bool born, double floorS)
        {
            double r = Real();
            if (born) RealBorn[(holder, g.Id)] = r;
            RealTry[(holder, g.Id)] = r + floorS;
        }

        private bool RealOk(Dictionary<(int, string), double> mark, int holder, Grudge g, double plus = 0.0)
        {
            double m;
            return !mark.TryGetValue((holder, g.Id), out m) || Real() >= m + plus;
        }

        private string NameOr(int id, string dflt)
        {
            string v;
            return d.Names.TryGetValue(id, out v) ? v : dflt;
        }

        // ---- offences -------------------------------------------------------
        public void OnEvent(JObject e)
        {
            if (!CanAct()) return;
            var off = d.Adapter.SocialOffence(e);
            if (off != null)
                Offence(off.Value.Offender, off.Value.Victim, NameOr(off.Value.Offender, "someone") + " " + off.Value.Verb + " " + NameOr(off.Value.Victim, "someone"),
                    ConcernEngine.GameOf(e, d.Bridge.Env.Game));
        }

        /// <summary>An applied spoken offence from speaker to listener.</summary>
        public void OnSay(int speaker, int? listener, string intent, bool applied)
        {
            if (!applied || listener == null || !CanAct()) return;
            if (!d.Adapter.OffenceIntents().Contains(intent)) return;
            Offence(speaker, listener.Value, NameOr(speaker, "someone") + " insulted " + NameOr(listener.Value, "someone"), d.Bridge.Env.Game);
        }

        public Grudge Offence(int offender, int victim, string cause, long game)
        {
            if (offender == victim || !d.Minds.ContainsKey(offender) || !d.Minds.ContainsKey(victim) || !CanAct()) return null;
            if (ReunionBetween(offender, victim, game) != null) return null;     // words during a make-up talk belong to its outcome, not a new grudge
            var mind = d.Minds[victim];
            if (mind.Grudges.Any(x => x.Against == offender && x.Phase != "done")) return null;     // already holding one against them
            var g = new Grudge
            {
                Id = "g" + offender.ToString(CultureInfo.InvariantCulture) + "-" + game.ToString(CultureInfo.InvariantCulture),
                Against = offender, Cause = GameData.Clean(cause, "event"), Born = game, NextTry = game + Units(d.Cfg.GrudgeCooldownDays),
            };
            mind.Grudges.Add(g);
            mind.Grudges = Mind.Tail(mind.Grudges, 6);
            Mark(victim, g, true, d.Cfg.GrudgeCooldownFloorRealS);
            Started++;
            d.Dirty.Add(victim);
            Note(new JObject { ["kind"] = "grudge", ["holder"] = victim, ["against"] = offender, ["game"] = game });
            d.Spawn(() => BeginAsync(victim, g));
            return g;
        }

        /// <summary>Avoid at once; the offender's guilt (unless abrasive) becomes a concern until the grudge ends.</summary>
        public async Task BeginAsync(int holder, Grudge g)
        {
            await ActAsync(holder, "avoid", g.Against, "").ConfigureAwait(false);
            if (d.Cfg.Concerns && g.Renewals == 0)
            {
                string style = "";
                try
                {
                    style = d.Adapter.GrudgeStyle(await d.Bridge.ActorAsync(g.Against).ConfigureAwait(false));
                }
                catch (BridgeError) { }
                if (style != "abrasive")
                    d.Concerns.Birth(new JObject
                    {
                        ["actor"] = g.Against, ["text"] = GuiltText, ["about"] = new JArray(holder), ["kind"] = "guilt",
                        ["hint"] = "grudge:" + g.Id, ["importance"] = 0.5,
                    }, g.Born);
            }
        }

        private async Task<bool> ActAsync(int actor, string action, int target, string topic)
        {
            try
            {
                await d.Bridge.SocialActionAsync(actor, action, target, topic).ConfigureAwait(false);
                return true;
            }
            catch (BridgeError e)
            {
                Note(new JObject { ["kind"] = "refused", ["actor"] = actor, ["action"] = action, ["code"] = e.Code });
                return false;
            }
        }

        // ---- time -----------------------------------------------------------
        public void Advance()
        {
            if (!On) return;
            long game = d.Bridge.Env.Game;
            foreach (var k in Pending.Where(kv => game >= kv.Value.Expires).Select(kv => kv.Key).ToList()) Pending.Remove(k);
            if (!CanAct()) return;
            foreach (var kv in d.Minds.ToList())
            {
                int aid = kv.Key;
                foreach (var g in kv.Value.Grudges.ToList())
                {
                    if (g.Phase == "done") continue;
                    if (g.Phase == "avoid" && game >= g.Born + Units(d.Cfg.GrudgeAvoidDays) && RealOk(RealBorn, aid, g, d.Cfg.GrudgeAvoidFloorRealS))
                    {
                        g.Phase = "cooldown";
                        d.Dirty.Add(aid);
                    }
                    if (g.Phase == "cooldown" && game >= g.NextTry && !Busy.Contains((aid, g.Id)) && RealOk(RealTry, aid, g))
                        g.Phase = "resolve";
                    if (g.Phase == "resolve" && game >= g.NextTry && !Busy.Contains((aid, g.Id)) && RealOk(RealTry, aid, g))
                    {
                        if (g.Attempts >= d.Cfg.GrudgeMaxAttempts)
                        {
                            End(aid, g, "faded", game);
                            continue;
                        }
                        g.Attempts += 1;
                        g.NextTry = game + Units(d.Cfg.GrudgeRetryDays);
                        Mark(aid, g, false, d.Cfg.GrudgeRetryFloorRealS);
                        d.Dirty.Add(aid);
                        Busy.Add((aid, g.Id));
                        var gg = g;
                        d.Spawn(() => ResolveAsync(aid, gg));
                    }
                }
            }
        }

        public async Task ResolveAsync(int holder, Grudge g)
        {
            try
            {
                int other = g.Against;
                if (!d.Minds.ContainsKey(other))
                {
                    End(holder, g, "faded", d.Bridge.Env.Game);
                    return;
                }
                JObject snapH, snapO;
                try
                {
                    snapH = await d.Bridge.ActorAsync(holder).ConfigureAwait(false);
                    snapO = await d.Bridge.ActorAsync(other).ConfigureAwait(false);
                }
                catch (BridgeError)
                {
                    return;                         // try again at next_try
                }
                var ad = d.Adapter;
                int? opinion = null;
                foreach (var (id, op) in ad.Relations(snapH)) if (id == other) opinion = op;
                bool guilty = d.Minds[other].Concerns.Any(c => c.Status == "open" && c.ResolveHint == "grudge:" + g.Id);
                var (who, approach) = Decide(opinion, ad.GrudgeStyle(snapH), ad.GrudgeStyle(snapO), guilty, d.Cfg.GrudgeRecoverOpinion);
                int mover = who == "offender" ? other : holder, target = who == "offender" ? holder : other;
                if (!CanAct()) return;
                bool ok = await ActAsync(mover, "seek_out", target, approach).ConfigureAwait(false);
                Note(new JObject { ["kind"] = "seek", ["holder"] = holder, ["mover"] = mover, ["target"] = target, ["approach"] = approach, ["ok"] = ok });
                if (!ok) return;
                Seeks++;
                long game = d.Bridge.Env.Game;
                g.Approach = approach; g.Mover = mover;
                Pending[Pair(mover, target)] = new Reunion(holder, g.Id, mover, target, approach, game + Units(ReunionWindowDays), g.Cause);
                d.Dirty.Add(holder);
            }
            finally
            {
                Busy.Remove((holder, g.Id));
            }
        }

        // ---- the talk -------------------------------------------------------
        private Reunion ReunionBetween(int a, int b, long game)
        {
            Reunion r;
            return Pending.TryGetValue(Pair(a, b), out r) && game < r.Expires ? r : null;
        }

        /// <summary>The pending reunion this event is (a social event between the pair, in the window).</summary>
        public Reunion ReunionFor(JObject e)
        {
            if (Pending.Count == 0 || !CanAct()) return null;
            var pair = d.Adapter.ConversationPair(e);
            if (pair == null) return null;
            return ReunionBetween(pair.Value.Item1, pair.Value.Item2, ConcernEngine.GameOf(e, d.Bridge.Env.Game));
        }

        /// <summary>What the talk is about, for both actors' trigger (game text: cleaned by the caller's data wrapping).</summary>
        public string NoteFor(Reunion r)
        {
            string m = GameData.Name(NameOr(r.Mover, "someone")), o = GameData.Name(NameOr(r.Other, "someone"));
            string cause = GameData.Clean(r.Cause, "event");
            if (r.Approach == Apologise) return m + " has come to apologise to " + o + ". The quarrel: " + cause;
            if (r.Approach == MakeUpApproach) return m + " has come to make up with " + o + ". The quarrel: " + cause;
            return m + " has come to confront " + o + " with words only. The quarrel: " + cause;
        }

        public void OnConversationDone(Conversation conv, long game)
        {
            if (string.IsNullOrEmpty(conv.Reunion)) return;
            int bar = conv.Reunion.IndexOf('|');
            string holderId = bar >= 0 ? conv.Reunion.Substring(0, bar) : conv.Reunion;
            string gid = bar >= 0 ? conv.Reunion.Substring(bar + 1) : "";
            int holder = int.Parse(Sanitize.PyStrip(holderId), CultureInfo.InvariantCulture);
            Mind mind;
            d.Minds.TryGetValue(holder, out mind);
            var g = mind == null ? null : mind.Grudges.FirstOrDefault(x => x.Id == gid);
            if (g == null || g.Phase == "done") return;
            Pending.Remove(Pair(conv.A, conv.B));
            string outc = OutcomeOf(conv.Turns.Select(t => t.Intent));
            Note(new JObject { ["kind"] = "outcome", ["holder"] = holder, ["grudge"] = gid, ["outcome"] = outc });
            if (outc == "made_up") MakeUp(holder, g, game);
            else if (outc == "escalated")
            {
                Escalated++;
                if (g.Renewals < 1)
                {
                    g.Renewals += 1;
                    g.Phase = "avoid"; g.Born = game; g.Attempts = 0;
                    g.NextTry = game + Units(d.Cfg.GrudgeCooldownDays);
                    Mark(holder, g, true, d.Cfg.GrudgeCooldownFloorRealS);
                    d.Dirty.Add(holder);
                    d.Spawn(() => BeginAsync(holder, g));
                }
                else End(holder, g, "faded", game);
            }
        }

        private void MakeUp(int holder, Grudge g, long game)
        {
            int other = g.Against;
            MadeUp++;
            foreach (var (me, them) in new[] { (holder, other), (other, holder) })
            {
                Mind m;
                if (!d.Minds.TryGetValue(me, out m)) continue;
                m.Memories.Add(new Memory
                {
                    Id = m.NextMemId(), Game = game, Source = "saw", Importance = 0.5, People = new List<int> { me, them }, Etype = "makeup",
                    Text = GameData.Name(NameOr(them, "They")) + " and I made things right after the quarrel.",
                });
                m.UnreflectedImportance += 0.3;
                d.Dirty.Add(me);
            }
            d.Chronicle.Note(game, GameData.Name(NameOr(holder, "Someone")) + " and " + GameData.Name(NameOr(other, "someone")) +
                " made up after a quarrel.", 0.3, false);
            End(holder, g, "made_up", game);
        }

        private void End(int holder, Grudge g, string how, long game)
        {
            g.Phase = "done"; g.Ended = how; g.Closed = game;
            if (how == "faded") Faded++;
            d.Dirty.Add(holder);
            Mind om;
            if (d.Minds.TryGetValue(g.Against, out om))         // the offender's guilt ends with the grudge, quietly
            {
                foreach (var c in om.Concerns)
                {
                    if (c.Status == "open" && c.ResolveHint == "grudge:" + g.Id)
                    {
                        ConcernOps.CloseConcern(c, game, how == "made_up" ? "resolved" : "faded", "");
                        d.Dirty.Add(g.Against);
                    }
                }
            }
        }

        /// <summary>Daily: finished grudges are dropped after a day.</summary>
        public void Upkeep()
        {
            long game = d.Bridge.Env.Game;
            foreach (var kv in d.Minds.ToList())
            {
                var keep = kv.Value.Grudges.Where(g => g.Phase != "done" || game - g.Closed < d.Day).ToList();
                if (keep.Count != kv.Value.Grudges.Count)
                {
                    kv.Value.Grudges = keep;
                    d.Dirty.Add(kv.Key);
                }
            }
        }
    }
}
