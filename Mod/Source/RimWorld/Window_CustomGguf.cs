using System;
using HermesVoice.Bridge;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// "Use my own GGUF file": a path field with Browse (in-game file browser, no native dialog), Check (the engine reads the
    /// header and says whether it fits), Use this file, and Stop using it. The file stays where it is: it is never copied or deleted.
    /// All engine calls run in the background through <see cref="EngineManager"/>.
    /// </summary>
    public class Window_CustomGguf : Window
    {
        string _path;
        string _checkedPath;   // the path the shown check belongs to
        Vector2 _scroll;
        float _viewHeight = 500f;

        public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(UI.screenWidth - 40f, 680f), Mathf.Min(UI.screenHeight - 80f, 560f)); } }

        public Window_CustomGguf()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            optionalTitle = UiUtil.T("HV_Custom_Title");
            var s = HermesVoiceMod.Settings;
            _path = s != null ? (s.customGgufPath ?? "") : "";
            EngineManager.ClearCustomCheck();
            EngineManager.PollNow();
            if (EngineManager.Client == null && !EngineManager.Launching) EngineManager.LaunchAsync();
        }

        static Color Col(string fits) { return fits == "yes" ? EngineManager.ColorOf(EngineManager.Light.Green) : fits == "tight" ? EngineManager.ColorOf(EngineManager.Light.Amber) : EngineManager.ColorOf(EngineManager.Light.Red); }

        static string Mib(int mib) { return mib >= 1024 ? UiUtil.T("HV_Unit_GB", (mib / 1024f).ToString("0.#")) : UiUtil.T("HV_Unit_MB", mib); }

        public override void DoWindowContents(Rect inRect)
        {
            EngineManager.Touch();
            var s = HermesVoiceMod.Settings;
            var st = EngineManager.Status;
            var view = new Rect(0f, 0f, inRect.width - 18f, _viewHeight);
            Widgets.BeginScrollView(inRect, ref _scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            l.Begin(view);

            GUI.color = Color.gray;
            l.Label(UiUtil.T("HV_Custom_Note"));
            GUI.color = Color.white;
            l.Gap(6f);

            // Current custom file.
            if (EngineManager.CustomActive)
            {
                GUI.color = st.Custom.Missing ? EngineManager.ColorOf(EngineManager.Light.Red) : EngineManager.ColorOf(EngineManager.Light.Green);
                l.Label(UiUtil.T(st.Custom.Missing ? "HV_Custom_StatusMissing" : "HV_Custom_StatusUsing", st.Custom.Name ?? "", st.Custom.Path ?? ""));
                GUI.color = Color.white;
                var srow = l.GetRect(30f);
                TooltipHandler.TipRegion(srow, UiUtil.T("HV_Tip_CustomClear"));
                if (Widgets.ButtonText(new Rect(srow.x, srow.y, 200f, 28f), UiUtil.T("HV_Custom_Stop"), true, true, !EngineManager.CustomBusy)) EngineManager.ClearCustom();
                l.GapLine();
            }

            if (EngineManager.Client == null)
            {
                GUI.color = Color.gray; l.Label(UiUtil.T("HV_Custom_NoEngine")); GUI.color = Color.white;
            }

            // Path + Browse.
            l.Label(UiUtil.T("HV_Custom_PathLabel"));
            var prow = l.GetRect(28f);
            TooltipHandler.TipRegion(prow, UiUtil.T("HV_Tip_CustomPath"));
            string newPath = Widgets.TextField(new Rect(prow.x, prow.y, prow.width - 120f, 26f), _path ?? "");
            if (newPath != _path) { _path = newPath; }
            if (Widgets.ButtonText(new Rect(prow.xMax - 110f, prow.y, 110f, 26f), UiUtil.T("HV_Custom_Browse")))
            {
                Find.WindowStack.Add(new Window_FileBrowser(_path, picked => { _path = picked; if (s != null) s.customGgufPath = picked; EngineManager.ClearCustomCheck(); _checkedPath = null; }));
            }
            if (s != null) s.customGgufPath = _path ?? "";

            bool hasPath = !string.IsNullOrWhiteSpace(_path);
            var crow = l.GetRect(32f);
            TooltipHandler.TipRegion(crow, UiUtil.T("HV_Tip_CustomCheck"));
            if (Widgets.ButtonText(new Rect(crow.x, crow.y, 120f, 30f), UiUtil.T("HV_Custom_Check"), true, true, hasPath && EngineManager.Client != null && !EngineManager.CustomCheckRunning))
            { _checkedPath = _path.Trim(); EngineManager.CheckCustom(_checkedPath); }
            if (EngineManager.CustomCheckRunning) { GUI.color = Color.gray; Widgets.Label(new Rect(crow.x + 130f, crow.y + 5f, crow.width - 130f, 26f), UiUtil.T("HV_Custom_Checking")); GUI.color = Color.white; }

            string cerr = EngineManager.CustomCheckError;
            if (!string.IsNullOrEmpty(cerr)) { GUI.color = EngineManager.ColorOf(EngineManager.Light.Red); l.Label(UiUtil.T("HV_Setup_ActionError", cerr)); GUI.color = Color.white; }

            var ck = EngineManager.LastCustomCheck;
            if (ck != null)
            {
                l.Gap(4f);
                if (!ck.Ok)
                {
                    GUI.color = EngineManager.ColorOf(EngineManager.Light.Red);
                    l.Label(UiUtil.T("HV_Custom_CheckFailed", string.IsNullOrEmpty(ck.Error) ? "?" : ck.Error));
                    GUI.color = Color.white;
                }
                else
                {
                    Text.Font = GameFont.Medium; l.Label(ck.Name ?? ""); Text.Font = GameFont.Small;
                    l.Label(UiUtil.T("HV_Custom_Facts", string.IsNullOrEmpty(ck.Arch) ? "?" : ck.Arch,
                        ck.Params > 0 ? UiUtil.T("HV_Custom_Params", (ck.Params / 1e9).ToString("0.0")) : "?",
                        string.IsNullOrEmpty(ck.Quant) ? "?" : ck.Quant,
                        ck.CtxTrain > 0 ? ck.CtxTrain.ToString() : "?",
                        Window_EngineSetup.Bytes(ck.Size)));
                    GUI.color = Col(ck.Fits);
                    string verdict = UiUtil.T(ck.Fits == "yes" ? "HV_Setup_FitYes" : ck.Fits == "tight" ? "HV_Setup_FitTight" : "HV_Setup_FitNo");
                    l.Label(verdict + (string.IsNullOrEmpty(ck.Why) ? "" : " - " + ck.Why));
                    GUI.color = Color.white;
                    if (ck.EstVramMib > 0) l.Label(UiUtil.T("HV_Setup_Est", Mib(ck.EstVramMib), Mib(ck.EstRamMib)));
                    if (!ck.ChatTemplate) { GUI.color = EngineManager.ColorOf(EngineManager.Light.Amber); l.Label(UiUtil.T("HV_Custom_NoTemplate")); GUI.color = Color.white; }
                    if (ck.Estimated && !ck.Known) { GUI.color = Color.gray; l.Label(UiUtil.T("HV_Custom_Estimated")); GUI.color = Color.white; }

                    var urow = l.GetRect(32f);
                    TooltipHandler.TipRegion(urow, UiUtil.T("HV_Tip_CustomUse"));
                    bool same = _checkedPath != null && _checkedPath == (_path ?? "").Trim();
                    if (Widgets.ButtonText(new Rect(urow.x, urow.y, 200f, 30f), UiUtil.T("HV_Custom_Use"), true, true, same && !EngineManager.CustomBusy && EngineManager.Client != null))
                    {
                        EngineManager.UseCustom(ck.Path ?? _checkedPath);
                        if (s != null) s.customGgufPath = ck.Path ?? _checkedPath;
                    }
                }
            }

            string err = EngineManager.ActionError;
            if (!string.IsNullOrEmpty(err)) { GUI.color = EngineManager.ColorOf(EngineManager.Light.Red); l.Label(UiUtil.T("HV_Setup_ActionError", err)); GUI.color = Color.white; }

            l.End();
            _viewHeight = l.CurHeight + 12f;
            Widgets.EndScrollView();
        }
    }
}
