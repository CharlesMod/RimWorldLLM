using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>One dismissible prompt when the engine reports no model installed: Set up / Use my own endpoint / Later.</summary>
    public class Window_EngineFirstRun : Window
    {
        public override Vector2 InitialSize { get { return new Vector2(520f, 250f); } }

        public Window_EngineFirstRun()
        {
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            optionalTitle = UiUtil.T("HV_First_Title");
        }

        public override void DoWindowContents(Rect inRect)
        {
            var s = HermesVoiceMod.Settings;
            Widgets.Label(new Rect(0f, 0f, inRect.width, inRect.height - 44f), UiUtil.T("HV_First_Text"));
            float w = (inRect.width - 20f) / 3f;
            float y = inRect.height - 36f;
            if (Widgets.ButtonText(new Rect(0f, y, w, 34f), UiUtil.T("HV_First_SetUp")))
            {
                if (s != null) { s.engineSetupDismissed = true; HermesVoiceMod.Instance.WriteSettings(); }
                Close();
                Find.WindowStack.Add(new Window_EngineSetup(true));   // installs the recommended model: one click
            }
            if (Widgets.ButtonText(new Rect(w + 10f, y, w, 34f), UiUtil.T("HV_First_Own")))
            {
                if (s != null) { s.engineSetupDismissed = true; s.llmSource = "own"; HermesVoiceMod.Instance.WriteSettings(); EngineManager.SettingsChanged(); }
                Close();
            }
            if (Widgets.ButtonText(new Rect(2f * (w + 10f), y, w, 34f), UiUtil.T("HV_First_Later"))) Close();
        }
    }
}
