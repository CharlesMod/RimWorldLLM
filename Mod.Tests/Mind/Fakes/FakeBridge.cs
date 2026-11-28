using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind.Fakes
{
    /// <summary>
    /// Port of games/rimworld/fakes.py: an in-process fake of the RimWorld mod's bridge (BRIDGE-API v2 wire names:
    /// tick, save_epoch, pawn, expires_tick) as an IBridgeTransport. A contract implementation, not the real mod.
    /// </summary>
    public sealed class FakeBridge : IBridgeTransport
    {
        // ---- sample pawns -----------------------------------------------------
        static JObject J(string json) { return (JObject)BridgeJson.Parse(json); }

        public static JObject DefaultPawn(int pid, string first, JObject kw = null)
        {
            var p = J(@"{
""id"": 0, ""name"": {""first"": """", ""nick"": """", ""last"": ""Tester""}, ""age"": 30, ""gender"": ""Female"",
""body_type"": ""Thin"", ""xenotype"": ""Baseliner"", ""genes"": [], ""xenotype_desc"": """",
""backstory"": {""child"": {""title"": ""Farm kid"", ""desc"": ""grew up on a farm""},
              ""adult"": {""title"": ""Cook"", ""desc"": ""ran a kitchen""}},
""traits"": [{""def"": ""Abrasive"", ""label"": ""abrasive"", ""degree"": 0,
            ""desc"": ""Has a sharp tongue and little patience for small talk.""}],
""skills"": [{""skill"": ""Cooking"", ""level"": 8, ""passion"": ""major""}],
""health"": {""conditions"": [], ""missing_parts"": [], ""pain"": 0.0, ""consciousness"": 1.0, ""talking"": 1.0, ""downed"": false},
""needs"": {""mood"": 0.6, ""food"": 0.5, ""rest"": 0.7, ""joy"": 0.4},
""mood"": {""value"": 0.6, ""thoughts"": [{""label"": ""Ate without table"", ""offset"": -3, ""cause_pawn"": null}]},
""asleep"": false, ""job"": ""Cooking"", ""gear"": {""worn"": [], ""held"": []}, ""relations"": [], ""status"": ""colonist"",
""faction"": ""colony"", ""map"": 0, ""pos"": [10, 10], ""room"": ""Kitchen"", ""alive"": true, ""spawned"": true
}");
            p["id"] = pid;
            p["name"]["first"] = first;
            Update(p, kw);
            return p;
        }

        /// <summary>A pawn with every card field the mod can send (backstory descs, traits with descs, ideoligion, history).</summary>
        public static JObject RichPawn(int pid = 7, string first = "Ada", JObject kw = null)
        {
            var o = J(@"{
""age"": 34, ""name"": {""first"": """", ""nick"": ""Wren"", ""last"": ""Voss""}, ""xenotype"": ""Baseliner"",
""xenotype_desc"": ""Baseliners are normal humans with no engineered genes. They have no special strengths."",
""genes"": [{""def"": ""Learning_Fast"", ""label"": ""quick study""}, {""def"": ""Hair_Dark"", ""label"": ""dark hair""}],
""backstory"": {
  ""child"": {""title"": ""Urbworld lab brat"", ""desc"": ""Grew up in the lower levels of a research arcology, taking apart discarded instruments and teaching herself from salvaged data slates. Her parents rarely noticed.""},
  ""adult"": {""title"": ""Field researcher"", ""desc"": ""Spent a decade cataloguing ruined sites for a university guild, trading comfort for answers. The work taught her patience and to distrust easy conclusions.""}},
""traits"": [{""def"": ""TooSmart"", ""label"": ""too smart"", ""degree"": 0,
             ""desc"": ""Learns quickly but forgets to eat, and is bothered by tedious company. Her mind races.""},
            {""def"": ""Sanguine"", ""label"": ""sanguine"", ""degree"": 2,
             ""desc"": ""Almost always in a cheerful mood. Recovers quickly from bad news.""}],
""skills"": [{""skill"": ""Intellectual"", ""level"": 15, ""passion"": ""major""}, {""skill"": ""Medicine"", ""level"": 9, ""passion"": ""minor""}],
""ideo"": {""name"": ""Seekers of the Quiet Light"",
  ""memes"": [{""def"": ""Rationalist"", ""label"": ""rationalist"", ""desc"": ""Reason is the highest virtue. Ritual is distrusted.""},
              {""def"": ""Structure"", ""label"": ""structure"", ""desc"": ""Order and hierarchy keep people safe.""}],
  ""precepts"": [""Drugs: prohibited"", ""Meat eating: abhorrent"", ""Rituals: rarely"", ""Slavery: horrible""], ""certainty"": 0.8},
""royal_title"": ""Knight"",
""history"": {""arrival"": ""Crash-landed with two companions and a cracked data slate."",
  ""tales"": [""Cured the colony of a plague"", ""Survived a raid alone"", ""Wrote the first colony ledger""]},
""appearance"": ""Short dark hair, pale skin, wears a lab coat and goggles."",
""relations"": [{""pawn"": 2, ""kind"": ""lover, bond"", ""opinion"": 60}, {""pawn"": 99, ""kind"": ""parent""}]
}");
            o["name"]["first"] = first;
            return DefaultPawn(pid, first, MergeInto(o, kw));
        }

        static JObject MergeInto(JObject target, JObject src)
        {
            Update(target, src);
            return target;
        }

        /// <summary>Python dict.update: existing keys keep their place, new ones go last.</summary>
        static void Update(JObject target, JObject src)
        {
            if (src == null) return;
            foreach (var p in src.Properties()) target[p.Name] = p.Value.DeepClone();
        }

        // ---- state (the attributes of the Python class) -----------------------
        public string Token;
        public int Tick = 1000, Epoch = 1, Speed = 1;
        public bool Paused;
        public Dictionary<int, JObject> Pawns = new Dictionary<int, JObject>();
        public Dictionary<int, JObject> Scenes = new Dictionary<int, JObject>();
        public Dictionary<int, KeyValuePair<JObject, int>> Minds = new Dictionary<int, KeyValuePair<JObject, int>>();
        public List<JObject> Events = new List<JObject>();
        public int Seq;
        public int MaxKept = 2000;
        public List<JObject> SayLog = new List<JObject>();
        public List<JObject> ThoughtLog = new List<JObject>();
        public List<JObject> SocialLog = new List<JObject>();
        public bool SocialSupported = true;       // the PROPOSED /social_action op; false models a mod without it
        public List<JObject> ChronicleLog = new List<JObject>();
        public bool ChronicleSupported = true;    // POST/GET /chronicle; false models an older mod (404)
        public List<JObject> Chronicle = new List<JObject>();   // what the mod holds (cap 120)
        public List<JObject> RecapLog = new List<JObject>();
        public bool RefuseComment;               // models a mod that answers who=comment with 400 (an older mod)
        public string RecapRefuse;                // "expired"/"conflict" models the mod refusing
        public bool ArtSupported = true;
        public List<JObject> ArtLog = new List<JObject>();
        public HashSet<int> ArtThings = new HashSet<int> { 500 };   // artworks that exist (a destroyed one answers conflict)
        public bool TextSupported;                  // GET /text/queue and POST /text/fill; off by default (an older mod: 404, so the art idle job keeps running); a test turns it on
        public List<JObject> TextItems = new List<JObject>();       // the queue (already sorted); a filled item leaves it
        public List<JObject> TextFillLog = new List<JObject>();     // POST /text/fill bodies accepted
        public Action OnTextQueue;                  // test hook: runs after /text/queue built its reply (a race: the mod drops an item meanwhile)
        public int TextPendingExtra;                // added to "pending" beyond the items held
        public List<JObject> TalkQueue = new List<JObject>();
        public Dictionary<string, JObject> SeenOps = new Dictionary<string, JObject>();
        public HashSet<int> SayConflictFor = new HashSet<int>();
        public Dictionary<string, int> PairEffects = new Dictionary<string, int>();
        public int PairCap = 6;
        public JObject Config = J(@"{""effects"": ""normal"", ""language"": ""en"", ""events"": {}, ""talk_frequency"": 1.0, ""pause_ambient_at_speed"": 4, ""dlc"": []}");
        public JArray Ledger = new JArray();
        public List<KeyValuePair<string, string>> Requests = new List<KeyValuePair<string, string>>();
        // ---- vivarium (docs/BRIDGE-API.md "Vivarium routes") ----
        public bool VivariumSupported = true;     // false models an older mod: every vivarium route answers 404
        public bool Playing;
        public bool PausedByNarrator;
        public JObject VivSettings = J(@"{""may_pause_for_emergencies"": true, ""controls_speed"": false, ""heartbeat_hours"": 6.0, ""slow_for_combat"": true, ""may_implant_genes"": false, ""may_execute_prisoners"": false, ""may_slaughter_pets"": false}");
        public List<JObject> Chat = new List<JObject>();   // entries, oldest first (cap 300)
        public int ChatSeq;
        public List<JObject> ChatStatus = new List<JObject>();     // POST /chat/status bodies (minus op_id), in order
        public List<JObject> ChatPartials = new List<JObject>();     // POST /chat/partial bodies (minus op_id), in order
        public List<string> ChatStreams = new List<string>();      // stream_id of each POST /chat, in order (null when absent)
        public bool ChatStatusEnabled = true;                      // false: answer 404 like an older mod
        public JObject GoalsBoard = J(@"{""rev"": 0, ""goals"": [], ""plan"": """", ""orders"": [], ""log"": []}");
        int _goalIds;
        public List<JObject> Actions = new List<JObject>();
        public List<JObject> Dialogs = new List<JObject>();    // what GET /dialogs lists; POST /act/choose removes the answered one
        public JObject Digest;                    // what GET /digest returns (null: 409 no_game)
        public JObject Combat;                    // what GET /combat returns (null: {"active": false}); a test sets a recorded view
        public bool CombatMissing;                // GET /combat answers 404 (a read that fails)
        public int CombatSince;                   // tick the fight began (GET /vivarium combat.since_tick)
        public int? SpeedBeforeCombat;            // /act/time {combat: true, speed: 1} remembers the speed here
        public List<KeyValuePair<string, JObject>> ActLog = new List<KeyValuePair<string, JObject>>();   // (verb, body) of every accepted /act/<verb>
        public Dictionary<string, string> ActRefuse = new Dictionary<string, string>();   // verb -> reason: refused 409 "refused"
        public Dictionary<string, JObject> ActExtra = new Dictionary<string, JObject>();   // verb -> extra fields in the success reply
        public List<KeyValuePair<string, Dictionary<string, string>>> Looks = new List<KeyValuePair<string, Dictionary<string, string>>>();   // (path, query) of every GET /trade/offer
        public List<Dictionary<string, string>> VisionAsks = new List<Dictionary<string, string>>();   // the query of every GET /vision
        public int VisionPending;                 // answer this many GET /vision with {"status":"pending"} before the picture
        public (int status, string code, string detail)? VisionError;   // answer GET /vision with this error
        public string VisionB64 = "QUJDREVGR0g=";  // the picture's base64 (what the game would send)
        public List<JObject> Traders;             // what GET /vivarium lists as traders; null: the digest's trade.traders that can trade
        public JObject TradePending;              // a walking trade ({negotiator, to, with}); the digest's trade block shows it, a second trade is refused
        JObject _walk = new JObject();
        public List<KeyValuePair<string, Dictionary<string, string>>> InspectAsks = new List<KeyValuePair<string, Dictionary<string, string>>>();   // (path, query) of every GET /inspect, /find and /survey
        public Dictionary<string, string> InspectTexts = new Dictionary<string, string>();   // "/inspect?target" etc: key "<path>|<value of target or what>" -> canned text; a missing key gets a plausible default
        public string UnknownTarget;              // GET /inspect with this target is refused ("no pawn named X; colonists: A, B")
        public string InspectRefusal = "no pawn named {0}; colonists: Ana, Bo";
        public Dictionary<string, JObject> Offers = new Dictionary<string, JObject>();   // trader id -> a canned /trade/offer reply that replaces the one built from the digest

        public FakeBridge(string token = "tok") { Token = token; }

        /// <summary>A BridgeClient over a FakeBridge with the RimWorld protocol names (the Python tests' mk()).</summary>
        public static BridgeClient Client(FakeBridge fb, string token = "tok")
        {
            return new BridgeClient(fb, RimWorldLLM.Mind.RimWorld.RimWorldAdapter.WireNames, token);
        }

        // ---- test helpers -----------------------------------------------------
        public JObject AddPawn(int pid, string first, JObject kw = null)
        {
            Pawns[pid] = DefaultPawn(pid, first, kw);
            return Pawns[pid];
        }

        public JObject AddEvent(string etype, IList<KeyValuePair<int, string>> actors, string summary, IList<int> witnesses,
            double severity = 0.5, string ledgerId = null, JObject data = null)
        {
            Seq++;
            var e = new JObject
            {
                ["seq"] = Seq, ["tick"] = Tick, ["type"] = etype, ["map"] = 0,
                ["pawns"] = new JArray(actors.Select(a => new JObject { ["id"] = a.Key, ["role"] = a.Value })),
                ["summary"] = summary, ["severity"] = severity, ["witnesses"] = new JArray(witnesses),
                ["ledger_id"] = ledgerId, ["data"] = data ?? new JObject()
            };
            Events.Add(e);
            KeepLast(Events, MaxKept);   // the mod's ring keeps the newest MaxKept events
            return e;
        }

        /// <summary>A save load: epoch increments, minds are whatever the save held.</summary>
        public void ReloadGame() { Epoch++; }

        /// <summary>A chat entry as the game's chat bar (player) or the steward (narrator/note) would add it.</summary>
        public JObject AddChat(string who, string text, int to = 0, string name = "")
        {
            ChatSeq++;
            var e = new JObject { ["seq"] = ChatSeq, ["tick"] = Tick, ["who"] = who, ["to"] = to, ["name"] = name, ["text"] = text };
            Chat.Add(e);
            KeepLast(Chat, 300);
            return e;
        }

        static void KeepLast<T>(List<T> l, int n) { if (l.Count > n) l.RemoveRange(0, l.Count - n); }

        // ---- contract -----------------------------------------------------------
        public JObject Env()
        {
            return new JObject { ["tick"] = Tick, ["save_epoch"] = Epoch, ["paused"] = Paused, ["speed"] = Speed };
        }

        static void SetAll(JObject target, JObject src)
        {
            if (src == null) return;
            foreach (var p in src.Properties()) target[p.Name] = p.Value.DeepClone();
        }

        BridgeReply Err(int status, string code, string detail = "", JObject extra = null)
        {
            var o = new JObject { ["ok"] = false, ["error"] = code, ["detail"] = detail };
            SetAll(o, Env());
            SetAll(o, extra);
            return new BridgeReply(status, o.ToString(Formatting.None));
        }

        BridgeReply Ok(JObject body = null)
        {
            var o = new JObject { ["ok"] = true };
            SetAll(o, Env());
            SetAll(o, body);
            return new BridgeReply(200, o.ToString(Formatting.None));
        }

        BridgeReply Ok(params (string, JToken)[] fields)
        {
            var b = new JObject();
            foreach (var f in fields) b[f.Item1] = f.Item2;
            return Ok(b);
        }

        public Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
        {
            return Task.FromResult(Handle(request));
        }

        static string PyStr(JToken t, string dflt = "")
        {
            if (t == null) return dflt;
            switch (t.Type)
            {
                case JTokenType.String: return t.Value<string>();
                case JTokenType.Null: return "None";
                case JTokenType.Boolean: return t.Value<bool>() ? "True" : "False";
                case JTokenType.Integer: return t.Value<long>().ToString(CultureInfo.InvariantCulture);
                case JTokenType.Float: return t.Value<double>().ToString("R", CultureInfo.InvariantCulture);
                default: return t.ToString(Formatting.None);
            }
        }

        static int CpLen(string s)
        {
            int n = 0;
            foreach (char c in s) if (!char.IsLowSurrogate(c)) n++;
            return n;
        }

        static string CpHead(string s, int n)
        {
            int cps = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLowSurrogate(s[i])) { if (cps == n) return s.Substring(0, i); cps++; }
            }
            return s;
        }

        static bool IsInt(JToken t) { return t != null && t.Type == JTokenType.Integer; }
        static bool Pos(JToken t) { return IsInt(t) && t.Value<long>() > 0; }
        static bool Has(JObject o, string k) { return o[k] != null; }
        static JToken Get(JObject o, string k) { return o[k]; }
        static bool NumEq(JToken t, double v) { return t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) && t.Value<double>() == v; }
        static int? IntOrNull(JToken t) { return IsInt(t) ? (int?)t.Value<int>() : null; }
        static bool T(JToken t) { return BridgeJson.Truthy(t, false); }
        static int Qint(BridgeRequest r, string key)
        {
            foreach (var kv in r.Query)
                if (kv.Key == key) { int v; return int.TryParse(kv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }
            return 0;
        }
        static string Qstr(BridgeRequest r, string key)
        {
            foreach (var kv in r.Query) if (kv.Key == key) return kv.Value;
            return null;
        }

        static readonly Regex PawnRoute = new Regex(@"^/(pawn|scene|mind)/(\d+)$", RegexOptions.CultureInvariant);

        /// <summary>Test hook: runs at the start of every request (a Python test wraps _handle to change the world mid-run).</summary>
        public Action<BridgeRequest> OnRequest;

        BridgeReply Handle(BridgeRequest req)
        {
            if (OnRequest != null) OnRequest(req);
            string path = req.Path, method = req.Method;
            Requests.Add(new KeyValuePair<string, string>(method, path));
            if (path == "/health")
                return Ok(("mod", "RimWorldLLM"), ("version", "2.0.0"), ("game_loaded", Epoch > 0));
            string auth = req.Authorization ?? "";
            if (auth != "Bearer " + Token && !(method == "GET" && Qstr(req, "token") == Token))
                return Err(401, "unauthorized");
            var body = new JObject();
            if (method == "POST" || method == "PUT")
            {
                try
                {
                    string raw = string.IsNullOrEmpty(req.JsonBody) ? "{}" : req.JsonBody;
                    body = BridgeJson.Parse(raw) as JObject;
                    if (body == null) throw new JsonException("not an object");
                }
                catch (JsonException) { return Err(400, "bad_request", "bad json"); }
                if (!T(body["op_id"])) return Err(400, "bad_request", "op_id required");
                string op = body.Value<string>("op_id");
                if (SeenOps.ContainsKey(op))   // only succeeded writes are remembered
                    return Err(409, "duplicate", "", new JObject { ["result"] = SeenOps[op] });
                if (body["expires_tick"] != null && body["expires_tick"].Type != JTokenType.Null && Tick > body["expires_tick"].Value<double>())
                    return Err(410, "expired");
            }
            if (path == "/config") return Ok(Config);
            if (path == "/colony")
            {
                var cols = new JArray(Pawns.Select(kv => new JObject { ["id"] = kv.Key, ["name"] = kv.Value["name"].DeepClone() }));
                return Ok(("maps", new JArray(0)), ("colonists", cols), ("ledger", Ledger), ("season", "spring"), ("weather", "clear"));
            }
            var m = PawnRoute.Match(path);
            if (m.Success)
            {
                string kind = m.Groups[1].Value;
                int pid = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (kind == "mind") return MindRoute(method, pid, body);
                if (!Pawns.ContainsKey(pid)) return Err(404, "not_found", "pawn " + pid);
                if (kind == "pawn") return Ok(Pawns[pid]);
                JObject sc;
                if (!Scenes.TryGetValue(pid, out sc) || sc == null || sc.Count == 0)
                {
                    sc = new JObject
                    {
                        ["room"] = J(@"{""role"": ""Kitchen"", ""impressiveness"": 30, ""clean"": ""clean"", ""temp"": 20}"),
                        ["present"] = new JArray(Pawns.Where(kv => kv.Key != pid).Select(kv => new JObject
                            { ["pawn"] = kv.Key, ["job"] = kv.Value["job"].DeepClone(), ["distance"] = 3, ["can_hear"] = true })),
                        ["time_of_day"] = "morning", ["weather"] = "clear", ["danger"] = 0,
                        ["trends"] = J(@"{""food_days"": [9, 7, 5]}")
                    };
                }
                return Ok(sc);
            }
            if (path == "/events")
            {
                int since = Qint(req, "since");
                bool gap = Events.Count > 0 && since < Events[0].Value<int>("seq") - 1 && since < Seq - MaxKept;
                var evs = Events.Where(e => e.Value<int>("seq") > since).Take(200).ToList();
                int nxt = evs.Count > 0 ? evs[evs.Count - 1].Value<int>("seq") : Math.Max(since, Seq);
                var o = new JObject { ["events"] = new JArray(evs), ["next_seq"] = nxt };
                if (gap) o["gap"] = true;
                return Ok(o);
            }
            if (path == "/talk") return Ok(("requests", new JArray(TalkQueue)));
            if (path == "/say" && method == "POST") return Say(body);
            if (path == "/social_action" && method == "POST" && SocialSupported)
            {
                string act = body.Value<string>("action");
                var target = IntOrNull(body["target"]); var pw = IntOrNull(body["pawn"]);
                if ((act != "seek_out" && act != "avoid") || target == null || !Pawns.ContainsKey(target.Value) || pw == null || !Pawns.ContainsKey(pw.Value))
                    return Err(400, "bad_request", "action, pawn and target are required");
                SocialLog.Add(body);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("applied", true));
            }
            if (path == "/thought" && method == "POST")
            {
                string kind = body["kind"] != null ? PyStr(body["kind"]) : "thought";
                if (kind != "thought" && kind != "diary" && kind != "dream")
                    return Err(400, "bad_request", "kind must be thought or diary");
                ThoughtLog.Add(body);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok();
            }
            if (path == "/recap")
            {
                if (!string.IsNullOrEmpty(RecapRefuse)) return Err(RecapRefuse == "conflict" ? 409 : 410, RecapRefuse);
                var lines = body["lines"] as JArray;
                if (lines == null || lines.Count < 1 || lines.Count > 3 || !lines.All(x => x.Type == JTokenType.String))
                    return Err(400, "bad_request", "lines: 1 to 3 strings");
                RecapLog.Add(body);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("shown", true));
            }
            if (path == "/text/queue" && method == "GET" && TextSupported)
            {
                string ns = Qstr(req, "n");
                int n = ns == null ? 4 : int.Parse(ns, CultureInfo.InvariantCulture);
                var qr = Ok(("pending", TextItems.Count + TextPendingExtra), ("items", new JArray(TextItems.Take(n))));
                OnTextQueue?.Invoke();
                return qr;
            }
            if (path == "/text/fill" && method == "POST" && TextSupported)
            {
                var item = TextItems.FirstOrDefault(i => JToken.DeepEquals(i["id"], body["id"]));
                if (item == null) return Err(409, "unknown_id", "no item " + PyStr(body["id"]));
                string text = body["text"] == null ? "" : PyStr(body["text"]).Trim();
                var fields = item["fields"] as JArray;
                if (text.Length == 0 || (body["title"] != null && !(fields != null && fields.Any(f => (string)f == "title"))))
                    return Err(400, "bad_text", "text required; title only when fields has it");
                TextItems.Remove(item);
                TextFillLog.Add(body);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("stored", true));
            }
            if (path == "/art" && method == "POST" && ArtSupported)
            {
                var thing = IntOrNull(body["thing"]);
                string text = PyStr(body["text"]).Trim();
                if (thing == null || text.Length == 0) return Err(400, "bad_request", "thing and text are required");
                if (!ArtThings.Contains(thing.Value)) return Err(409, "conflict", "no artwork " + thing.Value);
                ArtLog.Add(body);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("stored", true), ("count", ArtLog.Count));
            }
            if (path == "/chronicle" && ChronicleSupported)
            {
                if (method == "GET")
                {
                    int sinceDay = Qint(req, "since_day");
                    return Ok(("entries", new JArray(Chronicle.Where(e => e.Value<int>("day") >= sinceDay))));
                }
                var day = IntOrNull(body["day"]);
                string text = PyStr(body["text"]).Trim();
                string kind = body["kind"] != null ? PyStr(body["kind"]) : "day";
                if (day == null || day.Value < 0 || text.Length == 0 || (kind != "day" && kind != "major" && kind != "lore"))
                    return Err(400, "bad_request", "day, text and kind are required");
                ChronicleLog.Add(body);
                Chronicle.Add(new JObject { ["day"] = day.Value, ["kind"] = kind, ["text"] = CpHead(text, 2000), ["tick"] = Tick });
                KeepLast(Chronicle, 120);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("stored", true), ("count", Chronicle.Count));
            }
            if (VivariumSupported)
            {
                var r = Vivarium(req, path, body);
                if (r != null) return r;
            }
            return Err(404, "not_found", path);
        }

        BridgeReply Say(JObject body)
        {
            var pid = IntOrNull(body["pawn"]);
            if ((pid != null && SayConflictFor.Contains(pid.Value)) || pid == null || !Pawns.ContainsKey(pid.Value))
                return Err(409, "conflict", "speaker not available");
            int? lis = IntOrNull(body["listener"]), about = IntOrNull(body["about"]);
            string intent = body["intent"] != null ? PyStr(body["intent"]) : "none";
            if (lis != null && !Pawns.ContainsKey(lis.Value))
                return Err(409, "conflict", "listener not available");
            string refusal = null;
            string vref = body["vanilla_ref"] != null && T(body["vanilla_ref"]) ? PyStr(body["vanilla_ref"]) : null;
            if (vref != null) intent = "none";   // the real mod attaches the line to the vanilla entry and applies no effect
            if (intent == "gossip" && (about == null || about == lis || about == pid || lis == null))
                refusal = "no_about";
            else if (intent != "none" && Config["effects"] != null && PyStr(Config["effects"]) == "off")
                refusal = "effects_off";
            else if (intent == "comfort" && lis != null && !((JArray)Pawns[lis.Value]["mood"]["thoughts"])
                         .Any(t => t["offset"] != null ? t["offset"].Value<double>() < 0 : false))
                refusal = "not_negative";
            else if (intent != "none" && lis != null)
            {
                string k = Math.Min(pid.Value, lis.Value) + "," + Math.Max(pid.Value, lis.Value) + "," + intent;
                int n; PairEffects.TryGetValue(k, out n);
                PairEffects[k] = ++n;
                if (n > PairCap) refusal = "pair_cap";
            }
            JObject res;
            if (vref != null)
                res = new JObject { ["applied"] = new JObject { ["bubble"] = true, ["log"] = true, ["effect"] = JValue.CreateNull(), ["thoughts"] = new JArray(), ["attached"] = "attached" } };
            else if (refusal != null)   // the bubble still shows; only the effect is refused
                res = new JObject { ["refused"] = new JObject { ["effect"] = refusal } };
            else
                res = new JObject { ["applied"] = new JObject { ["bubble"] = true, ["log"] = true, ["effect"] = intent, ["thoughts"] = new JArray() } };
            SeenOps[body.Value<string>("op_id")] = res;
            SayLog.Add(body);
            if (T(body["talk_id"]))
            {
                string tid = PyStr(body["talk_id"]);
                TalkQueue = TalkQueue.Where(t => PyStr(t["id"]) != tid).ToList();
            }
            return Ok(res);
        }

        // ---- vivarium routes ------------------------------------------------------
        void LogAction(string verb, string note)
        {
            Actions.Add(new JObject { ["seq"] = Actions.Count + 1, ["tick"] = Tick, ["verb"] = verb, ["note"] = note, ["by"] = "narrator" });
        }

        JObject CombatBlock()
        {
            var cv = Combat ?? new JObject();
            bool active = T(cv["active"]);
            long hostiles = 0;
            if (cv["threats"] is JArray th)
                foreach (var g in th.OfType<JObject>()) hostiles += g["count"] == null ? 0 : (long)RimWorldLLM.Mind.RimWorld.DigestJson.PyInt(g["count"]);
            return new JObject { ["active"] = active, ["since_tick"] = active ? CombatSince : 0, ["hostiles"] = hostiles };
        }

        JArray VivTraders()
        {
            if (Traders != null) return new JArray(Traders.Select(x => x.DeepClone()));
            var t = Digest != null ? Digest["trade"] as JObject : null;
            var l = new JArray();
            if (t != null && t["traders"] is JArray tl)
                foreach (var x in tl.OfType<JObject>())
                    if (RimWorldLLM.Mind.RimWorld.DigestJson.Truthy(x["id"]) && (x["can_trade"] == null || T(x["can_trade"])))
                        l.Add(new JObject { ["id"] = x["id"].DeepClone(), ["kind"] = x["kind"] != null ? x["kind"].DeepClone() : "caravan", ["name"] = x["name"] != null ? x["name"].DeepClone() : x["id"].DeepClone() });
            return l;
        }

        BridgeReply Vivarium(BridgeRequest req, string path, JObject body)
        {
            string method = req.Method;
            if (path == "/vivarium" && method == "GET")
                return Ok(("playing", Playing), ("paused_by_narrator", PausedByNarrator), ("chat_seq", ChatSeq),
                    ("traders", VivTraders()), ("goals_rev", GoalsBoard["rev"].DeepClone()), ("actions_seq", Actions.Count), ("settings", VivSettings.DeepClone()),
                    ("combat", CombatBlock()));
            if (path == "/combat" && method == "GET" && CombatMissing) return Err(404, "not_found", "no combat");
            if (path == "/combat" && method == "GET")
                return Ok(Combat ?? new JObject { ["active"] = false });
            if (path == "/digest" && method == "GET")
            {
                if (Digest == null) return Err(409, "no_game", "no game loaded");
                var withDialogs = (JObject)Digest.DeepClone();
                if (withDialogs["trade"] is JObject trb) trb["pending"] = TradePending != null ? TradePending.DeepClone() : JValue.CreateNull();
                if (Dialogs.Count == 0) return Ok(withDialogs);
                withDialogs["dialogs"] = new JArray(Dialogs.Select(d => d.DeepClone()));
                return Ok(withDialogs);
            }
            if (path == "/vision" && method == "GET")
            {
                VisionAsks.Add(req.Query.ToDictionary(kv => kv.Key, kv => kv.Value));
                if (VisionError.HasValue) return Err(VisionError.Value.status, VisionError.Value.code, VisionError.Value.detail);
                if (VisionPending > 0) { VisionPending--; return Ok(("status", "pending"), ("retry_ms", 250)); }
                string kind = Qstr(req, "kind");
                return Ok(("status", "ready"), ("kind", kind), ("target", Qstr(req, "target")), ("mime", "image/jpeg"), ("width", 768), ("height", 576),
                    ("bytes", VisionB64.Length), ("image_b64", VisionB64),
                    ("legend", J(@"{""north"": ""up"", ""x0"": 100, ""z0"": 80, ""x1"": 140, ""z1"": 110, ""cells_w"": 40, ""cells_h"": 30, ""px_per_cell"": 19, ""target_name"": """", ""ui"": ""hidden"", ""note"": """"}")),
                    ("captured_tick", Tick), ("age_s", 0));
            }
            if ((path == "/inspect" || path == "/find" || path == "/survey") && method == "GET")
            {
                var q = req.Query.ToDictionary(kv => kv.Key, kv => kv.Value);
                InspectAsks.Add(new KeyValuePair<string, Dictionary<string, string>>(path, q));
                string key = path == "/inspect" ? (q.TryGetValue("target", out string tg) ? tg : "") : (q.TryGetValue("what", out string wh) ? wh : "");
                if (path == "/inspect" && UnknownTarget != null && string.Equals(key, UnknownTarget, StringComparison.OrdinalIgnoreCase))
                    return Err(409, "refused", string.Format(InspectRefusal, key));
                if (InspectTexts.TryGetValue(path + "|" + key, out string canned)) return Ok(("text", canned));
                string dflt = path == "/inspect" ? key + ": fine, no needs, at home"
                    : path == "/find" ? "2 found: #101 and #102, 4 cells N of home"
                    : key == "stock" ? "steel 120, wood 80, meals 14 (6 days of food)" : "no threats";
                return Ok(("text", dflt));
            }
            if (path == "/trade/offer" && method == "GET")
            {
                var q = req.Query.ToDictionary(kv => kv.Key, kv => kv.Value);
                Looks.Add(new KeyValuePair<string, Dictionary<string, string>>(path, q));
                return TradeOffer(q);
            }
            if (path == "/dialogs" && method == "GET")
                return Ok(("dialogs", new JArray(Dialogs.Select(d => d.DeepClone()))), ("timeout_s", 90));
            if (path == "/chat")
            {
                if (method == "GET")
                {
                    int since = Qint(req, "since");
                    return Ok(("entries", new JArray(Chat.Where(e => e.Value<int>("seq") > since))), ("next_seq", ChatSeq));
                }
                string text = PyStr(body["text"]).Trim();
                string who = body["who"] != null && body["who"].Type == JTokenType.String ? body.Value<string>("who") : null;
                if (who != "narrator" && who != "note" && who != "comment") return Err(400, "bad_request", "who must be narrator, note or comment");
                if (who == "comment" && RefuseComment) return Err(400, "bad_request", "who");
                int len = CpLen(text);
                if (len < 1 || len > 500) return Err(400, "bad_request", "text 1..500 chars");
                var e = AddChat(who, text);
                ChatStreams.Add(body["stream_id"] == null || body["stream_id"].Type == JTokenType.Null ? null : (string)body["stream_id"]);
                SeenOps[body.Value<string>("op_id")] = new JObject { ["seq"] = e["seq"].DeepClone() };
                return Ok(("seq", e["seq"].DeepClone()));
            }
            if (path == "/chat/status" && method == "POST")
            {
                if (!ChatStatusEnabled) return Err(404, "not_found");
                string who = body["who"] != null && body["who"].Type == JTokenType.String ? (string)body["who"] : null;
                string state = body["state"] != null && body["state"].Type == JTokenType.String ? (string)body["state"] : null;
                var sidTok = body["stream_id"]; var to = body["to"]; var prog = body["progress"];
                if ((who != "narrator" && who != "colonist") || (state != "thinking" && state != "typing" && state != "idle"))
                    return Err(400, "bad_request", "who/state invalid");
                if (sidTok == null || sidTok.Type != JTokenType.String
                    || !System.Text.RegularExpressions.Regex.IsMatch((string)sidTok, @"\A[A-Za-z0-9_.:-]{1,64}\z"))
                    return Err(400, "bad_request", "stream_id 1..64 of [A-Za-z0-9_.:-]");
                if (who == "colonist" && !Pos(to)) return Err(400, "bad_request", "colonist needs to > 0");
                if (prog != null && prog.Type != JTokenType.Null
                    && ((prog.Type != JTokenType.Integer && prog.Type != JTokenType.Float) || prog.Value<double>() < 0 || prog.Value<double>() > 1))
                    return Err(400, "bad_request", "progress 0..1");
                var kept = (JObject)body.DeepClone(); kept.Remove("op_id");
                ChatStatus.Add(kept);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("stream_id", sidTok.DeepClone()), ("state", state));
            }
            if (path == "/chat/partial" && method == "POST")
            {
                if (!ChatStatusEnabled) return Err(404, "not_found");
                var psid = body["stream_id"]; var ptxt = body["text"];
                if (psid == null || psid.Type != JTokenType.String
                    || !System.Text.RegularExpressions.Regex.IsMatch((string)psid, @"\A[A-Za-z0-9_.:-]{1,64}\z"))
                    return Err(400, "bad_request", "stream_id 1..64 of [A-Za-z0-9_.:-]");
                if (ptxt == null || ptxt.Type != JTokenType.String || CpLen((string)ptxt) > 500)
                    return Err(400, "bad_request", "text at most 500 chars");
                var pk = (JObject)body.DeepClone(); pk.Remove("op_id");
                ChatPartials.Add(pk);
                SeenOps[body.Value<string>("op_id")] = new JObject();
                return Ok(("stream_id", psid.DeepClone()), ("accepted", true));
            }
            if (path == "/goals") return Goals(method, body);
            if (path == "/actions" && method == "GET")
            {
                int since = Qint(req, "since");
                return Ok(("actions", new JArray(Actions.Where(a => a.Value<int>("seq") > since))), ("next_seq", Actions.Count + 1));
            }
            if (path.StartsWith("/act/", StringComparison.Ordinal) && method == "POST")
            {
                string verb = path.Substring(5);
                if (Array.IndexOf(new[] { "time", "work", "schedule", "policy", "bed", "research", "letter", "draft", "order", "room", "build", "zone", "designate", "bill", "squad", "defend", "choose", "trade", "caravan", "mech_mode", "mech_group", "mech_gestate", "genepack", "xenogerm", "hemogen", "child_schedule", "feed", "child_care", "job", "medical", "prisoner", "animal", "social" }, verb) < 0)
                    return null;
                if (verb == "caravan") return Err(409, "refused", "caravans are off");
                string bad = ActShape(verb, body);
                if (bad.Length > 0) return Err(400, "bad_request", bad);
                if (ActRefuse.ContainsKey(verb)) return Err(409, "refused", ActRefuse[verb]);
                string note, refusal = ActApply(verb, body, out note);
                if (refusal.Length > 0) return Err(409, "refused", refusal);
                var walk = _walk;
                var logged = new JObject();
                foreach (var p in body.Properties()) if (p.Name != "op_id" && p.Name != "expires_tick") logged[p.Name] = p.Value.DeepClone();
                ActLog.Add(new KeyValuePair<string, JObject>(verb, logged));
                LogAction(verb, note);
                SeenOps[body.Value<string>("op_id")] = new JObject { ["done"] = note };
                var o = new JObject { ["done"] = note };
                JObject extra;
                if (ActExtra.TryGetValue(verb, out extra)) SetAll(o, extra);
                SetAll(o, walk);
                return Ok(o);
            }
            return null;
        }

        /// <summary>A deterministic offer from the digest's trade block: the trader's sells at 2.0 each, our surplus stock at 1.0.</summary>
        BridgeReply TradeOffer(Dictionary<string, string> q)
        {
            string w = q.TryGetValue("with", out string wq) ? wq : "";
            if (Offers.TryGetValue(w, out JObject canned)) return Ok(canned);
            var t = (Digest != null && Digest["trade"] is JObject to) ? to : new JObject();
            var tr = (t["traders"] as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(x => x["id"] != null && x["id"].Type == JTokenType.String && (string)x["id"] == w);
            if (tr == null) return Err(404, "not_found", "no trader " + w);
            Func<string, JObject> line = text =>
            {
                int sp = text.LastIndexOf(' ');
                string n = sp >= 0 ? text.Substring(sp + 1) : text;
                bool dig = sp >= 0 && n.Length > 0 && n.All(char.IsDigit);
                return new JObject { ["item"] = dig ? text.Substring(0, sp) : text, ["count"] = dig ? int.Parse(n, CultureInfo.InvariantCulture) : 1, ["price"] = 2.0 };
            };
            var ours = (t["stock"] as JArray ?? new JArray()).OfType<JObject>().Where(x => T(x["surplus"]))
                .Select(x => new JObject { ["item"] = x["item"], ["count"] = x["surplus"], ["price"] = 1.0 });
            JToken neg = t["negotiator"] is JObject no ? no["name"] : null;
            return Ok(new JObject
            {
                ["with"] = w, ["name"] = tr["name"] ?? w, ["silver"] = tr["silver"] ?? 0, ["negotiator"] = neg ?? JValue.CreateNull(),
                ["buys"] = new JArray((tr["sells"] as JArray ?? new JArray()).Select(x => (object)line(PyStr(x)))),
                ["sells"] = new JArray(ours), ["left"] = 3,
            });
        }

        BridgeReply Goals(string method, JObject body)
        {
            var b = GoalsBoard;
            if (method == "GET") return Ok(b);
            if (!NumEq(body["rev"], b.Value<double>("rev")))
                return Err(409, "conflict", "stale rev", new JObject { ["board"] = b.DeepClone() });
            foreach (var kc in new[] { ("goals", 20), ("orders", 20) })
            {
                var arr = body[kc.Item1];
                if (arr != null && (!(arr is JArray) || ((JArray)arr).Count > kc.Item2))
                    return Err(400, "bad_request", kc.Item1 + ": at most " + kc.Item2);
            }
            foreach (var key in new[] { "goals", "orders" })
            {
                if (body[key] == null) continue;
                var items = new JArray();
                foreach (var tok in (JArray)body[key])
                {
                    var it = (JObject)tok.DeepClone();
                    if (PyStr(it["text"]).Trim().Length == 0 || CpLen(PyStr(it["text"])) > 300)
                        return Err(400, "bad_request", "text 1..300 chars");
                    if (!T(it["id"]))
                    {
                        _goalIds++;
                        it["id"] = (key == "goals" ? "g" : "o") + _goalIds.ToString(CultureInfo.InvariantCulture);
                        if (it["by"] == null) it["by"] = "narrator";
                    }
                    if (key == "goals")
                    {
                        if (it["horizon"] == null) it["horizon"] = "short";
                        if (it["status"] == null) it["status"] = "open";
                    }
                    items.Add(it);
                }
                b[key] = items;
            }
            if (body["plan"] != null) b["plan"] = PyStr(body["plan"]);
            var logAdd = body["log_add"] as JArray;
            if (logAdd != null)
                foreach (var line in logAdd) ((JArray)b["log"]).Add(new JObject { ["tick"] = Tick, ["text"] = PyStr(line) });
            var log = (JArray)b["log"];
            while (log.Count > 50) log.RemoveAt(0);
            b["rev"] = b.Value<int>("rev") + 1;
            SeenOps[body.Value<string>("op_id")] = new JObject { ["rev"] = b["rev"].DeepClone() };
            return Ok(b);
        }

        static string ActShape(string verb, JObject b)
        {
            switch (verb)
            {
                case "time":
                    if (b["combat"] != null && b["combat"].Type == JTokenType.Boolean && (bool)b["combat"] && b["restore"] != null && b["restore"].Type == JTokenType.Boolean && (bool)b["restore"])
                        return !b.ContainsKey("speed") && !b.ContainsKey("pause") ? "" : "restore takes no speed or pause";
                    if (!Has(b, "pause") && !Has(b, "speed")) return "pause or speed required";
                    if (b.ContainsKey("combat") && !(b["combat"].Type == JTokenType.Boolean && (bool)b["combat"] && NumEq(b["speed"], 1) && !b.ContainsKey("pause")))
                        return "combat: true goes with speed 1 only";
                    if (Has(b, "speed") && !(NumEq(b["speed"], 1) || NumEq(b["speed"], 2) || NumEq(b["speed"], 3) || NumEq(b["speed"], 4)))
                        return "speed 1..4";
                    break;
                case "work":
                {
                    var pr = b["priorities"] as JObject;
                    if (!Pos(b["pawn"]) || pr == null || pr.Count < 1 || pr.Count > 20
                        || !pr.Properties().All(p => IsInt(p.Value) && p.Value.Value<long>() >= 0 && p.Value.Value<long>() <= 4))
                        return "pawn and priorities {work: 0..4} (1..20 entries) required";
                    break;
                }
                case "schedule":
                    if (!Pos(b["pawn"]) || !(IsInt(b["from_hour"]) && b.Value<long>("from_hour") >= 0 && b.Value<long>("from_hour") <= 23)
                        || !(IsInt(b["to_hour"]) && b.Value<long>("to_hour") >= 0 && b.Value<long>("to_hour") <= 24)
                        || Array.IndexOf(new[] { "anything", "work", "joy", "sleep", "meditate" }, b["assignment"] != null && b["assignment"].Type == JTokenType.String ? b.Value<string>("assignment") : null) < 0)
                        return "pawn, from_hour 0..23, to_hour 0..24, assignment required";
                    break;
                case "policy":
                {
                    int nl = CpLen(PyStr(b["name"]));
                    if (!Pos(b["pawn"]) || Array.IndexOf(new[] { "outfit", "food", "drug" }, b["kind"] != null && b["kind"].Type == JTokenType.String ? b.Value<string>("kind") : null) < 0 || nl < 1 || nl > 60)
                        return "pawn, kind outfit/food/drug, name required";
                    break;
                }
                case "bed":
                    if (!Pos(b["pawn"]) || !Pos(b["bed"])) return "pawn and bed ids required";
                    break;
                case "research":
                {
                    int n = CpLen(PyStr(b["project"]));
                    if (n < 1 || n > 80) return "project 1..80 chars";
                    break;
                }
                case "letter":
                {
                    int n = CpLen(PyStr(b["choice"]));
                    if (!Pos(b["letter"]) || n < 1 || n > 80) return "letter id and choice required";
                    break;
                }
                case "choose":
                {
                    if (b["dialog"] == null || b["dialog"].Type != JTokenType.String || b.Value<string>("dialog").Length == 0) return "dialog required";
                    var op = b["option"]; var tx = b["text"];
                    bool optOk = op != null && (op.Type == JTokenType.Integer || op.Type == JTokenType.String || op.Type == JTokenType.Boolean);
                    bool txOk = tx != null && (tx.Type == JTokenType.String || tx.Type == JTokenType.Object);
                    bool grOk = (b["passions"] is JArray) || (b["trait"] != null && b["trait"].Type == JTokenType.String);
                    if (!optOk && !txOk && !grOk) return "option or text required";
                    break;
                }
                case "trade":
                {
                    var lines = ((b["buy"] as JArray) ?? new JArray()).Concat((b["sell"] as JArray) ?? new JArray()).ToList();
                    Func<JToken, bool> lineOk = x => x is JObject xo && xo["item"] != null && xo["item"].Type == JTokenType.String && xo.Value<string>("item").Length > 0 && Pos(xo["count"]);
                    if (b["with"] == null || b["with"].Type != JTokenType.String || b.Value<string>("with").Length == 0 || lines.Count < 1 || lines.Count > 12 || !lines.All(lineOk))
                        return "with, and 1..12 buy/sell lines {item, count} required";
                    break;
                }
                case "draft":
                {
                    var ids = b["pawns"] as JArray;
                    if (ids == null || ids.Count < 1 || ids.Count > 30 || !ids.All(Pos) || b["drafted"] == null || b["drafted"].Type != JTokenType.Boolean)
                        return "pawns [1..30 ids] and drafted required";
                    break;
                }
                case "squad":
                {
                    string[] ops = { "draft", "move", "hold", "fire_at_will", "focus", "retreat", "rescue", "extinguish", "flee", "undraft" };
                    var ids = b["pawns"];
                    string op = b["op"] != null && b["op"].Type == JTokenType.String ? b.Value<string>("op") : null;
                    if (Array.IndexOf(ops, op) < 0) return "op required: " + string.Join(", ", ops);
                    if (ids != null && ids.Type != JTokenType.Null && (!(ids is JArray ia) || ia.Count < 1 || ia.Count > 30 || !ia.All(Pos))) return "pawns: 1..30 ids";
                    if (b.ContainsKey("at") && b["at"].Type != JTokenType.String) return "at must be a position word";
                    if (op == "move" && !b.ContainsKey("at") && !(b.ContainsKey("x") && b.ContainsKey("z"))) return "move needs at or x, z";
                    if (op == "focus" && !Pos(b["target"])) return "focus needs target";
                    if (b.ContainsKey("below") && !((b["below"].Type == JTokenType.Integer || b["below"].Type == JTokenType.Float) && b["below"].Value<double>() >= 0.1 && b["below"].Value<double>() <= 0.9))
                        return "below 0.1..0.9";
                    break;
                }
                case "defend":
                {
                    string k = b["kind"] != null && b["kind"].Type == JTokenType.String ? b.Value<string>("kind") : null;
                    if (Array.IndexOf(new[] { "turret", "sandbags", "barricade", "killbox", "traps", "wall" }, k) < 0)
                        return "kind required: turret, sandbags, barricade, killbox, traps, wall";
                    if (b.ContainsKey("side") && Array.IndexOf(new[] { "n", "e", "s", "w", "threat" }, b["side"].Type == JTokenType.String ? b.Value<string>("side") : null) < 0) return "side n/e/s/w/threat";
                    if (b.ContainsKey("at") && Array.IndexOf(new[] { "chokepoint", "entrance" }, b["at"].Type == JTokenType.String ? b.Value<string>("at") : null) < 0) return "at chokepoint/entrance";
                    if (b.ContainsKey("count") && !(IsInt(b["count"]) && b.Value<long>("count") >= 1 && b.Value<long>("count") <= (k == "turret" ? 4 : 8))) return "count out of range";
                    if (b.ContainsKey("length") && !(IsInt(b["length"]) && b.Value<long>("length") >= 1 && b.Value<long>("length") <= 20)) return "length 1..20";
                    break;
                }
                case "order":
                {
                    string o = b["order"] != null && b["order"].Type == JTokenType.String ? b.Value<string>("order") : null;
                    if (!Pos(b["pawn"]) || (o != "move" && o != "attack" && o != "hold"))
                        return "pawn and order move/attack/hold required";
                    if (o == "move" && !(IsInt(b["x"]) && IsInt(b["z"]) && b.Value<long>("x") >= 0 && b.Value<long>("z") >= 0))
                        return "move needs x, z >= 0";
                    if (o == "attack" && !Pos(b["target"])) return "attack needs target > 0";
                    break;
                }
            }
            return "";
        }

        /// <summary>The settings checks of /act/time and a short success note. Returns the refusal ("" = none).</summary>
        internal string ActApply(string verb, JObject b, out string note)
        {
            note = "";
            _walk = new JObject();
            if (verb == "trade" && b["with"] != null && b["with"].Type == JTokenType.String && ((string)b["with"]).Length > 0 && (((string)b["with"])[0] == 'P' || ((string)b["with"])[0] == 'S'))
            {      // a walking trade: the negotiator walks there, the deal runs later
                if (TradePending != null)
                    return "a trade is already under way: " + PyStr(TradePending["negotiator"]) + " is walking to " + PyStr(TradePending["to"]);
                string wid = (string)b["with"];
                var tb = Digest != null ? Digest["trade"] as JObject : null;
                var found = tb != null && tb["traders"] is JArray tla ? tla.OfType<JObject>().FirstOrDefault(x => x["id"] != null && x["id"].Type == JTokenType.String && (string)x["id"] == wid) : null;
                string who = found != null ? PyStr(found["name"] ?? JValue.CreateNull()) : wid;
                var ngo = tb != null ? tb["negotiator"] as JObject : null;
                string neg = ngo != null && RimWorldLLM.Mind.RimWorld.DigestJson.Truthy(ngo["name"]) ? PyStr(ngo["name"]) : "A colonist";
                string to = wid[0] == 'S' ? "the comms console" : who;      // as the mod: orbital at the console, a visitor in person
                TradePending = new JObject { ["negotiator"] = neg, ["to"] = to, ["with"] = wid };
                _walk = new JObject { ["status"] = "walking", ["negotiator"] = neg, ["to"] = to };
                note = neg + " is walking to " + to + " to trade" + (to != who ? " with " + who : "");
                return "";
            }
            bool controls = T(VivSettings["controls_speed"]);
            bool mayPause = T(VivSettings["may_pause_for_emergencies"]);
            if (verb == "squad" && b.Value<string>("op") == "undraft" && Combat != null && T(Combat["active"])
                && (Combat["threats"] as JArray ?? new JArray()).OfType<JObject>().Any(t => !T(t["dormant"]) && (t["dist"] == null || t["dist"].Type == JTokenType.Null ? 99 : t.Value<double>("dist")) <= 30))
                return "enemies are still within 30 cells of home; the squad stays drafted";    // as the mod
            if (verb == "time" && T(b["combat"]))
            {
                if (T(b["restore"]))
                {
                    if (SpeedBeforeCombat == null || Speed != 1)
                        return "nothing to put back: the speed was not slowed for a fight, or the player changed it";
                    Speed = SpeedBeforeCombat.Value;
                    SpeedBeforeCombat = null;
                    note = "put the speed back to " + Speed.ToString(CultureInfo.InvariantCulture) + "x";
                    return "";
                }
                if (!T(VivSettings["slow_for_combat"])) return "the player has not allowed slowing the game for a fight";
                if (SpeedBeforeCombat == null && Speed > 1) SpeedBeforeCombat = Speed;
                Speed = 1;
                note = "slowed the game to normal speed for the fight";
                return "";
            }
            var pause = b["pause"];
            bool pauseTrue = pause != null && pause.Type == JTokenType.Boolean && pause.Value<bool>();
            bool pauseFalse = pause != null && pause.Type == JTokenType.Boolean && !pause.Value<bool>();
            if (verb == "time")
            {
                if (Has(b, "speed") && !controls) return "the player has not allowed the narrator to change speed";
                if (pauseTrue && !(controls || (T(b["emergency"]) && mayPause)))
                    return "pausing needs an emergency and the emergency-pause setting";
                if (pauseFalse && !(controls || PausedByNarrator))
                    return "the narrator may only unpause its own pause";
                if (pauseTrue)
                {
                    PausedByNarrator = (T(b["emergency"]) && !controls) || PausedByNarrator;
                    Paused = true;
                }
                if (pauseFalse) { Paused = false; PausedByNarrator = false; }
                if (Has(b, "speed")) Speed = b.Value<int>("speed");
                var parts = new List<string>();
                foreach (var k in new[] { "pause", "speed" }) if (Has(b, k)) parts.Add(k + " " + PyStr(b[k]));
                note = "time: " + string.Join(", ", parts);
                return "";
            }
            if (verb == "choose")
            {
                string did = PyStr(b["dialog"]);
                var hit = Dialogs.FirstOrDefault(d => d["id"] != null && d["id"].Type == JTokenType.String && (string)d["id"] == did);
                if (hit == null) return "no open dialog " + did;
                Dialogs.Remove(hit);
                note = "choose " + did + ": " + RimWorldLLM.Mind.Py.PyJson.Dumps(b.ContainsKey("option") ? b["option"] : b["text"]);
                return "";
            }
            switch (verb)
            {
                case "work":
                    note = "set " + string.Join(", ", ((JObject)b["priorities"]).Properties().Select(p => p.Name + " " + PyStr(p.Value))) + " for pawn " + PyStr(b["pawn"]);
                    break;
                case "schedule": note = "schedule " + PyStr(b["from_hour"]) + "-" + PyStr(b["to_hour"]) + " " + PyStr(b["assignment"]) + " for pawn " + PyStr(b["pawn"]); break;
                case "policy": note = PyStr(b["kind"]) + " policy " + PyStr(b["name"]) + " for pawn " + PyStr(b["pawn"]); break;
                case "bed": note = "bed " + PyStr(b["bed"]) + " for pawn " + PyStr(b["pawn"]); break;
                case "research": note = "research " + PyStr(b["project"]); break;
                case "letter": note = "letter " + PyStr(b["letter"]) + ": " + PyStr(b["choice"]); break;
                case "draft": note = (b.Value<bool>("drafted") ? "drafted" : "undrafted") + " " + ((JArray)b["pawns"]).Count + " pawn(s)"; break;
                case "order": note = PyStr(b["order"]) + " order for pawn " + PyStr(b["pawn"]); break;
                case "trade": case "mech_mode": case "mech_group": case "mech_gestate": case "genepack": case "xenogerm": case "hemogen": case "child_schedule": case "feed": case "child_care": case "job": case "medical": case "prisoner": case "animal": case "social":      // phase 4 and Biotech hands: accepted as sent
                    note = verb + ": " + string.Join(", ", b.Properties().Where(p => p.Name != "why").Select(p => p.Name + " " + (p.Value.Type == JTokenType.String ? (string)p.Value : RimWorldLLM.Mind.Py.PyJson.Repr(p.Value))));
                    break;
                case "room": case "build": case "zone": case "designate": case "bill": case "squad": case "defend":     // phase 2-3 hands: accepted as sent
                    note = verb + ": " + string.Join(", ", b.Properties().Where(p => p.Name != "why").Select(p => p.Name + " " + PyStr(p.Value)));
                    break;
            }
            return "";
        }

        BridgeReply MindRoute(string method, int pid, JObject body)
        {
            KeyValuePair<JObject, int> cur;
            if (!Minds.TryGetValue(pid, out cur)) cur = new KeyValuePair<JObject, int>(null, 0);
            int rev = cur.Value;
            if (method == "GET")
                return Ok(("mind", cur.Key != null ? (JToken)cur.Key.DeepClone() : JValue.CreateNull()), ("rev", rev));
            var mind = body["mind"] ?? new JObject();
            if (Encoding.UTF8.GetByteCount(mind.ToString(Formatting.None)) > 32 * 1024) return Err(413, "too_large");
            if (!NumEq(body["rev"], rev)) return Err(409, "conflict", "stale rev", new JObject { ["rev"] = rev });
            Minds[pid] = new KeyValuePair<JObject, int>((JObject)body["mind"], rev + 1);
            SeenOps[body.Value<string>("op_id")] = new JObject { ["rev"] = rev + 1 };
            return Ok(("rev", rev + 1));
        }
    }

    /// <summary>A transport made from a function (for tests that need an unreachable or odd bridge).</summary>
    public sealed class FuncTransport : IBridgeTransport
    {
        readonly Func<BridgeRequest, BridgeReply> _f;
        public FuncTransport(Func<BridgeRequest, BridgeReply> f) { _f = f; }
        public Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
        {
            return Task.FromResult(_f(request));
        }
    }
}
