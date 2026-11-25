using System.Linq;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class RecapGateTests
    {
        static RecapGate Loaded(int epoch = 2, long at = 1000)
        {
            var g = new RecapGate();
            g.Arm(epoch, true, at);
            return g;
        }

        [Fact]
        public void AcceptedOnceInTheWindow()
        {
            var g = Loaded();
            Assert.Equal(RecapVerdict.Accept, g.Check(2, 1000 + 5000, true));
            Assert.Equal(RecapVerdict.Duplicate, g.Check(2, 1000 + 6000, true));
        }

        [Fact]
        public void NewGameEpochIsRefused()
        {
            var g = new RecapGate();
            g.Arm(1, false, 0);
            Assert.Equal(RecapVerdict.NotLoaded, g.Check(1, 10, true));
        }

        [Fact]
        public void WrongEpochIsStaleAndKeepsTheOneRecap()
        {
            var g = Loaded(3);
            Assert.Equal(RecapVerdict.StaleEpoch, g.Check(2, 1100, true));
            Assert.Equal(RecapVerdict.Accept, g.Check(3, 1100, true));
        }

        [Fact]
        public void ExpiresAfterTheWindow()
        {
            var g = Loaded();
            Assert.Equal(RecapVerdict.Accept, g.Check(2, 1000 + RecapGate.WindowMs, true));
            var h = Loaded();
            Assert.Equal(RecapVerdict.Expired, h.Check(2, 1000 + RecapGate.WindowMs + 1, true));
        }

        [Fact]
        public void SettingOffRefusesAndDoesNotConsume()
        {
            var g = Loaded();
            Assert.Equal(RecapVerdict.Disabled, g.Check(2, 1100, false));
            Assert.Equal(RecapVerdict.Accept, g.Check(2, 1100, true));
        }

        [Fact]
        public void ReloadArmsAFreshEpoch()
        {
            var g = Loaded(2);
            g.Check(2, 1100, true);
            g.Arm(3, true, 50000);
            Assert.Equal(RecapVerdict.Accept, g.Check(3, 50100, true));
        }

        [Fact]
        public void DisarmedRefusesEverything()
        {
            var g = Loaded();
            g.Disarm();
            Assert.Equal(RecapVerdict.StaleEpoch, g.Check(2, 1100, true));
        }

        [Fact]
        public void ErrorCodes()
        {
            Assert.Equal("conflict", RecapGate.ErrorCode(RecapVerdict.Duplicate));
            Assert.Equal("conflict", RecapGate.ErrorCode(RecapVerdict.StaleEpoch));
            Assert.Equal("expired", RecapGate.ErrorCode(RecapVerdict.Expired));
            Assert.Equal("refused", RecapGate.ErrorCode(RecapVerdict.Disabled));
            Assert.Equal("refused", RecapGate.ErrorCode(RecapVerdict.NotLoaded));
        }

        [Fact]
        public void LinesAreCappedTrimmedAndLimitedToThree()
        {
            var lines = RecapGate.CleanLines(new[] { "  one \n two ", "", new string('x', 400), "three", "four" });
            Assert.Equal(3, lines.Count);
            Assert.Equal("one two", lines[0]);
            Assert.Equal(RecapGate.MaxChars, lines[1].Length);
            Assert.EndsWith("…", lines[1]);
            Assert.Equal("three", lines[2]);
            Assert.Empty(RecapGate.CleanLines(null));
            Assert.Empty(RecapGate.CleanLines(new[] { " ", "" }.ToList()));
        }
    }
}
