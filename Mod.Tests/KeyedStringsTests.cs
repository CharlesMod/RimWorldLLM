using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>Every UI string goes through a key in Languages/English/Keyed/RimWorldLLM.xml; keys used in code must exist.</summary>
    public class KeyedStringsTests
    {
        static string Root([CallerFilePath] string here = "") { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", "Mod")); }

        static HashSet<string> Keys()
        {
            var doc = XDocument.Load(Path.Combine(Root(), "Languages", "English", "Keyed", "RimWorldLLM.xml"));
            return new HashSet<string>(doc.Root.Elements().Select(e => e.Name.LocalName));
        }

        static IEnumerable<string> Sources() { return Directory.GetFiles(Path.Combine(Root(), "Source", "RimWorld"), "*.cs"); }

        [Fact]
        public void EveryKeyUsedInCodeExists()
        {
            var keys = Keys();
            var rx = new Regex("(?:\\bT\\(|labelKey\\s*=\\s*)\"(RWL_[A-Za-z0-9_]*[A-Za-z0-9])\"");
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
            foreach (ProbeCode c in Enum.GetValues(typeof(ProbeCode))) wanted.Add("RWL_Probe_" + c);
            foreach (var s in new[] { "Model", "Colonists", "Display", "Content", "Storage", "Advanced" }) wanted.Add("RWL_Section_" + s);
            foreach (var s in new[] { "off", "light", "normal" }) wanted.Add("RWL_Effects_" + s);
            foreach (var s in new[] { "effects_off", "no_intent", "no_effect", "no_listener", "not_negative", "no_about", "pair_cap", "stack_full", "vanilla_interaction" }) wanted.Add("RWL_Refused_" + s);
            foreach (var s in Bridge.EntryKinds.Wording) wanted.Add("RWL_LogEffect_" + s);
            foreach (var k in new[] { Bridge.GpuLine.KeyHostAndModel, Bridge.GpuLine.KeyModelOnly, Bridge.GpuLine.KeyShared, Bridge.GpuLine.KeyCpu }) wanted.Add(k);
            var gone = wanted.Where(k => !keys.Contains(k)).ToList();
            Assert.True(gone.Count == 0, string.Join(", ", gone));
        }

        [Fact]
        public void NoUnusedKeysAndNoDuplicates()
        {
            var doc = XDocument.Load(Path.Combine(Root(), "Languages", "English", "Keyed", "RimWorldLLM.xml"));
            var names = doc.Root.Elements().Select(e => e.Name.LocalName).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            string all = string.Join("\n", Sources().Select(File.ReadAllText));
            var dynamicPrefixes = new[] { "RWL_Probe_", "RWL_Section_", "RWL_Effects_", "RWL_Refused_", "RWL_LogEffect_", "RWL_Gpu_" };
            var unused = names.Where(n => !dynamicPrefixes.Any(p => n.StartsWith(p)) && all.IndexOf("\"" + n + "\"", StringComparison.Ordinal) < 0).ToList();
            Assert.Empty(unused);
        }
    }
}
