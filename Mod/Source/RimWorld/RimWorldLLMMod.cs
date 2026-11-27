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
            Log.Message("[RimWorldLLM] Mod loaded — applying Harmony patches");
            new Harmony("charleszard.rimworldllm").PatchAll(typeof(RimWorldLLMMod).Assembly);
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
