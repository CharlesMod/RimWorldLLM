// Expected values recorded from CPython 3.14 by ~/.local/share/rimworld-hermes-engine/csmind/text/rec.py
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class PyRandomTests
    {
        public static readonly JObject Rec = JObject.Parse(@"{""1"": {""random"": [0.13436424411240122, 0.8474337369372327, 0.763774618976614, 0.2550690257394217, 0.49543508709194095], ""bits"": [0, 120, 2798570523, 5925402262, 1731403761479293229, 1100741980127809444553165122690], ""randrange"": [6, 6, 9, 0, 7, 4, 3, 9, 409, 850, 262, 245], ""randint"": [1, 6, 5, 1, 4, 6], ""uniform"": [0.012765186736873494, 0.017286564662819782, 0.008638897326647094], ""choice"": [3, 7, 7, 8, 3], ""shuffle"": [4, 9, 1, 0, 2, 7, 6, 8, 3, 5], ""sample_small"": [2, 4], ""sample_7"": [3, 10, 16, 13, 6, 4, 14], ""sample_big"": [601, 996, 903, 511, 866], ""choices"": [9, 3, 8, 4], ""after"": 0.7437306552931983}, ""7"": {""random"": [0.32383276483316237, 0.15084917392450192, 0.6509344730398537, 0.07243628666754276, 0.5358820043066892], ""bits"": [0, 149, 249103477, 8202116500, 691672907343361484, 112359174146832493601299145562], ""randrange"": [3, 1, 8, 6, 0, 9, 1, 3, 1491, 1484, 1393, 326], ""randint"": [5, 5, 4, 1, 2, 1], ""uniform"": [0.02024662775461604, 0.010929845961715312, 0.017221058958572233], ""choice"": [8, 1, 9, 4, 8], ""shuffle"": [8, 7, 6, 5, 4, 0, 9, 3, 1, 2], ""sample_small"": [9, 3], ""sample_7"": [15, 17, 13, 10, 14, 9, 7], ""sample_big"": [370, 306, 254, 813, 184], ""choices"": [6, 2, 5, 5], ""after"": 0.8751374955734289}, ""42"": {""random"": [0.6394267984578837, 0.025010755222666936, 0.27502931836911926, 0.22321073814882275, 0.7364712141640124], ""bits"": [1, 189, 3831882064, 2342331444, 7783083932390163561, 245107789584464165588405774579], ""randrange"": [3, 8, 9, 0, 8, 3, 8, 6, 651, 1119, 1406, 769], ""randint"": [1, 2, 6, 4, 3, 3], ""uniform"": [0.011420548995859195, 0.029058687588549185, 0.01540507999247779], ""choice"": [1, 6, 1, 5, 5], ""shuffle"": [2, 6, 1, 7, 8, 3, 5, 0, 4, 9], ""sample_small"": [8, 4], ""sample_7"": [19, 11, 6, 2, 1, 10, 3], ""sample_big"": [791, 296, 81, 875, 238], ""choices"": [8, 3, 4, 8], ""after"": 0.1626540971560848}, ""0"": {""random"": [0.8444218515250481, 0.7579544029403025, 0.420571580830845, 0.25891675029296335, 0.5112747213686085], ""bits"": [0, 235, 3366389305, 3564191072, 8791662011684601223, 1179838910780978050100758126511], ""randrange"": [3, 8, 2, 4, 2, 1, 9, 4, 1290, 1432, 500, 835], ""randint"": [1, 6, 1, 6, 3, 4], ""uniform"": [0.020315900938327806, 0.015783409249423296, 0.014956327389475055], ""choice"": [3, 8, 7, 7, 8], ""shuffle"": [9, 1, 2, 6, 3, 5, 7, 8, 0, 4], ""sample_small"": [5, 3], ""sample_7"": [10, 2, 6, 7, 16, 12, 18], ""sample_big"": [822, 556, 458, 93, 82], ""choices"": [9, 8, 9, 4], ""after"": 0.3014467921202424}, ""1099511627781"": {""random"": [0.5043802970418443, 0.2686044399723282, 0.9257865475671585, 0.530959239292452, 0.8179643216302002], ""bits"": [1, 244, 3902914310, 7562016055, 3052220819794923309, 223133519591951163786224434595], ""randrange"": [3, 2, 3, 6, 1, 8, 3, 7, 313, 428, 1073, 290], ""randint"": [3, 4, 3, 2, 6, 1], ""uniform"": [0.025925346656790003, 0.02264130247714188, 0.009249890334171314], ""choice"": [7, 1, 2, 8, 3], ""shuffle"": [3, 5, 6, 1, 8, 4, 0, 7, 2, 9], ""sample_small"": [8, 4], ""sample_7"": [9, 11, 6, 8, 16, 14, 15], ""sample_big"": [815, 454, 493, 580, 825], ""choices"": [0, 1, 0, 2], ""after"": 0.5355896747618075}, ""repr"": [""0.1"", ""1.0"", ""1e+16"", ""1e-05"", ""123456789.125"", ""0.5"", ""2.5"", ""0.3333333333333333"", ""5e-324"", ""1e+22"", ""1.5e+300"", ""100.0"", ""0.0001"", ""1.2345678901234568e+16"", ""-2.675""], ""fl"": [0.1, 1.0, 1e+16, 1e-05, 123456789.125, 0.5, 2.5, 0.3333333333333333, 5e-324, 1e+22, 1.5e+300, 100.0, 0.0001, 1.2345678901234568e+16, -2.675], ""fixed"": [[0.5, 0, ""0""], [0.5, 1, ""0.5""], [0.5, 2, ""0.50""], [0.5, 3, ""0.500""], [1.5, 0, ""2""], [1.5, 1, ""1.5""], [1.5, 2, ""1.50""], [1.5, 3, ""1.500""], [2.5, 0, ""2""], [2.5, 1, ""2.5""], [2.5, 2, ""2.50""], [2.5, 3, ""2.500""], [2.675, 0, ""3""], [2.675, 1, ""2.7""], [2.675, 2, ""2.67""], [2.675, 3, ""2.675""], [0.125, 0, ""0""], [0.125, 1, ""0.1""], [0.125, 2, ""0.12""], [0.125, 3, ""0.125""], [-0.0, 0, ""-0""], [-0.0, 1, ""-0.0""], [-0.0, 2, ""-0.00""], [-0.0, 3, ""-0.000""], [1e-07, 0, ""0""], [1e-07, 1, ""0.0""], [1e-07, 2, ""0.00""], [1e-07, 3, ""0.000""], [123.456, 0, ""123""], [123.456, 1, ""123.5""], [123.456, 2, ""123.46""], [123.456, 3, ""123.456""], [0.3333333333333333, 0, ""0""], [0.3333333333333333, 1, ""0.3""], [0.3333333333333333, 2, ""0.33""], [0.3333333333333333, 3, ""0.333""], [-1.005, 0, ""-1""], [-1.005, 1, ""-1.0""], [-1.005, 2, ""-1.00""], [-1.005, 3, ""-1.005""]], ""round"": [[0.5, 0, ""0.0""], [0.5, 1, ""0.5""], [0.5, 2, ""0.5""], [0.5, 3, ""0.5""], [1.5, 0, ""2.0""], [1.5, 1, ""1.5""], [1.5, 2, ""1.5""], [1.5, 3, ""1.5""], [2.675, 0, ""3.0""], [2.675, 1, ""2.7""], [2.675, 2, ""2.67""], [2.675, 3, ""2.675""], [0.1234567, 0, ""0.0""], [0.1234567, 1, ""0.1""], [0.1234567, 2, ""0.12""], [0.1234567, 3, ""0.123""], [0.0005, 0, ""0.0""], [0.0005, 1, ""0.0""], [0.0005, 2, ""0.0""], [0.0005, 3, ""0.001""], [0.7655, 0, ""1.0""], [0.7655, 1, ""0.8""], [0.7655, 2, ""0.77""], [0.7655, 3, ""0.765""], [0.3333333333333333, 0, ""0.0""], [0.3333333333333333, 1, ""0.3""], [0.3333333333333333, 2, ""0.33""], [0.3333333333333333, 3, ""0.333""]]}");

        public static IEnumerable<object[]> Seeds() { foreach (var s in new[] { "1", "7", "42", "0", "1099511627781" }) yield return new object[] { s }; }

        [Theory, MemberData(nameof(Seeds))]
        public void SequencesMatchCPython(string seed)
        {
            var d = (JObject)Rec[seed];
            var r = new PyRandom(long.Parse(seed));
            var ints = (string k) => d[k].Select(t => (long)t).ToList();
            Assert.Equal(d["random"].Select(t => (double)t).ToList(), Enumerable.Range(0, 5).Select(_ => r.Random()).ToList());
            var bits = d["bits"].Select(t => System.Numerics.BigInteger.Parse(t.ToString())).ToList();
            Assert.Equal(bits, new[] { 1, 8, 32, 33, 64, 100 }.Select(k => r.GetRandBitsBig(k)).ToList());
            var rr = ints("randrange");
            Assert.Equal(rr.Take(8).ToList(), Enumerable.Range(0, 8).Select(_ => r.RandRange(10)).ToList());
            Assert.Equal(rr.Skip(8).ToList(), Enumerable.Range(0, 4).Select(_ => r.RandRange(200, 1500)).ToList());
            Assert.Equal(ints("randint"), Enumerable.Range(0, 6).Select(_ => r.RandInt(1, 6)).ToList());
            Assert.Equal(d["uniform"].Select(t => (double)t).ToList(), Enumerable.Range(0, 3).Select(_ => r.Uniform(0.008, 0.03)).ToList());
            var ten = Enumerable.Range(0, 10).Select(i => (long)i).ToList();
            Assert.Equal(ints("choice"), Enumerable.Range(0, 5).Select(_ => r.Choice(ten)).ToList());
            var l = Enumerable.Range(0, 10).Select(i => (long)i).ToList(); r.Shuffle(l);
            Assert.Equal(ints("shuffle"), l);
            Assert.Equal(ints("sample_small"), r.Sample(Enumerable.Range(0, 10).Select(i => (long)i).ToList(), 2));
            Assert.Equal(ints("sample_7"), r.Sample(Enumerable.Range(0, 20).Select(i => (long)i).ToList(), 7));
            Assert.Equal(ints("sample_big"), r.Sample(Enumerable.Range(0, 1000).Select(i => (long)i).ToList(), 5));
            Assert.Equal(ints("choices"), r.Choices(ten, 4));
            Assert.Equal((double)d["after"], r.Random());
        }

        [Fact]
        public void ReprMatchesPython()
        {
            var fl = Rec["fl"].Select(t => (double)t).ToList();
            var want = Rec["repr"].Select(t => (string)t).ToList();
            for (int i = 0; i < fl.Count; i++) Assert.Equal(want[i], PyFmt.Repr(fl[i]));
        }

        [Fact]
        public void FixedMatchesPython()
        {
            foreach (var t in Rec["fixed"]) Assert.Equal((string)t[2], PyFmt.Fixed((double)t[0], (int)t[1]));
        }

        [Fact]
        public void RoundMatchesPython()
        {
            foreach (var t in Rec["round"]) Assert.Equal((string)t[2], PyFmt.Repr(PyFmt.Round((double)t[0], (int)t[1])));
        }

        [Fact]
        public void IntSemantics()
        {
            Assert.Equal(-4, PyFmt.FloorDiv(-7, 2)); Assert.Equal(1, PyFmt.Mod(-7, 2)); Assert.Equal(-1, PyFmt.Mod(7, -2));
            Assert.Equal(2, PyFmt.RoundInt(2.5)); Assert.Equal(4, PyFmt.RoundInt(3.5)); Assert.Equal(-2, PyFmt.RoundInt(-2.5));
        }
    }
}
