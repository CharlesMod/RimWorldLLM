using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class FinalTextTests
    {
        [Theory]
        [InlineData("", null)]
        [InlineData("{", null)]
        [InlineData("{\"", null)]
        [InlineData("{\"fin", null)]
        [InlineData("{\"final", null)]
        [InlineData("{\"final\"", null)]
        [InlineData("{\"final\":", null)]
        [InlineData("{\"final\": ", null)]
        [InlineData("{\"final\": \"", "")]
        [InlineData("{\"final\": \"Hel", "Hel")]
        [InlineData("{\"final\": \"Hello\"", "Hello")]
        [InlineData("{\"final\": \"Hello\"}", "Hello")]
        [InlineData("  {  \"final\"  :  \"Hi\"  }", "Hi")]
        [InlineData("{\"final\":\"a\\nb\"}", "a\nb")]
        [InlineData("{\"final\":\"a\\\"b\\\\c\\/d\"}", "a\"b\\c/d")]
        [InlineData("{\"final\":\"t\\tr\\rb\\bf\\f\"}", "t\tr\rb\bf\f")]
        [InlineData("{\"final\":\"say \\\"hi", "say \"hi")]
        [InlineData("{\"final\":\"abc\\", "abc")]                       // lone backslash at the end
        [InlineData("{\"final\":\"abc\\u", "abc")]                      // partial \u
        [InlineData("{\"final\":\"abc\\u00", "abc")]
        [InlineData("{\"final\":\"abc\\u00e", "abc")]
        [InlineData("{\"final\":\"abc\\u00e9", "abcé")]
        [InlineData("{\"final\":\"abc\\u00E9x\"}", "abcéx")]
        [InlineData("{\"final\":\"\\ud83d", "")]                        // lone high surrogate at the end is held back
        [InlineData("{\"final\":\"\\ud83d\\", "")]
        [InlineData("{\"final\":\"\\ud83d\\ude0", "")]
        [InlineData("{\"final\":\"\\ud83d\\ude00", "😀")]
        [InlineData("{\"final\":\"x\\ud83d\\ude00y\"}", "x😀y")]
        [InlineData("{\"final\":\"raw é 😀 ok\"}", "raw é 😀 ok")]
        [InlineData("{\"tool\":\"look\",\"final\":\"after\"}", "after")]
        [InlineData("{\"a\":{\"final\":\"nested\"},\"final\":\"top\"}", "top")]
        [InlineData("{\"a\":{\"final\":\"nested\"}}", null)]
        [InlineData("{\"a\":[1,2,{\"x\":\"}\"}],\"final\":\"top\"}", "top")]
        [InlineData("{\"a\":\"has \\\" quote, and final\",\"final\":\"z", "z")]
        [InlineData("{\"n\":12,\"final\":\"z\"}", "z")]
        [InlineData("{\"n\":12", null)]
        [InlineData("{\"n\":tru", null)]
        [InlineData("{\"n\":true,\"final\":\"z\"}", "z")]
        [InlineData("{\"n\":null , \"final\" : \"z\"}", "z")]
        [InlineData("{\"a\":{\"b\":\"un", null)]
        [InlineData("{\"final\":null}", null)]
        [InlineData("{\"final\":12}", null)]
        [InlineData("[\"final\"]", null)]
        [InlineData("no json", null)]
        [InlineData("{\"final\":\"first\",\"final\":\"second\"}", "first")]
        [InlineData("{\"final\":\"\"}", "")]
        public void Decodes(string raw, string expected) => Assert.Equal(expected, FinalText.Decode(raw));

        [Fact]
        public void CompleteFlag()
        {
            Assert.Equal("ab", FinalText.Decode("{\"final\":\"ab\"", out bool c)); Assert.True(c);
            Assert.Equal("ab", FinalText.Decode("{\"final\":\"ab", out c)); Assert.False(c);
            Assert.Null(FinalText.Decode("{\"nope\":1}", out c)); Assert.False(c);
        }

        [Fact]
        public void EveryPrefixIsAPrefixOfTheNextAndEndsEqual()
        {
            string raw = "{\"thought\":\"x \\\"y\\\" {z}\",\"final\":\"Olá \\u00e9 \\ud83d\\ude00 line\\nnext \\\\ end\"}";
            string full = FinalText.Decode(raw);
            Assert.Equal("Olá é 😀 line\nnext \\ end", full);
            string prev = "";
            for (int i = 1; i <= raw.Length; i++)
            {
                string v = FinalText.Decode(raw.Substring(0, i)) ?? "";
                Assert.StartsWith(prev, v);                 // never shrinks, never rewrites
                Assert.StartsWith(v, full);
                Assert.True(v.Length == 0 || !char.IsHighSurrogate(v[v.Length - 1]) || v == full);
                prev = v;
            }
            Assert.Equal(full, prev);
        }
    }
}
