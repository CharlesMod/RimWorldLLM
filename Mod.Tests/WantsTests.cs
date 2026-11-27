using System.Collections.Generic;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class WantsTests
    {
        static WantsFacts Ok() { return new WantsFacts { Enabled = true, Action_SeekOut = true, Spawned = true, SameMap = true, TargetSpawned = true, TargetReachable = true }; }

        [Fact]
        public void AvoidExpiresOnGameTicks()
        {
            var t = new AvoidTable();
            t.Add(1, 2, 1000, 2500);
            Assert.True(t.IsAvoiding(1, 2, 1000));
            Assert.True(t.IsAvoiding(1, 2, 3499));
            Assert.False(t.IsAvoiding(2, 1, 1000));          // one direction only
            Assert.True(t.Blocks(1, 2, 1000));               // but no chat starts either way
            Assert.True(t.Blocks(2, 1, 1000));
            Assert.False(t.Blocks(2, 3, 1000));
            Assert.False(t.IsAvoiding(1, 2, 3500));
            Assert.Equal(0, t.Count);                         // expiry removed it
        }

        [Fact]
        public void AvoidSurvivesSaveAndDropsExpired()
        {
            var t = new AvoidTable();
            t.Add(1, 2, 0, 5000); t.Add(3, 4, 0, 100);
            var saved = t.Export(500);                        // (3,4) already expired
            var u = new AvoidTable();
            u.Import(saved, 600);
            Assert.True(u.IsAvoiding(1, 2, 600));
            Assert.False(u.IsAvoiding(3, 4, 600));
            Assert.Equal(1, u.Count);
        }

        [Fact]
        public void AvoidIsBoundedAndZeroDurationIsNothing()
        {
            var t = new AvoidTable();
            t.Add(1, 2, 0, 0);
            Assert.Equal(0, t.Count);
            for (int i = 0; i < 1000; i++) t.Add(i, i + 1, 0, 100);
            Assert.True(t.Count <= AvoidTable.MaxEntries);
            Assert.Equal(2500, AvoidTable.HoursToTicks(1f));
        }

        [Fact]
        public void ConfigurableDurationConverts() { Assert.Equal(625, AvoidTable.HoursToTicks(0.25f)); Assert.Equal(0, AvoidTable.HoursToTicks(-3f)); }

        [Fact]
        public void HealthyPawnIsNotRefused() { Assert.Null(WantsGate.Refusal(Ok())); }

        [Fact]
        public void EachRefusalReason()
        {
            var cases = new List<KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>>
            {
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("disabled", f => { f.Enabled = false; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("dead", f => { f.Dead = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("not_spawned", f => { f.Spawned = false; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("other_map", f => { f.SameMap = false; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("downed", f => { f.Downed = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("drafted", f => { f.Drafted = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("asleep", f => { f.Asleep = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("mental_state", f => { f.MentalState = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("forced_work", f => { f.ForcedWork = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("in_combat", f => { f.InCombat = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("busy", f => { f.JobBlocks = true; return f; }),
                new KeyValuePair<string, System.Func<WantsFacts, WantsFacts>>("unreachable", f => { f.TargetReachable = false; return f; }),
            };
            foreach (var c in cases) Assert.Equal(c.Key, WantsGate.Refusal(c.Value(Ok())));
        }

        [Fact]
        public void AvoidDoesNotNeedReachability()
        {
            var f = Ok(); f.Action_SeekOut = false; f.TargetReachable = false; f.TargetSpawned = false;
            Assert.Null(WantsGate.Refusal(f));
        }

        [Fact]
        public void DisabledBeatsEverything() { var f = Ok(); f.Enabled = false; f.Dead = true; Assert.Equal("disabled", WantsGate.Refusal(f)); }

        static string Parse(string json, out SocialRequest r) { return SocialRequest.Parse(JObject.Parse(json), out r); }

        [Fact]
        public void ValidBodyParses()
        {
            SocialRequest r;
            Assert.Null(Parse("{\"op_id\":\"x\",\"pawn\":1,\"action\":\"seek_out\",\"target\":2,\"topic\":\"  the bread \"}", out r));
            Assert.Equal(1, r.Pawn); Assert.Equal(2, r.Target); Assert.Equal("seek_out", r.Action); Assert.Equal("the bread", r.Topic);
            Assert.Null(Parse("{\"pawn\":1,\"action\":\"avoid\",\"target\":2}", out r));
            Assert.Equal("", r.Topic);
        }

        [Theory]
        [InlineData("{\"action\":\"avoid\",\"target\":2}")]
        [InlineData("{\"pawn\":1,\"action\":\"avoid\"}")]
        [InlineData("{\"pawn\":1,\"target\":2}")]
        [InlineData("{\"pawn\":1,\"action\":\"flirt\",\"target\":2}")]
        [InlineData("{\"pawn\":1,\"action\":\"romance\",\"target\":2}")]
        [InlineData("{\"pawn\":\"1\",\"action\":\"avoid\",\"target\":2}")]
        [InlineData("{\"pawn\":1,\"action\":\"avoid\",\"target\":1}")]
        [InlineData("{\"pawn\":1,\"action\":\"avoid\",\"target\":2,\"topic\":5}")]
        public void BadBodiesAreRejected(string json)
        {
            SocialRequest r;
            Assert.NotNull(Parse(json, out r));
            Assert.Null(r);
        }

        [Fact]
        public void TopicIsCappedAt80()
        {
            SocialRequest r;
            Assert.Null(Parse("{\"pawn\":1,\"action\":\"seek_out\",\"target\":2,\"topic\":\"" + new string('a', 200) + "\"}", out r));
            Assert.Equal(80, r.Topic.Length);
        }

        [Fact]
        public void MinorsLoseTheTopic()
        {
            Assert.Equal("", SocialRequest.GuardTopic("the bread", 17, 30));
            Assert.Equal("", SocialRequest.GuardTopic("the bread", 30, 12));
            Assert.Equal("the bread", SocialRequest.GuardTopic("the bread", 18, 18));
            Assert.Equal("", SocialRequest.GuardTopic(null, 30, 30));
        }

        [Fact]
        public void RefusedMapsToConflictStatus() { Assert.Equal(409, Envelope.StatusFor("refused")); }
    }
}
