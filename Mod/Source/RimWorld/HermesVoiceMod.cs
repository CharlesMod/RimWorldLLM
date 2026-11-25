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

            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            list.Label("HermesVoice connects your colony to an external Hermes agent.");
            list.Label("The agent reads colony state and makes your pawns speak.");
            list.GapLine();

            bool enabledTemp = s.enabled;
            list.CheckboxLabeled("Enabled", ref enabledTemp);
            s.enabled = enabledTemp;
            list.GapLine();

            list.Label("Bridge (inbound HTTP — agent reads state, issues /say)");

            list.Label("Bridge port");
            string portBuf = s.bridgePort.ToString();
            list.TextFieldNumeric(ref s.bridgePort, ref portBuf, 1, 65535);

            list.Label("Bind address");
            s.bindAddress = Widgets.TextField(list.GetRect(22f), s.bindAddress ?? "");

            list.Label("Agent dial-out URL (mod POSTs colony events here)");
            s.agentUrl = Widgets.TextField(list.GetRect(22f), s.agentUrl ?? "");

            list.Label("Fixed shared token (blank = random per run, read from bridge.json)");
            s.sharedToken = Widgets.TextField(list.GetRect(22f), s.sharedToken ?? "");
            if (!HermesBridge.IsLoopback(s.bindAddress) && string.IsNullOrWhiteSpace(s.sharedToken))
            {
                GUI.color = Color.red;
                list.Label(HermesBridge.OpenWarning + ".");
                GUI.color = Color.white;
            }
            list.GapLine();

            bool bubblesTemp = s.showBubbles;
            list.CheckboxLabeled("Show overhead speech bubbles", ref bubblesTemp);
            s.showBubbles = bubblesTemp;

            list.Label("Bubble duration (seconds)");
            string durBuf = s.bubbleDurationSec.ToString();
            list.TextFieldNumeric(ref s.bubbleDurationSec, ref durBuf, 1, 120);
            list.GapLine();

            // --- Colony events (improve/events) ---
            list.Label("Colony events (mod POSTs these to the agent URL)");
            list.CheckboxLabeled("Push events to the agent URL (events are always kept for GET /events)", ref s.sendColonyEvents);
            {
                list.CheckboxLabeled("  Injuries", ref s.evInjury);
                list.CheckboxLabeled("  Deaths", ref s.evDeath);
                list.CheckboxLabeled("  Mental breaks", ref s.evMentalBreak);
                list.CheckboxLabeled("  Skill level-ups", ref s.evLevelUp);
                list.CheckboxLabeled("  Medical tending", ref s.evTend);
                list.CheckboxLabeled("  Social interactions", ref s.evSocial);
                list.CheckboxLabeled("  Raids", ref s.evRaid);
                list.CheckboxLabeled("  Quests", ref s.evQuest);
                list.CheckboxLabeled("  Lodgers", ref s.evLodger);
                list.CheckboxLabeled("  Prisoners", ref s.evPrisoner);
                list.CheckboxLabeled("  Funerals", ref s.evFuneral);
                list.CheckboxLabeled("  Extreme weather", ref s.evWeather);
                list.CheckboxLabeled("  Notable thoughts", ref s.evThought);
            }
            list.GapLine();

            list.Label("Effects of spoken lines: " + s.effects);
            if (list.ButtonText("Effects: " + s.effects + " (click to change)"))
                s.effects = s.effects == "normal" ? "light" : (s.effects == "light" ? "off" : "normal");
            list.Label("Talk frequency: " + s.talkFrequency.ToString("0.0"));
            s.talkFrequency = list.Slider(s.talkFrequency, 0f, 2f);
            list.GapLine();

            string status = HermesBridge.IsRunning ? $"Bridge running on port {HermesBridge.Port}" : "Bridge not running";
            list.Label(status);
            if (!HermesBridge.IsRunning && !string.IsNullOrEmpty(HermesBridge.LastError))
            {
                GUI.color = Color.red;
                list.Label("Last start error: " + HermesBridge.LastError);
                GUI.color = Color.white;
            }
            if (list.ButtonText("Restart bridge"))
            {
                HermesBridge.Start();
            }

            list.End();
        }
    }
}
