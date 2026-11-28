using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>Registers the vivarium routes. hands may be null in tests of the non-game routes.</summary>
    public static partial class VivariumRoutes
    {
        static readonly string[] Assignments = { "anything", "work", "joy", "sleep", "meditate" };
        static readonly string[] Kinds = { "outfit", "food", "drug" };
        static readonly string[] Orders = { "move", "attack", "hold" };

        public static void Register(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("GET", "/vivarium", false, c => ApiResult.Success(vivarium().StatusJson()));
            router.Add("GET", "/chat", false, c => ChatGet(c, vivarium()));
            router.Add("POST", "/chat", false, c => ChatPost(c, vivarium()));
            router.Add("POST", "/chat/status", false, c => ChatStatus(c, vivarium()));
            router.Add("POST", "/chat/partial", false, c => ChatPartial(c, vivarium()));
            router.Add("GET", "/goals", false, c => ApiResult.Success(vivarium().Goals.ToJson()));
            router.Add("PUT", "/goals", false, c => GoalsPut(c, vivarium()));
            router.Add("GET", "/actions", false, c => ActionsGet(c, vivarium()));
            router.Add("GET", "/digest", true, c =>
            {
                var h = hands();
                if (h == null) return NoGame();
                var d = h.Digest();
                return d == null ? NoGame() : ApiResult.Success(d);
            });

            router.Add("POST", "/act/time", true, c => Act(c, vivarium, hands, "time", b => Time(b, vivarium)));
            router.Add("POST", "/act/work", true, c => Act(c, vivarium, hands, "work", Work));
            router.Add("POST", "/act/schedule", true, c => Act(c, vivarium, hands, "schedule", Schedule));
            router.Add("POST", "/act/policy", true, c => Act(c, vivarium, hands, "policy", Policy));
            router.Add("POST", "/act/bed", true, c => Act(c, vivarium, hands, "bed", Bed));
            router.Add("POST", "/act/research", true, c => Act(c, vivarium, hands, "research", Research));
            router.Add("POST", "/act/letter", true, c => Act(c, vivarium, hands, "letter", Letter));
            router.Add("POST", "/act/draft", true, c => Act(c, vivarium, hands, "draft", Draft));
            router.Add("POST", "/act/order", true, c => Act(c, vivarium, hands, "order", Order));
            router.Add("GET", "/dialogs", false, c =>
            {
                var v = vivarium();
                return ApiResult.Success(v.Dialogs.ToJson(v.Settings.DialogTimeoutS, Vivarium.NowReal()));
            });
            router.Add("POST", "/act/choose", true, c => Choose(c, vivarium, hands));
            RegisterBuild(router, vivarium, hands);
            RegisterBio(router, vivarium, hands);
            RegisterTrade(router, vivarium, hands);
            RegisterCombat(router, vivarium, hands);
            RegisterZones(router, vivarium, hands);
            RegisterFloors(router, vivarium, hands);
            RegisterUndo(router, vivarium, hands);
        }

        static ApiResult NoGame() { return ApiResult.Fail("no_game", "no game is loaded"); }
        static ApiResult Bad(string detail) { return ApiResult.Fail("bad_request", detail); }

        // ---------------------------------------------------------------- non-game routes
        static ApiResult SinceArg(RouteContext c, out int since)
        {
            since = 0;
            string q = c.Q("since");
            if (string.IsNullOrEmpty(q)) return null;
            if (!int.TryParse(q, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out since))
                return Bad("since must be an integer");
            return null;
        }

        static ApiResult ChatGet(RouteContext c, Vivarium v)
        {
            int since;
            var err = SinceArg(c, out since);
            return err ?? ApiResult.Success(v.Chat.Since(since));
        }

        static ApiResult ActionsGet(RouteContext c, Vivarium v)
        {
            int since;
            var err = SinceArg(c, out since);
            return err ?? ApiResult.Success(v.Actions.Since(since));
        }

        static ApiResult ChatPost(RouteContext c, Vivarium v)
        {
            var b = c.Body;
            string who = b["who"] != null && b["who"].Type == JTokenType.String ? b.Value<string>("who") : null;
            if (who != "narrator" && who != "note" && who != ChatKinds.Comment) return Bad("who must be \"narrator\", \"note\" or \"comment\"");
            if (b["text"] == null || b["text"].Type != JTokenType.String) return Bad("text is required");
            string text = b.Value<string>("text").Trim();
            if (text.Length == 0) return Bad("text is empty");
            if (text.Length > ChatLog.MaxText) return Bad("text must be at most " + ChatLog.MaxText + " chars");
            string sid = null;
            if (b["stream_id"] != null && b["stream_id"].Type != JTokenType.Null)
            {
                sid = b["stream_id"].Type == JTokenType.String ? b.Value<string>("stream_id") : null;
                if (!ChatLive.ValidId(sid)) return Bad("stream_id must be 1..64 chars of A-Z a-z 0-9 _ . : -");
            }
            var e = v.Chat.Add(who, text, c.State.Tick);
            if (sid != null) v.Live.End(sid);   // the line is permanent now; the live line goes
            return ApiResult.Success(new JObject { ["seq"] = e.Seq });
        }

        static ApiResult ChatStatus(RouteContext c, Vivarium v)
        {
            var b = c.Body;
            string sid = b["stream_id"] != null && b["stream_id"].Type == JTokenType.String ? b.Value<string>("stream_id") : null;
            string state = b["state"] != null && b["state"].Type == JTokenType.String ? b.Value<string>("state") : null;
            string who = b["who"] != null && b["who"].Type == JTokenType.String ? b.Value<string>("who") : null;
            int to = 0;
            var tt = b["to"];
            if (tt != null && tt.Type != JTokenType.Null)
            {
                if (tt.Type != JTokenType.Integer) return Bad("to must be an integer (the colonist's id)");
                long l = tt.Value<long>();
                if (l <= 0 || l > int.MaxValue) return Bad("to must be a positive colonist id");
                to = (int)l;
            }
            float progress = float.NaN;
            var pt = b["progress"];
            if (pt != null && pt.Type != JTokenType.Null)
            {
                if (pt.Type != JTokenType.Integer && pt.Type != JTokenType.Float) return Bad("progress must be a number 0..1");
                double p = pt.Value<double>();
                if (double.IsNaN(p) || p < 0 || p > 1) return Bad("progress must be a number 0..1");
                progress = (float)p;
            }
            string name = b["name"] != null && b["name"].Type == JTokenType.String ? b.Value<string>("name") : null;
            string err = v.Live.Status(sid, who, to, name, state, progress);
            if (err != null) return Bad(err);
            if (state == "thinking" && who == "narrator") v.Budget.NewCycle(c.State.Tick);   // a steward cycle starts: fresh build caps
            return ApiResult.Success(new JObject { ["stream_id"] = sid, ["state"] = state, ["live"] = v.Live.IsLive(sid) });
        }

        static ApiResult ChatPartial(RouteContext c, Vivarium v)
        {
            var b = c.Body;
            string sid = b["stream_id"] != null && b["stream_id"].Type == JTokenType.String ? b.Value<string>("stream_id") : null;
            if (!ChatLive.ValidId(sid)) return Bad("stream_id must be 1..64 chars of A-Z a-z 0-9 _ . : -");
            if (b["text"] == null || b["text"].Type != JTokenType.String) return Bad("text is required (the whole text so far)");
            string text = b.Value<string>("text");
            if (text.Trim().Length > ChatLog.MaxText) return Bad("text must be at most " + ChatLog.MaxText + " chars");
            bool ok = v.Live.Partial(sid, text);
            return ApiResult.Success(new JObject { ["stream_id"] = sid, ["accepted"] = ok });
        }

        static ApiResult GoalsPut(RouteContext c, Vivarium v)
        {
            var b = c.Body;
            var rt = b["rev"];
            if (rt == null || rt.Type != JTokenType.Integer) return Bad("rev is required (the board's current rev, an integer)");
            if (rt.Value<long>() != v.Goals.Rev)
                return ApiResult.Fail("conflict", "stale rev " + rt.Value<long>() + "; the board is at rev " + v.Goals.Rev, new JObject { ["board"] = v.Goals.ToJson() });
            string err = v.Goals.Replace(b, c.State.Tick);
            if (err != null) return Bad(err);
            return ApiResult.Success(v.Goals.ToJson());
        }

        // ---------------------------------------------------------------- argument helpers
        sealed class ArgError : Exception { public ArgError(string m) : base(m) { } }

        static int Int(JObject b, string f, int min, int max)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) throw new ArgError(f + " is required");
            if (t.Type != JTokenType.Integer) throw new ArgError(f + " must be an integer");
            long v = t.Value<long>();
            if (v < min || v > max) throw new ArgError(f + " must be " + min + ".." + (max == int.MaxValue ? "max" : max.ToString()));
            return (int)v;
        }

        static int? OptInt(JObject b, string f, int min, int max)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return null;
            return Int(b, f, min, max);
        }

        static bool? OptBool(JObject b, string f)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Boolean) throw new ArgError(f + " must be true or false");
            return t.Value<bool>();
        }

        static bool Bool(JObject b, string f)
        {
            var v = OptBool(b, f);
            if (v == null) throw new ArgError(f + " is required");
            return v.Value;
        }

        static string Str(JObject b, string f, int min, int max)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) throw new ArgError(f + " is required");
            if (t.Type != JTokenType.String) throw new ArgError(f + " must be a string");
            string s = t.Value<string>().Trim();
            if (s.Length < min || s.Length > max) throw new ArgError(f + " must be " + min + ".." + max + " chars");
            return s;
        }

        static string OneOf(JObject b, string f, string[] allowed)
        {
            string s = Str(b, f, 1, 40).ToLowerInvariant();
            if (Array.IndexOf(allowed, s) < 0) throw new ArgError(f + " must be one of " + string.Join(", ", allowed));
            return s;
        }

        static int Pawn(JObject b, string f) { return Int(b, f, 1, int.MaxValue); }

        // ---------------------------------------------------------------- /act/*

        // Each verb checks its arguments first (throwing ArgError) and only then calls the hand,
        // so a 400 never reaches the game. The two-phase shape is Check (parse) + Run (gate + hand).
        sealed class Plan
        {
            public Func<Vivarium, HandResult> Gate;                     // null = nothing to refuse; else a refusal or null
            public Func<Vivarium, int, HandResult> GateAt;              // the same, with the game tick
            public Func<IVivariumHands, HandResult> Run;
            public Func<IVivariumHands, BuildBudget, HandResult> RunLeft; // phase 2: gets what the cycle caps still allow
            public Action<Vivarium> OnOk;
            public Action<Vivarium, HandResult> OnResult;               // phase 3: like OnOk, with the hand's result (the combat speed memory)
            public Func<string> What;                                   // plain English of what is attempted, from the parsed args ("pause the game")
        }
        delegate Plan Parser(JObject body);

        static ApiResult Act(RouteContext c, Func<Vivarium> vivarium, Func<IVivariumHands> hands, string verb, Parser parse)
        {
            Plan plan;
            try { plan = parse(c.Body); }
            catch (ArgError e) { return Bad(e.Message); }
            string why = "";
            JToken wt = c.Body["why"];
            if (wt != null && wt.Type != JTokenType.Null)
            {
                if (wt.Type != JTokenType.String) return Bad("why must be a string");
                why = ChatFeed.CleanWhy((string)wt);
            }
            var h = hands();
            if (h == null) return NoGame();
            var v = vivarium();
            if (plan.Gate != null)
            {
                var refusal = plan.Gate(v);
                if (refusal != null)
                {
                    ChatFeed.Refused(v.Chat, c.State.Tick, plan.What != null ? plan.What() : verb, refusal.Reason);
                    return ApiResult.Fail("refused", refusal.Reason);
                }
            }
            if (plan.GateAt != null)
            {
                var refusal = plan.GateAt(v, c.State.Tick);
                if (refusal != null)
                {
                    ChatFeed.Refused(v.Chat, c.State.Tick, plan.What != null ? plan.What() : verb, refusal.Reason);
                    return ApiResult.Fail("refused", refusal.Reason);
                }
            }
            v.Budget.Tick(c.State.Tick);
            var r = plan.RunLeft != null ? plan.RunLeft(h, v.Budget.Left()) : plan.Run(h);
            if (r == null) return ApiResult.Fail("refused", "the game gave no answer");
            if (!r.Ok)
            {
                string reason = string.IsNullOrEmpty(r.Reason) ? "refused" : r.Reason;
                ChatFeed.Refused(v.Chat, c.State.Tick, !string.IsNullOrEmpty(r.What) ? r.What : plan.What != null ? plan.What() : verb, reason);
                return ApiResult.Fail("refused", reason);
            }
            if (plan.OnOk != null) plan.OnOk(v);
            if (plan.OnResult != null) plan.OnResult(v, r);
            string note = r.Note ?? "";
            if (r.Pending)
            {
                // nothing has happened yet (a colonist is on the way): no greentext, no action; a short note line instead
                if (note.Length > 0) v.Chat.Add(ChatKinds.Note, ChatFeed.Shorten(note, 160), c.State.Tick);
            }
            else
            {
                v.Actions.Add(verb, note, c.State.Tick, "narrator", why);
                ChatFeed.Action(v.Chat, c.State.Tick, note, why);
            }
            var body = new JObject { ["done"] = note };
            if (r.Marks != null && r.Marks.Count > 0)
            {
                v.Budget.Charge(r.Marks);
                var e = v.Ledger.Add(verb, note, c.State.Tick, r.Marks);
                body["undo_seq"] = e.Seq;
                body["left"] = v.Budget.ToJson();
            }
            if (r.Extra != null)
                foreach (var p in r.Extra.Properties())
                    if (body[p.Name] == null) body[p.Name] = p.Value;
            return ApiResult.Success(body);
        }

        static Plan Time(JObject b, Func<Vivarium> vivarium)
        {
            bool? pause = OptBool(b, "pause");
            int? speed = OptInt(b, "speed", 1, 4);
            bool emergency = OptBool(b, "emergency") ?? false;
            bool combat = OptBool(b, "combat") ?? false;
            bool restore = OptBool(b, "restore") ?? false;
            if (combat) return CombatTime(pause, speed, restore, vivarium);
            if (restore) throw new ArgError("restore goes with combat: true");
            if (pause == null && speed == null) throw new ArgError("give pause and/or speed");
            return new Plan
            {
                Gate = v =>
                {
                    var s = v.Settings;
                    if (speed != null && !s.ControlsSpeed) return HandResult.No("the player has not let the narrator change speed");
                    if (pause == true && !s.ControlsSpeed)
                    {
                        if (!emergency) return HandResult.No("the player has not let the narrator pause the game except in an emergency (send emergency: true)");
                        if (!s.MayPauseForEmergencies) return HandResult.No("the player has not let the narrator pause for emergencies");
                    }
                    if (pause == false && !s.ControlsSpeed && !v.PausedByNarrator)
                        return HandResult.No("the narrator did not pause the game, and the player has not let it change speed");
                    return null;
                },
                What = () => VivariumHandsPure.TimeWhat(pause, speed),
                Run = h => h.SetTime(pause, speed),
                OnOk = v =>
                {
                    if (pause == true) v.PausedByNarrator = true;
                    else if (pause == false) v.PausedByNarrator = false;
                    if (speed != null) v.Combat.SpeedBefore = 0;   // an explicit speed replaces the fight's memory
                }
            };
        }

        /// <summary>The fight's speed rule: combat + speed 1 slows to normal speed (setting slow_for_combat, or controls_speed) and
        /// remembers the speed before; combat + restore puts it back (only if the narrator slowed it and the game is still at 1x).</summary>
        static Plan CombatTime(bool? pause, int? speed, bool restore, Func<Vivarium> vivarium)
        {
            if (pause != null) throw new ArgError("combat: true takes speed 1 or restore, not pause");
            if (restore && speed != null) throw new ArgError("give speed 1 or restore, not both");
            if (!restore && speed != 1) throw new ArgError("combat: true only slows to speed 1 (or restore: true puts the speed back)");
            if (restore)
                return new Plan
                {
                    Gate = v => v.Combat.SpeedBefore > 0 ? null : HandResult.No("the narrator did not slow the game for a fight"),
                    What = () => "put the speed back after the fight",
                    Run = h => h.CombatSpeed(vivarium().Combat.SpeedBefore, true),
                    OnOk = v => v.Combat.SpeedBefore = 0
                };
            return new Plan
            {
                Gate = v => v.Settings.SlowForCombat || v.Settings.ControlsSpeed ? null
                    : HandResult.No("the player has not let the narrator slow the game for fights"),
                What = () => "slow the game to normal speed for the fight",
                Run = h => h.CombatSpeed(1, false),
                OnResult = (v, r) =>
                {
                    int before = r.Extra != null ? VivJson.Int(r.Extra["before"]) : 0;
                    if (before >= 2 && before <= 4 && v.Combat.SpeedBefore == 0) v.Combat.SpeedBefore = before;
                }
            };
        }

        static Plan Work(JObject b)
        {
            int pawn = Pawn(b, "pawn");
            var po = b["priorities"] as JObject;
            if (po == null) throw new ArgError("priorities must be an object of work type to 0..4");
            if (po.Count < 1 || po.Count > 20) throw new ArgError("priorities must have 1..20 entries");
            var d = new Dictionary<string, int>();
            foreach (var p in po.Properties())
            {
                string name = p.Name.Trim();
                if (name.Length == 0) throw new ArgError("priorities has an empty work type name");
                if (p.Value.Type != JTokenType.Integer || p.Value.Value<long>() < 0 || p.Value.Value<long>() > 4)
                    throw new ArgError("priority for " + name + " must be an integer 0..4");
                d[name] = p.Value.Value<int>();
            }
            return new Plan { What = () => "change pawn " + pawn + "'s work priorities", Run = h => h.SetWork(pawn, d) };
        }

        static Plan Schedule(JObject b)
        {
            int pawn = Pawn(b, "pawn");
            int from = Int(b, "from_hour", 0, 23), to = Int(b, "to_hour", 0, 24);
            if (from == to) throw new ArgError("from_hour and to_hour must differ (use 0 and 24 for the whole day)");
            string a = OneOf(b, "assignment", Assignments);
            return new Plan { What = () => "change pawn " + pawn + "'s schedule", Run = h => h.SetSchedule(pawn, from, to, a) };
        }

        static Plan Policy(JObject b)
        {
            int pawn = Pawn(b, "pawn");
            string kind = OneOf(b, "kind", Kinds);
            string name = Str(b, "name", 1, 60);
            return new Plan { What = () => VivariumHandsPure.PolicyWhat("pawn " + pawn, kind, name), Run = h => h.SetPolicy(pawn, kind, name) };
        }

        static Plan Bed(JObject b)
        {
            int pawn = Pawn(b, "pawn"), bed = Pawn(b, "bed");
            return new Plan { What = () => "give pawn " + pawn + " bed " + bed, Run = h => h.AssignBed(pawn, bed) };
        }

        static Plan Research(JObject b)
        {
            string p = Str(b, "project", 1, 80);
            return new Plan { What = () => "start researching " + p, Run = h => h.SetResearch(p) };
        }

        static Plan Letter(JObject b)
        {
            int letter = Pawn(b, "letter");
            string choice = Str(b, "choice", 1, 80);
            return new Plan { What = () => "answer the letter with " + choice, Run = h => h.AnswerLetter(letter, choice) };
        }

        static Plan Draft(JObject b)
        {
            var arr = b["pawns"] as JArray;
            if (arr == null) throw new ArgError("pawns must be an array of pawn ids");
            if (arr.Count < 1 || arr.Count > 30) throw new ArgError("pawns must have 1..30 ids");
            var ids = new List<int>();
            foreach (var t in arr)
            {
                if (t.Type != JTokenType.Integer || t.Value<long>() < 1 || t.Value<long>() > int.MaxValue) throw new ArgError("pawns must be positive integer ids");
                ids.Add(t.Value<int>());
            }
            bool drafted = Bool(b, "drafted");
            return new Plan { What = () => VivariumHandsPure.DraftWhat(ids.ConvertAll(i => "pawn " + i), drafted), Run = h => h.Draft(ids, drafted) };
        }

        static Plan Order(JObject b)
        {
            int pawn = Pawn(b, "pawn");
            string order = OneOf(b, "order", Orders);
            int x = 0, z = 0, target = 0; bool hold = false;
            if (order == "move") { x = Int(b, "x", 0, int.MaxValue); z = Int(b, "z", 0, int.MaxValue); }
            else if (order == "attack") target = Int(b, "target", 1, int.MaxValue);
            else hold = Bool(b, "hold");
            return new Plan { What = () => VivariumHandsPure.OrderWhat("pawn " + pawn, order, x, z, "target " + target, hold), Run = h => h.Order(pawn, order, x, z, target, hold) };
        }
    }
}
