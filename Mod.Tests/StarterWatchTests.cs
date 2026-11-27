using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class StarterWatchTests
    {
        [Fact]
        public void Starter_then_full_gives_each_message_once()
        {
            var w = new StarterWatch();
            Assert.Equal(StarterWatch.Event.None, w.Update(false, "starter"));     // no status yet
            Assert.Equal(StarterWatch.Event.Starter, w.Update(true, "starter"));
            Assert.True(w.KeepPolling);
            Assert.Equal(StarterWatch.Event.None, w.Update(true, "starter"));
            Assert.Equal(StarterWatch.Event.None, w.Update(true, ""));             // mid-swap / no model: wait
            Assert.Equal(StarterWatch.Event.Ready, w.Update(true, "full"));
            Assert.False(w.KeepPolling);
            Assert.Equal(StarterWatch.Event.None, w.Update(true, "full"));
            Assert.Equal(StarterWatch.Event.None, w.Update(true, "starter"));      // done for this session
        }

        [Fact]
        public void A_full_model_from_the_start_says_nothing()
        {
            var w = new StarterWatch();
            Assert.Equal(StarterWatch.Event.None, w.Update(true, "full"));
            Assert.Equal(StarterWatch.Event.None, w.Update(true, ""));
            Assert.False(w.KeepPolling);
        }

        [Fact]
        public void The_checksum_pass_is_recognised()
        {
            Assert.True(new EngineProgress { Step = "verifying model gemma4-rp-maxxed-q3kxl" }.Verifying);
            Assert.False(new EngineProgress { Step = "model gemma4-rp-maxxed-q3kxl" }.Verifying);
            Assert.False(new EngineProgress().Verifying);
        }

        [Theory]
        [InlineData(0, 0, -1)]
        [InlineData(0, 100, 0)]
        [InlineData(55, 100, 55)]
        [InlineData(150, 100, 100)]
        [InlineData(5_000_000_000L, 10_000_000_000L, 50)]
        public void Percent_is_whole_and_clamped(long done, long total, int want)
        {
            Assert.Equal(want, StarterWatch.Percent(done, total));
        }
    }
}
