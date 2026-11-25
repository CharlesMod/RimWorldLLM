using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    /// <summary>Every UI string goes through a key in Languages/English/Keyed/HermesVoice.xml; keys used in code must exist.</summary>
    public class KeyedStringsTests
    {
        static string Root([CallerFilePath] string here = "") { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "Mod")); }

        static HashSet<string> Keys()
        {
            var doc = XDocument.Load(Path.Combine(Root(), "Languages", "English", "Keyed", "HermesVoice.xml"));
            return new HashSet<string>(doc.Root.Elements().Select(e => e.Name.LocalName));
        }

        static IEnumerable<string> Sources() { return Directory.GetFiles(Path.Combine(Root(), "Source", "RimWorld"), "*.cs"); }

        [Fact]
        public void EveryKeyUsedInCodeExists()
        {
            var keys = Keys();
            var rx = new Regex("(?:\\bT\\(|labelKey\\s*=\\s*)\"(HV_[A-Za-z0-9_]*[A-Za-z0-9])\"");
            var missing = new List<string>();
            foreach (var f in Sources())
                foreach (Match m in rx.Matches(File.ReadAllText(f)))
                    if (!keys.Contains(m.Groups[1].Value)) missing.Add(Path.GetFileName(f) + ": " + m.Groups[1].Value);
            Assert.Empty(missing);
        }

        [Fact]
        public void DynamicKeysExist()
        {
            var keys = Keys();
            var wanted = new List<string>();
            foreach (ProbeCode c in Enum.GetValues(typeof(ProbeCode))) wanted.Add("HV_Probe_" + c);
            foreach (var s in new[] { "Connection", "Talk", "Effects", "Events", "Bubbles", "Advanced" }) wanted.Add("HV_Section_" + s);
            foreach (var s in new[] { "off", "light", "normal" }) wanted.Add("HV_Effects_" + s);
            foreach (var s in new[] { "effects_off", "no_intent", "no_effect", "no_listener", "not_negative", "no_about", "pair_cap", "stack_full", "vanilla_interaction" }) wanted.Add("HV_Refused_" + s);
            foreach (var s in Bridge.EntryKinds.Wording) wanted.Add("HV_LogEffect_" + s);
            var gone = wanted.Where(k => !keys.Contains(k)).ToList();
            Assert.True(gone.Count == 0, string.Join(", ", gone));
        }

        [Fact]
        public void NoUnusedKeysAndNoDuplicates()
        {
            var doc = XDocument.Load(Path.Combine(Root(), "Languages", "English", "Keyed", "HermesVoice.xml"));
            var names = doc.Root.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            string all = string.Join("\n", Sources().Select(File.ReadAllText));
            var dynamicPrefixes = new[] { "HV_Probe_", "HV_Section_", "HV_Effects_", "HV_Refused_", "HV_LogEffect_" };
            var unused = names.Where(n => !dynamicPrefixes.Any(p => n.StartsWith(p)) && all.IndexOf("\"" + n + "\"", StringComparison.Ordinal) < 0).ToList();
            Assert.Empty(unused);
        }
    }
}
