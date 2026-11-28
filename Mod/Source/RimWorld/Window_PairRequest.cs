using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// "&lt;guest&gt; wants to use this PC's engine. Code: 493 201. Allow / Deny" — the host's answer to a pairing
    /// request, matching the code on the guest's screen. Does not pause the game; Escape does not answer. After the
    /// answer it says what happened, then moves on to the next waiting request or closes.
    /// </summary>
    public class Window_PairRequest : Window
    {
        string _key;
        float _doneAt = -1f;

        public override Vector2 InitialSize { get { return new Vector2(460f, 250f); } }

        public Window_PairRequest(string key)
        {
            _key = key;
            forcePause = false;
            doCloseX = false;
            closeOnCancel = false;
            closeOnAccept = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            layer = WindowLayer.Super;
            optionalTitle = UiUtil.T("RWL_PairReq_Title");
        }

        public override void DoWindowContents(Rect inRect)
        {
            var it = EngineShareManager.Host.Book.Get(_key);
            if (it == null) { Close(); return; }
            if (!it.Live)
            {
                // say the outcome a moment, then the next request or close
                if (_doneAt < 0f) _doneAt = Time.realtimeSinceStartup;
                var next = EngineShareManager.Host.Book.Current;
                if (Time.realtimeSinceStartup - _doneAt > 2.5f)
                {
                    if (next != null) { _key = next.Key; _doneAt = -1f; } else { Close(); return; }
                }
            }
            else _doneAt = -1f;

            float y = 0f;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, y, inRect.width, 48f), UiUtil.T("RWL_PairReq_Body", it.Name));
            y += 48f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, inRect.width, 34f), UiUtil.T("RWL_PairReq_Code", PairingSession.FormatCode(it.Code)));
            Text.Font = GameFont.Small;
            y += 36f;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, y, inRect.width, 22f), UiUtil.T("RWL_PairReq_From", it.From));
            y += 22f;
            GUI.color = Color.white;

            string note = null;
            Color nc = Color.gray;
            switch (it.State)
            {
                case PairAskState.Allowed: note = UiUtil.T("RWL_PairReq_Allowed", it.Name); nc = new Color(0.5f, 0.85f, 0.5f); break;
                case PairAskState.Denied: note = UiUtil.T("RWL_PairReq_Denied", it.Name); break;
                case PairAskState.Expired: note = UiUtil.T("RWL_PairReq_Gone"); break;
                case PairAskState.Answering: note = UiUtil.T("RWL_PairReq_Sending"); break;
                default:
                    if (it.CodeChanged) { note = UiUtil.T("RWL_PairReq_Changed"); nc = new Color(1f, 0.8f, 0.4f); }
                    else if (it.AnswerFailed) { note = UiUtil.T("RWL_PairReq_Failed"); nc = new Color(1f, 0.45f, 0.4f); }
                    else note = UiUtil.T("RWL_PairReq_Compare");
                    break;
            }
            GUI.color = nc;
            Widgets.Label(new Rect(0f, y, inRect.width, 44f), note);
            GUI.color = Color.white;

            float btnH = 34f, w = (inRect.width - 12f) / 2f;
            bool can = it.State == PairAskState.Pending;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - btnH, w, btnH), UiUtil.T("RWL_PairReq_Allow"), true, true, can))
                EngineShareManager.Answer(it.Key, true);
            if (Widgets.ButtonText(new Rect(w + 12f, inRect.height - btnH, w, btnH), UiUtil.T("RWL_PairReq_Deny"), true, true, can))
                EngineShareManager.Answer(it.Key, false);
        }
    }
}
