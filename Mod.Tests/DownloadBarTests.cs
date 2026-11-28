using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class DownloadBarTests
    {
        const long GB = 1000L * 1000 * 1000, MB = 1000L * 1000;

        static EngineStatus Eng(string phase, long done, long total, string step = "model gemma")
        {
            return new EngineStatus { Phase = phase, Progress = new EngineProgress { DoneBytes = done, TotalBytes = total, Step = step } };
        }

        static EngineStatus Up(string state, long done, long total, string name = "Gemma 4 26B", string err = null)
        {
            return new EngineStatus
            {
                Phase = EnginePhase.Ready,
                Upgrade = new EngineUpgrade { State = state, TargetName = name, Error = err, Progress = new EngineProgress { DoneBytes = done, TotalBytes = total, Step = "model x" } }
            };
        }

        [Fact]
        public void Idle_and_null_status_are_hidden()
        {
            var b = new DownloadBar();
            Assert.False(b.Update(null, 0).Visible);
            Assert.False(b.Update(new EngineStatus { Phase = EnginePhase.Ready }, 10).Visible);
        }

        [Fact]
        public void Small_download_is_hidden()
        {
            var b = new DownloadBar();
            Assert.False(b.Update(Eng(EnginePhase.Downloading, 5 * MB, 20 * MB, "backend cpu"), 0).Visible);
        }

        [Fact]
        public void Large_engine_download_shows_a_bar_with_whole_percent()
        {
            var b = new DownloadBar();
            var v = b.Update(Eng(EnginePhase.Downloading, 420 * MB, GB, "backend cuda"), 0);
            Assert.Equal(DownloadBarView.Kind.Bar, v.Mode);
            Assert.Equal("RWL_Bar_Engine", v.LabelKey);
            Assert.Equal(42, v.Percent);
            Assert.True(b.Active);
        }

        [Fact]
        public void Model_download_names_the_model_when_known()
        {
            var b = new DownloadBar();
            var v = b.Update(Up("downloading", GB, 10 * GB), 0);
            Assert.Equal("RWL_Bar_Model", v.LabelKey);
            Assert.Equal("Gemma 4 26B", v.LabelArg);
            Assert.Equal(10, v.Percent);
            var g = b.Update(Eng(EnginePhase.Downloading, GB, 10 * GB), 1000);
            Assert.Equal("RWL_Bar_ModelGeneric", g.LabelKey);
        }

        [Fact]
        public void Verifying_has_its_own_pass()
        {
            var b = new DownloadBar();
            var v = b.Update(Eng(EnginePhase.Downloading, 3 * GB, 10 * GB, "verifying model x"), 0);
            Assert.Equal("RWL_Bar_Checking", v.LabelKey);
            Assert.Equal(30, v.Percent);
        }

        [Fact]
        public void Unknown_total_shows_megabytes_not_a_percent()
        {
            var b = new DownloadBar();
            var v = b.Update(Eng(EnginePhase.Downloading, 128 * MB, 0), 0);
            Assert.Equal(DownloadBarView.Kind.Plain, v.Mode);
            Assert.Equal(-1, v.Percent);
            Assert.Equal("128", v.LabelArg);
            Assert.False(new DownloadBar().Update(Eng(EnginePhase.Downloading, 128 * MB, 0, "backend x"), 0).Visible);
        }

        [Theory]
        [InlineData(0, 100, 0)]
        [InlineData(1, 100, 1)]
        [InlineData(999, 1000, 99)]
        [InlineData(1000, 1000, 99)]      // never 100 before done
        [InlineData(2000, 1000, 99)]      // over-report clamps
        [InlineData(-5, 100, 0)]
        [InlineData(5, 0, -1)]
        public void Percent_clamps(long done, long total, int want) { Assert.Equal(want, DownloadBar.Percent(done, total)); }

        [Fact]
        public void Done_shows_Ready_then_hides()
        {
            var b = new DownloadBar();
            b.Update(Up("downloading", 9 * GB, 10 * GB), 0);
            var v = b.Update(new EngineStatus { Phase = EnginePhase.Ready }, 1000);
            Assert.Equal(DownloadBarView.Kind.Ready, v.Mode);
            Assert.Equal(DownloadBarView.Kind.Ready, b.Update(new EngineStatus { Phase = EnginePhase.Ready }, 4999).Mode);
            Assert.False(b.Update(new EngineStatus { Phase = EnginePhase.Ready }, 5001).Visible);
            Assert.False(b.Update(new EngineStatus { Phase = EnginePhase.Ready }, 9000).Visible);   // not again
        }

        [Fact]
        public void Upgrade_errors_show_one_line_without_a_bar()
        {
            var b = new DownloadBar();
            var v = b.Update(Up("retrying", 1, 10 * GB), 0);
            Assert.Equal(DownloadBarView.Kind.Error, v.Mode);
            Assert.Equal("RWL_Bar_Paused", v.LabelKey);
            Assert.Equal(-1, v.Percent);
            var f = b.Update(Up("no_space", 1, 10 * GB, err: "not enough disk space\nfree 3 GB"), 1000);
            Assert.Equal("not enough disk space free 3 GB", f.Text);
            Assert.False(b.Active);
        }

        [Fact]
        public void Engine_error_after_a_download_shows_a_line_but_a_cold_error_does_not()
        {
            var b = new DownloadBar();
            Assert.False(b.Update(new EngineStatus { Phase = EnginePhase.Error, Message = "x" }, 0).Visible);
            b.Update(Eng(EnginePhase.Downloading, GB, 2 * GB), 1000);
            var v = b.Update(new EngineStatus { Phase = EnginePhase.Error, Message = new string('a', 300) }, 2000);
            Assert.Equal(DownloadBarView.Kind.Error, v.Mode);
            Assert.True(v.Text.Length <= 90);
            var err = new EngineStatus { Phase = EnginePhase.Error, Message = "x" };
            Assert.Equal(DownloadBarView.Kind.Error, b.Update(err, 5000).Mode);    // stays for a few seconds
            Assert.False(b.Update(err, 11000).Visible);                             // then hides
        }

        [Fact]
        public void Sampler_is_at_most_1Hz_while_active_and_slower_idle()
        {
            var b = new DownloadBar();
            Assert.True(b.ShouldSample(0));
            Assert.False(b.ShouldSample(500));
            b.Update(Eng(EnginePhase.Downloading, GB, 2 * GB), 600);
            Assert.False(b.ShouldSample(999));
            Assert.True(b.ShouldSample(1000));
            Assert.False(b.ShouldSample(1999));
            Assert.True(b.ShouldSample(2000));
            b.Update(new EngineStatus { Phase = EnginePhase.Ready }, 2100);   // idle again (Ready line is not active)
            Assert.False(b.ShouldSample(3000));
            Assert.True(b.ShouldSample(4000));
        }
    }
}
