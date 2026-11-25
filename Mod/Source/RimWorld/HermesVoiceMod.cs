using UnityEngine;
using Verse;
using HarmonyLib;

namespace HermesVoice
{
    public class HermesVoiceMod : Mod
    {
        public static HermesVoiceMod Instance { get; private set; }
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

        public static HermesVoiceSettings Settings => Instance?.GetSettings<HermesVoiceSettings>();

        public HermesVoiceMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Log.Message("[HermesVoice] Mod loaded — applying Harmony patches");
            new Harmony("charless.hermesvoice").PatchAll(typeof(HermesVoiceMod).Assembly);
        }

        public override string SettingsCategory() => "HermesVoice";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var s = Settings;
            if (s == null) return;
            SettingsWindow.Draw(inRect, s);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            HermesBridge.RefreshLlm();   // bridge.json carries the LLM endpoint
        }
    }
}
