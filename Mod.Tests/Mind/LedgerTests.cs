using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    // The Python has no dedicated ledger tests (it is exercised through the director); these pin the module's own behaviour.
    public class LedgerTests
    {
        [Fact]
        public void OpenKnowTellAndEnd()
        {
            var l = new Ledger();
            l.Apply(JObject.Parse("{\"ledger_id\":\"raid1\",\"type\":\"raid_start\",\"summary\":\"Raiders\",\"game\":50,\"witnesses\":[3,\"4\"]}"), new[] { 1 });
            l.Apply(JObject.Parse("{\"type\":\"social\",\"summary\":\"no id\"}"));
            Assert.Single(l.Entries);
            var e = l.Entries["raid1"];
            Assert.Equal(("raid_start", "Raiders", 50L), (e.Etype, e.Summary, e.OpenedTime));
            Assert.True(e.KnownBy.SetEquals(new[] { 1, 3, 4 }));
            l.Apply(JObject.Parse("{\"ledger_id\":\"raid1\",\"type\":\"raid_update\",\"summary\":\"More\"}"));
            Assert.Equal("More", e.Summary);
            l.Tell("raid1", 9); l.Tell("nope", 9);
            Assert.Equal(new[] { "raid1" }, l.KnownTo(9).Select(x => x.LedgerId).ToArray());
            Assert.Empty(l.KnownTo(77));
            l.Apply(JObject.Parse("{\"ledger_id\":\"raid1\",\"type\":\"raid_end\"}"));
            Assert.Empty(l.Entries); Assert.True(e.Closed);
        }

        [Fact]
        public void LoadWorldAndStableOrder()
        {
            var l = new Ledger();
            l.LoadWorld(new[]
            {
                JObject.Parse("{\"id\":\"b\",\"type\":\"x\",\"game\":5,\"known_by\":[1]}"),
                JObject.Parse("{\"ledger_id\":\"a\",\"type\":\"x\",\"game\":5,\"witnesses\":[1]}"),
                JObject.Parse("{\"ledger_id\":\"c\",\"game\":1,\"known_by\":[1]}"),
                JObject.Parse("{\"ledger_id\":\"a\",\"game\":99,\"known_by\":[2]}"),
                JObject.Parse("{\"summary\":\"no id\"}"),
            });
            Assert.Equal(new[] { "c", "a", "b" }, l.KnownTo(1).Select(x => x.LedgerId).ToArray());
            Assert.Equal(5L, l.Entries["a"].OpenedTime);
            l.Clear(); Assert.Empty(l.Entries);
        }
    }
}
