using UnityEngine;
using Verse;
using HarmonyLib;

namespace RimWorldLLM
{
    public class RimWorldLLMMod : Mod
    {
        public static RimWorldLLMMod Instance { get; private set; }
        const string FallbackVersion = "2.0.0";
        /// <summary>Mod version, read from About.xml (modVersion) when available.</summary>
        public static string Version
        {
            get
            {
                try
                {
                    string v = Instance?.Content?.ModMetaData?.ModVersion;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                catch { }
                return FallbackVersion;
            }
        }

        public static RimWorldLLMSettings Settings => Instance?.GetSettings<RimWorldLLMSettings>();

        public RimWorldLLMMod(ModContentPack content) : base(content)
        {
            Instance = this;
            CarryOverOldSettings();
            Log.Message("[RimWorldLLM] Mod loaded — applying Harmony patches");
            new Harmony("charleszard.rimworldllm").PatchAll(typeof(RimWorldLLMMod).Assembly);
        }

        // The mod was called HermesVoice before 2.1.0; its settings file had that name. Read once before GetSettings.
        void CarryOverOldSettings()
        {
            try
            {
                // Verse: Config/Mod_<folder>_<Mod class>.xml (LoadedModManager.GetSettingsFilename, not public)
                string file = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename("Mod_" + Content.FolderName + "_" + GetType().Name + ".xml"));
                var outcome = RimWorldLLM.Bridge.LegacySettings.Migrate(file, out string old);
                if (outcome == RimWorldLLM.Bridge.LegacySettings.Outcome.Copied)
                    Log.Message("[RimWorldLLM] Carried over settings from " + old + " (left in place)");
                else if (outcome == RimWorldLLM.Bridge.LegacySettings.Outcome.Failed)
                    Log.Warning("[RimWorldLLM] Could not carry over the old HermesVoice settings; starting with defaults");
            }
            catch (System.Exception e) { Log.Warning("[RimWorldLLM] Settings carry-over skipped: " + e.Message); }
        }

        public override string SettingsCategory() => "RimWorldLLM";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var s = Settings;
            if (s == null) return;
            SettingsWindow.Draw(inRect, s);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            EngineManager.SettingsChanged();   // pushes the source to the engine, then rewrites bridge.json (it carries the LLM endpoint)
        }
    }
}
