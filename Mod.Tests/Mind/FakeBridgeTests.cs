using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    // Self-tests of the FakeBridge port (games/rimworld/fakes.py had no tests of its own; the rest of the suite leaned on it).
    public class FakeBridgeTests
    {
        static (FakeBridge, BridgeClient) Mk()
        {
            var fb = new FakeBridge("tok");
            fb.AddPawn(1, "Ana"); fb.AddPawn(2, "Bram");
            return (fb, FakeBridge.Client(fb));
        }

        static async Task<BridgeError> Fails(Task t) { return await Assert.ThrowsAsync<BridgeError>(async () => await t); }

        [Fact]
        public void DefaultAndRichPawnShapes()
        {
            var p = FakeBridge.DefaultPawn(5, "Eve", JObject.Parse("{\"age\": 41, \"job\": \"Hauling\"}"));
            Assert.True((int)p["id"] == 5 && (string)p["name"]["first"] == "Eve" && (string)p["name"]["last"] == "Tester");
            Assert.True((int)p["age"] == 41 && (string)p["job"] == "Hauling" && (string)p["status"] == "colonist");
            Assert.Equal("id", ((JProperty)p.First).Name);
            var r = FakeBridge.RichPawn();
            Assert.True((int)r["id"] == 7 && (string)r["name"]["first"] == "Ada" && (string)r["name"]["nick"] == "Wren");
            Assert.Equal(2, r["traits"].Count());
            Assert.True((string)r["royal_title"] == "Knight" && (double)r["ideo"]["certainty"] == 0.8);
            Assert.Equal(2, r["relations"].Count());
            Assert.Equal(FakeBridge.RichPawn(9, "Zed", JObject.Parse("{\"age\": 20}"))["age"], (JToken)20);
        }

        [Fact]
        public async Task ColonyPawnSceneAndConfig()
        {
            var (fb, b) = Mk();
            var col = await b.RosterAsync();
            Assert.Equal(new[] { 1, 2 }, col["colonists"].Select(c => (int)c["id"]));
            Assert.True((string)col["season"] == "spring" && col["maps"].Count() == 1);
            Assert.Equal("Ana", (string)(await b.ActorAsync(1))["name"]["first"]);
            var sc = await b.SceneAsync(1);
            Assert.True((string)sc["room"]["role"] == "Kitchen" && (int)sc["present"][0]["pawn"] == 2 && (bool)sc["present"][0]["can_hear"]);
            fb.Scenes[1] = JObject.Parse("{\"room\": {\"role\": \"Hall\"}}");
            Assert.Equal("Hall", (string)(await b.SceneAsync(1))["room"]["role"]);
            Assert.Equal("normal", (string)(await b.ConfigAsync())["effects"]);
            Assert.Contains(new System.Collections.Generic.KeyValuePair<string, string>("GET", "/colony"), fb.Requests);
        }

        [Fact]
        public async Task ErrorShapeCarriesEnvelopeAfterOkAndDetail()
        {
            var (fb, b) = Mk();
            var e = await Fails(b.ActorAsync(99));
            Assert.Equal(new[] { "ok", "error", "detail", "tick", "save_epoch", "paused", "speed" }, e.Body.Properties().Select(p => p.Name));
            Assert.Equal("pawn 99", e.Detail);
            Assert.Equal("not_found", (await Fails(b.ActAsync("nope", new JObject()))).Code);
        }

        [Fact]
        public async Task TokenInQueryIsAcceptedForGetOnly()
        {
            var (fb, _) = Mk();
            var ok = await fb.SendAsync(new BridgeRequest { Path = "/config", Query = { new System.Collections.Generic.KeyValuePair<string, string>("token", "tok") } });
            Assert.Equal(200, ok.Status);
            var bad = await fb.SendAsync(new BridgeRequest { Method = "POST", Path = "/thought", JsonBody = "{}", Query = { new System.Collections.Generic.KeyValuePair<string, string>("token", "tok") } });
            Assert.Equal(401, bad.Status);
        }

        [Fact]
        public async Task WriteValidation()
        {
            var (fb, b) = Mk();
            var r = await fb.SendAsync(new BridgeRequest { Method = "POST", Path = "/thought", Authorization = "Bearer tok", JsonBody = "not json" });
            Assert.Equal(400, r.Status);
            r = await fb.SendAsync(new BridgeRequest { Method = "POST", Path = "/thought", Authorization = "Bearer tok", JsonBody = "{\"pawn\": 1}" });
            Assert.Equal("op_id required", (string)JObject.Parse(r.Body)["detail"]);
            Assert.Equal("bad_request", (await Fails(b.ThoughtAsync(1, "x", kind: "poem"))).Code);
            await b.ThoughtAsync(1, "x", kind: "dream");
            Assert.Single(fb.ThoughtLog);
        }

        [Fact]
        public async Task SayEffectsPairCapAndVanillaRef()
        {
            var (fb, b) = Mk();
            fb.PairCap = 2;
            for (int i = 0; i < 2; i++) Assert.Equal("kind", (string)(await b.SayAsync(1, "x", listener: 2, intent: "kind"))["applied"]["effect"]);
            Assert.Equal("pair_cap", (string)(await b.SayAsync(1, "x", listener: 2, intent: "kind"))["refused"]["effect"]);
            Assert.Equal("pair_cap", (string)(await b.SayAsync(2, "x", listener: 1, intent: "kind"))["refused"]["effect"]);   // the pair key is unordered
            var v = await b.SayAsync(1, "x", listener: 2, intent: "kind", vanillaRef: "Chitchat");
            Assert.True((string)v["applied"]["attached"] == "attached" && v["applied"]["effect"].Type == JTokenType.Null);
            Assert.Equal("conflict", (await Fails(b.SayAsync(1, "x", listener: 9))).Code);
            Assert.Equal("conflict", (await Fails(b.SayAsync(9, "x"))).Code);
            fb.TalkQueue.Add(JObject.Parse("{\"id\": \"t1\"}")); fb.TalkQueue.Add(JObject.Parse("{\"id\": \"t2\"}"));
            await b.SayAsync(1, "x", talkId: "t1");
            Assert.Equal(new[] { "t2" }, fb.TalkQueue.Select(t => (string)t["id"]));
        }

        [Fact]
        public async Task SocialActionAndItsUnsupportedFlag()
        {
            var (fb, b) = Mk();
            Assert.True((bool)(await b.SocialActionAsync(1, "avoid", 2))["applied"]);
            Assert.Equal("bad_request", (await Fails(b.SocialActionAsync(1, "hug", 2))).Code);
            Assert.Equal("bad_request", (await Fails(b.SocialActionAsync(1, "avoid", 9))).Code);
            fb.SocialSupported = false;
            Assert.Equal("not_found", (await Fails(b.SocialActionAsync(1, "avoid", 2))).Code);
        }

        [Fact]
        public async Task ChronicleRecapArtAndTheirSwitches()
        {
            var (fb, b) = Mk();
            Assert.Equal(1, (int)(await b.ChronicleAsync(2, "  Day two.  "))["count"]);
            Assert.Equal("Day two.", (string)(await b.GetChronicleAsync())[0]["text"]);
            Assert.Equal("bad_request", (await Fails(b.ChronicleAsync(2, "x", kind: "epic"))).Code);
            Assert.Equal("bad_request", (await Fails(b.ChronicleAsync(2, "   "))).Code);
            for (int i = 0; i < 130; i++) await b.ChronicleAsync(i, "e" + i);
            Assert.Equal(120, fb.Chronicle.Count);
            fb.ChronicleSupported = false;
            Assert.Equal("not_found", (await Fails(b.GetChronicleAsync())).Code);
            Assert.True((bool)(await b.RecapAsync(1, new[] { "a", "b", "c" }))["shown"]);
            Assert.Equal("bad_request", (await Fails(b.RecapAsync(1, new string[0]))).Code);
            Assert.Equal("bad_request", (await Fails(b.RecapAsync(1, new[] { "a", "b", "c", "d" }))).Code);
            fb.RecapRefuse = "conflict";
            Assert.Equal(409, (await Fails(b.RecapAsync(1, new[] { "a" }))).Status);
            fb.RecapRefuse = "expired";
            Assert.Equal(410, (await Fails(b.RecapAsync(1, new[] { "a" }))).Status);
            Assert.Equal(1, (int)(await b.ArtAsync(500, "A fine statue"))["count"]);
            Assert.Equal("conflict", (await Fails(b.ArtAsync(501, "x"))).Code);
            Assert.Equal("bad_request", (await Fails(b.ArtAsync(500, " "))).Code);
            fb.ArtSupported = false;
            Assert.Equal("not_found", (await Fails(b.ArtAsync(500, "x"))).Code);
        }

        [Fact]
        public async Task MindRevsAndSizeCap()
        {
            var (fb, b) = Mk();
            Assert.Equal(1, await b.PutMindAsync(1, new JObject { ["a"] = 1 }, 0));
            Assert.Equal(2, await b.PutMindAsync(1, new JObject { ["a"] = 2 }, 1));
            var e = await Fails(b.PutMindAsync(1, new JObject(), 0));
            Assert.True(e.Code == "conflict" && (int)e.Body["rev"] == 2);
            var big = await Fails(b.PutMindAsync(1, new JObject { ["t"] = new string('x', 33 * 1024) }, 2));
            Assert.Equal(413, big.Status);
            Assert.Equal(2, (await b.GetMindAsync(1)).Value);
        }

        [Fact]
        public async Task EventGapAndPaging()
        {
            var (fb, b) = Mk();
            var who = new[] { new System.Collections.Generic.KeyValuePair<int, string>(1, "victim") };
            for (int i = 0; i < 250; i++) fb.AddEvent("injury", who, "e", new[] { 1 }, 0.9, "L1", new JObject { ["k"] = 1 });
            var page = await b.EventsAsync(0);
            Assert.Equal(200, page["events"].Count());
            Assert.True((int)page["next_seq"] == 200 && page["gap"] == null);
            Assert.Equal("L1", (string)page["events"][0]["ledger_id"]);
            Assert.Equal("victim", (string)page["events"][0]["pawns"][0]["role"]);
            Assert.Equal(250, (int)(await b.EventsAsync(250))["next_seq"]);   // nothing new: next_seq stays at the newest
            fb.MaxKept = 100; fb.AddEvent("injury", who, "e", new[] { 1 });
            Assert.Equal(100, fb.Events.Count);
            Assert.True((bool)(await b.EventsAsync(0))["gap"]);
        }

        [Fact]
        public async Task VivariumReadsChatAndDigest()
        {
            var (fb, b) = Mk();
            fb.Playing = true;
            var v = await b.VivariumAsync();
            Assert.True((bool)v["playing"] && (int)v["chat_seq"] == 0 && (int)v["goals_rev"] == 0 && (bool)v["settings"]["may_pause_for_emergencies"]);
            Assert.Equal("no_game", (await Fails(b.DigestAsync())).Code);
            fb.Digest = JObject.Parse("{\"colonists\": 3}");
            Assert.Equal(3, (int)(await b.DigestAsync())["colonists"]);
            fb.AddChat("player", "hello", 1, "Ana");
            Assert.Equal(2, (int)(await b.PostChatAsync("narrator", "Hi."))["seq"]);
            var c = await b.ChatSinceAsync(1);
            Assert.True(c["entries"].Count() == 1 && (int)c["next_seq"] == 2);
            Assert.Equal("bad_request", (await Fails(b.PostChatAsync("player", "x"))).Code);
            Assert.Equal("bad_request", (await Fails(b.PostChatAsync("note", new string('x', 501)))).Code);
            fb.VivariumSupported = false;
            Assert.Equal("not_found", (await Fails(b.VivariumAsync())).Code);
        }

        [Fact]
        public async Task GoalsBoardRevIdsAndConflict()
        {
            var (fb, b) = Mk();
            var r = await b.PutGoalsAsync(0, JObject.Parse("{\"goals\": [{\"text\": \"Eat\"}], \"orders\": [{\"text\": \"Dig\"}], \"plan\": \"p\", \"log_add\": [\"a\", \"b\"]}"));
            Assert.Equal(1, (int)r["rev"]);
            Assert.True((string)r["goals"][0]["id"] == "g1" && (string)r["goals"][0]["horizon"] == "short" && (string)r["goals"][0]["status"] == "open" && (string)r["goals"][0]["by"] == "narrator");
            Assert.True((string)r["orders"][0]["id"] == "o2" && r["orders"][0]["horizon"] == null);
            Assert.True((string)r["plan"] == "p" && r["log"].Count() == 2 && (int)r["log"][0]["tick"] == 1000);
            var e = await Fails(b.PutGoalsAsync(0, new JObject()));
            Assert.True(e.Code == "conflict" && (int)e.Body["board"]["rev"] == 1);
            Assert.Equal("bad_request", (await Fails(b.PutGoalsAsync(1, JObject.Parse("{\"goals\": [{\"text\": \"\"}]}")))).Code);
            Assert.Equal("bad_request", (await Fails(b.PutGoalsAsync(1, new JObject { ["goals"] = new JArray(Enumerable.Range(0, 21).Select(i => new JObject { ["text"] = "x" })) }))).Code);
            Assert.Equal(1, (int)(await b.GoalsAsync())["rev"]);
            var keep = await b.PutGoalsAsync(1, JObject.Parse("{\"goals\": [{\"id\": \"g1\", \"text\": \"Eat more\", \"status\": \"done\"}]}"));
            Assert.True((string)keep["goals"][0]["status"] == "done" && keep["goals"][0]["horizon"] != null && keep["goals"][0]["by"] == null && (int)keep["rev"] == 2);
        }

        [Fact]
        public async Task ActShapesRefusalsAndLog()
        {
            var (fb, b) = Mk();
            Assert.Equal("bad_request", (await Fails(b.ActAsync("time", new JObject()))).Code);
            Assert.Equal("bad_request", (await Fails(b.ActAsync("time", new JObject { ["speed"] = 9 }))).Code);
            var e = await Fails(b.ActAsync("time", new JObject { ["speed"] = 2 }));
            Assert.True(e.Code == "refused" && e.Status == 409 && e.Detail.Contains("speed"));
            // emergency pause is allowed by default; the narrator may unpause only its own pause
            var ok = await b.ActAsync("time", new JObject { ["pause"] = true, ["emergency"] = true });
            Assert.True((string)ok["done"] == "time: pause True" && fb.Paused && fb.PausedByNarrator);
            Assert.Equal("time: pause False", (string)(await b.ActAsync("time", new JObject { ["pause"] = false }))["done"]);
            Assert.True(!fb.Paused && !fb.PausedByNarrator);
            Assert.Equal("refused", (await Fails(b.ActAsync("time", new JObject { ["pause"] = false }))).Code);
            Assert.Equal("refused", (await Fails(b.ActAsync("time", new JObject { ["pause"] = true }))).Code);
            fb.VivSettings["controls_speed"] = true;
            Assert.Equal("time: speed 3", (string)(await b.ActAsync("time", new JObject { ["speed"] = 3 }))["done"]);
            Assert.Equal(3, fb.Speed);
            Assert.Equal("set Cooking 2, Hauling 0 for pawn 1", (string)(await b.ActAsync("work", JObject.Parse("{\"pawn\": 1, \"priorities\": {\"Cooking\": 2, \"Hauling\": 0}}")))["done"]);
            Assert.Equal("schedule 0-6 sleep for pawn 1", (string)(await b.ActAsync("schedule", JObject.Parse("{\"pawn\": 1, \"from_hour\": 0, \"to_hour\": 6, \"assignment\": \"sleep\"}")))["done"]);
            Assert.Equal("food policy Lavish for pawn 2", (string)(await b.ActAsync("policy", JObject.Parse("{\"pawn\": 2, \"kind\": \"food\", \"name\": \"Lavish\"}")))["done"]);
            Assert.Equal("bed 8 for pawn 2", (string)(await b.ActAsync("bed", JObject.Parse("{\"pawn\": 2, \"bed\": 8}")))["done"]);
            Assert.Equal("research Smithing", (string)(await b.ActAsync("research", JObject.Parse("{\"project\": \"Smithing\"}")))["done"]);
            Assert.Equal("letter 4: Accept", (string)(await b.ActAsync("letter", JObject.Parse("{\"letter\": 4, \"choice\": \"Accept\"}")))["done"]);
            Assert.Equal("drafted 2 pawn(s)", (string)(await b.ActAsync("draft", JObject.Parse("{\"pawns\": [1, 2], \"drafted\": true}")))["done"]);
            Assert.Equal("move order for pawn 1", (string)(await b.ActAsync("order", JObject.Parse("{\"pawn\": 1, \"order\": \"move\", \"x\": 3, \"z\": 4}")))["done"]);
            Assert.Equal("bad_request", (await Fails(b.ActAsync("order", JObject.Parse("{\"pawn\": 1, \"order\": \"attack\"}")))).Code);
            Assert.Equal("bad_request", (await Fails(b.ActAsync("work", JObject.Parse("{\"pawn\": 1, \"priorities\": {\"Cooking\": 5}}")))).Code);
            Assert.Equal(11, fb.ActLog.Count);
            Assert.DoesNotContain(fb.ActLog, a => a.Value["op_id"] != null || a.Value["expires_tick"] != null);
            var acts = await b.ActionsSinceAsync(9);
            Assert.True(acts["actions"].Count() == 2 && (int)acts["next_seq"] == 12 && (string)acts["actions"][0]["by"] == "narrator");
            fb.ActRefuse["bed"] = "no beds";
            var re = await Fails(b.ActAsync("bed", JObject.Parse("{\"pawn\": 2, \"bed\": 8}")));
            Assert.True(re.Code == "refused" && re.Detail == "no beds");
            fb.ActExtra["research"] = JObject.Parse("{\"eta\": 5}");
            Assert.Equal(5, (int)(await b.ActAsync("research", JObject.Parse("{\"project\": \"X\"}")))["eta"]);
        }

        [Fact]
        public async Task ReloadGameChangesEpochAndOpIdsReplayAsDuplicates()
        {
            var (fb, b) = Mk();
            await b.ThoughtAsync(1, "x", opId: "k");
            var e = await Fails(b.ThoughtAsync(1, "x", opId: "k"));
            Assert.Equal(409, e.Status);
            Assert.NotNull(e.Body["result"]);
            fb.ReloadGame();
            Assert.Equal(2, (int)(await b.HealthAsync())["save_epoch"]);
        }
    }
}
