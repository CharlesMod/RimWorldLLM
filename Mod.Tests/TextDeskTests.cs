using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class TextDeskTests
    {
        static TextItem Item(string id, string kind, string example = "vanilla text")
        {
            return new TextItem { Id = id, Kind = kind, Example = example, Context = new JObject { ["who"] = "Rook" } };
        }

        const string ArtText = "A small marble figure of a cook staring at a burnt pie, rendered with suspicious tenderness.";

        [Fact]
        public void KindsHaveSaneBoundsAndOnlyLogsCarryTokens()
        {
            foreach (var k in TextKinds.All)
            {
                Assert.True(k.MinLen > 0 && k.MaxLen > k.MinLen, k.Id);
                Assert.Equal(k.Id == TextKinds.Social || k.Id == TextKinds.Combat, k.Tokens.Length > 0);
            }
            Assert.False(TextKinds.Get(TextKinds.Combat).DefaultOn);
            Assert.True(TextKinds.Get(TextKinds.Art).Prio > TextKinds.Get(TextKinds.Social).Prio);
            Assert.Null(TextKinds.Get("names"));
        }

        [Fact]
        public void AnIdIsQueuedOnceEvenAfterItLeaves()
        {
            var d = new TextDesk();
            Assert.True(d.Enqueue(Item("art:1", "art"), 0));
            Assert.False(d.Enqueue(Item("art:1", "art"), 0));
            d.Queue.Clear();
            Assert.False(d.Enqueue(Item("art:1", "art"), 0));
            Assert.True(d.Tried("art:1"));
        }

        [Fact]
        public void DisabledOrUnknownKindsAreNeverQueuedOrShown()
        {
            var d = new TextDesk { Enabled = k => k != "social" };
            Assert.False(d.Enqueue(Item("log:1", "social"), 0));
            Assert.False(d.Enqueue(Item("x:1", "names"), 0));
            Assert.True(d.Enqueue(Item("art:1", "art"), 0));
            string e1, e2;
            Assert.NotNull(d.Fill("art:1", ArtText, "Pie Regret", 5, out e1, out e2));
            Assert.NotNull(d.Get("art:1"));
            d.Enabled = k => false;
            Assert.Null(d.Get("art:1"));        // switched off: the game's own text shows again
        }

        [Fact]
        public void FullQueueDropsTheOldestLowestPriorityFirst()
        {
            var d = new TextDesk();
            for (int i = 0; i < TextDesk.QueueCap - 1; i++) Assert.True(d.Enqueue(Item("log:" + i, "social"), 0));
            Assert.True(d.Enqueue(Item("art:1", "art"), 0));
            Assert.Equal(TextDesk.QueueCap, d.Queue.Count);
            Assert.True(d.Enqueue(Item("book:1", "book"), 0));        // drops log:0, the oldest social
            Assert.Null(d.Find("log:0"));
            Assert.NotNull(d.Find("log:1"));
            Assert.NotNull(d.Find("art:1"));
            d.Enabled = k => true;
            Assert.False(d.Enqueue(Item("log:c1", "combat"), 0));     // lower than everything queued: the new one goes
            Assert.Equal(2, d.Dropped);
            Assert.Equal(TextDesk.QueueCap, d.Queue.Count);
        }

        [Fact]
        public void NextServesPriorityThenAgeAndDropsStaleItems()
        {
            var d = new TextDesk();
            d.Enqueue(Item("log:1", "social"), 0);
            d.Enqueue(Item("art:1", "art"), 0);
            d.Enqueue(Item("book:1", "book"), 0);
            var next = d.Next(2, 10);
            Assert.Equal(new[] { "art:1", "book:1" }, next.Select(i => i.Id).ToArray());
            d.Next(2, 10); d.Next(2, 10);                // art and book served three times now
            Assert.Equal(new[] { "log:1" }, d.Next(4, 10).Select(i => i.Id).ToArray());
            Assert.Empty(d.Next(4, TextDesk.TtlTicks + 1));   // expired
        }

        [Fact]
        public void FillValidatesStoresAndLeavesTheQueue()
        {
            var d = new TextDesk();
            d.Enqueue(Item("art:1", "art"), 0);
            d.Enqueue(Item("log:2", "social"), 0);
            string err, det;
            Assert.Null(d.Fill("art:9", ArtText, null, 1, out err, out det));
            Assert.Equal("unknown_id", err);
            Assert.Null(d.Fill("art:1", "too short", null, 1, out err, out det));
            Assert.Equal("bad_text", err);
            Assert.Null(d.Fill("art:1", ArtText + " <color=red>x</color>", null, 1, out err, out det));
            Assert.Contains("markup", det);
            Assert.Null(d.Fill("log:2", "[INITIATOR] told [BOB] a joke about rice.", null, 1, out err, out det));
            Assert.Contains("brackets", det);
            Assert.Null(d.Fill("art:1", ArtText, new string('x', 61), 1, out err, out det));
            Assert.Contains("title", det);
            var e = d.Fill("art:1", "  \"" + ArtText + "\"  ", " Pie   Regret ", 7, out err, out det);
            Assert.Equal(ArtText, e.Text);
            Assert.Equal("Pie Regret", e.Title);
            Assert.Null(d.Find("art:1"));
            Assert.Equal(ArtText, d.Get("art:1").Text);
            Assert.Null(d.Fill("art:1", ArtText, null, 8, out err, out det));    // filled once only
            Assert.Equal("unknown_id", err);
            Assert.NotNull(d.Fill("log:2", "[INITIATOR] told [RECIPIENT] a joke about rice.", null, 1, out err, out det));
        }

        [Fact]
        public void StoreKeepsTheNewestPerKind()
        {
            var d = new TextDesk();
            int cap = TextKinds.Get("combat").StoreCap;
            d.Enabled = k => true;
            string err, det;
            for (int i = 0; i < cap + 5; i++)
            {
                d.Queue.Clear();
                Assert.True(d.Enqueue(Item("log:" + i, "combat"), 0));
                Assert.NotNull(d.Fill("log:" + i, "[INITIATOR] swung at [RECIPIENT] and hit a wall.", null, i, out err, out det));
            }
            Assert.Equal(cap, d.Count("combat"));
            Assert.Null(d.Get("log:0"));
            Assert.NotNull(d.Get("log:" + (cap + 4)));
        }

        [Fact]
        public void SaveAndLoadKeepTextsQueueAndTried()
        {
            var d = new TextDesk();
            d.Enqueue(Item("art:1", "art"), 0);
            d.Enqueue(Item("grave:2", "grave", "RIP"), 3);
            string err, det;
            d.Fill("art:1", ArtText, "Pie Regret", 4, out err, out det);
            var json = d.Export().ToString();
            var d2 = new TextDesk();
            d2.Import(JObject.Parse(json));
            Assert.Equal("Pie Regret", d2.Get("art:1").Title);
            Assert.Equal("RIP", d2.Find("grave:2").Example);
            Assert.Equal("Rook", (string)d2.Find("grave:2").Context["who"]);
            Assert.False(d2.Enqueue(Item("art:1", "art"), 5));
            Assert.True(d2.Enqueue(Item("book:3", "book"), 5));
            Assert.True(d2.Find("book:3").Seq > d2.Find("grave:2").Seq);
            d2.Import(null);
            Assert.Empty(d2.Queue);
        }

        [Fact]
        public void TokensFillWithNamesAndUnknownOnesBecomeSomeone()
        {
            var v = new Dictionary<string, string> { ["INITIATOR"] = "Rook", ["RECIPIENT"] = "Mira" };
            Assert.Equal("Rook told Mira about rice.", TextTokens.Fill("[INITIATOR] told [RECIPIENT] about rice.", v));
            Assert.Equal("Rook told someone.", TextTokens.Fill("[INITIATOR] told [RECIPIENT].", new Dictionary<string, string> { ["INITIATOR"] = "Rook" }));
            Assert.Equal("plain", TextTokens.Fill("plain", null));
        }

        [Fact]
        public void RomanceAndWoundLinesStayTheGames()
        {
            foreach (var n in new[] { "RomanceAttempt", "MarriageProposal", "Breakup", "LovinNotice", "Flirt", "" })
                Assert.True(TextLogRules.Unsafe(n), n);
            foreach (var n in new[] { "Chitchat", "DeepTalk", "Insult", "KindWords", "Slight" })
                Assert.False(TextLogRules.Unsafe(n), n);
            Assert.True(TextLogRules.MeleeOk("Combat_Miss"));
            Assert.False(TextLogRules.MeleeOk("Combat_Hit"));
            Assert.False(TextLogRules.MeleeOk(null));
        }

        [Fact]
        public void TidyStripsWrappingQuotesAndSpace()
        {
            Assert.Equal("Hello there", TextValidate.Tidy(" “Hello \n there” "));
            string r;
            Assert.Null(TextValidate.Clean("(*Name)Bob(/Name) waved hello there", 5, 100, null, out r));
            Assert.Equal("markup", r);
        }
    }

    public class TextRouteTests
    {
        sealed class Applier : ITextApplier
        {
            public List<TextEntry> Got = new List<TextEntry>();
            public string Result;
            public string Apply(TextEntry e) { Got.Add(e); return Result; }
        }

        static (Harness h, TextDesk d, Applier a) Make()
        {
            var h = new Harness();
            var d = new TextDesk();
            var a = new Applier();
            TextRoutes.Register(h.Router, () => d, () => a, () => 100);
            return (h, d, a);
        }

        const string Grave = "Here lies Rook, who tried to pet the boomalope.";

        [Fact]
        public void QueueListsItemsWithTheirShape()
        {
            var (h, d, _) = Make();
            d.Enqueue(new TextItem { Id = "art:5", Kind = "art", Example = "A carving.", ExampleTitle = "Thing", Context = new JObject { ["material"] = "jade" } }, 90);
            d.Enqueue(new TextItem { Id = "log:6", Kind = "social" }, 90);
            var r = h.Call("GET", "/text/queue?n=1");
            Assert.True(r.Ok);
            Assert.Equal(2, (int)r.Body["pending"]);
            var it = (JObject)r.Body["items"][0];
            Assert.Equal("art:5", (string)it["id"]);
            Assert.Equal(new[] { "title", "text" }, it["fields"].Select(x => (string)x).ToArray());
            Assert.Equal("jade", (string)it["context"]["material"]);
            Assert.Equal("Thing", (string)it["example_title"]);
            Assert.Equal(60, (int)it["title_max"]);
            Assert.Equal(400, h.Call("GET", "/text/queue?n=zero").Status);
            var all = h.Call("GET", "/text/queue?n=50");
            Assert.Equal(new[] { "INITIATOR", "RECIPIENT" }, all.Body["items"][1]["tokens"].Select(x => (string)x).ToArray());
            Assert.Null(all.Body["items"][1]["title_max"]);
        }

        [Fact]
        public void FillStoresAppliesAndDedupsByOpId()
        {
            var (h, d, a) = Make();
            d.Enqueue(new TextItem { Id = "grave:7", Kind = "grave" }, 90);
            var body = new JObject { ["op_id"] = "f1", ["id"] = "grave:7", ["text"] = Grave }.ToString();
            var r = h.Call("POST", "/text/fill", body);
            Assert.True(r.Ok);
            Assert.True((bool)r.Body["applied"]);
            Assert.Single(a.Got);
            Assert.Equal(Grave, d.Get("grave:7").Text);
            var again = h.Call("POST", "/text/fill", body);        // same op: the first result, no second apply
            Assert.Single(a.Got);
            var other = h.Call("POST", "/text/fill", new JObject { ["op_id"] = "f2", ["id"] = "grave:7", ["text"] = Grave }.ToString());
            Assert.False(other.Ok);
            Assert.Equal(409, other.Status);
            Assert.Equal("unknown_id", (string)other.Body["error"]);
        }

        [Fact]
        public void FillRejectsBadInput()
        {
            var (h, d, a) = Make();
            d.Enqueue(new TextItem { Id = "grave:7", Kind = "grave" }, 90);
            Assert.Equal(400, h.Call("POST", "/text/fill", new JObject { ["op_id"] = "a", ["text"] = Grave }.ToString()).Status);
            Assert.Equal(400, h.Call("POST", "/text/fill", new JObject { ["op_id"] = "b", ["id"] = "grave:7" }.ToString()).Status);
            var bad = h.Call("POST", "/text/fill", new JObject { ["op_id"] = "c", ["id"] = "grave:7", ["text"] = "Here lies [PAWN_nameDef], gone." }.ToString());
            Assert.Equal(400, bad.Status);
            Assert.Equal("bad_text", (string)bad.Body["error"]);
            Assert.Empty(a.Got);
            Assert.NotNull(d.Find("grave:7"));       // still wanted
        }

        [Fact]
        public void AGoneInstanceStillStoresTheText()
        {
            var (h, d, a) = Make();
            a.Result = "gone";
            d.Enqueue(new TextItem { Id = "grave:8", Kind = "grave" }, 90);
            var r = h.Call("POST", "/text/fill", new JObject { ["op_id"] = "g", ["id"] = "grave:8", ["text"] = Grave }.ToString());
            Assert.True(r.Ok);
            Assert.False((bool)r.Body["applied"]);
        }
    }
}
