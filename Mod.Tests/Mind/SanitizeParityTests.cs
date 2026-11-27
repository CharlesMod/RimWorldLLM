using System;
using System.Collections.Generic;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>Outputs recorded from CPython's sanitize_line / sanitize_prose / leaks_secret over a spread of inputs (unicode whitespace, quotes, bullets, labels...).</summary>
    [Collection("AdultState")]
    public class SanitizeParityTests : IDisposable
    {
        public SanitizeParityTests() { Adult.SetEnabled(false); }
        public void Dispose() { Adult.SetEnabled(false); }

        static HashSet<string> S(params string[] a) { return new HashSet<string>(a); }
        public static IEnumerable<object[]> Lines => SanitizeData.Lines;
        public static IEnumerable<object[]> Prose => SanitizeData.Prose;
        public static IEnumerable<object[]> Secrets => SanitizeData.Secrets;

        [Theory, MemberData(nameof(Lines))]
        public void SanitizeLineMatchesPython(string raw, string opts, string text, string reason)
        {
            var o = opts.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var r = Sanitize.SanitizeLine(raw, S("Ana"), S("Bram", "Moth"), S("Ana", "Bram", "Cy", "Moth", "Tuk"), S("Kitchen"),
                truncated: Array.IndexOf(o, "truncated") >= 0, narrationGuard: Array.IndexOf(o, "narration_guard") >= 0,
                involvesMinor: Array.IndexOf(o, "involves_minor") >= 0, intentLabels: Array.IndexOf(o, "intent_labels") >= 0 ? new[] { "kind", "none" } : null);
            Assert.Equal(text, r.Text);
            Assert.Equal(reason, r.Reason);
        }

        [Theory, MemberData(nameof(Prose))]
        public void SanitizeProseMatchesPython(string raw, string text, string reason)
        {
            var r = Sanitize.SanitizeProse(raw, S("Bram"), S("Ana", "Bram", "Cy"), S(), speakerNames: S("Ana"), minWords: 3);
            Assert.Equal(text, r.Text);
            Assert.Equal(reason, r.Reason);
        }

        [Theory, MemberData(nameof(Secrets))]
        public void LeaksSecretMatchesPython(string text, string[] secrets, bool want) { Assert.Equal(want, Sanitize.LeaksSecret(text, secrets)); }
    }
}
