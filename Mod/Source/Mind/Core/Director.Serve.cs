// Director, part 3 (core/director.py): one voice, the line the game applies, bystander reactions, conversations, the narrator, saving.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public sealed partial class Director
    {
        private static string Sha1Hex(string s)
        {
            byte[] h = SHA1.HashData(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder();
            foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static ISet<string> TokensOfAll(IDictionary<int, HashSet<string>> map, IEnumerable<int> ids)
        {
            var s = new HashSet<string>();
            foreach (int i in ids) { HashSet<string> t; if (map.TryGetValue(i, out t)) s.UnionWith(t); }
            return s;
        }

        // ---- building an actor's lease prefix -------------------------------
        public async Task<(SanitizeCtx Ctx, JObject Scene, string Sig)> PrepareAsync(int aid, string trigger, JObject ev, Lease lease, int? convWith = null)
        {
            Mind mind = Minds[aid];
            JObject snap = await Bridge.ActorAsync(aid);
            JObject scene = await Bridge.SceneAsync(aid);
            var presentIds = new HashSet<int>(Adapter.ScenePresent(scene).Select(p => p.Id).Where(i => Minds.ContainsKey(i)));
            var relIds = new HashSet<int>();
            foreach (var (other, opinion) in Adapter.Relations(snap))
            {
                relIds.Add(other);
                string k = other.ToString(CultureInfo.InvariantCulture);
                Relation rel;
                if (!mind.Relations.TryGetValue(k, out rel))
                {
                    string nm;
                    rel = new Relation { Name = _names.TryGetValue(other, out nm) ? nm : k };
                    mind.Relations[k] = rel;
                }
                rel.Opinion = opinion;
            }
            var evIds = new HashSet<int>(ev == null ? Enumerable.Empty<int>() : ActorsOf(ev).Select(IdOf));
            var known = new HashSet<int>(presentIds);
            known.UnionWith(relIds); known.UnionWith(evIds); known.Add(aid);
            known.IntersectWith(_names.Keys);
            LearnAge(aid, snap);
            await LearnAgesAsync(_names.Keys);
            ISet<int> minors = MinorIds();
            var hearers = new HashSet<int>(Adapter.ScenePresent(scene).Where(p => p.CanHear && p.Id != aid).Select(p => p.Id));
            Scene sc = Adapter.BuildScene(snap, scene, trigger, _names, known, minors);
            var sceneFolk = new HashSet<int>(presentIds); sceneFolk.UnionWith(evIds);
            List<Memory> mems = MemoryOps.SelectMemories(mind, Now().Game, sceneFolk, trigger + " " + string.Join(" ", sc.Present), Day);
            foreach (Memory m in mems) foreach (int p in m.People) if (_names.ContainsKey(p)) known.Add(p);
            List<LedgerEntry> led = Ledger.KnownTo(aid);
            List<string> thoughts = Cfg.InnerMonologue ? Inner.RecentInner(mind, 3).Select(t => t.Text).ToList() : null;
            List<string> talk = CommonTalk(aid, trigger, sc, presentIds, convWith);
            List<string> worries = Cfg.Concerns ? ConcernOps.WorryLines(mind) : null;
            string prefix = PromptBlocks.BuildPrefix(Adapter.BlockA, mind, mems, led.Select(x => x.Summary).ToList(), sc, thoughts,
                                                     talk.Count > 0 ? talk : null, worries);
            HashSet<string> own;
            _nameTokens.TryGetValue(aid, out own);
            own = own ?? new HashSet<string>();
            lease.Data["prefix"] = prefix;
            lease.Data["turns"] = new List<(string, string)>();
            lease.Data["snap"] = snap;
            lease.Data["speech"] = string.IsNullOrEmpty(sc.SpeechShort) ? null : sc.SpeechShort;
            ISet<string> knownTokens = TokensOfAll(_nameTokens, known);
            var allTokens = new HashSet<string>();
            foreach (var v in _nameTokens.Values) allTokens.UnionWith(v);
            var ctx = new SanitizeCtx
            {
                SpeakerNames = own,
                KnownNames = knownTokens,
                AllNames = allTokens,
                PromptWords = Sanitize.PromptCapWords(prefix),
                CommonWords = new HashSet<string>(Adapter.CommonWords),
                ThirdPerson = mind.Voice.SpeaksThirdPerson(own),
                MinorInvolved = IsMinor((int?)aid) || hearers.Overlaps(minors),
                MinorNames = MinorTokens(),
                IntentLabels = Adapter.IntentLabels.ToArray(),
            };
            string job = sc.Job ?? "";
            string sig = Sha1Hex(string.Join("|", new[] { trigger, sc.State, sc.Where, string.Join(",", sc.Present), job }));
            return (ctx, scene, sig);
        }

        /// <summary>Block D's shared-talk lines: the lore names that match the scene, and (in a conversation only) what the speaker
        /// could pass on to this listener. Empty when both switches are off or nothing applies.</summary>
        private List<string> CommonTalk(int aid, string trigger, Scene sc, ISet<int> presentIds, int? convWith)
        {
            var o = new List<string>();
            if (Cfg.Lore)
            {
                var parts = new List<string> { trigger, sc.Where };
                parts.AddRange(sc.Present);
                string line = Lore.NamesLine(string.Join(" ", parts), 3, Adapter.LoreLabel);
                if (!string.IsNullOrEmpty(line)) o.Add(line);
            }
            if (Cfg.GossipChain && convWith != null && Minds.ContainsKey(convWith.Value))
            {
                var pres = new HashSet<int>(presentIds) { aid, convWith.Value };
                string hint = Gossip.PassOnHint(Minds[aid], Minds[convWith.Value], convWith.Value, pres, MinorIds());
                if (!string.IsNullOrEmpty(hint)) o.Add(hint);
            }
            return o;
        }

        /// <summary>Test seam (Python tests monkeypatch d._listener): when set, it picks the listener instead.</summary>
        public Func<JObject, int, int?, int?> ListenerHook;

        private int? ListenerOf(JObject scene, int aid, int? prefer)
        {
            if (ListenerHook != null) return ListenerHook(scene, aid, prefer);
            if (prefer != null) return prefer;
            foreach (var (i, canHear) in Adapter.ScenePresent(scene))
                if (canHear && Minds.ContainsKey(i) && i != aid) return i;
            return null;
        }

        // ---- a single voice -------------------------------------------------
        public async Task ServeAsync(WorkItem item, Lease lease)
        {
            int aid = item.ActorId;
            try
            {
                JObject vanilla = item.Payload["vanilla"] as JObject;
                string trigger;
                string evSummary = item.Event == null ? item.Kind : JobsJson.Str(item.Event, "summary", item.Kind);
                if (item.Kind == "player_talk")
                {
                    trigger = "The player says to you: " + PyJson.Str(item.Payload["text"]);
                    lease.Data["talk_id"] = item.Payload["talk_id"];
                }
                else if (BridgeJson.Truthy(item.Payload["trigger"], false)) trigger = PyJson.Str(item.Payload["trigger"]);          // a payoff line (core/concerns.py)
                else if (vanilla != null) trigger = BridgeJson.Truthy(vanilla["sentence"], false) ? PyJson.Str(vanilla["sentence"]) : evSummary;
                else if (BridgeJson.Truthy(item.Payload["first"], false)) trigger = Adapter.FirstTrigger(PyJson.Str(item.Payload["first"]), evSummary);
                else trigger = evSummary;
                SanitizeCtx ctx; JObject scene; string sig;
                try { (ctx, scene, sig) = await PrepareAsync(aid, trigger, item.Event, lease); }
                catch (BridgeError e) { Dropped.Add((aid, "bridge:" + e.Code)); return; }
                if (item.Cls < Cls.Urgent && Leases.NothingNew(aid, sig)) { Dropped.Add((aid, "nothing_new")); return; }
                Mind mind = Minds[aid];
                int? listener = ListenerOf(scene, aid, item.Listener);
                string lname = null;
                if (listener != null) _names.TryGetValue(listener.Value, out lname);
                ctx.Secrets = MemoryOps.SecretTexts(mind, lname);
                ctx.MinorInvolved = ctx.MinorInvolved || IsMinor(listener);
                bool minor = ctx.MinorInvolved;
                if (item.Kind == "player_talk" && AgeGate.Violation(PyJson.Str(item.Payload["text"]), minor, ctx.MinorNames))
                {
                    // a romantic or sexual message to or about a minor never reaches the model
                    Gate.SkippedPlayerText += 1;
                    Gate.Note("player_text", new JObject { ["actor"] = aid });
                    Dropped.Add((aid, "minor_safety"));
                    return;
                }
                lease.Generating = true;
                object res;
                string prefix = (string)lease.Data["prefix"];
                string speech = lease.Data.TryGetValue("speech", out object sp0) ? sp0 as string : null;
                if (vanilla != null)
                {
                    // Words for the game's own interaction: no intent pass, the game applies its own effects.
                    int turnNo; _lineCount.TryGetValue(aid, out turnNo);
                    var (s, _) = await Passes.LinePassAsync(Llm, lease.Slot, mind, _names[aid], prefix, new List<(string, string)>(), turnNo, ctx,
                                                            Cfg.Llm.LineMaxTokens, Adapter.VanillaRequest(vanilla, _names[aid], lname ?? "them"),
                                                            speech: speech);
                    if (s.Ok && Adapter.DropsMeta(s.Text, new[] { PyJson.Str(vanilla["kind"]), PyJson.Str(vanilla["sentence"]) }))
                        s = new Sanitized(null, "meta_talk");       // a line that only remarks on the chatter itself is never shown
                    res = s.Ok ? (object)new Spoken(s.Text, Adapter.IntentLabels[0]) : new Dropped(s.Reason);
                }
                else
                {
                    int turnNo; _lineCount.TryGetValue(aid, out turnNo);
                    InnerArgs ia = InnerKw(mind, listener);
                    res = await Passes.SpeakAsync(Llm, lease.Slot, mind, _names[aid], prefix, new List<(string, string)>(), turnNo, ctx, lname,
                                                  LabelsFor(minor), Cfg.Llm.LineMaxTokens, null, ia.Aim, ia.SoloAim, ia.FreshAim, null, 0, null,
                                                  speech, ia.AimExtra, ia.IntentLlm, ia.DropJustWant);
                }
                lease.Generating = false;
                var dropped = res as Dropped;
                if (dropped != null) { Dropped.Add((aid, dropped.Reason)); return; }
                var spoken = (Spoken)res;
                Now now = Now();
                if (lease.Preempted) { Dropped.Add((aid, "preempted")); return; }
                if (item.Expired(now)) { Dropped.Add((aid, "expired")); return; }        // an expired line is dropped silently
                bool ok = await SayAsync(aid, spoken, listener, WireExpiry(item),
                                         talkId: item.Payload["talk_id"] == null || item.Payload["talk_id"].Type == JTokenType.Null ? null : PyJson.Str(item.Payload["talk_id"]),
                                         combat: item.Event != null && Adapter.IsUrgentVisual(item.Event) && vanilla == null,
                                         ev: item.Event, vanillaRef: vanilla != null ? PyJson.Str(vanilla["ref"]) : null, minor: minor);
                if (ok)
                {
                    Leases.Quotas.Record(aid, listener, now, pair: vanilla == null);
                    Leases.MarkSpoke(aid, sig);
                    mind.LastLineTime = now.Game;
                    int lc; _lineCount.TryGetValue(aid, out lc);
                    _lineCount[aid] = lc + 1;
                    RememberOwn(aid, spoken.Text);
                    _dirty.Add(aid);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { LlmLog.Warning("rimworldllm", "serve failed: " + PyNames.Of(e) + ": " + e); }
            finally
            {
                lease.Generating = false;
                if (!lease.Ended) Leases.Release(lease, "done");
            }
        }

        /// <summary>The third actor a gossip line is about: one in the event, else the one the speaker feels most about.</summary>
        private int? PickAbout(int aid, int? listener, JObject ev)
        {
            foreach (JObject a in ev == null ? Enumerable.Empty<JObject>() : ActorsOf(ev))
            {
                int i = IdOf(a);
                if (i != aid && i != listener && _names.ContainsKey(i)) return i;
            }
            int? best = null; double score = -1.0;
            foreach (var kv in Minds[aid].Relations)
            {
                string k = kv.Key;
                int v;
                if (k.TrimStart('-').Length > 0 && k.TrimStart('-').All(char.IsDigit) && int.TryParse(k, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v)
                    && v != aid && v != listener && _names.ContainsKey(v))
                {
                    double s = Math.Abs(kv.Value.Opinion ?? 0);
                    if (s > score) { best = v; score = s; }
                }
            }
            return best;
        }

        /// <summary>The delivery the intent pass chose, bounded: a shout needs the scene (combat, an insult, a major threat)
        /// and a pause since this speaker's last shout; otherwise it is said normally. null = let the mod guess.</summary>
        private string Delivery(int aid, Spoken sp, bool combat, JObject ev)
        {
            if (sp.Delivery != "normal" && sp.Delivery != "shout" && sp.Delivery != "whisper") return null;
            if (sp.Delivery != "shout") return sp.Delivery;
            bool loud = combat || sp.Intent == "insult" || (ev != null && Adapter.IsMajorThreat(ev));
            double now = _clock();
            double until;
            if (!loud || now < (ShoutUntil.TryGetValue(aid, out until) ? until : -1.0)) return "normal";
            ShoutUntil[aid] = now + Cfg.ShoutGapRealS;
            return "shout";
        }

        /// <summary>speak() arguments for the inner monologue: defaults when the master switch is off (today's calls, byte for byte).</summary>
        public sealed class InnerArgs
        {
            public bool Aim, SoloAim = true, DropJustWant;
            public string FreshAim, AimExtra;
            public ILlmClient IntentLlm;
        }

        public InnerArgs InnerKw(Mind mind, int? listener = null)
        {
            var kw = new InnerArgs();
            if (!Cfg.InnerMonologue) return kw;
            long window = (long)(Cfg.InnerAimFreshDays * Day);
            kw.Aim = true; kw.SoloAim = Cfg.InnerSoloAim;
            kw.FreshAim = Inner.FreshAim(mind, Bridge.Env.Game, window);
            if (Cfg.AimGround)
            {
                int aid = mind.ActorId;
                long hour = Bridge.Env.Game / Math.Max(1, Day / 24);
                int lc; _lineCount.TryGetValue(aid, out lc);
                string kind = Inner.SubtextKind(aid, lc, (int)hour);
                string ln = null;
                if (listener != null) _names.TryGetValue(listener.Value, out ln);
                kw.AimExtra = Inner.AimTension(mind, aid, listener, ln, kind);
                kw.DropJustWant = true;
            }
            if (!string.IsNullOrEmpty(Cfg.AimSampler))
            {
                ILlmClient routed = AimLlm();
                if (routed != null) kw.IntentLlm = routed;
            }
            return kw;
        }

        /// <summary>The client for the aim-carrying intent pass (cfg.aim_sampler), or null to use the main one.</summary>
        private ILlmClient AimLlm()
        {
            string name = Cfg.AimSampler;
            if (string.IsNullOrEmpty(name)) return null;
            var c = _aimLlmCache;
            if (c == null || c.Value.Name != name || !ReferenceEquals(c.Value.From, Llm))
            {
                c = (name, Llm, SamplerRoute.Route(Llm, name));
                _aimLlmCache = c;
            }
            return c.Value.Routed;
        }

        /// <summary>A line the game applied: its aim joins the stream and may show as a contrast cloud.</summary>
        public async Task InnerAfterSayAsync(int aid, Spoken sp, bool minor)
        {
            if (!Cfg.InnerMonologue || string.IsNullOrEmpty(sp.Aim)) return;
            if (Inner.RepeatsRecentAim(Minds[aid], sp.Aim)) return;      // the same aim again: no duplicate in the stream, no repeat cloud
            Inner.AddInner(Minds[aid], sp.Aim, "aim", Bridge.Env.Game, Cfg.InnerStreamKeep);
            _dirty.Add(aid);
            if (!ModelTier.FeatureOn(Tier, Cfg.InnerCloud, Cfg.InnerCloudSmall) || (minor && sp.AimGated) || sp.Aim.Length > Cfg.ThoughtMaxChars) return;
            JToken ps = BridgeCfg["pause_ambient_at_speed"];
            if (Bridge.Env.Speed >= (ps != null ? (int)JobsJson.PyInt(ps) : Cfg.PauseAmbientAtSpeed)) return;
            if (!CloudGate.Allow(aid, Now().Real, Cfg.InnerCloudGapRealS, Cfg.InnerCloudChance, InnerRng.Random())) return;
            try { await Bridge.ThoughtAsync(aid, sp.Aim, surface: sp.Aim); }
            catch (BridgeError e) { LlmLog.Warning("rimworldllm", "aim cloud refused for " + aid + ": " + e.Message); }
        }

        /// <summary>Writes a line to the game. False when the game's liveness check refused (gone or out of range).</summary>
        public async Task<bool> SayAsync(int aid, Spoken sp, int? listener, int? expires, string conv = null, string talkId = null,
                                         bool combat = false, JObject ev = null, string vanillaRef = null, bool minor = false)
        {
            Probes.Say?.Invoke(aid, ev);
            if (AgeGate.RefuseIntent(sp.Intent, Romantic(), minor || IsMinor((int?)aid) || IsMinor(listener)))
            {
                Gate.RefusedIntents += 1;                       // last guard before the write
                Gate.Note("intent", new JObject { ["actor"] = aid, ["intent"] = sp.Intent });
                sp = new Spoken(sp.Text, Adapter.IntentLabels[0], sp.Delivery, sp.Aim, sp.AimGated);
            }
            int? about = null;
            if (Adapter.AboutIntents().Contains(sp.Intent))
            {
                about = PickAbout(aid, listener, ev);
                if (about == null || listener == null)
                    sp = new Spoken(sp.Text, Adapter.IntentLabels[0], sp.Delivery, sp.Aim, sp.AimGated);       // nobody to gossip about
            }
            string kind = Delivery(aid, sp, combat, ev);
            JObject r;
            try
            {
                r = await Bridge.SayAsync(aid, sp.Text, listener, sp.Intent, conv, combat, talkId, expires, null, about, vanillaRef, kind);
            }
            catch (BridgeError e) { Dropped.Add((aid, "say:" + e.Code)); return false; }
            JToken applied = r["applied"];
            Leases.Budget.Spend(Now());
            string nm; _names.TryGetValue(aid, out nm);
            Said.Add(new JObject
            {
                ["actor"] = aid, ["name"] = nm, ["text"] = sp.Text, ["intent"] = sp.Intent, ["delivery"] = kind,
                ["conv"] = conv, ["applied"] = applied ?? JValue.CreateNull(), ["refused"] = r["refused"] ?? JValue.CreateNull(),
            });
            bool didApply = BridgeJson.Truthy(applied, false);
            if (didApply && Cfg.Bystanders && Adapter.ArgumentIntents.Contains(sp.Intent) && listener != null)
            {
                int lis0 = listener.Value;
                _ = Spawn(() => SayBystandersAsync(aid, lis0));
            }
            if (didApply)
            {
                Grudges.OnSay(aid, listener, sp.Intent, true);
                await InnerAfterSayAsync(aid, sp, minor || IsMinor((int?)aid) || IsMinor(listener));
            }
            var told = Adapter.ToldIntents();
            // the agent records only what the game applied
            if (didApply && listener != null && Minds.ContainsKey(listener.Value) && told.ContainsKey(sp.Intent))
            {
                if (MemoryOps.RecordToldLine(Minds, aid, listener.Value, _names, sp.Text, Bridge.Env.Game, told[sp.Intent], about,
                                             Cfg.GossipChain, Cfg.GossipHopDecay, MinorIds()) != null)
                {
                    _dirty.Add(aid); _dirty.Add(listener.Value);
                }
            }
            return true;
        }

        // ---- bystander reactions (item 9, core/bystanders.py) ----------------------
        /// <summary>A vanilla insult or slight between two actors: its witnesses (the mod's own list) are the audience.</summary>
        private void BystandReact(JObject e)
        {
            if (!Cfg.Bystanders || !JobsJson.EventNotOff(BridgeCfg, EType(e))) return;
            List<int> parties = Adapter.ArgumentParties(e);
            if (parties != null && parties.Count > 0)
            {
                Now now = Now();
                JToken gt = e["game"];
                long g = gt != null && gt.Type != JTokenType.Null ? JobsJson.PyInt(gt) : now.Game;
                if (now.Game - g <= LeaseManager.RequestExpiry[Cls.Ambient].Units)     // not a backlog event
                {
                    var wit = e["witnesses"] is JArray wa ? wa.Select(t => (int)JobsJson.PyInt(t)).ToList() : new List<int>();
                    BystandStart(parties, wit, JobsJson.Str(e, "summary", ""), e["seq"] == null || e["seq"].Type == JTokenType.Null ? (object)null : Plain(e["seq"]));
                }
            }
        }

        /// <summary>An applied insult or slight /say: the mod does not report who heard a /say, so the audience is the scene's
        /// present list (can_hear, the speaker's view), minus the two of them.</summary>
        private async Task SayBystandersAsync(int aid, int listener)
        {
            List<int> hearers;
            try { hearers = Adapter.ScenePresent(await Bridge.SceneAsync(aid)).Where(p => p.CanHear).Select(p => p.Id).ToList(); }
            catch (BridgeError) { return; }
            BystandStart(new List<int> { aid, listener }, hearers, "", "say:" + aid + ":" + Bridge.Env.Game);
        }

        public void BystandStart(List<int> parties, List<int> witnesses, string summary, object key)
        {
            Bystanders bs = Bystand;
            Now now = Now();
            if (Leases.AmbientPaused(Bridge.Env.Speed)) { bs.Skipped++; return; }
            int? who = BystanderOps.Pick(witnesses, parties, Minds.ToDictionary(k => k.Key, k => k.Value), bs, now.Real, Cfg.BystanderGapRealS);
            if (who == null) return;
            bs.Mark(who.Value, now.Real);                              // one reaction per argument and per actor gap
            bs.Asked++;
            var info = new JObject { ["parties"] = new JArray(parties), ["summary"] = summary, ["key"] = PyStr.StrOf(key) };
            if (Leases.FreeSlots().Count == 0)                          // every window is busy: a template, no model call
            {
                int w = who.Value;
                string text = BystanderOps.Template(Adapter.BystanderTemplates.ToList(), w, key);
                _ = Spawn(() => ShowReactionAsync(w, text, "template"));
                return;
            }
            WorkItem item = Leases.MakeItem(who.Value, "bystander", now, null, cls: Cls.Ambient, payload: new JObject { ["bystander"] = info });
            Leases.Submit(item, now, Bridge.Env.Speed);
        }

        public async Task RunBystanderAsync(WorkItem item, Lease lease)
        {
            int aid = item.ActorId;
            try
            {
                var info = (JObject)item.Payload["bystander"];
                Mind mind = Minds[aid];
                List<int> parties = ((JArray)info["parties"]).Select(t => (int)JobsJson.PyInt(t)).ToList();
                bool minor = IsMinor((int?)aid) || parties.Any(p => IsMinor((int?)p));
                string words = await Speech.RegisterPhraseAsync(Adapter, Bridge, aid, mind);
                var msgs = BystanderOps.Messages(Adapter, mind, _names, parties, PyJson.Str(info["summary"]), minor, words);
                lease.Generating = true;
                Completion c;
                try
                {
                    c = await Llm.CompleteAsync(msgs.Select(m => new LlmMessage(m.Item1, m.Item2)).ToList(), lease.Slot, "reaction",
                                                BystanderOps.ReactionMaxTokens, new[] { "\n" }, null, 0.9, aid);
                }
                catch (LlmError) { Bystand.Dropped++; return; }
                finally { lease.Generating = false; }
                if (lease.Preempted || item.Expired(Now())) { Bystand.Dropped++; return; }
                var nt = new Dictionary<int, ISet<string>>();
                foreach (var kv in _nameTokens) nt[kv.Key] = kv.Value;
                string text = BystanderOps.CleanReaction(c.Text, msgs[1].Item2, aid, nt, new HashSet<string>(Adapter.CommonWords), minor,
                                                       MinorTokens(), Cfg.ThoughtMaxChars);
                if (text == null) { Bystand.Dropped++; Dropped.Add((aid, "reaction")); return; }
                await ShowReactionAsync(aid, text, "llm");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { LlmLog.Warning("rimworldllm", "bystander failed: " + PyNames.Of(e) + ": " + e); }
            finally
            {
                lease.Generating = false;
                if (!lease.Ended) Leases.Release(lease, "done");
            }
        }

        private async Task ShowReactionAsync(int aid, string text, string how)
        {
            Mind mind;
            if (!Minds.TryGetValue(aid, out mind)) return;
            Inner.AddInner(mind, text, "reaction", Bridge.Env.Game, Cfg.InnerStreamKeep);
            _dirty.Add(aid);
            Bystand.Shown++;
            if (how == "template") Bystand.Templated++;
            Bystand.Log.Add(new JObject { ["actor"] = aid, ["text"] = text, ["how"] = how, ["game"] = Bridge.Env.Game });
            try { await Bridge.ThoughtAsync(aid, text, surface: text); }
            catch (BridgeError e) { LlmLog.Warning("rimworldllm", "reaction cloud refused for " + aid + ": " + e.Message); }
        }

        // ---- conversations --------------------------------------------------
        public async Task RunConversationAsync(WorkItem item, Lease leaseA)
        {
            int a = item.ActorId;
            int b = item.Listener.Value;
            Lease leaseB = null;
            try
            {
                Now now = Now();
                WorkItem itemB = Leases.MakeItem(b, item.Kind, now, item.Event, a, Cls.Social);
                leaseB = Leases.Acquire(itemB, now);
                if (leaseB == null)                // only one window: fall back to a single voice
                {
                    await ServeAsync(item, leaseA);
                    return;
                }
                _convN++;
                var conv = new Conversation("c-" + _convN.ToString(CultureInfo.InvariantCulture), a, b,
                                            new Dictionary<int, string> { [a] = _names[a], [b] = _names[b] });
                string summary = item.Event == null ? "" : JobsJson.Str(item.Event, "summary", "");
                if (BridgeJson.Truthy(item.Payload["reunion"], false))
                {
                    summary = (summary + " " + PyJson.Str(item.Payload["reunion"])).Trim();
                    conv.Reunion = PyJson.Str(item.Payload["reunion_id"]);
                }
                var leases = new Dictionary<int, Lease> { [a] = leaseA, [b] = leaseB };
                var ctxs = new Dictionary<int, SanitizeCtx>();
                foreach (var (aid, trig) in new[] { (a, summary + " You start talking to " + _names[b] + "."), (b, summary + " " + _names[a] + " is talking to you.") })
                    ctxs[aid] = (await PrepareAsync(aid, trig, item.Event, leases[aid], aid == a ? b : a)).Ctx;
                bool pairMinor = IsMinor((int?)a) || IsMinor((int?)b);
                foreach (SanitizeCtx c in ctxs.Values) c.MinorInvolved = c.MinorInvolved || pairMinor;
                var startJobs = new Dictionary<int, string>();
                foreach (int aid in new[] { a, b }) startJobs[aid] = Adapter.SnapshotJob((JObject)leases[aid].Data["snap"]);
                double lastTurnReal = Now().Real;
                while (string.IsNullOrEmpty(conv.Ended))
                {
                    if (conv.Turns.Count > 0)
                    {
                        await PaceAsync(lastTurnReal, new[] { leaseA, leaseB });
                        if (leaseA.Preempted || leaseB.Preempted) { conv.Ended = "preempted"; break; }
                    }
                    conv.TurnCap = Math.Min(conv.TurnCap, TurnCap(Now().Rate));
                    int spId = conv.NextSpeaker();
                    Lease li = leases[spId];
                    int lis = conv.ListenerOf(spId);
                    li.Generating = true;
                    ctxs[spId].Secrets = MemoryOps.SecretTexts(Minds[spId], _names.TryGetValue(lis, out string lisName) ? lisName : null);
                    var (check, onCheck) = RepeatCheck(conv.Turns, spId);
                    InnerArgs ia = InnerKw(Minds[spId], lis);
                    object res = await Passes.SpeakAsync(Llm, li.Slot, Minds[spId], _names[spId], (string)li.Data["prefix"], conv.TurnsForPrompt(),
                                                         conv.Turns.Count / 2, ctxs[spId], _names[lis], LabelsFor(ctxs[spId].MinorInvolved),
                                                         Cfg.Llm.LineMaxTokens, null, ia.Aim, ia.SoloAim, ia.FreshAim, check, Cfg.RepeatRetries, onCheck,
                                                         li.Data.TryGetValue("speech", out object sp1) ? sp1 as string : null, ia.AimExtra, ia.IntentLlm,
                                                         ia.DropJustWant);
                    li.Generating = false;
                    var dr = res as Dropped;
                    if (dr != null)
                    {
                        Dropped.Add((spId, dr.Reason));
                        // a repeat that survived its retry ends the talk with no line: never a repeat on screen
                        conv.Ended = dr.Reason == "repeat" ? "repeat" : "dropped";
                        break;
                    }
                    var sp = (Spoken)res;
                    if (leaseA.Preempted || leaseB.Preempted) { conv.Ended = "preempted"; break; }
                    if (item.Expired(Now()) && conv.Turns.Count == 0) { conv.Ended = "expired"; break; }
                    bool ok = await SayAsync(spId, sp, lis, conv.Turns.Count == 0 ? WireExpiry(item) : null, conv.Id, null, false, item.Event, null,
                                             ctxs[spId].MinorInvolved);
                    if (!ok) { conv.Ended = "apart"; break; }        // the game's liveness check refused: gone or out of range
                    conv.Add(spId, sp.Text, sp.Intent);
                    RememberOwn(spId, sp.Text);
                    lastTurnReal = Now().Real;
                    Leases.Quotas.Record(spId, lis, Now(), pair: conv.Turns.Count == 1);
                    int lc; _lineCount.TryGetValue(spId, out lc);
                    _lineCount[spId] = lc + 1;
                    foreach (Lease l in new[] { leaseA, leaseB }) Leases.Touch(l, Now());
                    bool apart = false, jobChanged = false;
                    try
                    {
                        JObject sc = await Bridge.SceneAsync(spId);
                        apart = !Adapter.ScenePresent(sc).Any(p => p.Id == lis && p.CanHear);
                        var jobs = new Dictionary<int, string>();
                        foreach (int aid in new[] { a, b }) jobs[aid] = Adapter.SnapshotJob(await Bridge.ActorAsync(aid));
                        if (conv.Turns.Count == 1) startJobs = jobs;       // the game may set a talking job on the first line
                        else jobChanged = jobs[a] != startJobs[a] || jobs[b] != startJobs[b];
                    }
                    catch (BridgeError) { apart = true; }
                    conv.Ended = conv.CheckEnd(Rng, apart, jobChanged, leaseA.Preempted || leaseB.Preempted);
                }
                if (conv.Turns.Count > 0)
                {
                    await FinishConversationAsync(conv, leases);
                    Grudges.OnConversationDone(conv, Bridge.Env.Game);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { LlmLog.Warning("rimworldllm", "conversation failed: " + PyNames.Of(e) + ": " + e); }
            finally
            {
                foreach (Lease l in new[] { leaseA, leaseB })
                {
                    if (l != null)
                    {
                        l.Generating = false;
                        if (!l.Ended) Leases.Release(l, "conversation_end");
                    }
                }
            }
        }

        /// <summary>Fewer turns as the game speeds up (6 at 1x, 4 at 2x, 3 at 3x, 2 at dev speed).</summary>
        public static int TurnCap(double rate) { return rate <= 60 ? 6 : rate <= 180 ? 4 : rate <= 360 ? 3 : 2; }

        /// <summary>Give the last line time to be read before the next turn: turn_gap_ticks of game time, but at least
        /// turn_gap_floor_real_s of play time. Ends at once if a window was taken for something more important.</summary>
        public async Task PaceAsync(double lastReal, IEnumerable<Lease> leases)
        {
            var ls = leases.ToList();
            Now now = Now();
            double wait = Math.Max(Cfg.TurnGapFloorRealS, Cfg.TurnGapTicks / Math.Max(now.Rate, 1.0)) - (now.Real - lastReal);
            while (wait > 0 && !ls.Any(l => l.Preempted))
            {
                double step = Math.Min(wait, 0.25);
                await _delay(step, CancellationToken.None);
                if (!Bridge.Env.Paused) wait -= step;          // a pause is not waiting time: the line is still on screen
                foreach (Lease l in ls) Leases.Touch(l, Now());
            }
        }

        /// <summary>(check, on_check) for speak(): refuse a turn that repeats an earlier turn of this conversation or the
        /// speaker's own recent lines; count and trace every check (Loop). (null, null) when the guard is off.</summary>
        public (LineCheck Check, Action<CheckEvent> OnCheck) RepeatCheck(IList<Turn> turnList, int speaker)
        {
            if (!Cfg.RepeatGuard) return (null, null);
            var turns = turnList.Select(t => (t.Speaker, t.Text)).ToList();
            List<string> ownL;
            var own = RecentOwn.TryGetValue(speaker, out ownL) ? new List<string>(ownL) : new List<string>();
            var hits = new Dictionary<string, Hit>();
            LineCheck check = text =>
            {
                Hit hit = LoopGuard.FindRepeat(text, speaker, turns, own);
                if (hit == null) return null;
                hits[text] = hit;
                return ("repeat", LoopGuard.RetryRequest(_names[speaker], hit));
            };
            int turnNo = turnList.Count;
            string convId = ConvIdOf(turnList);
            Action<CheckEvent> onCheck = ev =>
            {
                LoopStats st = Loop;
                if (ev.Attempt == 0) st.Checked++;
                Hit hit; hits.TryGetValue(ev.Text, out hit);
                if (ev.Refused)
                {
                    if (ev.Attempt == 0) { st.Caught++; st.Retried += Cfg.RepeatRetries > 0 ? 1 : 0; }
                    if (ev.Attempt >= Cfg.RepeatRetries) st.Ended++;
                }
                else if (ev.Attempt > 0) st.Recovered++;
                st.Trace.Add(new JObject
                {
                    ["conv"] = convId, ["speaker"] = speaker, ["turn"] = turnNo, ["attempt"] = ev.Attempt, ["text"] = ev.Text,
                    ["refused"] = ev.Refused, ["against"] = hit?.Other, ["score"] = Math.Round(hit != null ? hit.Score : 0.0, 3),
                    ["kind"] = hit?.Kind,
                });
            };
            return (check, onCheck);
        }

        // The conversation id for the trace: the one conversation whose turns these are (set by RunConversation / Group).
        private string ConvIdOf(IList<Turn> turns)
        {
            return _traceConv;
        }

        private string _traceConv = null;

        public void RememberOwn(int aid, string text)
        {
            List<string> lst;
            if (!RecentOwn.TryGetValue(aid, out lst)) { lst = new List<string>(); RecentOwn[aid] = lst; }
            lst.Add(text);
            int keep = Math.Max(1, Cfg.RepeatOwnRecent);
            if (lst.Count > keep) lst.RemoveRange(0, lst.Count - keep);
        }

        /// <summary>One memory and one relation update per actor, written by that actor's window (template fallback).
        /// From 3x the templates are used at once: the windows are freed instead of waiting on four more model calls,
        /// and the memory is written either way.</summary>
        public async Task FinishConversationAsync(Conversation conv, IDictionary<int, Lease> leases)
        {
            Probes.Finish?.Invoke(conv);
            long now = Bridge.Env.Game;
            bool fast = Now().Rate >= Cfg.FastSummaryRate;
            foreach (int me in new[] { conv.A, conv.B })
            {
                int other = me == conv.A ? conv.B : conv.A;
                Mind mind = Minds[me];
                Lease li = leases[me];
                var baseMsgs = new List<(string, string)>
                {
                    ("system", (string)li.Data["prefix"]),
                    ("user", "=== TURNS SO FAR ===\n" + string.Join("\n", conv.TurnsForPrompt().Select(x => x.Item1 + ": " + x.Item2))),
                };
                string memText = null, feel = null;
                if (!fast)
                {
                    memText = await AskLineAsync(baseMsgs, "=== REQUEST ===\nIn one first-person sentence, what do you remember of this talk?", li.Slot, me, "memory");
                    feel = await AskLineAsync(baseMsgs, "=== REQUEST ===\nIn one short line, how do you feel about " + _names[other] + " now?", li.Slot, me, "relation");
                }
                var (tFeel, tHist) = Conversation.TemplateRelation(conv, me);
                var told = Adapter.ToldIntents();
                mind.Memories.Add(new Memory
                {
                    Id = mind.NextMemId(), Game = now, Text = string.IsNullOrEmpty(memText) ? Conversation.TemplateMemory(conv, me) : memText,
                    Source = "saw", Importance = 0.35 + (conv.Turns.Any(t => told.ContainsKey(t.Intent)) ? 0.2 : 0),
                    People = new List<int> { me, other }, Etype = "conversation",
                });
                string k = other.ToString(CultureInfo.InvariantCulture);
                Relation rel;
                if (!mind.Relations.TryGetValue(k, out rel)) { rel = new Relation { Name = _names[other] }; mind.Relations[k] = rel; }
                rel.Feeling = string.IsNullOrEmpty(feel) ? tFeel : feel;
                rel.History = tHist;
                rel.UpdatedTime = now;
                mind.UnreflectedImportance += 0.3;
                _dirty.Add(me);
            }
        }

        private async Task<string> AskLineAsync(List<(string, string)> msgs, string request, int slot, int aid, string kind)
        {
            Completion c;
            try
            {
                var m = msgs.Take(msgs.Count - 1).Select(x => new LlmMessage(x.Item1, x.Item2)).ToList();
                m.Add(new LlmMessage("user", msgs[msgs.Count - 1].Item2 + "\n" + request));
                c = await Llm.CompleteAsync(m, slot, kind, 40, new[] { "\n" }, null, null, aid);
            }
            catch (LlmError) { return null; }
            string t = PyStr.Strip(c.Text).Trim('"');
            int words = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
            return words >= 2 && words <= 40 ? t : null;
        }

        // ---- narrator -------------------------------------------------------
        /// <summary>All windows busy and nothing preemptable: one third-person line on the narrator slot.</summary>
        public async Task NarratorCoverAsync(WorkItem item)
        {
            Leases.NarratorBusy = true;
            try
            {
                string summary = item.Event == null ? item.Kind : JobsJson.Str(item.Event, "summary", item.Kind);
                Completion c = await Llm.CompleteAsync(new[]
                {
                    new LlmMessage("system", Adapter.NarratorPrompt),
                    new LlmMessage("user", "What just happened: " + summary + "\nWrite the narrator's one line."),
                }, Cfg.NarratorSlot, "narrator", 48, new[] { "\n" });
                if (!item.Expired(Now())) NarratorLines.Add(PyStr.Strip(c.Text));
            }
            catch (LlmError) { }
            finally { Leases.NarratorBusy = false; }
        }

        // ---- saving ---------------------------------------------------------
        public async Task SaveDirtyAsync()
        {
            foreach (int aid in _dirty.OrderBy(x => x).ToList())
            {
                Mind mind;
                if (!Minds.TryGetValue(aid, out mind)) { _dirty.Remove(aid); continue; }
                JObject blob = mind.ToBlob(Bridge.Env.Game, Day);
                try
                {
                    try
                    {
                        int rv; Revs.TryGetValue(aid, out rv);
                        Revs[aid] = await Bridge.PutMindAsync(aid, blob, rv);
                    }
                    catch (BridgeError e)
                    {
                        if (e.Code != "conflict") throw;
                        var kv = await Bridge.GetMindAsync(aid);         // stale rev: take the game's, retry once
                        Revs[aid] = kv.Value;
                        if (MergePlayerEdits(mind, kv.Key)) blob = mind.ToBlob(Bridge.Env.Game, Day);
                        Revs[aid] = await Bridge.PutMindAsync(aid, blob, Revs[aid]);
                    }
                    _dirty.Remove(aid);
                }
                catch (BridgeError e)
                {
                    LlmLog.Warning("rimworldllm", "mind save failed for " + aid + ": " + e.Message);
                    if (e.Code == "too_large") _dirty.Remove(aid);
                }
            }
        }
    }
}
