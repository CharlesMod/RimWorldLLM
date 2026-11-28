using System;

namespace RimWorldLLM.Bridge
{
    /// <summary>The keeper's code names for the model tiers. The engine sends them (status model_codename, upgrade.target_codename);
    /// this table is the fallback for an engine that does not, keyed by catalog model id.</summary>
    public static class ModelCodename
    {
        static readonly string[][] Table =
        {
            new[] { "gemma4-e2b-q4km", "Flashlight", "Gemma 4 E2B" },
            new[] { "gemma4-e4b-rp-q40", "Lantern", "Gemma 4 E4B RP" },
            new[] { "gemma4-rp-maxxed-q3kxl", "Campfire", "Gemma 4 26B RP" },
        };

        /// <summary>The code name: the engine's when it sent one, else the table's for the id, else null.</summary>
        public static string For(string modelId, string fromEngine)
        {
            if (!string.IsNullOrWhiteSpace(fromEngine)) return fromEngine.Trim();
            var row = Row(modelId);
            return row != null ? row[1] : null;
        }

        /// <summary>The table's plain model name for an id, or null.</summary>
        public static string PlainName(string modelId) { var row = Row(modelId); return row != null ? row[2] : null; }

        /// <summary>"Campfire (Gemma 4 26B RP)"; just the name when there is no code name; just the code name when there is no name.</summary>
        public static string Display(string codename, string name)
        {
            bool c = !string.IsNullOrWhiteSpace(codename), n = !string.IsNullOrWhiteSpace(name);
            if (c && n) return codename.Trim() + " (" + name.Trim() + ")";
            if (c) return codename.Trim();
            return n ? name.Trim() : "";
        }

        /// <summary>Display for a catalog id with the engine's own fields (either may be empty): codename and name fall back to the table.</summary>
        public static string DisplayFor(string modelId, string engineCodename, string engineName)
        {
            string name = !string.IsNullOrWhiteSpace(engineName) ? engineName : PlainName(modelId);
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(modelId) && Row(modelId) == null) name = modelId;
            return Display(For(modelId, engineCodename), name);
        }

        static string[] Row(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            foreach (var r in Table) if (string.Equals(r[0], id, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }
    }
}
