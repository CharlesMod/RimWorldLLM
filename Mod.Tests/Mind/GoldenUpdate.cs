// Deliberate golden updates: RWL_UPDATE_GOLDENS=1 dotnet test Mod.Tests --filter <golden tests> rewrites the named fields of a golden in
// Mod.Tests/Mind/Data (same layout as the Python capture: json.dumps(indent=1, ensure_ascii=False)) instead of asserting them. Run it only when a
// change to the prompt or the tools is meant, then rebuild, run the tests normally and read the data diff before committing.
using System;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Tests.Mind
{
    public static class GoldenUpdate
    {
        public static bool On => Environment.GetEnvironmentVariable("RWL_UPDATE_GOLDENS") == "1";
        private static readonly object Gate = new object();

        private static string DataDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here), "Data");

        /// <summary>Loads Data/<paramref name="file"/>, applies <paramref name="edit"/> and writes it back in the Python layout.</summary>
        public static void Patch(string file, Action<JObject> edit)
        {
            lock (Gate)
            {
                string path = Path.Combine(DataDir(), file);
                string old = File.ReadAllText(path);
                JObject o;
                using (var r = new JsonTextReader(new StringReader(old)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
                    o = JObject.Load(r);
                edit(o);
                var sw = new StringWriter { NewLine = "\n" };
                using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 1, IndentChar = ' ' })
                    o.WriteTo(w);
                string text = sw.ToString().Replace("\r\n", "\n") + (old.EndsWith("\n") ? "\n" : "");
                File.WriteAllText(path, text);
            }
        }
    }
}
