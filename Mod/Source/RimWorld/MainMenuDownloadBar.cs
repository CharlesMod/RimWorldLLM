using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// The download bar on RimWorld's main menu (0-100, one sample per second, no ETA). The menu drives its own status poll,
    /// since nothing else polls before a game is loaded. The engine itself is launched or attached at startup
    /// (BridgeStartup -> EngineManager.StartAtStartup), so a first-run download progresses and shows from here.
    /// In game the existing status lines stay; the bar is not drawn there.
    /// </summary>
    public static class MainMenuDownloadBar
    {
        public const float PanelW = 300f, PanelH = 46f, BottomGap = 14f;
        static readonly DownloadBar _bar = new DownloadBar();
        static DownloadBarView _view = DownloadBarView.Hidden;

        /// <summary>Panel rect: bottom centre of the screen, which vanilla leaves free (menu column is mid-screen, the
        /// expansion icons bottom left, language/credits bottom right, version and mods top left, news top right).</summary>
        public static Rect PanelRect(float screenW, float screenH)
        {
            return new Rect((screenW - PanelW) / 2f, screenH - PanelH - BottomGap, PanelW, PanelH);
        }

        public static void Frame()
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null || !EngineManager.LocalSource) return;
            long now = Environment.TickCount64;
            if (_bar.ShouldSample(now))
            {
                EngineManager.SampleNow();
                _view = _bar.Update(EngineManager.Status, now);   // the status the previous sample left; 1 Hz granularity
            }
            if (!_view.Visible) return;
            Draw(PanelRect(UI.screenWidth, UI.screenHeight), _view);
        }

        static string Label(DownloadBarView v)
        {
            return v.LabelArg != null ? UiUtil.T(v.LabelKey, v.LabelArg) : UiUtil.T(v.LabelKey);
        }

        static void Draw(Rect r, DownloadBarView v)
        {
            Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.55f));
            Widgets.DrawBox(r);
            var inner = r.ContractedBy(6f);
            var oldFont = Text.Font; var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            var label = new Rect(inner.x, inner.y, inner.width, 20f);
            if (v.Mode == DownloadBarView.Kind.Error && !string.IsNullOrEmpty(v.Text))
            {
                Widgets.Label(new Rect(inner.x, inner.y, inner.width, inner.height), v.Text);
            }
            else
            {
                Widgets.Label(label, Label(v));
                if (v.Mode == DownloadBarView.Kind.Bar)
                {
                    var bar = new Rect(inner.x, inner.y + 22f, inner.width, 14f);
                    Widgets.FillableBar(bar, v.Percent / 100f);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(bar, v.Percent + "%");
                }
            }
            Text.Font = oldFont; Text.Anchor = oldAnchor;
        }
    }

    /// <summary>Draws the bar after the main menu's own controls.</summary>
    [HarmonyPatch]
    public static class Patch_MainMenuDownloadBar
    {
        static MethodBase TargetMethod() { return AccessTools.Method(typeof(MainMenuDrawer), "DoMainMenuControls"); }
        static bool Prepare() { return TargetMethod() != null; }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try { MainMenuDownloadBar.Frame(); }
            catch (Exception e) { Log.ErrorOnce("[RimWorldLLM] download bar failed: " + e.Message, 7340302); }
        }
    }
}
