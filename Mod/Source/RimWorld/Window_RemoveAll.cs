using HermesVoice.Bridge;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// The "Remove downloaded files" confirmation: Dialog_MessageBox cannot hold a checkbox, so this small window has the
    /// explanation, "Also delete HermesVoice's memory and logs" (on by default, with its size), and Remove / Cancel.
    /// </summary>
    public class Window_RemoveAll : Window
    {
        readonly long _downloadBytes;
        readonly long _memoryBytes;
        bool _alsoMemory = true;

        public override Vector2 InitialSize { get { return new Vector2(520f, 330f); } }

        public Window_RemoveAll(long downloadBytes)
        {
            _downloadBytes = downloadBytes;
            try { _memoryBytes = AgentDataDir.SizeOf(AgentDataDir.ResolveHere(EnginePlatform.Detect()).Path); } catch { _memoryBytes = 0; }
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            optionalTitle = UiUtil.T("HV_Storage_RemoveTitle");
        }

        public override void DoWindowContents(Rect inRect)
        {
            float btnH = 34f;
            var textRect = new Rect(0f, 0f, inRect.width, inRect.height - btnH - 52f);
            Widgets.Label(textRect, UiUtil.T("HV_Storage_RemoveConfirm", Window_EngineSetup.Bytes(_downloadBytes)));
            var cb = new Rect(0f, inRect.height - btnH - 44f, inRect.width, 28f);
            Widgets.CheckboxLabeled(cb, UiUtil.T("HV_Storage_RemoveMemory", Window_EngineSetup.Bytes(_memoryBytes)), ref _alsoMemory);
            TooltipHandler.TipRegion(cb, UiUtil.T("HV_Tip_RemoveMemory"));
            float w = (inRect.width - 12f) / 2f;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - btnH, w, btnH), UiUtil.T("HV_Storage_RemoveGo")))
            {
                EngineManager.RemoveEverything(_alsoMemory);
                Close();
            }
            if (Widgets.ButtonText(new Rect(w + 12f, inRect.height - btnH, w, btnH), "Cancel".Translate())) Close();
        }
    }
}
