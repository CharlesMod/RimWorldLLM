using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    // Ports of the non-Director parts of agent/tests/test_textfill.py (games/rimworld/textfill.py). The tests that need the
    // Director's idle runner (art skip, idle wiring) are deferred, see docs/csmind/parity-delta.md.
    public class TextFillTests
    {
        private const string Good = "Rook carved this in the rain, mostly to keep away from the kitchen.";

        private static JObject Item(string id = "art:T1", string kind = "art", Action<JObject> over = null)
        {
            var o = new JObject
            {
                ["id"] = id, ["kind"] = kind, ["prio"] = 3,
                ["fields"] = kind == "art" ? new JArray("title", "text") : new JArray("text"),
                ["min_len"] = 20, ["max_len"] = 200, ["title_max"] = 30, ["tokens"] = new JArray(), ["about"] = "an artwork's description",
                ["example"] = "A plain statue.", ["example_title"] = "Statue",
                ["context"] = JObject.Parse(@"{""maker"": {""name"": ""Rook"", ""mood"": ""low (31%)"", ""traits"": [""Pyromaniac""]}, ""material"": ""marble"",
                    ""colony"": {""weather"": ""rain"", ""recent"": [""Raid by pirates""]}}"),
            };
            over?.Invoke(o);
            return o;
        }

        private static string Reply(params object[] objs) => new JArray(objs.Select(o => JObject.FromObject(o))).ToString(Newtonsoft.Json.Formatting.None);

        private sealed class Host : ITextFillHost
        {
            public FakeBridge Fb = new FakeBridge("tok");
            public FakeLlm Fl = new FakeLlm();
            public BridgeClient Client;
            public Host() { Client = FakeBridge.Client(Fb, "tok"); Fb.TextSupported = true; }
            public IBridge Bridge { get { return Client; } }
            public ILlmClient Llm { get { return Fl; } }
            public double TextFillWaitS { get; set; }
            public bool TextQueueLive { get; set; }
            public HashSet<string> TextTried { get; } = new HashSet<string>();
            public List<(int Actor, string Reason)> Dropped { get; } = new List<(int, string)>();
        }

        private static Host WorldWith(IEnumerable<JObject> items, params string[] script)
        {
            var h = new Host();
            h.Fb.TextItems = items.ToList();
            if (script.Length > 0) h.Fl.Script["textfill"] = script.ToList();
            return h;
        }

        // ---- batch / schema / messages
        [Fact]
        public void BatchFirstPlusSameKindUpToThree()
        {
            var items = new List<JObject> { Item("a", "art"), Item("b", "grave"), Item("c", "art"), Item("d", "art"), Item("e", "art") };
            Assert.Equal(new[] { "a", "c", "d" }, TextFill.Batch(items).Select(i => (string)i["id"]).ToArray());
            Assert.Empty(TextFill.Batch(new List<JObject>()));
        }

        [Fact]
        public void SchemaIdsBoundsAndTitle()
        {
            var s = TextFill.Schema(new List<JObject> { Item("a"), Item("b", over: o => o["max_len"] = 300) });
            Assert.True((string)s["type"] == "array" && (int)s["minItems"] == 1 && (int)s["maxItems"] == 2);
            var p = (JObject)s["items"]["properties"];
            Assert.Equal(new[] { "a", "b" }, p["id"]["enum"].Select(x => (string)x).ToArray());
            Assert.True((int)p["text"]["minLength"] == 20 && (int)p["text"]["maxLength"] == 300);
            Assert.True((int)p["title"]["maxLength"] == 30);
            Assert.Equal(new[] { "id", "text" }, s["items"]["required"].Select(x => (string)x).ToArray());
            Assert.Null(((JObject)TextFill.Schema(new List<JObject> { Item("g", "grave") })["items"]["properties"])["title"]);
        }

        [Fact]
        public void MessagesCarryContextExampleAndMinorLine()
        {
            var it = Item("s1", "social", o => { o["fields"] = new JArray("text"); o["tokens"] = new JArray("INITIATOR", "RECIPIENT");
                                                 o["context"] = JObject.Parse(@"{""minor_involved"": true, ""nearby"": [""Mira""]}"); });
            var msgs = TextFill.Messages(new List<JObject> { it, Item("art:x") });
            string flat = msgs[1].Text;
            Assert.True(msgs[0].Role == "system" && flat.Contains("[INITIATOR], [RECIPIENT]") && flat.Contains("Mira"));
            Assert.True(flat.Contains("child or teenager") && flat.Contains("A plain statue.") && flat.Contains("Pyromaniac") && flat.Contains("low (31%)"));
            Assert.True(flat.Contains("none (no square brackets at all)") && !flat.Split(new[] { "context:" }, StringSplitOptions.None)[1].Split(new[] { "sample" }, StringSplitOptions.None)[0].Contains("{"));
        }

        // ---- clean
        [Fact]
        public void CleanStripsQuotesAndWhitespace()
        {
            var got = TextFill.Clean(Item(), JObject.FromObject(new { text = "  \"" + Good + "\n\"  ", title = " 'Smoke'  " }));
            Assert.Equal((Good, "Smoke"), got.Value);
        }

        [Fact]
        public void CleanRejectsLengthTitleBracketsMarkup()
        {
            var it = Item();
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = "too short", title = "T" })));
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = string.Concat(Enumerable.Repeat("x ", 150)), title = "T" })));
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = Good })));                                   // title wanted
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = Good, title = new string('T', 31) })));
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = Good + " [Rook]", title = "T" })));          // no tokens allowed
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = Good, title = "A <b>" })));
            foreach (var bad in new[] { "<b>bold</b> " + Good, Good + " {x}", Good + " (*note*)" })
                Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = bad, title = "T" })));
            Assert.Null(TextFill.Clean(it, new JValue("x")));
        }

        [Fact]
        public void CleanAllowsOnlyTheItemsTokens()
        {
            var it = Item("s", "social", o => { o["fields"] = new JArray("text"); o["tokens"] = new JArray("INITIATOR", "RECIPIENT"); });
            string ok = "[INITIATOR] told [RECIPIENT] the soup was fine, which was brave.";
            Assert.Equal((ok, (string)null), TextFill.Clean(it, JObject.FromObject(new { text = ok })).Value);
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = "[INITIATOR] told [OTHER] the soup was fine, truly." })));
            Assert.Null(TextFill.Clean(it, JObject.FromObject(new { text = "[INITIATOR] told the soup was [fine] today." })));
        }

        [Fact]
        public void CleanDropsRomanceForSocialAndMinorInvolved()
        {
            Assert.True(AgeGate.Romantic("They shared a passionate kiss and made love tonight."));
            var soc = Item("s", "social", o => { o["fields"] = new JArray("text"); o["tokens"] = new JArray("A", "B"); });
            Assert.Null(TextFill.Clean(soc, JObject.FromObject(new { text = "[A] shared a passionate kiss and made love to [B] tonight." })));
            var graveMinor = Item("g", "grave", o => { o["fields"] = new JArray("text"); o["context"] = JObject.Parse(@"{""minor_involved"": true}"); });
            Assert.Null(TextFill.Clean(graveMinor, JObject.FromObject(new { text = "Here lies a lover who shared a passionate kiss and made love." })));
            var grave = Item("g2", "grave", o => o["fields"] = new JArray("text"));
            Assert.NotNull(TextFill.Clean(grave, JObject.FromObject(new { text = "Here lies a lover who shared a passionate kiss and made love." })));
        }

        // ---- fill_once
        [Fact]
        public async Task FillOncePostsCleanedTextWithOpIds()
        {
            var w = WorldWith(new[] { Item("a"), Item("b", "grave"), Item("c") },
                Reply(new { id = "a", text = "\"" + Good + "\"", title = "Smoke" }, new { id = "c", text = Good, title = "Ash" }));
            int n = await TextFill.FillOnceAsync(w, 1);
            Assert.Equal(2, n);
            var log = w.Fb.TextFillLog;
            Assert.Equal(new[] { "a", "c" }, log.Select(b => (string)b["id"]).ToArray());
            Assert.True((string)log[0]["text"] == Good && (string)log[0]["title"] == "Smoke");
            Assert.True(log.All(b => !string.IsNullOrEmpty((string)b["op_id"])) && (string)log[0]["op_id"] != (string)log[1]["op_id"]);
            Assert.Equal(new[] { "b" }, w.Fb.TextItems.Select(i => (string)i["id"]).ToArray());
            var call = w.Fl.Calls.Last();
            Assert.True(call.Kind == "textfill" && call.Slot == 1 && (int)call.JsonSchema["maxItems"] == 2);
            Assert.True(w.TextQueueLive && w.TextFillWaitS == TextFill.WaitOkS);
        }

        [Fact]
        public async Task FillOnceBadJsonAndBadItemsPostNothing()
        {
            var w = WorldWith(new[] { Item("a") }, "not json at all");
            Assert.True(await TextFill.FillOnceAsync(w, 1) == 0 && w.Fb.TextFillLog.Count == 0);
            w.Fl.Script["textfill"] = new List<string> { Reply(new { id = "a", text = "short" }) };
            Assert.Equal(0, await TextFill.FillOnceAsync(w, 1));                      // "a" was tried once: not asked again
            Assert.True(w.Fl.Calls.Count == 1 && w.TextFillWaitS == TextFill.WaitEmptyS);
        }

        [Fact]
        public async Task FillOnceRejectedTextIsRecordedAsDropped()
        {
            var w = WorldWith(new[] { Item("a") }, Reply(new { id = "a", text = "short" }));
            Assert.Equal(0, await TextFill.FillOnceAsync(w, 1));
            Assert.Equal(new[] { (-2, "textfill:art:rejected") }, w.Dropped.ToArray());
        }

        [Fact]
        public async Task FillOnce404BacksOffAndEmptyQueue()
        {
            var w = WorldWith(new JObject[0]);
            Assert.True(await TextFill.FillOnceAsync(w, 1) == 0 && w.TextQueueLive && w.TextFillWaitS == TextFill.WaitEmptyS);
            w.Fb.TextSupported = false;
            Assert.Equal(0, await TextFill.FillOnceAsync(w, 1));
            Assert.True(!w.TextQueueLive && w.TextFillWaitS == TextFill.WaitUnsupportedS && w.Fl.Calls.Count == 0);
        }

        [Fact]
        public async Task FillOnceUnknownIdDoesNotCrash()
        {
            var w = WorldWith(new[] { Item("a"), Item("c") },
                Reply(new { id = "a", text = Good, title = "T" }, new { id = "zzz", text = Good }, new { id = "c", text = Good, title = "U" }));
            // a real race: the queue still lists a, but the mod no longer holds it
            w.Fb.OnTextQueue = () => w.Fb.TextItems = w.Fb.TextItems.Where(i => (string)i["id"] != "a").ToList();
            Assert.Equal(1, await TextFill.FillOnceAsync(w, 1));
            Assert.Equal(new[] { "c" }, w.Fb.TextFillLog.Select(b => (string)b["id"]).ToArray());
        }

        // ---- bridge routes (the Python tests reach them through the world fixture)
        [Fact]
        public async Task BridgeTextQueueAndFillRoutes()
        {
            var w = WorldWith(new[] { Item("a", "grave") });
            var q = await w.Client.TextQueueAsync(4);
            Assert.True((int)q["pending"] == 1 && ((JArray)q["items"]).Count == 1);
            var e = await Assert.ThrowsAsync<BridgeError>(async () => await w.Client.TextFillAsync("zzz", "x"));
            Assert.Equal("unknown_id", e.Code);
            e = await Assert.ThrowsAsync<BridgeError>(async () => await w.Client.TextFillAsync("a", "x", "has no title field"));
            Assert.Equal("bad_text", e.Code);
            var ok = await w.Client.TextFillAsync("a", "fine", null);
            Assert.True((bool)ok["stored"]);
            w.Fb.TextSupported = false;
            e = await Assert.ThrowsAsync<BridgeError>(async () => await w.Client.TextQueueAsync(4));
            Assert.Equal(404, e.Status);
        }
    }
}
