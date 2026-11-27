// Director, part 2 (core/director.py): events -> memories -> leased work. ingest, react, take_talk, launch, daily upkeep.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public sealed partial class Director
    {
        // ---- JSON reading the way the Python's dict.get / int() / float() do ---------------------
        private static string EType(JObject e) { JToken t = e["type"]; return t == null || t.Type == JTokenType.Null ? "" : PyJson.Str(t); }

        private static IEnumerable<JObject> ActorsOf(JObject e)
        {
            var a = e["actors"] as JArray;
            if (a == null) yield break;
            foreach (JToken t in a) { var o = t as JObject; if (o != null) yield return o; }
        }

        private static int IdOf(JObject a) { return (int)JobsJson.PyInt(a["id"]); }

        private static double FloatOf(JToken t, double dflt)
        {
            if (t == null || t.Type == JTokenType.Null) return dflt;
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.Boolean: return (bool)t ? 1.0 : 0.0;
                case JTokenType.String:
                    double d;
                    if (double.TryParse(PyStr.Strip((string)t), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                    throw new PyValueError("could not convert string to float: " + PyJson.StrRepr((string)t));
                default: throw new PyTypeError("float() argument must be a string or a real number");
            }
        }

        private static object Plain(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.String: return (string)t;
                default: return t.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        // ---- events ---------------------------------------------------------
        public async Task IngestAsync(IList<JObject> events)
        {
            var silent = Adapter.SilentEvents;
            foreach (JObject e in events)
            {
                Steward.Observe(e);
                Commentary.Note(e);
                if (silent.Contains(EType(e))) continue;           // for the steward only: no memory, no chronicle, no speech
                Ledger.Apply(e, ActorsOf(e).Select(IdOf).ToList());
                var wit = e["witnesses"] as JArray;
                if (wit != null)
                    foreach (JToken wt in wit.ToList())
                    {
                        int aid = (int)JobsJson.PyInt(wt);
                        if (!Minds.ContainsKey(aid) && !Adapter.IsControlled(aid)) await LoadActorAsync(aid);
                    }
                if (EType(e) == Dreams.SleepEvent)                  // a sleeper's night, not a memory: it only queues a dream
                {
                    DreamBook.Note(e, this, (int)Bridge.Env.Game);
                    continue;
                }
                if (await RimWorldLLM.Mind.Group.HandleAsync(this, e)) continue;   // a gathering: its own scene, no memory of the bare event
                Dictionary<int, string> fm = FirstMarks(e);
                if (EType(e) == "first_cue" && fm.Count == 0) continue;             // a signal only: nobody here meets it for the first time
                var written = MemoryOps.RememberEvent(Minds, e, Adapter);
                foreach (var w in written) _dirty.Add(w.Item1);
                if (fm.Count > 0) foreach (int a in Firsts.Record(Minds, e, Adapter, fm)) _dirty.Add(a);
                if (Adapter.ChronicleWorthy(e))
                {
                    JToken g = e["game"];
                    Chronicle.Note(g != null && g.Type != JTokenType.Null ? JobsJson.PyInt(g) : Bridge.Env.Game,
                                   JobsJson.Str(e, "summary", ""), FloatOf(e["severity"], 0.3), Adapter.ChronicleTrigger(e));
                }
                if (EType(e) == "art_created" && RimWorldLLM.Mind.Art.Enabled(this)) Art.Note(e, (int)Bridge.Env.Game);
                Concerns.OnEvent(e);
                Grudges.OnEvent(e);
                React(e);
                BystandReact(e);
            }
        }

        /// <summary>Witnesses meeting this kind of thing for the first time (item 8); stored on the event for react(). Empty when off.</summary>
        private Dictionary<int, string> FirstMarks(JObject e)
        {
            if (!Cfg.Firsts || !JobsJson.EventNotOff(BridgeCfg, EType(e))) return new Dictionary<int, string>();
            var fm = Firsts.Marks(Minds, e, Adapter);
            if (fm.Count > 0)
            {
                var o = new JObject();
                foreach (var kv in fm) o[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
                e["_first"] = o;
            }
            return fm;
        }

        private static Dictionary<int, string> FirstOf(JObject e)
        {
            var d = new Dictionary<int, string>();
            var o = e["_first"] as JObject;
            if (o != null) foreach (var p in o.Properties()) d[int.Parse(p.Name, CultureInfo.InvariantCulture)] = (string)p.Value;
            return d;
        }

        public void React(JObject e)
        {
            string etype = EType(e);
            Cls cls = Adapter.Priority(etype);
            if (cls == Cls.Player) return;                       // player talk arrives through take_talk
            if (!JobsJson.EventNotOff(BridgeCfg, etype)) return;
            if (etype == "funeral" && RimWorldLLM.Mind.Group.OwnsFuneral(this)) return;   // its gathering speaks for it (core/group.py)
            double sev = FloatOf(e["severity"], 0);
            double floor = cls <= Cls.Social ? Cfg.AmbientMinSeverity : Cfg.EventMinSeverity;
            Reunion reunion = Grudges.ReunionFor(e);             // the chat a seek_out for a grudge leads to: always a conversation
            if (sev < floor && reunion == null) return;          // salience first: low scores never reach the model
            Now now = Now();
            Expiry ex = LeaseManager.RequestExpiry[cls];
            JObject vanilla = Adapter.VanillaSocial(e);
            long? units = ex.Units;
            double flo = ex.FloorRealS;
            if (vanilla != null)                                 // the line must attach to the game's own entry in time
            {
                long wt = JobsJson.PyInt(vanilla["window_ticks"]);
                units = units == null ? wt : Math.Min(units.Value, wt);
                flo = Math.Max(Math.Max(flo, Cfg.VanillaAttachFloorS), vanilla["window_real_s"] != null ? FloatOf(vanilla["window_real_s"], 0.0) : 0.0);
            }
            JToken gt = e["game"];
            long age = Math.Max(0, now.Game - (gt != null && gt.Type != JTokenType.Null ? JobsJson.PyInt(gt) : now.Game));
            if (units != null && age > Math.Max(units.Value, (long)(flo * now.Rate))) return;     // a backlog event, too old to speak about
            // the moment's clock starts when it happened, not when we heard of it
            Now born = new Now(now.Game - age, now.Real - age / Math.Max(now.Rate, 1.0), now.Rate);
            var ids = ActorsOf(e).Select(IdOf).ToList();
            List<int> witnesses = e["witnesses"] is JArray wa ? wa.Select(t => (int)JobsJson.PyInt(t)).ToList() : ids;
            if (vanilla != null && BridgeJson.Truthy(vanilla["romantic"], false)
                && (IsMinor((int?)(int)JobsJson.PyInt(vanilla["initiator"])) || IsMinor((int?)(int)JobsJson.PyInt(vanilla["recipient"]))))
            {
                Gate.SkippedEvents += 1;             // no words, no memory of words, for a romantic move with a minor
                Gate.Note("vanilla_romance", new JObject { ["ref"] = vanilla["ref"], ["initiator"] = vanilla["initiator"], ["recipient"] = vanilla["recipient"] });
                return;
            }
            if (vanilla != null && reunion == null && Minds.ContainsKey((int)JobsJson.PyInt(vanilla["initiator"]))
                && Minds.ContainsKey((int)JobsJson.PyInt(vanilla["recipient"])))
            {
                // The game's own interaction: one line for it, in its priority tier (chitchat only when idle).
                Cls vcls = (string)vanilla["tier"] == "high" ? Cls.Social : Cls.Ambient;
                WorkItem vitem = Leases.MakeItem((int)JobsJson.PyInt(vanilla["initiator"]), "vanilla_social", born, e,
                                                 (int)JobsJson.PyInt(vanilla["recipient"]), vcls, new JObject { ["vanilla"] = vanilla },
                                                 units: units, floorRealS: flo);
                Leases.Submit(vitem, now, Bridge.Env.Speed);
                return;
            }
            var pair = Adapter.ConversationPair(e);
            if (pair != null && Minds.ContainsKey(pair.Value.Item1) && Minds.ContainsKey(pair.Value.Item2))
            {
                var payload = new JObject { ["conversation"] = true };
                if (reunion != null)
                {
                    payload["reunion"] = Grudges.NoteFor(reunion);
                    payload["reunion_id"] = reunion.Holder.ToString(CultureInfo.InvariantCulture) + "|" + reunion.Grudge;
                }
                WorkItem citem = Leases.MakeItem(pair.Value.Item1, etype, born, e, pair.Value.Item2, Cls.Social, payload);
                Leases.Submit(citem, now, Bridge.Env.Speed);
                return;
            }
            var cands = ids.Where(i => Minds.ContainsKey(i) && witnesses.Contains(i)).ToList();
            foreach (int w in witnesses) if (Minds.ContainsKey(w) && !cands.Contains(w)) cands.Add(w);
            int n = sev >= 0.7 ? 2 : 1;
            Dictionary<int, string> fm = FirstOf(e);
            if (etype == "first_cue")
            {
                // a signal event: only actors meeting it for the first time speak, a couple at most, each within their daily cap
                cands = Firsts.PickSpeakers(fm, Plain(e["seq"])).Where(a => Minds.ContainsKey(a)).ToList();
                n = cands.Count;
            }
            int picked = 0;
            string seqStr = e["seq"] == null || e["seq"].Type == JTokenType.Null ? "None" : PyJson.Str(e["seq"]);
            foreach (int aid in cands)
            {
                if (picked >= n) break;
                string first = null;
                if (fm.Count > 0 && FirstCap.Allow(aid, (int)(now.Game / Day), Cfg.FirstsPerDay)) fm.TryGetValue(aid, out first);
                if (etype == "first_cue" && first == null) continue;
                if (cls <= Cls.Social)
                {
                    double tf = BridgeCfg["talk_frequency"] != null ? FloatOf(BridgeCfg["talk_frequency"], 1.0) : 1.0;
                    double w;
                    _weights.TryGetValue(aid, out w);
                    if (!_weights.ContainsKey(aid)) w = 1.0;
                    double wgt = Math.Min(1.0, w * tf * 0.7);
                    double roll = Inner.Sha1Mod(seqStr + ":" + aid.ToString(CultureInfo.InvariantCulture), 1000) / 1000.0;
                    if (roll >= wgt) continue;
                }
                WorkItem item = Leases.MakeItem(aid, etype, born, e, payload: first != null ? new JObject { ["first"] = first } : null);
                if (Leases.Submit(item, now, Bridge.Env.Speed) == "queued")
                {
                    picked++;
                    if (first != null) FirstCap.Spend(aid, (int)(now.Game / Day));
                }
            }
        }

        public async Task TakeTalkAsync()
        {
            string field = Adapter.Protocol.ActorField;
            List<JObject> reqs;
            try { reqs = await Bridge.TalkAsync(); }
            catch (BridgeError) { return; }
            foreach (JObject t in reqs)
            {
                int aid = (int)JobsJson.PyInt(t[field]);
                if (!Minds.ContainsKey(aid) && !Adapter.IsControlled(aid)) await LoadActorAsync(aid);
                string tid = PyJson.Str(t["id"]);
                if (!Minds.ContainsKey(aid) || Leases.Queue.Any(q => q.Payload["talk_id"] != null && PyJson.Str(q.Payload["talk_id"]) == tid)) continue;
                if (Leases.Leases.Values.Any(l => l.Data.TryGetValue("talk_id", out object v) && v != null && PyJson.Str(v is JToken jt ? jt : JToken.FromObject(v)) == tid)) continue;
                var payload = new JObject
                {
                    ["talk_id"] = t["id"],
                    ["text"] = t["text"] ?? "",
                    ["from"] = t["from"] ?? "player",
                };
                WorkItem item = Leases.MakeItem(aid, "player_talk", Now(), null, cls: Cls.Player, payload: payload);
                Leases.Submit(item, Now(), Bridge.Env.Speed);
            }
        }

        // ---- launching work -------------------------------------------------
        public void Launch()
        {
            Now now = Now();
            foreach (var (item, lease) in Leases.PopRunnable(now))
            {
                WorkItem it = item; Lease ls = lease;
                if (BridgeJson.Truthy(it.Payload["conversation"], false)) _ = Spawn(() => RunConversationAsync(it, ls));
                else if (BridgeJson.Truthy(it.Payload["bystander"], false)) _ = Spawn(() => RunBystanderAsync(it, ls));
                else if (it.Payload["gathering"] != null && BridgeJson.Truthy(it.Payload["gathering"], false)) _ = Spawn(() => RimWorldLLM.Mind.Group.RunAsync(this, it, ls));
                else Spawn(() => ServeAsync(it, ls));
            }
            Idle.RunOnce();
            Commentary.RunOnce();
        }

        /// <summary>Once per game day: merge similar old memories and drop decayed ones, for every mind.</summary>
        public void DailyUpkeep()
        {
            int today = (int)(Bridge.Env.Game / Day);
            if (today == _upkeepDay) return;
            _upkeepDay = today;
            foreach (var kv in Minds.ToList())
            {
                var r = MemoryOps.Upkeep(kv.Value, Bridge.Env.Game, Day, Cfg.MemoryFloor, Cfg.MemoryMergeSimilarity);
                if (r.Merged > 0 || r.Dropped > 0) _dirty.Add(kv.Key);
            }
            Concerns.Upkeep();
            Grudges.Upkeep();
            RefreshReputations();
        }

        /// <summary>Rebuild what the group says about each actor (rules only, no model). Block C takes it at reflection.</summary>
        public void RefreshReputations()
        {
            _reputations.Clear();
            if (Cfg.Reputation)
                foreach (var kv in Gossip.Reputations(Minds, MinorIds())) _reputations[kv.Key] = kv.Value;
        }
    }
}
