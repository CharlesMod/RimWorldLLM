using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    sealed class FakeHands : IVivariumHands
    {
        public List<string> Calls = new List<string>();
        public HandResult Next = HandResult.Done("did it");
        public JObject DigestJson = new JObject { ["weather"] = "Rain", ["map"] = "Ferngrove" };
        public JObject Digest() { Calls.Add("digest"); return DigestJson; }
        public HandResult SetTime(bool? pause, int? speed) { Calls.Add("time " + (pause?.ToString() ?? "-") + " " + (speed?.ToString() ?? "-")); return Next; }
        public HandResult SetWork(int pawn, IDictionary<string, int> p) { Calls.Add("work " + pawn + " " + string.Join(",", p.Select(x => x.Key + "=" + x.Value))); return Next; }
        public HandResult SetSchedule(int pawn, int f, int t, string a) { Calls.Add("schedule " + pawn + " " + f + " " + t + " " + a); return Next; }
        public HandResult SetPolicy(int pawn, string k, string n) { Calls.Add("policy " + pawn + " " + k + " " + n); return Next; }
        public HandResult AssignBed(int pawn, int bed) { Calls.Add("bed " + pawn + " " + bed); return Next; }
        public HandResult SetResearch(string p) { Calls.Add("research " + p); return Next; }
        public HandResult AnswerLetter(int l, string c) { Calls.Add("letter " + l + " " + c); return Next; }
        public HandResult Choose(DialogRecord d, int o, IDictionary<string,string> t) { Calls.Add("choose " + d.Id + " " + o + " " + (t == null ? "-" : string.Join(",", t.Select(x => x.Key + "=" + x.Value)))); return Next; }
        public HandResult Draft(IList<int> pawns, bool d) { Calls.Add("draft " + string.Join(",", pawns) + " " + d); return Next; }
        public HandResult Order(int pawn, string o, int x, int z, int t, bool h) { Calls.Add("order " + pawn + " " + o + " " + x + " " + z + " " + t + " " + h); return Next; }
        // phase 2: the last args and budget seen, for the build tests
        public object LastArgs; public BuildBudget LastLeft; public IList<NarratorMark> LastUndo;
        public JObject BenchesJson = new JObject { ["benches"] = new JArray() };
        HandResult Took(string verb, object a, BuildBudget left) { Calls.Add(verb); LastArgs = a; LastLeft = left; return Next; }
        public HandResult Designate(DesignateArgs a, BuildBudget left) { return Took("designate", a, left); }
        public HandResult Build(BuildArgs a, BuildBudget left) { return Took("build", a, left); }
        public HandResult Room(RoomArgs a, BuildBudget left) { return Took("room", a, left); }
        public HandResult Zone(ZoneArgs a, BuildBudget left) { return Took("zone", a, left); }
        public HandResult Area(AreaArgs a, BuildBudget left) { return Took("area", a, left); }
        public HandResult Bill(BillArgs a, BuildBudget left) { return Took("bill", a, left); }
        public JObject Benches() { Calls.Add("benches"); return BenchesJson; }
        public HandResult UndoNext = HandResult.Done("cancelled it");
        public HandResult Undo(IList<NarratorMark> marks) { Calls.Add("undo " + marks.Count); LastUndo = marks; return UndoNext; }
    }

    public class VivariumTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        IVivariumHands CurrentHands;
        int op;

        public VivariumTests()
        {
            CurrentHands = Hands;
            VivariumRoutes.Register(H.Router, () => V, () => CurrentHands);
        }

        ApiResult Post(string path, object body) { return Send("POST", path, body); }
        ApiResult Send(string method, string path, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "op" + (++op);
            return H.Call(method, path, j.ToString());
        }
        static void BadRequest(ApiResult r, string contains = null)
        {
            Assert.False(r.Ok); Assert.Equal("bad_request", r.Error); Assert.Equal(400, r.Status);
            if (contains != null) Assert.Contains(contains, (string)r.Body["detail"]);
        }

        // ------------------------------------------------------------ status
        [Fact]
        public void StatusShape()
        {
            V.Playing = true; V.Settings.HeartbeatHours = 4.5f;
            V.Chat.Add("player", "hi", 1); V.Goals.AddGoal("x", "long"); V.Actions.Add("work", "n", 1, "narrator");
            var r = H.Call("GET", "/vivarium", null, GameState.Menu);
            Assert.True(r.Ok);
            Assert.True((bool)r.Body["playing"]); Assert.False((bool)r.Body["paused_by_narrator"]);
            Assert.Equal(1, (int)r.Body["chat_seq"]); Assert.Equal(1, (int)r.Body["goals_rev"]); Assert.Equal(1, (int)r.Body["actions_seq"]);
            Assert.True((bool)r.Body["settings"]["may_pause_for_emergencies"]);
            Assert.False((bool)r.Body["settings"]["controls_speed"]);
            Assert.Equal(4.5, (double)r.Body["settings"]["heartbeat_hours"]);
            Assert.True((bool)r.Body["settings"]["commentary"]);
        }

        // ------------------------------------------------------------ chat
        [Fact]
        public void ChatPostAndGetSince()
        {
            var r = Post("/chat", new { who = "narrator", text = "  hello  " });
            Assert.True(r.Ok); Assert.Equal(1, (int)r.Body["seq"]);
            Post("/chat", new { who = "note", text = "-> did a thing" });
            var g = H.Call("GET", "/chat?since=0");
            Assert.Equal(2, g.Body["entries"].Count()); Assert.Equal(2, (int)g.Body["next_seq"]);
            var e = g.Body["entries"][0];
            Assert.Equal("narrator", (string)e["who"]); Assert.Equal("hello", (string)e["text"]);
            Assert.Equal(1000, (int)e["tick"]); Assert.Equal(0, (int)e["to"]); Assert.Equal("", (string)e["name"]);
            var g2 = H.Call("GET", "/chat?since=1");
            Assert.Single(g2.Body["entries"]); Assert.Equal(2, (int)g2.Body["next_seq"]);
            var g3 = H.Call("GET", "/chat?since=2");
            Assert.Empty(g3.Body["entries"]); Assert.Equal(2, (int)g3.Body["next_seq"]);
            Assert.Equal(2, (int)H.Call("GET", "/chat?since=99").Body["next_seq"]);   // cursor past the end is pulled back
            Assert.Equal(2, H.Call("GET", "/chat").Body["entries"].Count());           // no since = all
            BadRequest(H.Call("GET", "/chat?since=abc"));
        }

        [Fact]
        public void ChatPostChecks()
        {
            BadRequest(Post("/chat", new { who = "player", text = "x" }), "who");
            BadRequest(Post("/chat", new { who = "colonist", text = "x" }), "who");
            BadRequest(Post("/chat", new { text = "x" }), "who");
            BadRequest(Post("/chat", new { who = "narrator" }), "text");
            BadRequest(Post("/chat", new { who = "narrator", text = "   " }), "empty");
            BadRequest(Post("/chat", new { who = "narrator", text = new string('a', 501) }), "500");
            Assert.True(Post("/chat", new { who = "narrator", text = new string('a', 500) }).Ok);
            Assert.Equal(1, V.Chat.LastSeq);
        }

        [Fact]
        public void ChatWorksWithoutAGame()
        {
            var r = H.Call("POST", "/chat", "{\"op_id\":\"z\",\"who\":\"note\",\"text\":\"t\"}", GameState.Menu);
            Assert.True(r.Ok);
        }

        [Fact]
        public void ChatCapsAndTrims()
        {
            Assert.Null(V.Chat.Add("player", "   ", 1));
            Assert.Equal(0, V.Chat.LastSeq);
            var e = V.Chat.Add("player", new string('b', 800), 5, 7, "Ana");
            Assert.Equal(500, e.Text.Length); Assert.Equal(7, e.To); Assert.Equal("Ana", e.Name);
            for (int i = 0; i < ChatLog.Max + 100; i++) V.Chat.Add("note", "n" + i, i);
            Assert.Equal(ChatLog.Max, V.Chat.Entries.Count);
            Assert.Equal(ChatLog.Max + 101, V.Chat.LastSeq);
            Assert.Equal(102, V.Chat.Entries[0].Seq);
            Assert.Equal(ChatLog.Max, H.Call("GET", "/chat?since=0").Body["entries"].Count());
        }

        // ------------------------------------------------------------ goals
        [Fact]
        public void GoalsPutAssignsIdsAndBumpsRev()
        {
            Assert.Equal(0, (int)H.Call("GET", "/goals").Body["rev"]);
            var r = Send("PUT", "/goals", new
            {
                rev = 0,
                goals = new object[] { new { text = "Freezer", horizon = "short" }, new { text = "Win", horizon = "long", by = "player" } },
                plan = " Food first ",
                orders = new object[] { new { text = "keep 20 meals" } },
                log_add = new[] { "started", "  ", "second" }
            });
            Assert.True(r.Ok);
            Assert.Equal(1, (int)r.Body["rev"]);
            Assert.Equal("g1", (string)r.Body["goals"][0]["id"]); Assert.Equal("g2", (string)r.Body["goals"][1]["id"]);
            Assert.Equal("narrator", (string)r.Body["goals"][0]["by"]); Assert.Equal("player", (string)r.Body["goals"][1]["by"]);
            Assert.Equal("open", (string)r.Body["goals"][0]["status"]);
            Assert.Equal("Food first", (string)r.Body["plan"]);
            Assert.Equal("o1", (string)r.Body["orders"][0]["id"]); Assert.Equal("narrator", (string)r.Body["orders"][0]["by"]);
            Assert.Equal(2, r.Body["log"].Count()); Assert.Equal(1000, (int)r.Body["log"][0]["tick"]);
            Assert.Equal(1, (int)H.Call("GET", "/vivarium").Body["goals_rev"]);
        }

        [Fact]
        public void GoalsPutKeepsIdsAndByAndPartsNotGiven()
        {
            V.Goals.AddGoal("Player goal", "long");           // g1 by player, rev 1
            V.Goals.AddOrder("player order");                  // o1, rev 2
            var r = Send("PUT", "/goals", new { rev = 2, goals = new object[] { new { id = "g1", text = "Player goal v2", status = "done", by = "narrator" }, new { id = "g99", text = "unknown id" } } });
            Assert.True(r.Ok);
            Assert.Equal("g1", (string)r.Body["goals"][0]["id"]);
            Assert.Equal("player", (string)r.Body["goals"][0]["by"]);        // by kept for existing ids
            Assert.Equal("long", (string)r.Body["goals"][0]["horizon"]);     // not given: kept
            Assert.Equal("done", (string)r.Body["goals"][0]["status"]);
            Assert.Equal("g2", (string)r.Body["goals"][1]["id"]);            // unknown id gets a fresh one
            Assert.Equal("player order", (string)r.Body["orders"][0]["text"]); // orders untouched
            // dropping a goal and adding another never reuses an id
            var r2 = Send("PUT", "/goals", new { rev = 3, goals = new object[] { new { text = "new" } } });
            Assert.Equal("g3", (string)r2.Body["goals"][0]["id"]);
        }

        [Fact]
        public void GoalsStaleRevIs409WithBoard()
        {
            V.Goals.AddGoal("a", "short");
            var r = Send("PUT", "/goals", new { rev = 0, plan = "x" });
            Assert.False(r.Ok); Assert.Equal("conflict", r.Error); Assert.Equal(409, r.Status);
            Assert.Equal(1, (int)r.Body["board"]["rev"]); Assert.Equal("a", (string)r.Body["board"]["goals"][0]["text"]);
            Assert.Equal("", V.Goals.ToJson().Value<string>("plan"));
            BadRequest(Send("PUT", "/goals", new { plan = "x" }), "rev");
            BadRequest(Send("PUT", "/goals", new { rev = "1" }), "rev");
        }

        [Fact]
        public void GoalsPutValidatesAndChangesNothingOnError()
        {
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = "x" }), "goals");
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = new object[] { new { text = "" } } }), "text");
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = new object[] { new { text = new string('a', 301) } } }), "300");
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = new object[] { new { text = "a", horizon = "mid" } } }), "horizon");
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = new object[] { new { text = "a", status = "x" } } }), "status");
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = new object[] { 5 } }), "object");
            BadRequest(Send("PUT", "/goals", new { rev = 0, orders = new object[] { new { text = "" } } }), "text");
            BadRequest(Send("PUT", "/goals", new { rev = 0, plan = new string('p', 301) }), "plan");
            BadRequest(Send("PUT", "/goals", new { rev = 0, log_add = new object[] { 1 } }), "log_add");
            BadRequest(Send("PUT", "/goals", new { rev = 0, goals = Enumerable.Range(0, 21).Select(i => new { text = "g" + i }).ToArray() }), "20");
            BadRequest(Send("PUT", "/goals", new { rev = 0, orders = Enumerable.Range(0, 21).Select(i => new { text = "o" + i }).ToArray() }), "20");
            // one bad part rejects the whole PUT
            BadRequest(Send("PUT", "/goals", new { rev = 0, plan = "ok", goals = new object[] { new { text = "" } } }));
            Assert.Equal(0, V.Goals.Rev); Assert.Equal("", (string)V.Goals.ToJson()["plan"]);
            Assert.True(Send("PUT", "/goals", new { rev = 0, goals = Enumerable.Range(0, 20).Select(i => new { text = "g" + i }).ToArray() }).Ok);
        }

        [Fact]
        public void GoalsLogCapAndTextCap()
        {
            var lines = Enumerable.Range(0, 60).Select(i => "line" + i).ToArray();
            Assert.True(Send("PUT", "/goals", new { rev = 0, log_add = lines }).Ok);
            var b = V.Goals.ToJson();
            Assert.Equal(50, b["log"].Count()); Assert.Equal("line10", (string)b["log"][0]["text"]);
            Assert.True(Send("PUT", "/goals", new { rev = 1, log_add = new[] { new string('z', 400) } }).Ok);
            Assert.Equal(300, ((string)V.Goals.ToJson()["log"].Last["text"]).Length);
        }

        [Fact]
        public void GoalsUiEdits()
        {
            V.Goals.AddGoal("g", "weird");                   // bad horizon becomes short
            Assert.Equal("short", (string)V.Goals.ToJson()["goals"][0]["horizon"]);
            int rev = V.Goals.Rev;
            V.Goals.SetGoalStatus("g1", "done"); Assert.Equal(rev + 1, V.Goals.Rev);
            V.Goals.SetGoalStatus("g1", "done"); Assert.Equal(rev + 1, V.Goals.Rev);   // no change, no bump
            V.Goals.SetGoalStatus("g1", "bogus"); V.Goals.SetGoalStatus("nope", "open");
            Assert.Equal(rev + 1, V.Goals.Rev);
            V.Goals.SetPlan("p"); V.Goals.AddOrder("o"); V.Goals.Log("l", 5);
            Assert.Equal(rev + 4, V.Goals.Rev);
            V.Goals.RemoveGoal("g1"); V.Goals.RemoveOrder("o1");
            Assert.Equal(rev + 6, V.Goals.Rev);
            V.Goals.RemoveGoal("g1");
            Assert.Equal(rev + 6, V.Goals.Rev);
            for (int i = 0; i < 25; i++) { V.Goals.AddGoal("x" + i, "short"); V.Goals.AddOrder("y" + i); }
            Assert.Equal(20, V.Goals.ToJson()["goals"].Count()); Assert.Equal(20, V.Goals.ToJson()["orders"].Count());
            Assert.Equal("player", (string)V.Goals.ToJson()["orders"][0]["by"]);
        }

        // ------------------------------------------------------------ actions
        [Fact]
        public void ActionsRoute()
        {
            Post("/act/research", new { project = "Batteries" });
            Post("/act/bed", new { pawn = 1, bed = 2 });
            var r = H.Call("GET", "/actions?since=0");
            Assert.Equal(2, r.Body["actions"].Count()); Assert.Equal(2, (int)r.Body["next_seq"]);
            var a = r.Body["actions"][0];
            Assert.Equal(1, (int)a["seq"]); Assert.Equal("research", (string)a["verb"]); Assert.Equal("did it", (string)a["note"]);
            Assert.Equal("narrator", (string)a["by"]); Assert.Equal(1000, (int)a["tick"]);
            Assert.Single(H.Call("GET", "/actions?since=1").Body["actions"]);
            BadRequest(H.Call("GET", "/actions?since=x"));
        }

        ChatEntry LastChat() { return V.Chat.Entries.Last(); }

        [Fact]
        public void WhyFlowsToActionLogAndChat()
        {
            Hands.Next = HandResult.Done("started researching Batteries");
            Assert.True(Post("/act/research", new { project = "Batteries", why = "The power is short." }).Ok);
            Assert.Equal("The power is short", (string)H.Call("GET", "/actions?since=0").Body["actions"][0]["why"]);
            Assert.Equal("action", LastChat().Who);
            Assert.Equal("started researching Batteries, since the power is short", LastChat().Text);
            // exported and imported back
            var v2 = new Vivarium(); v2.Actions.Import(V.Actions.Export());
            Assert.Equal("The power is short", (string)v2.Actions.Export()[0]["why"]);
        }

        [Fact]
        public void ActWithoutWhyOmitsIt()
        {
            Post("/act/research", new { project = "Batteries" });
            Assert.Null(H.Call("GET", "/actions?since=0").Body["actions"][0]["why"]);
            Assert.Equal("did it", LastChat().Text);
        }

        [Fact]
        public void WhyTooLongIsTruncatedAndNonStringIs400()
        {
            string why = "because " + string.Join(" ", Enumerable.Repeat("something", 30));
            Assert.True(Post("/act/research", new { project = "Batteries", why }).Ok);
            Assert.True(((string)H.Call("GET", "/actions?since=0").Body["actions"][0]["why"]).Length <= 80);
            int chats = V.Chat.Entries.Count, acts = V.Actions.LastSeq;
            BadRequest(Post("/act/research", new { project = "Batteries", why = 5 }), "why");
            BadRequest(Post("/act/research", new { project = "Batteries", why = new[] { "x" } }), "why");
            Assert.Equal(chats, V.Chat.Entries.Count); Assert.Equal(acts, V.Actions.LastSeq);
        }

        [Fact]
        public void GateRefusalWritesRefusedLine()
        {
            var r = Post("/act/time", new { speed = 2, why = "it drags" });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error);
            Assert.Equal("refused", LastChat().Who);
            Assert.Equal("couldn't change the game speed: the player has not let the narrator change speed", LastChat().Text);
            Assert.Empty(V.Actions.Export());
        }

        [Fact]
        public void HandRefusalWritesRefusedLineWithHandsWhatOrFallback()
        {
            Hands.Next = HandResult.No("Ana is incapable of Cooking", "set Ana's Cooking");
            Assert.False(Post("/act/work", new { pawn = 7, priorities = new { Cooking = 1 } }).Ok);
            Assert.Equal("couldn't set Ana's Cooking: Ana is incapable of Cooking", LastChat().Text);
            Hands.Next = HandResult.No("no such bed");
            Post("/act/bed", new { pawn = 7, bed = 9 });
            Assert.Equal("couldn't give pawn 7 bed 9: no such bed", LastChat().Text);
            Assert.Empty(V.Actions.Export());
        }

        [Fact]
        public void BadRequestAndNoGameWriteNothing()
        {
            BadRequest(Post("/act/bed", new { pawn = 0, bed = 2 }));
            Assert.Equal("no_game", H.Call("POST", "/act/bed", "{\"op_id\":\"q2\",\"pawn\":1,\"bed\":2}", GameState.Menu).Error);
            Assert.Empty(V.Chat.Entries);
        }

        [Fact]
        public void ActionLogCap()
        {
            for (int i = 0; i < 520; i++) V.Actions.Add("v", "n" + i, i, "narrator");
            var j = V.Actions.Since(0);
            Assert.Equal(500, j["actions"].Count()); Assert.Equal(21, (int)j["actions"][0]["seq"]); Assert.Equal(520, (int)j["next_seq"]);
        }

        // ------------------------------------------------------------ digest, no_game
        [Fact]
        public void DigestPassthrough()
        {
            var r = H.Call("GET", "/digest");
            Assert.True(r.Ok); Assert.Equal("Rain", (string)r.Body["weather"]); Assert.Equal("Ferngrove", (string)r.Body["map"]);
        }

        [Fact]
        public void NoGameWhenHandsNullOrNoGameLoaded()
        {
            CurrentHands = null;
            var r = H.Call("GET", "/digest");
            Assert.Equal("no_game", r.Error); Assert.Equal(503, r.Status);
            Assert.Equal("no_game", Post("/act/research", new { project = "x" }).Error);
            Assert.True(H.Call("GET", "/vivarium").Ok);       // non-game routes still work
            Assert.True(Post("/chat", new { who = "note", text = "x" }).Ok);
            CurrentHands = Hands;
            Assert.Equal("no_game", H.Call("GET", "/digest", null, GameState.Menu).Error);
            Assert.Equal("no_game", H.Call("POST", "/act/bed", "{\"op_id\":\"q\",\"pawn\":1,\"bed\":2}", GameState.Menu).Error);
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void BadArgsNeverReachTheHandsAndNeverLog()
        {
            BadRequest(Post("/act/bed", new { pawn = 0, bed = 2 }));
            Assert.Empty(Hands.Calls); Assert.Equal(0, V.Actions.LastSeq);
        }

        // ------------------------------------------------------------ act routes
        [Fact]
        public void WorkRoute()
        {
            var r = Post("/act/work", new { pawn = 101, priorities = new { Cooking = 1, Hauling = 0 } });
            Assert.True(r.Ok); Assert.Equal("did it", (string)r.Body["done"]);
            Assert.Equal("work 101 Cooking=1,Hauling=0", Hands.Calls.Single());
            BadRequest(Post("/act/work", new { priorities = new { Cooking = 1 } }), "pawn");
            BadRequest(Post("/act/work", new { pawn = "a", priorities = new { Cooking = 1 } }), "pawn");
            BadRequest(Post("/act/work", new { pawn = 1 }), "priorities");
            BadRequest(Post("/act/work", new { pawn = 1, priorities = new { } }), "1..20");
            BadRequest(Post("/act/work", new { pawn = 1, priorities = new { Cooking = 5 } }), "0..4");
            BadRequest(Post("/act/work", new { pawn = 1, priorities = new { Cooking = -1 } }), "0..4");
            BadRequest(Post("/act/work", new { pawn = 1, priorities = new { Cooking = "1" } }), "0..4");
            BadRequest(Post("/act/work", new { pawn = 1, priorities = new { Cooking = 1.5 } }), "0..4");
            var many = new JObject(); for (int i = 0; i < 21; i++) many["T" + i] = 1;
            BadRequest(Post("/act/work", new { pawn = 1, priorities = many }), "1..20");
            Assert.Single(Hands.Calls);
        }

        [Fact]
        public void ScheduleRoute()
        {
            Assert.True(Post("/act/schedule", new { pawn = 1, from_hour = 22, to_hour = 6, assignment = "Sleep" }).Ok);
            Assert.True(Post("/act/schedule", new { pawn = 1, from_hour = 0, to_hour = 24, assignment = "work" }).Ok);
            Assert.Equal("schedule 1 22 6 sleep", Hands.Calls[0]);
            BadRequest(Post("/act/schedule", new { pawn = 1, from_hour = 24, to_hour = 6, assignment = "sleep" }), "from_hour");
            BadRequest(Post("/act/schedule", new { pawn = 1, from_hour = -1, to_hour = 6, assignment = "sleep" }), "from_hour");
            BadRequest(Post("/act/schedule", new { pawn = 1, from_hour = 1, to_hour = 25, assignment = "sleep" }), "to_hour");
            BadRequest(Post("/act/schedule", new { pawn = 1, from_hour = 5, to_hour = 5, assignment = "sleep" }), "differ");
            BadRequest(Post("/act/schedule", new { pawn = 1, from_hour = 1, to_hour = 5, assignment = "nap" }), "assignment");
            BadRequest(Post("/act/schedule", new { pawn = 1, from_hour = 1, to_hour = 5 }), "assignment");
            BadRequest(Post("/act/schedule", new { pawn = 1, to_hour = 5, assignment = "joy" }), "from_hour");
            foreach (var a in new[] { "anything", "work", "joy", "sleep", "meditate" })
                Assert.True(Post("/act/schedule", new { pawn = 1, from_hour = 1, to_hour = 2, assignment = a }).Ok);
        }

        [Fact]
        public void PolicyRoute()
        {
            Assert.True(Post("/act/policy", new { pawn = 1, kind = "food", name = " Fine " }).Ok);
            Assert.Equal("policy 1 food Fine", Hands.Calls.Single());
            BadRequest(Post("/act/policy", new { pawn = 1, kind = "hat", name = "x" }), "kind");
            BadRequest(Post("/act/policy", new { pawn = 1, kind = "drug", name = "" }), "name");
            BadRequest(Post("/act/policy", new { pawn = 1, kind = "drug", name = new string('n', 61) }), "name");
            BadRequest(Post("/act/policy", new { pawn = 1, name = "x" }), "kind");
        }

        [Fact]
        public void BedResearchLetterRoutes()
        {
            Assert.True(Post("/act/bed", new { pawn = 1, bed = 3301 }).Ok);
            Assert.True(Post("/act/research", new { project = "Batteries" }).Ok);
            Assert.True(Post("/act/letter", new { letter = 77, choice = "dismiss" }).Ok);
            Assert.Equal(new[] { "bed 1 3301", "research Batteries", "letter 77 dismiss" }, Hands.Calls);
            BadRequest(Post("/act/bed", new { pawn = 1 }), "bed");
            BadRequest(Post("/act/bed", new { pawn = 1, bed = -3 }), "bed");
            BadRequest(Post("/act/research", new { project = "" }), "project");
            BadRequest(Post("/act/research", new { project = new string('p', 81) }), "project");
            BadRequest(Post("/act/research", new { }), "project");
            BadRequest(Post("/act/letter", new { choice = "x" }), "letter");
            BadRequest(Post("/act/letter", new { letter = 5, choice = "" }), "choice");
            BadRequest(Post("/act/letter", new { letter = 5, choice = new string('c', 81) }), "choice");
        }

        [Fact]
        public void DraftRoute()
        {
            Assert.True(Post("/act/draft", new { pawns = new[] { 1, 2 }, drafted = true }).Ok);
            Assert.Equal("draft 1,2 True", Hands.Calls.Single());
            BadRequest(Post("/act/draft", new { pawns = new int[0], drafted = true }), "1..30");
            BadRequest(Post("/act/draft", new { pawns = Enumerable.Range(1, 31).ToArray(), drafted = true }), "1..30");
            BadRequest(Post("/act/draft", new { pawns = new[] { 1, 0 }, drafted = true }), "positive");
            BadRequest(Post("/act/draft", new { pawns = new object[] { "a" }, drafted = true }), "positive");
            BadRequest(Post("/act/draft", new { pawns = 5, drafted = true }), "pawns");
            BadRequest(Post("/act/draft", new { pawns = new[] { 1 } }), "drafted");
            BadRequest(Post("/act/draft", new { pawns = new[] { 1 }, drafted = "yes" }), "drafted");
            Assert.True(Post("/act/draft", new { pawns = Enumerable.Range(1, 30).ToArray(), drafted = false }).Ok);
        }

        [Fact]
        public void OrderRoute()
        {
            Assert.True(Post("/act/order", new { pawn = 1, order = "move", x = 40, z = 61 }).Ok);
            Assert.True(Post("/act/order", new { pawn = 1, order = "attack", target = 5512 }).Ok);
            Assert.True(Post("/act/order", new { pawn = 1, order = "hold", hold = true }).Ok);
            Assert.True(Post("/act/order", new { pawn = 1, order = "move", x = 0, z = 0 }).Ok);
            Assert.Equal(new[] { "order 1 move 40 61 0 False", "order 1 attack 0 0 5512 False", "order 1 hold 0 0 0 True", "order 1 move 0 0 0 False" }, Hands.Calls);
            BadRequest(Post("/act/order", new { pawn = 1, order = "dance" }), "order");
            BadRequest(Post("/act/order", new { pawn = 1 }), "order");
            BadRequest(Post("/act/order", new { pawn = 1, order = "move", x = 4 }), "z");
            BadRequest(Post("/act/order", new { pawn = 1, order = "move", x = -1, z = 4 }), "x");
            BadRequest(Post("/act/order", new { pawn = 1, order = "attack" }), "target");
            BadRequest(Post("/act/order", new { pawn = 1, order = "attack", target = 0 }), "target");
            BadRequest(Post("/act/order", new { pawn = 1, order = "hold" }), "hold");
            BadRequest(Post("/act/order", new { order = "hold", hold = true }), "pawn");
        }

        [Fact]
        public void RefusedPassthroughAndNoLog()
        {
            Hands.Next = HandResult.No("Ana is incapable of Cooking");
            var r = Post("/act/work", new { pawn = 1, priorities = new { Cooking = 1 } });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Equal("Ana is incapable of Cooking", (string)r.Body["detail"]);
            Assert.Equal(0, V.Actions.LastSeq);
            // a refusal is not remembered by the op_id store, so a retry reaches the hands again
            Hands.Next = HandResult.Done("ok");
            Assert.True(Post("/act/work", new { pawn = 1, priorities = new { Cooking = 1 } }).Ok);
        }

        [Fact]
        public void SuccessShapeLogAndExtra()
        {
            Hands.Next = HandResult.Done("set Cooking 1 for Ana", new JObject { ["pawn"] = "Ana", ["done"] = "ignored" });
            var r = Post("/act/work", new { pawn = 1, priorities = new { Cooking = 1 } });
            Assert.True(r.Ok);
            Assert.Equal("set Cooking 1 for Ana", (string)r.Body["done"]); Assert.Equal("Ana", (string)r.Body["pawn"]);
            var a = V.Actions.Since(0)["actions"][0];
            Assert.Equal("work", (string)a["verb"]); Assert.Equal("set Cooking 1 for Ana", (string)a["note"]); Assert.Equal("narrator", (string)a["by"]);
            Assert.Equal(1, V.Chat.LastSeq); Assert.Equal("action", V.Chat.Entries[0].Who);   // exactly one greentext line
        }

        [Fact]
        public void DuplicateOpIdDoesNotActTwice()
        {
            var body = "{\"op_id\":\"same\",\"project\":\"Batteries\"}";
            Assert.True(H.Call("POST", "/act/research", body).Ok);
            Assert.Equal("duplicate", H.Call("POST", "/act/research", body).Error);
            Assert.Single(Hands.Calls); Assert.Equal(1, V.Actions.LastSeq);
        }

        [Fact]
        public void HandExceptionBecomesInternal()
        {
            CurrentHands = new Throwing();
            var r = Post("/act/research", new { project = "x" });
            Assert.Equal("internal", r.Error);
            Assert.Equal(0, V.Actions.LastSeq);
        }
        sealed class Throwing : IVivariumHands
        {
            public JObject Digest() { throw new InvalidOperationException("x"); }
            public HandResult SetTime(bool? p, int? s) { throw new InvalidOperationException("boom"); }
            public HandResult SetWork(int p, IDictionary<string, int> d) { throw new InvalidOperationException("boom"); }
            public HandResult SetSchedule(int p, int f, int t, string a) { throw new InvalidOperationException("boom"); }
            public HandResult SetPolicy(int p, string k, string n) { throw new InvalidOperationException("boom"); }
            public HandResult AssignBed(int p, int b) { throw new InvalidOperationException("boom"); }
            public HandResult SetResearch(string p) { throw new InvalidOperationException("boom"); }
            public HandResult AnswerLetter(int l, string c) { throw new InvalidOperationException("boom"); }
            public HandResult Draft(IList<int> p, bool d) { throw new InvalidOperationException("boom"); }
            public HandResult Choose(DialogRecord d, int o, IDictionary<string, string> t) { throw new InvalidOperationException("boom"); }
            public HandResult Order(int p, string o, int x, int z, int t, bool h) { throw new InvalidOperationException("boom"); }
            public HandResult Designate(DesignateArgs a, BuildBudget l) { throw new InvalidOperationException("boom"); }
            public HandResult Build(BuildArgs a, BuildBudget l) { throw new InvalidOperationException("boom"); }
            public HandResult Room(RoomArgs a, BuildBudget l) { throw new InvalidOperationException("boom"); }
            public HandResult Zone(ZoneArgs a, BuildBudget l) { throw new InvalidOperationException("boom"); }
            public HandResult Area(AreaArgs a, BuildBudget l) { throw new InvalidOperationException("boom"); }
            public HandResult Bill(BillArgs a, BuildBudget l) { throw new InvalidOperationException("boom"); }
            public JObject Benches() { throw new InvalidOperationException("boom"); }
            public HandResult Undo(IList<NarratorMark> m) { throw new InvalidOperationException("boom"); }
        }

        // ------------------------------------------------------------ time gate
        // pause/speed given, controls_speed, may_pause, emergency, paused_by_narrator -> allowed?
        [Theory]
        // speed
        [InlineData(null, 2, false, true, false, false, false)]
        [InlineData(null, 2, true, true, false, false, true)]
        [InlineData(null, 4, true, false, false, false, true)]
        // pause true
        [InlineData(true, null, false, true, false, false, false)]   // no emergency flag
        [InlineData(true, null, false, true, true, false, true)]     // emergency + may
        [InlineData(true, null, false, false, true, false, false)]   // emergency but not allowed
        [InlineData(true, null, true, false, false, false, true)]    // controls speed
        // pause false
        [InlineData(false, null, false, true, false, false, false)]
        [InlineData(false, null, false, true, false, true, true)]    // its own pause
        [InlineData(false, null, true, false, false, false, true)]
        // both
        [InlineData(true, 3, false, true, true, false, false)]       // speed still needs the setting
        [InlineData(true, 3, true, false, false, false, true)]
        public void TimeGate(bool? pause, int? speed, bool controls, bool may, bool emergency, bool pausedByNarrator, bool allowed)
        {
            V.Settings.ControlsSpeed = controls; V.Settings.MayPauseForEmergencies = may; V.PausedByNarrator = pausedByNarrator;
            var body = new JObject { ["emergency"] = emergency };
            if (pause != null) body["pause"] = pause; if (speed != null) body["speed"] = speed;
            var r = Post("/act/time", body);
            if (allowed)
            {
                Assert.True(r.Ok); Assert.Single(Hands.Calls);
                Assert.Equal("time " + (pause?.ToString() ?? "-") + " " + (speed?.ToString() ?? "-"), Hands.Calls[0]);
                Assert.Equal(1, V.Actions.LastSeq);
            }
            else
            {
                Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
                Assert.NotEmpty((string)r.Body["detail"]);
                Assert.Empty(Hands.Calls); Assert.Equal(0, V.Actions.LastSeq);
                Assert.Equal(pausedByNarrator, V.PausedByNarrator);   // refusal changes nothing
            }
        }

        [Fact]
        public void TimeRefusalReasonsAreInPlainWords()
        {
            V.Settings.ControlsSpeed = false;
            Assert.Equal("the player has not let the narrator change speed", (string)Post("/act/time", new { speed = 3 }).Body["detail"]);
            V.Settings.MayPauseForEmergencies = false;
            Assert.Equal("the player has not let the narrator pause for emergencies", (string)Post("/act/time", new { pause = true, emergency = true }).Body["detail"]);
        }

        [Fact]
        public void PausedByNarratorFlagLifecycle()
        {
            Assert.True(Post("/act/time", new { pause = true, emergency = true }).Ok);
            Assert.True(V.PausedByNarrator);
            Assert.True((bool)H.Call("GET", "/vivarium").Body["paused_by_narrator"]);
            Assert.True(Post("/act/time", new { pause = false }).Ok);
            Assert.False(V.PausedByNarrator);
            Assert.Equal("refused", Post("/act/time", new { pause = false }).Error);   // no longer its pause
            // a hand refusal on pause does not set the flag
            Hands.Next = HandResult.No("already paused");
            Assert.Equal("refused", Post("/act/time", new { pause = true, emergency = true }).Error);
            Assert.False(V.PausedByNarrator);
            // a hand refusal on unpause keeps the flag
            V.PausedByNarrator = true;
            Assert.Equal("refused", Post("/act/time", new { pause = false }).Error);
            Assert.True(V.PausedByNarrator);
        }

        [Fact]
        public void TimeArgChecks()
        {
            BadRequest(Post("/act/time", new { }), "pause");
            BadRequest(Post("/act/time", new { emergency = true }), "pause");
            BadRequest(Post("/act/time", new { speed = 0 }), "speed");
            BadRequest(Post("/act/time", new { speed = 5 }), "speed");
            BadRequest(Post("/act/time", new { speed = "fast" }), "speed");
            BadRequest(Post("/act/time", new { pause = "yes" }), "pause");
            BadRequest(Post("/act/time", new { pause = true, emergency = "x" }), "emergency");
            Assert.Empty(Hands.Calls);
        }

        // ------------------------------------------------------------ save / load
        [Fact]
        public void ExportImportRoundTrip()
        {
            V.Playing = true; V.PausedByNarrator = true;
            V.Settings.ControlsSpeed = true;
            V.Chat.Add("player", "one", 10); V.Chat.Add("colonist", "two", 11, 5, "Ana"); V.Chat.Add("narrator", "three", 12);
            V.Goals.AddGoal("a", "long"); V.Goals.AddGoal("b", "short"); V.Goals.AddOrder("o"); V.Goals.SetPlan("plan"); V.Goals.Log("did", 9);
            V.Goals.RemoveGoal("g2"); V.Goals.RemoveOrder("o1"); V.Goals.AddOrder("again");   // counters ahead of what's on the board
            V.Actions.Add("work", "n1", 3, "narrator"); V.Actions.Add("time", "n2", 4, "narrator");
            var saved = V.Export();
            var text = saved.ToString();
            Assert.False(saved.ContainsKey("settings"));

            var w = new Vivarium();
            w.Settings.ControlsSpeed = false;
            w.Import(JObject.Parse(text));
            Assert.True(w.Playing); Assert.True(w.PausedByNarrator);
            Assert.False(w.Settings.ControlsSpeed);   // settings are not saved here
            Assert.Equal(V.Export().ToString(), w.Export().ToString());
            Assert.Equal(V.StatusJson()["goals_rev"], w.StatusJson()["goals_rev"]);
            Assert.Equal(3, w.Chat.LastSeq); Assert.Equal("Ana", w.Chat.Entries[1].Name); Assert.Equal(5, w.Chat.Entries[1].To);
            // counters survive
            Assert.Equal(4, w.Chat.Add("note", "x", 1).Seq);
            Assert.Equal(2, w.Actions.LastSeq);
            w.Goals.AddGoal("c", "short"); w.Goals.AddOrder("d");
            var b = w.Goals.ToJson();
            Assert.Equal("g3", (string)b["goals"].Last["id"]); Assert.Equal("o3", (string)b["orders"].Last["id"]);
        }

        [Fact]
        public void SeqSurvivesEmptyLogs()
        {
            for (int i = 0; i < 5; i++) { V.Chat.Add("note", "x", 1); V.Actions.Add("v", "n", 1, "narrator"); }
            var saved = V.Export();
            saved["chat"] = new JArray(); saved["actions"] = new JArray();
            var w = new Vivarium(); w.Import(saved);
            Assert.Equal(6, w.Chat.Add("note", "y", 1).Seq);
            Assert.Equal(5, w.Actions.LastSeq);
        }

        [Fact]
        public void ImportToleratesMissingAndGarbage()
        {
            var w = new Vivarium();
            w.Import(null);
            w.Import(new JObject());
            Assert.False(w.Playing); Assert.Equal(0, w.Chat.LastSeq); Assert.Equal(0, w.Goals.Rev);

            w.Import(JObject.Parse(@"{""playing"":""yes"",""paused_by_narrator"":5,""chat"":""no"",""goals"":[1],""actions"":{""a"":1}}"));
            Assert.False(w.Playing); Assert.False(w.PausedByNarrator); Assert.Empty(w.Chat.Entries); Assert.Equal(0, w.Goals.Rev);

            w.Import(JObject.Parse(@"{""playing"":true,
              ""chat"":[1,null,{""seq"":""x"",""text"":""kept"",""who"":7,""tick"":""t""},{""text"":""""},{""seq"":9,""text"":""nine""},{""seq"":2,""text"":""late""}],
              ""goals"":{""rev"":-4,""plan"":3,""goals"":[{""text"":""a"",""id"":""zzz"",""horizon"":""huh"",""status"":9,""by"":""bob""},{""id"":""g5""},null,{""text"":""b"",""id"":""g1""},{""text"":""c"",""id"":""g1""}],
                 ""orders"":[{""text"":""o"",""id"":""o7""}],""log"":[{""tick"":""x"",""text"":""l""},5]},
              ""actions"":[{""seq"":4,""verb"":""work""},{""note"":""n"",""seq"":1},7,{}]}"));
            Assert.True(w.Playing);
            Assert.Equal(new[] { 1, 9, 10 }, w.Chat.Entries.Select(e => e.Seq).ToArray());   // seqs forced increasing
            Assert.Equal("note", w.Chat.Entries[0].Who);
            var g = w.Goals.ToJson();
            Assert.Equal(0, (int)g["rev"]); Assert.Equal("", (string)g["plan"]);
            Assert.Equal(3, g["goals"].Count());
            Assert.Equal("short", (string)g["goals"][0]["horizon"]); Assert.Equal("open", (string)g["goals"][0]["status"]); Assert.Equal("narrator", (string)g["goals"][0]["by"]);
            Assert.Equal(3, g["goals"].Select(x => (string)x["id"]).Distinct().Count());    // unique ids
            Assert.Single(g["log"]);
            w.Goals.AddGoal("new", "short"); w.Goals.AddOrder("new");
            var after = w.Goals.ToJson();
            Assert.Equal(4, after["goals"].Count());
            Assert.Equal(4, after["goals"].Select(x => (string)x["id"]).Distinct().Count());
            Assert.Equal("o8", (string)after["orders"].Last["id"]);
            Assert.Equal(2, w.Actions.Since(0)["actions"].Count());
        }

        [Fact]
        public void ClearResetsEverything()
        {
            V.Playing = true; V.Chat.Add("note", "x", 1); V.Goals.AddGoal("g", "short"); V.Actions.Add("v", "n", 1, "narrator");
            V.Clear();
            Assert.False(V.Playing); Assert.Equal(0, V.Chat.LastSeq); Assert.Equal(0, V.Goals.Rev); Assert.Equal(0, V.Actions.LastSeq);
            Assert.Equal(0, V.Chat.Since(0)["next_seq"].Value<int>());
        }

        [Fact]
        public void BridgeRuntimeHoldsOneVivarium()
        {
            var rt = new BridgeRuntime(new NullGame(), () => 0);
            Assert.NotNull(rt.Vivarium);
        }
        sealed class NullGame : IGameAdapter
        {
            public GameState BuildState() { return GameState.Menu; }
            public void Info(string m) { } public void Warn(string m) { } public void Error(string m) { }
        }
    }
}
