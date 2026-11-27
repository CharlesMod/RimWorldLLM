// Pure parts of core/idle.py (the wants roll, floor arithmetic, float() of a bridge value, constants). The scheduler tests need the
// Director/world fixture and are deferred (docs/csmind/parity-idle.md).
using System;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class IdlePureTests
    {
        // expected values printed by ~/.local/share/rimworld-hermes-engine/csmind/director-idle/rolls.py
        [Theory]
        [InlineData(3, 0, 1500, 0.372)]
        [InlineData(3, 1499, 1500, 0.372)]
        [InlineData(3, 1500, 1500, 0.551)]
        [InlineData(12, 123456, 1500, 0.003)]
        [InlineData(0, 60000, 1500, 0.561)]
        [InlineData(7, 987654, 1, 0.994)]
        [InlineData(5, 5000, 0, 0.076)]
        [InlineData(41, 2999999, 1500, 0.889)]
        public void WantsRollMatchesPythonSha1(int aid, long now, long gap, double expected)
        {
            Assert.Equal(expected, IdleRunner.WantsRoll(aid, now, gap));
        }

        [Theory]
        [InlineData(125000, 60000, 2, 5000)]
        [InlineData(0, 60000, 0, 0)]
        [InlineData(59999, 60000, 0, 59999)]
        [InlineData(60000, 60000, 1, 0)]
        [InlineData(-1, 60000, -1, 59999)]
        public void FloorDivAndModFollowPython(long a, long b, long q, long r)
        {
            Assert.Equal(q, IdleRunner.FloorDiv(a, b));
            Assert.Equal(r, IdleRunner.PyMod(a, b));
        }

        [Fact]
        public void FloatOfABridgeValue()
        {
            Assert.Equal(1.0, IdleRunner.PyFloatOf(null, 1.0));
            Assert.Equal(0.5, IdleRunner.PyFloatOf(new JValue(0.5), 1.0));
            Assert.Equal(2.0, IdleRunner.PyFloatOf(new JValue(2), 1.0));
            Assert.Equal(1.5, IdleRunner.PyFloatOf(new JValue(" 1.5 "), 1.0));
            Assert.Equal(1.0, IdleRunner.PyFloatOf(new JValue(true), 0.0));
            Assert.Throws<PyValueError>(() => IdleRunner.PyFloatOf(new JValue("fast"), 1.0));
            Assert.Throws<PyTypeError>(() => IdleRunner.PyFloatOf(JValue.CreateNull(), 1.0));
        }

        [Fact]
        public void TextFillConstants()
        {
            Assert.Equal(-2, IdleRunner.TextFillActor);
            Assert.Equal(10.0, IdleRunner.TextFillGapS);
            Assert.Equal(10, IdleRunner.MaxDiaryMemories);
        }
    }
}
