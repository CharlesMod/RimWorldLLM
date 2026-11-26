using System;
using System.Collections.Generic;
using HermesVoice.Bridge;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Local model setup, the in-game twin of the engine's setup page: hardware, the recommended model with Install,
    /// every model with its fit verdict, progress with speed and ETA, Cancel, import a file, and a link to the browser page.
    /// All data comes from <see cref="EngineManager"/>, which fetches off the game thread.
    /// </summary>
    public class Window_EngineSetup : Window
    {
        Vector2 _scroll;
        float _viewHeight = 700f;
        string _importPath = "";
        // Set by the first-run "Set up": install the recommended model as soon as the plan is known (one click).
        bool _installRecommended;

        public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(UI.screenWidth - 40f, 780f), Mathf.Min(UI.screenHeight - 80f, 700f)); } }

        public Window_EngineSetup() : this(false) { }

        public Window_EngineSetup(bool installRecommended)
        {
            _installRecommended = installRecommended;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            optionalTitle = UiUtil.T("HV_Setup_Title");
            EngineManager.PollNow();
            if (EngineManager.Client == null && !EngineManager.Launching) EngineManager.LaunchAsync();
        }

        static void Head(Listing_Standard l, string text)
        {
            l.Gap(6f);
            Text.Font = GameFont.Medium;
            l.Label(text);
            Text.Font = GameFont.Small;
        }

        static string Mib(int mib) { return mib >= 1024 ? (mib / 1024f).ToString("0.#") + " GB" : mib + " MB"; }
        public static string Bytes(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
            return (b / 1024L) + " KB";
        }
        static string Eta(double s)
        {
            if (s <= 0) return "-";
            int t = (int)s;
            if (t < 90) return t + " s";
            if (t < 5400) return (t / 60) + " min";
            return (t / 3600) + " h " + ((t % 3600) / 60) + " min";
        }

        public override void DoWindowContents(Rect inRect)
        {
            EngineManager.Touch();
            var view = new Rect(0f, 0f, inRect.width - 18f, _viewHeight);
            Widgets.BeginScrollView(inRect, ref _scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            l.Begin(view);

            // Phase light + status line.
            var row = l.GetRect(26f);
            var light = EngineManager.CurrentLight();
            Color c = EngineManager.ColorOf(light);
            Widgets.DrawBoxSolid(new Rect(row.x, row.y + 5f, 16f, 16f), c);
            GUI.color = c;
            Widgets.Label(new Rect(row.x + 24f, row.y + 2f, row.width - 24f, 24f), EngineManager.StatusLine());
            GUI.color = Color.white;

            var st = EngineManager.Status;
            string fbLine = EngineManager.FallbackLine();
            if (fbLine != null)
            {
                GUI.color = EngineManager.ColorOf(EngineManager.Light.Amber);
                l.Label(fbLine);
                GUI.color = Color.white;
            }
            if (st != null && st.Agent != null && !string.IsNullOrEmpty(st.Agent.Phase))
            {
                string am = string.IsNullOrEmpty(st.Agent.Message) ? "" : " - " + st.Agent.Message;
                Dim(l, UiUtil.T("HV_Setup_Agent", st.Agent.Phase, st.Agent.Restarts) + am);
            }
            string err = EngineManager.ActionError;
            if (!string.IsNullOrEmpty(err)) { GUI.color = EngineManager.ColorOf(EngineManager.Light.Red); l.Label(UiUtil.T("HV_Setup_ActionError", err)); GUI.color = Color.white; }

            if (EngineManager.Client == null)
            {
                l.Gap(6f);
                if (!EngineManager.Launching && l.ButtonText(UiUtil.T("HV_Setup_Retry"))) EngineManager.LaunchAsync();
                l.End();
                _viewHeight = l.CurHeight + 12f;
                Widgets.EndScrollView();
                return;
            }

            if (_installRecommended && EngineManager.Plan != null && st != null)
            {
                _installRecommended = false;
                string id = SetupChoice.AutoInstallTarget(EngineManager.Plan, st);
                if (id != null) EngineManager.Install(id);
            }

            DrawHardware(l);
            DrawProgress(l, st);
            DrawModels(l, st);

            // Import.
            l.GapLine();
            Head(l, UiUtil.T("HV_Setup_ImportTitle"));
            Dim(l, UiUtil.T("HV_Setup_ImportHelp"));
            var irow = l.GetRect(28f);
            _importPath = Widgets.TextField(new Rect(irow.x, irow.y, irow.width - 130f, 26f), _importPath ?? "");
            bool busy = st != null && EnginePhase.IsBusy(st.Phase);
            if (Widgets.ButtonText(new Rect(irow.xMax - 120f, irow.y, 120f, 26f), UiUtil.T("HV_Setup_ImportButton"), true, true, !busy && !string.IsNullOrWhiteSpace(_importPath)))
                EngineManager.Import(_importPath.Trim());

            // Footer.
            l.GapLine();
            if (st != null && st.Shape != null && st.Shape.Slots > 0)
                l.Label(UiUtil.T("HV_Setup_Shape", st.Shape.Slots, EngineManager.Ctx(st.Shape.CtxPerSlot), st.Shape.NCpuMoe, st.Shape.KvType ?? "", Mib(st.Shape.VramMib)));
            if (l.ButtonText(UiUtil.T("HV_Setup_OpenPage"))) Application.OpenURL(EngineManager.SetupUrl);
            if (st != null && st.Errors != null && st.Errors.Count > 0)
            {
                GUI.color = EngineManager.ColorOf(EngineManager.Light.Red);
                foreach (var e in st.Errors) l.Label(e);
                GUI.color = Color.white;
            }

            l.End();
            _viewHeight = l.CurHeight + 12f;
            Widgets.EndScrollView();
        }

        static void Dim(Listing_Standard l, string text)
        {
            GUI.color = Color.gray;
            l.Label(text);
            GUI.color = Color.white;
        }

        void DrawHardware(Listing_Standard l)
        {
            Head(l, UiUtil.T("HV_Setup_HardwareTitle"));
            var hw = EngineManager.Hardware;
            if (hw == null) { Dim(l, UiUtil.T("HV_Setup_Probing")); return; }
            if (hw.Gpus != null && hw.Gpus.Count > 0)
                foreach (var g in hw.Gpus)
                    l.Label(UiUtil.T("HV_Setup_Gpu", g.Name, Mib(g.VramTotalMiB), Mib(Math.Max(0, g.VramTotalMiB - g.VramUsedMiB))));
            else l.Label(UiUtil.T("HV_Setup_NoGpu"));
            l.Label(UiUtil.T("HV_Setup_Ram", Mib(hw.RamTotalMiB), Mib(hw.RamAvailMiB)));
            l.Label(UiUtil.T("HV_Setup_Disk", Mib(hw.DiskFreeMiB)));
            if (l.ButtonText(UiUtil.T("HV_Setup_Reprobe"))) EngineManager.RefreshHardware();
        }

        void DrawProgress(Listing_Standard l, EngineStatus st)
        {
            if (st == null || !EnginePhase.IsBusy(st.Phase)) return;
            l.Gap(6f);
            var p = st.Progress;
            var bar = l.GetRect(24f);
            float frac = p != null ? (float)p.Fraction : 0f;
            Widgets.FillableBar(new Rect(bar.x, bar.y, bar.width - 110f, bar.height), frac);
            if (p != null && p.TotalBytes > 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(bar.x, bar.y, bar.width - 110f, bar.height),
                    Bytes(p.DoneBytes) + " / " + Bytes(p.TotalBytes) + "   " + UiUtil.T("HV_Setup_Speed", Bytes((long)p.RateBps)) + "   " + UiUtil.T("HV_Setup_Eta", Eta(p.EtaSeconds)));
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (Widgets.ButtonText(new Rect(bar.xMax - 100f, bar.y, 100f, 24f), UiUtil.T("HV_Setup_Cancel"))) EngineManager.Cancel();
            if (p != null && !string.IsNullOrEmpty(p.Step)) Dim(l, UiUtil.T("HV_Setup_Step", p.Step));
        }

        void DrawModels(Listing_Standard l, EngineStatus st)
        {
            var plan = EngineManager.Plan;
            Head(l, UiUtil.T("HV_Setup_ModelsTitle"));
            if (plan == null || plan.Models == null) { Dim(l, UiUtil.T("HV_Setup_Probing")); return; }
            bool busy = st != null && EnginePhase.IsBusy(st.Phase);
            var models = plan.Models;

            EnginePlanModel rec = models.Find(m => m.Id == plan.Recommended);
            if (rec != null)
            {
                Text.Font = GameFont.Medium;
                l.Label(UiUtil.T("HV_Setup_Recommended"));
                Text.Font = GameFont.Small;
                ModelRow(l, rec, busy, st, true);
                l.GapLine();
            }
            foreach (var m in models)
                if (m != rec) ModelRow(l, m, busy, st, false);
        }

        void ModelRow(Listing_Standard l, EnginePlanModel m, bool busy, EngineStatus st, bool big)
        {
            var row = l.GetRect((big ? 54f : 44f) + (m.Fallback ? 36f : 0f));
            float btnW = 110f;
            Color vc = m.Fits == "yes" ? EngineManager.ColorOf(EngineManager.Light.Green)
                     : m.Fits == "tight" ? EngineManager.ColorOf(EngineManager.Light.Amber)
                     : EngineManager.ColorOf(EngineManager.Light.Red);
            string head = (string.IsNullOrEmpty(m.Name) ? m.Id : m.Name) + "  (" + Bytes(m.Size) + ")";
            if (m.Installed) head += "  " + UiUtil.T("HV_Setup_Installed");
            if (st != null && st.Model == m.Id && st.Phase == EnginePhase.Ready) head += "  " + UiUtil.T("HV_Setup_InUse");
            Widgets.Label(new Rect(row.x, row.y, row.width - btnW - 10f, 22f), head);
            GUI.color = vc;
            string verdict = UiUtil.T(m.Fits == "yes" ? "HV_Setup_FitYes" : m.Fits == "tight" ? "HV_Setup_FitTight" : "HV_Setup_FitNo");
            string why = string.IsNullOrEmpty(m.Why) ? "" : " - " + m.Why;
            string est = m.EstVramMib > 0 ? "  " + UiUtil.T("HV_Setup_Est", Mib(m.EstVramMib), Mib(m.EstRamMib)) : "";
            Widgets.Label(new Rect(row.x, row.y + 20f, row.width - btnW - 10f, big ? 34f : 24f), verdict + why + est);
            GUI.color = Color.white;
            if (m.Fallback)
            {
                GUI.color = EngineManager.ColorOf(EngineManager.Light.Amber);
                Widgets.Label(new Rect(row.x, row.y + (big ? 54f : 44f), row.width - btnW - 10f, 36f), UiUtil.T("HV_Fallback_RowWarning", EngineManager.FallbackNote(m.FallbackNote)));
                Widgets.Label(new Rect(row.xMax - btnW, row.y + 34f, btnW, 22f), UiUtil.T("HV_Fallback_Badge"));
                GUI.color = Color.white;
            }
            var btn = new Rect(row.xMax - btnW, row.y + 4f, btnW, 28f);
            if (m.ImportOnly && !m.Installed) { GUI.color = Color.gray; Widgets.Label(btn, UiUtil.T("HV_Setup_ImportOnly")); GUI.color = Color.white; }
            else if (Widgets.ButtonText(btn, UiUtil.T(m.Installed ? "HV_Setup_Use" : "HV_Setup_Install"), true, true, !busy && m.Fits != "no"))
            {
                if (!m.Fallback) EngineManager.Install(m.Id);
                else
                {
                    string id = m.Id;
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("HV_Fallback_ConfirmText", EngineManager.FallbackNote(m.FallbackNote)), () => EngineManager.Install(id)));
                }
            }
        }
    }
}
