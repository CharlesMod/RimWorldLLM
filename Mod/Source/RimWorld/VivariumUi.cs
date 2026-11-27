using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    [StaticConstructorOnStartup]
    public static class VivariumIcons
    {
        public static readonly Texture2D Narrator = ContentFinder<Texture2D>.Get("RimWorldLLM/UI/RWL_NarratorPlays", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Watch = ContentFinder<Texture2D>.Get("RimWorldLLM/UI/RWL_WatchMode", false) ?? BaseContent.BadTex;
    }

    /// <summary>The "Narrator plays" switch (play-settings row, key) and the per-frame push of the mod settings into the Vivarium.</summary>
    public static class VivariumSwitch
    {
        static KeyBindingDef _key;

        public static void SetPlaying(bool on, bool announce)
        {
            var v = VivariumGame.State;
            if (v.Playing == on) return;
            v.Playing = on;
            if (announce) v.Chat.Add("note", UiUtil.T(on ? "RWL_Viv_NoteOn" : "RWL_Viv_NoteOff"), VivariumGame.NowTick);
        }

        public static void Toggle() { SetPlaying(!VivariumGame.State.Playing, true); }

        /// <summary>Per frame (GameComponentUpdate): the mod settings flow into the Vivarium the routes and the agent read.</summary>
        public static void PushSettings()
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null) return;
            var vs = VivariumGame.State.Settings;
            vs.MayPauseForEmergencies = s.narratorMayPauseForEmergencies;
            vs.ControlsSpeed = s.narratorControlsSpeed;
            vs.HeartbeatHours = s.stewardHeartbeatHours;
        }

        public static void OnGUIKey()
        {
            if (Event.current == null || Event.current.type != EventType.KeyDown) return;
            if (_key == null) _key = DefDatabase<KeyBindingDef>.GetNamedSilentFail("RWL_ToggleNarratorPlays");
            if (_key != null && _key.KeyDownEvent) { Toggle(); Event.current.Use(); }
        }
    }

    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_NarratorSwitch
    {
        [HarmonyPostfix]
        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null || Current.Game == null) return;
            bool playing = VivariumGame.State.Playing;
            row.ToggleableIcon(ref playing, VivariumIcons.Narrator, UiUtil.T("RWL_Viv_SwitchTip"), SoundDefOf.Mouseover_ButtonToggle);
            if (playing != VivariumGame.State.Playing) VivariumSwitch.SetPlaying(playing, true);
            bool watching = WatchCamera.Enabled;
            row.ToggleableIcon(ref watching, VivariumIcons.Watch, UiUtil.T("RWL_Watch_Tip"), SoundDefOf.Mouseover_ButtonToggle);
            if (watching != WatchCamera.Enabled) WatchCamera.SetEnabled(watching, true);
        }
    }
}
