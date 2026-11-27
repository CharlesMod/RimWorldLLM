using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>Tolerant readers for saved JSON (old or damaged saves must never throw).</summary>
    static class VivJson
    {
        public static int Int(JToken t, int dflt = 0)
        {
            if (t == null) return dflt;
            if (t.Type == JTokenType.Integer) { long v = (long)t; return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v; }
            if (t.Type == JTokenType.Float) { double d = (double)t; return double.IsNaN(d) ? dflt : d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d; }
            return dflt;
        }
        public static string Str(JToken t, string dflt = "") { return t != null && t.Type == JTokenType.String ? (string)t : dflt; }
        public static bool Bool(JToken t, bool dflt = false) { return t != null && t.Type == JTokenType.Boolean ? (bool)t : dflt; }
        public static string Clean(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length > max ? s.Substring(0, max).TrimEnd() : s;
        }
    }

    /// <summary>Mod settings the steward and the time hand obey; the game side refreshes it from RimWorldLLMSettings.</summary>
    public sealed class VivariumSettings
    {
        public bool MayPauseForEmergencies = true;
        public bool ControlsSpeed = false;
        public float HeartbeatHours = 6f;
        /// <summary>Phase 3: the narrator may slow the game to normal speed while a fight is on (and put it back after).</summary>
        public bool SlowForCombat = true;
        /// <summary>Unattended play: answer open dialogs with the safe default after DialogTimeoutS real seconds, while Narrator plays is on.</summary>
        public bool AnswerDialogs = true;
        public int DialogTimeoutS = DialogBook.DefaultTimeoutS;
        /// <summary>Unattended play: the narrator's documentary lines at big moments and a daily recap line (agent side).</summary>
        public bool Commentary = true;
        /// <summary>Biotech: may the narrator implant xenogerms into colonists. Off until the player says so.</summary>
        public bool MayImplantGenes = false;
        /// <summary>Phase 4: trade and caravans (docs/VIVARIUM.md "Phase 4"); the game side refreshes these from the mod settings.</summary>
        public bool Trade = true;
        public bool Caravans = true;
        public int TradeMaxDeal = 2000;
        public int TradeMaxDay = 6000;
        public int TradeDealsPerDay = 3;
        public int KeepMedicine = 10;
        public int KeepFoodDays = 10;
        public List<string> NeverSell = new List<string>();
        public int CaravanMaxPawns = 3;

        public TradeLimits Limits()
        {
            return new TradeLimits
            {
                MaxDeal = TradeMaxDeal, MaxDay = TradeMaxDay, DealsPerDay = TradeDealsPerDay,
                KeepMedicine = KeepMedicine, KeepFoodDays = KeepFoodDays, NeverSell = new List<string>(NeverSell ?? new List<string>())
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["answer_dialogs"] = AnswerDialogs,
                ["commentary"] = Commentary,
                ["dialog_timeout_s"] = DialogTimeoutS,
                ["may_pause_for_emergencies"] = MayPauseForEmergencies,
                ["controls_speed"] = ControlsSpeed,
                ["slow_for_combat"] = SlowForCombat,
                ["heartbeat_hours"] = HeartbeatHours,
                ["may_implant_genes"] = MayImplantGenes,
                ["trade"] = Trade,
                ["caravans"] = Caravans,
                ["trade_max_deal"] = TradeMaxDeal,
                ["trade_max_day"] = TradeMaxDay,
                ["trade_deals_per_day"] = TradeDealsPerDay,
                ["keep_medicine"] = KeepMedicine,
                ["keep_food_days"] = KeepFoodDays,
                ["never_sell"] = JArray.FromObject(NeverSell ?? new List<string>()),
                ["caravan_max_pawns"] = CaravanMaxPawns
            };
        }
    }

    /// <summary>One chat line. Who: one of <see cref="ChatKinds"/> (player, narrator, note, colonist, thought, dream, diary,
    /// chronicle, recap, art, action, refused). To: a pawn id for "@Name" lines and colonist lines, else 0.
    /// Listener: the addressee's label on a colonist line ("Ana → Bram"), else empty.</summary>
    public sealed class ChatEntry
    {
        public int Seq;
        public int Tick;
        public string Who;
        public int To;
        public string Name;   // speaker or addressee label for display ("Ana")
        public string Text;
        public string Listener = "";
        public JObject ToJson()
        {
            var o = new JObject { ["seq"] = Seq, ["tick"] = Tick, ["who"] = Who ?? "", ["to"] = To, ["name"] = Name ?? "", ["text"] = Text ?? "" };
            if (!string.IsNullOrEmpty(Listener)) o["listener"] = Listener;
            return o;
        }
    }

    /// <summary>The chat log (every generated line), capped (600). Game thread.</summary>
    public sealed class ChatLog
    {
        public const int Max = 600;   // every generated line lands here now (speech, thoughts, actions...), so more room than the talk alone needed
        public const int MaxText = 500;
        readonly List<ChatEntry> _entries = new List<ChatEntry>();
        int _seq;

        public int LastSeq { get { return _seq; } }
        public IReadOnlyList<ChatEntry> Entries { get { return _entries; } }

        /// <summary>Appends; text trimmed and capped at 500 chars; returns the entry (null for empty text).</summary>
        public ChatEntry Add(string who, string text, int tick, int to = 0, string name = null, string listener = null)
        {
            text = VivJson.Clean(text, MaxText);
            if (text.Length == 0) return null;
            var e = new ChatEntry { Seq = ++_seq, Tick = tick, Who = string.IsNullOrEmpty(who) ? "note" : who, To = to, Name = name ?? "", Text = text, Listener = VivJson.Clean(listener, 60) };
            _entries.Add(e);
            while (_entries.Count > Max) _entries.RemoveAt(0);
            return e;
        }

        /// <summary>Entries with seq greater than the given one, oldest first; next_seq is the cursor to pass next time.</summary>
        public JObject Since(int seq)
        {
            var arr = new JArray();
            int next = seq < 0 ? 0 : seq > _seq ? _seq : seq;
            foreach (var e in _entries)
            {
                if (e.Seq <= seq) continue;
                arr.Add(e.ToJson());
                next = e.Seq;
            }
            return new JObject { ["entries"] = arr, ["next_seq"] = next };
        }

        public JArray Export()
        {
            var a = new JArray();
            foreach (var e in _entries) a.Add(e.ToJson());
            return a;
        }

        /// <summary>Replaces the log from saved entries; garbage entries are skipped. The counter is the highest seq seen.</summary>
        public void Import(JArray saved)
        {
            Clear();
            if (saved == null) return;
            foreach (var t in saved)
            {
                var o = t as JObject;
                if (o == null) continue;
                string text = VivJson.Clean(VivJson.Str(o["text"]), MaxText);
                if (text.Length == 0) continue;
                int seq = VivJson.Int(o["seq"]);
                if (seq <= _seq) seq = _seq + 1;
                _entries.Add(new ChatEntry { Seq = seq, Tick = VivJson.Int(o["tick"]), Who = VivJson.Str(o["who"], "note"), To = VivJson.Int(o["to"]), Name = VivJson.Str(o["name"]), Text = text, Listener = VivJson.Clean(VivJson.Str(o["listener"]), 60) });
                _seq = seq;
            }
            while (_entries.Count > Max) _entries.RemoveAt(0);
        }

        /// <summary>Raises the counter (never lowers it); used by Vivarium.Import so seq survives an empty log.</summary>
        public void RestoreSeq(int seq) { if (seq > _seq) _seq = seq; }

        public void Clear() { _entries.Clear(); _seq = 0; }
    }

    /// <summary>The goals board (docs/BRIDGE-API.md "GET /goals"). Game thread. Rev bumps on every change.</summary>
    public sealed class GoalsBoard
    {
        public const int MaxGoals = 20, MaxOrders = 20, MaxLog = 50, MaxText = 300;

        sealed class Goal { public string Id, Text, Horizon, Status, By; }
        sealed class Order { public string Id, Text, By; }
        sealed class LogLine { public int Tick; public string Text; }

        readonly List<Goal> _goals = new List<Goal>();
        readonly List<Order> _orders = new List<Order>();
        readonly List<LogLine> _log = new List<LogLine>();
        string _plan = "";
        int _rev, _goalSeq, _orderSeq;

        public int Rev { get { return _rev; } }

        static bool Horizon(string h) { return h == "long" || h == "short"; }
        static bool Status(string s) { return s == "open" || s == "done" || s == "dropped"; }
        static string By(string b, string dflt) { return b == "player" || b == "narrator" ? b : dflt; }

        public JObject ToJson()
        {
            var o = Body();
            o["rev"] = _rev;
            return o;
        }

        JObject Body()
        {
            var g = new JArray();
            foreach (var x in _goals) g.Add(new JObject { ["id"] = x.Id, ["text"] = x.Text, ["horizon"] = x.Horizon, ["status"] = x.Status, ["by"] = x.By });
            var od = new JArray();
            foreach (var x in _orders) od.Add(new JObject { ["id"] = x.Id, ["text"] = x.Text, ["by"] = x.By });
            var l = new JArray();
            foreach (var x in _log) l.Add(new JObject { ["tick"] = x.Tick, ["text"] = x.Text });
            return new JObject { ["rev"] = _rev, ["goals"] = g, ["plan"] = _plan, ["orders"] = od, ["log"] = l };
        }

        /// <summary>Replaces goals/plan/orders from a PUT body, appends log_add; returns null or an error detail. Nothing changes on an error.</summary>
        public string Replace(JObject body, int tick)
        {
            if (body == null) return "body is missing";
            List<Goal> newGoals = null; List<Order> newOrders = null; string newPlan = null; List<string> add = null;
            int goalSeq = _goalSeq, orderSeq = _orderSeq;

            var gt = body["goals"];
            if (gt != null && gt.Type != JTokenType.Null)
            {
                var arr = gt as JArray;
                if (arr == null) return "goals must be an array";
                if (arr.Count > MaxGoals) return "at most " + MaxGoals + " goals";
                newGoals = new List<Goal>();
                var used = new HashSet<string>();
                int i = 0;
                foreach (var t in arr)
                {
                    i++;
                    var o = t as JObject;
                    if (o == null) return "goals[" + (i - 1) + "] must be an object";
                    if (o["text"] == null || o["text"].Type != JTokenType.String) return "goals[" + (i - 1) + "].text is required";
                    string text = o.Value<string>("text").Trim();
                    if (text.Length == 0 || text.Length > MaxText) return "goals[" + (i - 1) + "].text must be 1.." + MaxText + " chars";
                    string hz = o["horizon"] == null || o["horizon"].Type == JTokenType.Null ? null : VivJson.Str(o["horizon"]);
                    if (hz != null && !Horizon(hz)) return "goals[" + (i - 1) + "].horizon must be long or short";
                    string st = o["status"] == null || o["status"].Type == JTokenType.Null ? null : VivJson.Str(o["status"]);
                    if (st != null && !Status(st)) return "goals[" + (i - 1) + "].status must be open, done or dropped";
                    string id = VivJson.Str(o["id"]);
                    var old = id.Length == 0 || used.Contains(id) ? null : _goals.Find(x => x.Id == id);
                    if (old != null)
                    {
                        used.Add(id);
                        newGoals.Add(new Goal { Id = id, Text = text, Horizon = hz ?? old.Horizon, Status = st ?? old.Status, By = old.By });
                    }
                    else
                    {
                        string nid = "g" + (++goalSeq);
                        used.Add(nid);
                        newGoals.Add(new Goal { Id = nid, Text = text, Horizon = hz ?? "short", Status = st ?? "open", By = By(VivJson.Str(o["by"]), "narrator") });
                    }
                }
            }

            var ot = body["orders"];
            if (ot != null && ot.Type != JTokenType.Null)
            {
                var arr = ot as JArray;
                if (arr == null) return "orders must be an array";
                if (arr.Count > MaxOrders) return "at most " + MaxOrders + " orders";
                newOrders = new List<Order>();
                var used = new HashSet<string>();
                int i = 0;
                foreach (var t in arr)
                {
                    i++;
                    var o = t as JObject;
                    if (o == null) return "orders[" + (i - 1) + "] must be an object";
                    if (o["text"] == null || o["text"].Type != JTokenType.String) return "orders[" + (i - 1) + "].text is required";
                    string text = o.Value<string>("text").Trim();
                    if (text.Length == 0 || text.Length > MaxText) return "orders[" + (i - 1) + "].text must be 1.." + MaxText + " chars";
                    string id = VivJson.Str(o["id"]);
                    var old = id.Length == 0 || used.Contains(id) ? null : _orders.Find(x => x.Id == id);
                    if (old != null)
                    {
                        used.Add(id);
                        newOrders.Add(new Order { Id = id, Text = text, By = old.By });
                    }
                    else
                    {
                        string nid = "o" + (++orderSeq);
                        used.Add(nid);
                        newOrders.Add(new Order { Id = nid, Text = text, By = By(VivJson.Str(o["by"]), "narrator") });
                    }
                }
            }

            var pt = body["plan"];
            if (pt != null && pt.Type != JTokenType.Null)
            {
                if (pt.Type != JTokenType.String) return "plan must be a string";
                newPlan = pt.Value<string>().Trim();
                if (newPlan.Length > MaxText) return "plan must be at most " + MaxText + " chars";
            }

            var lt = body["log_add"];
            if (lt != null && lt.Type != JTokenType.Null)
            {
                var arr = lt as JArray;
                if (arr == null) return "log_add must be an array of strings";
                add = new List<string>();
                foreach (var t in arr)
                {
                    if (t.Type != JTokenType.String) return "log_add must be an array of strings";
                    string text = t.Value<string>().Trim();
                    if (text.Length == 0) continue;
                    add.Add(text.Length > MaxText ? text.Substring(0, MaxText).TrimEnd() : text);
                }
            }

            if (newGoals != null) { _goals.Clear(); _goals.AddRange(newGoals); _goalSeq = goalSeq; }
            if (newOrders != null) { _orders.Clear(); _orders.AddRange(newOrders); _orderSeq = orderSeq; }
            if (newPlan != null) _plan = newPlan;
            if (add != null) foreach (var a in add) AppendLog(a, tick);
            _rev++;
            return null;
        }

        void AppendLog(string text, int tick)
        {
            _log.Add(new LogLine { Tick = tick, Text = text });
            while (_log.Count > MaxLog) _log.RemoveAt(0);
        }

        /// <summary>Player edits from the UI. A blank text or a full board is ignored.</summary>
        public void AddGoal(string text, string horizon)
        {
            text = VivJson.Clean(text, MaxText);
            if (text.Length == 0 || _goals.Count >= MaxGoals) return;
            _goals.Add(new Goal { Id = "g" + (++_goalSeq), Text = text, Horizon = Horizon(horizon) ? horizon : "short", Status = "open", By = "player" });
            _rev++;
        }

        public void SetGoalStatus(string id, string status)
        {
            if (!Status(status)) return;
            var g = _goals.Find(x => x.Id == id);
            if (g == null || g.Status == status) return;
            g.Status = status;
            _rev++;
        }

        public void RemoveGoal(string id) { if (_goals.RemoveAll(x => x.Id == id) > 0) _rev++; }

        public void SetPlan(string text)
        {
            text = VivJson.Clean(text, MaxText);
            if (text == _plan) return;
            _plan = text;
            _rev++;
        }

        public void AddOrder(string text)
        {
            text = VivJson.Clean(text, MaxText);
            if (text.Length == 0 || _orders.Count >= MaxOrders) return;
            _orders.Add(new Order { Id = "o" + (++_orderSeq), Text = text, By = "player" });
            _rev++;
        }

        public void RemoveOrder(string id) { if (_orders.RemoveAll(x => x.Id == id) > 0) _rev++; }

        public void Log(string text, int tick)
        {
            text = VivJson.Clean(text, MaxText);
            if (text.Length == 0) return;
            AppendLog(text, tick);
            _rev++;
        }

        /// <summary>The whole board plus the id counters.</summary>
        public JObject Export()
        {
            var o = Body();
            o["goal_seq"] = _goalSeq;
            o["order_seq"] = _orderSeq;
            return o;
        }

        /// <summary>Replaces the board from a save; missing or garbage parts are skipped. Counters never fall below the highest id kept.</summary>
        public void Import(JObject saved)
        {
            Clear();
            if (saved == null) return;
            _rev = Math.Max(0, VivJson.Int(saved["rev"]));
            _plan = VivJson.Clean(VivJson.Str(saved["plan"]), MaxText);
            _goalSeq = Math.Max(0, VivJson.Int(saved["goal_seq"]));
            _orderSeq = Math.Max(0, VivJson.Int(saved["order_seq"]));
            var ga = saved["goals"] as JArray;
            if (ga != null)
                foreach (var t in ga)
                {
                    var o = t as JObject;
                    if (o == null || _goals.Count >= MaxGoals) continue;
                    string text = VivJson.Clean(VivJson.Str(o["text"]), MaxText);
                    if (text.Length == 0) continue;
                    string id = VivJson.Str(o["id"]);
                    int n = IdNum(id, 'g');
                    if (n < 0 || _goals.Exists(x => x.Id == id)) { id = "g" + (++_goalSeq); n = _goalSeq; }
                    if (n > _goalSeq) _goalSeq = n;
                    string hz = VivJson.Str(o["horizon"]), st = VivJson.Str(o["status"]);
                    _goals.Add(new Goal { Id = id, Text = text, Horizon = Horizon(hz) ? hz : "short", Status = Status(st) ? st : "open", By = By(VivJson.Str(o["by"]), "narrator") });
                }
            var oa = saved["orders"] as JArray;
            if (oa != null)
                foreach (var t in oa)
                {
                    var o = t as JObject;
                    if (o == null || _orders.Count >= MaxOrders) continue;
                    string text = VivJson.Clean(VivJson.Str(o["text"]), MaxText);
                    if (text.Length == 0) continue;
                    string id = VivJson.Str(o["id"]);
                    int n = IdNum(id, 'o');
                    if (n < 0 || _orders.Exists(x => x.Id == id)) { id = "o" + (++_orderSeq); n = _orderSeq; }
                    if (n > _orderSeq) _orderSeq = n;
                    _orders.Add(new Order { Id = id, Text = text, By = By(VivJson.Str(o["by"]), "narrator") });
                }
            var la = saved["log"] as JArray;
            if (la != null)
                foreach (var t in la)
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    string text = VivJson.Clean(VivJson.Str(o["text"]), MaxText);
                    if (text.Length == 0) continue;
                    _log.Add(new LogLine { Tick = VivJson.Int(o["tick"]), Text = text });
                }
            while (_log.Count > MaxLog) _log.RemoveAt(0);
        }

        static int IdNum(string id, char prefix)
        {
            int n;
            if (id == null || id.Length < 2 || id[0] != prefix || !int.TryParse(id.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) || n <= 0) return -1;
            return n;
        }

        public void Clear()
        {
            _goals.Clear(); _orders.Clear(); _log.Clear();
            _plan = ""; _rev = 0; _goalSeq = 0; _orderSeq = 0;
        }
    }

    /// <summary>Every action a hand took (honesty log), capped (500). Game thread.</summary>
    public sealed class ActionLog
    {
        public const int Max = 500;
        sealed class Item { public int Seq, Tick; public string Verb, Note, By, Why; }
        readonly List<Item> _items = new List<Item>();
        int _seq;

        public int LastSeq { get { return _seq; } }

        public void Add(string verb, string note, int tick, string by, string why = null)
        {
            _items.Add(new Item { Seq = ++_seq, Tick = tick, Verb = verb ?? "", Note = VivJson.Clean(note, 500), By = by ?? "narrator", Why = ChatFeed.CleanWhy(why) });
            while (_items.Count > Max) _items.RemoveAt(0);
        }

        static JObject J(Item i)
        {
            var o = new JObject { ["seq"] = i.Seq, ["tick"] = i.Tick, ["verb"] = i.Verb, ["note"] = i.Note, ["by"] = i.By };
            if (!string.IsNullOrEmpty(i.Why)) o["why"] = i.Why;
            return o;
        }

        public JObject Since(int seq)
        {
            var arr = new JArray();
            int next = seq < 0 ? 0 : seq > _seq ? _seq : seq;
            foreach (var i in _items)
            {
                if (i.Seq <= seq) continue;
                arr.Add(J(i));
                next = i.Seq;
            }
            return new JObject { ["actions"] = arr, ["next_seq"] = next };
        }

        public JArray Export()
        {
            var a = new JArray();
            foreach (var i in _items) a.Add(J(i));
            return a;
        }

        public void Import(JArray saved)
        {
            Clear();
            if (saved == null) return;
            foreach (var t in saved)
            {
                var o = t as JObject;
                if (o == null) continue;
                string verb = VivJson.Str(o["verb"]);
                string note = VivJson.Clean(VivJson.Str(o["note"]), 500);
                if (verb.Length == 0 && note.Length == 0) continue;
                int seq = VivJson.Int(o["seq"]);
                if (seq <= _seq) seq = _seq + 1;
                _items.Add(new Item { Seq = seq, Tick = VivJson.Int(o["tick"]), Verb = verb, Note = note, By = VivJson.Str(o["by"], "narrator"), Why = ChatFeed.CleanWhy(VivJson.Str(o["why"])) });
                _seq = seq;
            }
            while (_items.Count > Max) _items.RemoveAt(0);
        }

        public void RestoreSeq(int seq) { if (seq > _seq) _seq = seq; }

        public void Clear() { _items.Clear(); _seq = 0; }
    }

    /// <summary>All vivarium state for one game. BridgeRuntime holds one; the GameComponent saves it.</summary>
    public sealed class Vivarium
    {
        public bool Playing;
        public bool PausedByNarrator;   // set when the steward paused; cleared on any narrator unpause
        public readonly VivariumSettings Settings = new VivariumSettings();
        public readonly ChatLog Chat = new ChatLog();
        public readonly GoalsBoard Goals = new GoalsBoard();
        public readonly ActionLog Actions = new ActionLog();
        /// <summary>Live "is thinking / is typing" statuses. Never saved.</summary>
        public readonly ChatLive Live = new ChatLive();
        /// <summary>Phase 2: the narrator's world changes, for undo. Saved.</summary>
        public readonly NarratorLedger Ledger = new NarratorLedger();
        /// <summary>Phase 2: per-cycle caps on blueprints, zone/area cells, designations and bills. Never saved.</summary>
        public readonly BuildBudget Budget = new BuildBudget();
        /// <summary>Phase 3: the fight (refreshed by the game), the squad and the speed memory (saved).</summary>
        public readonly CombatState Combat = new CombatState();
        /// <summary>Unattended play: the choice windows and letters open right now. Never saved.</summary>
        public readonly DialogBook Dialogs = new DialogBook();
        /// <summary>Phase 4: today's trade counters (saved as trade_day) and what each narrator caravan is for (caravan_plans).</summary>
        public readonly TradeDay TradeDay = new TradeDay();
        public readonly CaravanPlans CaravanPlans = new CaravanPlans();
        static readonly System.Diagnostics.Stopwatch RealClock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Real seconds since start (modal windows pause the game, so dialog timeouts use this). Tests may replace it.</summary>
        public static Func<double> NowReal = () => RealClock.Elapsed.TotalSeconds;

        public JObject StatusJson()
        {
            return new JObject
            {
                ["playing"] = Playing,
                ["paused_by_narrator"] = PausedByNarrator,
                ["chat_seq"] = Chat.LastSeq,
                ["goals_rev"] = Goals.Rev,
                ["actions_seq"] = Actions.LastSeq,
                ["undoable"] = Ledger.Count,
                ["combat"] = Combat.StatusJson(),
                ["settings"] = Settings.ToJson()
            };
        }

        /// <summary>Whole state for the save (settings excluded: they come from the mod settings).</summary>
        public JObject Export()
        {
            return new JObject
            {
                ["playing"] = Playing,
                ["paused_by_narrator"] = PausedByNarrator,
                ["chat"] = Chat.Export(),
                ["chat_seq"] = Chat.LastSeq,
                ["goals"] = Goals.Export(),
                ["actions"] = Actions.Export(),
                ["actions_seq"] = Actions.LastSeq,
                ["ledger"] = Ledger.Export(),
                ["combat"] = Combat.Export(),
                ["trade_day"] = TradeDay.Export(),
                ["caravan_plans"] = CaravanPlans.Export()
            };
        }

        public void Import(JObject saved)
        {
            Clear();
            if (saved == null) return;
            Playing = VivJson.Bool(saved["playing"]);
            PausedByNarrator = VivJson.Bool(saved["paused_by_narrator"]);
            Chat.Import(saved["chat"] as JArray);
            Chat.RestoreSeq(VivJson.Int(saved["chat_seq"]));
            Goals.Import(saved["goals"] as JObject);
            Actions.Import(saved["actions"] as JArray);
            Actions.RestoreSeq(VivJson.Int(saved["actions_seq"]));
            Ledger.Import(saved["ledger"] as JArray);
            Combat.Import(saved["combat"] as JObject);
            TradeDay.Import(saved["trade_day"] as JObject);
            CaravanPlans.Import(saved["caravan_plans"] as JArray);
        }

        public void Clear()
        {
            Playing = false; PausedByNarrator = false;
            Chat.Clear(); Goals.Clear(); Actions.Clear(); Live.Clear(); Ledger.Clear(); Combat.Clear(); Dialogs.Clear(); TradeDay.Clear(); CaravanPlans.Clear();
        }
    }
}
