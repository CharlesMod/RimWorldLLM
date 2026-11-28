using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class GuestDiscoveryTests
    {
        const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        static ShareFound T(string id, string root, string name = "den-pc")
        {
            var s = new ShareFound { EngineId = id, Name = name, ModelName = "Gemma 4 E2B", TokenRequired = true, Pairing = true, ViaTailscale = true };
            s.Roots.Add(root);
            return s;
        }

        [Fact]
        public void OneRowPerEngineWithBothBadges()
        {
            var lan = new List<FoundEngine> { new FoundEngine { EngineId = A, Name = "den-pc", Root = "http://192.168.1.20:47480", Answered = true } };
            var rows = GuestDiscovery.Merge(lan, new[] { T(A, "http://100.101.1.2:47480"), T(B, "http://100.101.1.3:47480", "box") }, new PairStore());
            Assert.Equal(2, rows.Count);
            var den = rows.Single(r => r.EngineId == A);
            Assert.Equal("http://192.168.1.20:47480", den.Root);   // the LAN root wins
            Assert.True(den.ViaLan);
            Assert.True(den.ViaTailscale);
            var box = rows.Single(r => r.EngineId == B);
            Assert.False(box.ViaLan);
            Assert.True(box.ViaTailscale);
            Assert.True(box.Answered);
            Assert.True(box.NeedsToken);
        }

        [Fact]
        public void ALanRowThatDidNotAnswerTakesTheTailnetRoot()
        {
            var lan = new List<FoundEngine> { new FoundEngine { EngineId = A, Name = "den-pc", Root = "http://192.168.1.20:47480", Answered = false } };
            var den = Assert.Single(GuestDiscovery.Merge(lan, new[] { T(A, "http://100.101.1.2:47480") }, null));
            Assert.Equal("http://100.101.1.2:47480", den.Root);
            Assert.True(den.Answered);
        }

        [Fact]
        public void HeldTokensAreMarkedByEngineId()
        {
            var store = new PairStore();
            store.Put(new PairedEngine { EngineId = B, Token = "t", Name = "box", Address = "http://192.168.1.99:47480" });
            var row = Assert.Single(GuestDiscovery.Merge(null, new[] { T(B, "http://100.101.1.3:47480") }, store));
            Assert.True(row.HaveToken);
        }

        [Fact]
        public async Task OneSearchFailingLeavesTheOther()
        {
            var rows = await GuestDiscovery.FindAllAsync(() => throw new System.Exception("mdns"), () => Task.FromResult(new List<ShareFound> { T(A, "http://100.101.1.2:47480") }), null);
            Assert.Single(rows);
            rows = await GuestDiscovery.FindAllAsync(() => Task.FromResult(new List<FoundEngine> { new FoundEngine { EngineId = B, Root = "http://192.168.1.5:47480", Answered = true } }), () => Task.FromException<List<ShareFound>>(new System.Exception("ts")), null);
            Assert.Single(rows);
            Assert.True(rows[0].ViaLan);
        }

        [Fact]
        public async Task NoTailscaleInstalledIsAnEmptyList()
        {
            // TailscaleCli.Find returns null when the CLI is nowhere; FindAsync with no cli is empty, never throws
            Assert.Empty(await TailnetDiscovery.FindAsync((p, a, t) => null, null, (r, c) => Task.FromResult<EngineHello>(null)));
        }
    }
}
