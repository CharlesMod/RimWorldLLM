using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// The mod settings page, in tabs built around what a player does: Model, Colonists, Display, Content, Storage, Advanced.
    /// Every option carries a one-line tooltip. Nothing here waits on the engine: it reads <see cref="EngineManager"/>'s
    /// volatile fields and starts background calls.
    /// </summary>
    public static class SettingsWindow
    {
        const int TabModel = 0, TabColonists = 1, TabDisplay = 2, TabContent = 3, TabStorage = 4, TabAdvanced = 5;
        static readonly string[] Sections = { "Model", "Colonists", "Display", "Content", "Storage", "Advanced" };
        static int _section;
        static Vector2 _scroll;
        static float _viewHeight = 600f;
        static bool _revealToken, _revealKey, _confirmRegen;
        static string _portBuf, _speedBuf, _durBuf, _thoughtPawnBuf, _thoughtGlobalBuf, _watchPauseBuf;

        static readonly Color Green = new Color(0.4f, 0.85f, 0.4f), Amber = new Color(0.95f, 0.75f, 0.25f), Red = new Color(0.9f, 0.3f, 0.3f);

        public static void Draw(Rect inRect, RimWorldLLMSettings s)
        {
            float bw = inRect.width / Sections.Length;
            for (int i = 0; i < Sections.Length; i++)
            {
                var r = new Rect(inRect.x + i * bw, inRect.y, bw - 4f, 28f);
                var prev = GUI.color;
                if (i == _section) GUI.color = new Color(0.6f, 0.85f, 1f);
                if (Widgets.ButtonText(r, UiUtil.T("RWL_Section_" + Sections[i])))
                {
                    _section = i; _scroll = Vector2.zero;
                    if (i == TabStorage) EngineManager.RefreshStorage();
                }
                GUI.color = prev;
            }

            var outRect = new Rect(inRect.x, inRect.y + 36f, inRect.width, inRect.height - 36f);
            var view = new Rect(0f, 0f, outRect.width - 18f, _viewHeight);
            Widgets.BeginScrollView(outRect, ref _scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            l.Begin(view);
            switch (_section)
            {
                case TabModel: Model(l, s); break;
                case TabColonists: Colonists(l, s); break;
                case TabDisplay: Display(l, s); break;
                case TabContent: Content(l, s); break;
                case TabStorage: Storage(l, s); break;
                default: Advanced(l, s); break;
            }
            _viewHeight = l.CurHeight + 12f;
            l.End();
            Widgets.EndScrollView();
        }

        // ------------------------------------------------------------------------------------ row helpers (each with a tooltip)
        static void Title(Listing_Standard l, string key)
        {
            Text.Font = GameFont.Medium;
            l.Label(UiUtil.T(key));
            Text.Font = GameFont.Small;
        }

        static void Note(Listing_Standard l, string text)
        {
            GUI.color = Color.gray;
            l.Label(text);
            GUI.color = Color.white;
        }

        static string Mask(string v) { return string.IsNullOrEmpty(v) ? "" : new string('•', Math.Min(v.Length, 24)); }

        /// <summary>Tooltip over everything drawn since <paramref name="y0"/> (the listing's height before the row).</summary>
        static void Tip(Listing_Standard l, float y0, string tip)
        {
            if (string.IsNullOrEmpty(tip)) return;
            TooltipHandler.TipRegion(new Rect(0f, y0, l.ColumnWidth, Mathf.Max(4f, l.CurHeight - y0)), tip);
        }

        static void Check(Listing_Standard l, string label, ref bool v, string tip)
        {
            l.CheckboxLabeled(UiUtil.T(label), ref v, UiUtil.T(tip));
        }

        static void SliderRow(Listing_Standard l, string label, object arg, ref float v, float min, float max, string tip)
        {
            float y0 = l.CurHeight;
            l.Label(UiUtil.T(label, arg));
            v = l.Slider(v, min, max);
            Tip(l, y0, UiUtil.T(tip));
        }

        static void NumRow(Listing_Standard l, string label, ref int v, ref string buf, int min, int max, string tip)
        {
            float y0 = l.CurHeight;
            l.Label(UiUtil.T(label));
            if (buf == null) buf = v.ToString();
            l.TextFieldNumeric(ref v, ref buf, min, max);
            Tip(l, y0, UiUtil.T(tip));
        }

        static void TextRow(Listing_Standard l, string label, ref string v, string tip)
        {
            float y0 = l.CurHeight;
            l.Label(UiUtil.T(label));
            v = Widgets.TextField(l.GetRect(24f), v ?? "");
            Tip(l, y0, UiUtil.T(tip));
        }

        static bool Button(Listing_Standard l, string label, string tip, bool enabled = true)
        {
            var r = l.GetRect(30f);
            TooltipHandler.TipRegion(r, UiUtil.T(tip));
            return Widgets.ButtonText(r, UiUtil.T(label), true, true, enabled);
        }

        static void Radio(Listing_Standard l, string label, string tip, bool on, Action pick)
        {
            if (l.RadioButton(UiUtil.T(label), on, 0f, UiUtil.T(tip)) && !on) pick();
        }

        // ------------------------------------------------------------------------------------ Model
        static void Model(Listing_Standard l, RimWorldLLMSettings s)
        {
            Title(l, "RWL_Model_Title");
            Check(l, "RWL_Conn_Enabled", ref s.enabled, "RWL_Tip_Enabled");
            l.Gap(4f);
            l.Label(UiUtil.T("RWL_Eng_SourceLabel"));
            bool local = s.llmSource != "own";
            Radio(l, "RWL_Eng_SourceLocal", "RWL_Eng_SourceLocalTip", local, () => { s.llmSource = "local"; EngineManager.SettingsChanged(); });
            Radio(l, "RWL_Eng_SourceOwn", "RWL_Eng_SourceOwnTip", !local, () => { s.llmSource = "own"; EngineManager.SettingsChanged(); });
            l.Gap(4f);

            if (local) { LocalEngine(l, s); return; }
            Note(l, UiUtil.T("RWL_Conn_EndpointHelp"));
            EnginePairingUi.Draw(l, s);
            TextRow(l, "RWL_Conn_LlmUrl", ref s.llmBaseUrl, "RWL_Tip_LlmUrl");
            TextRow(l, "RWL_Conn_LlmModel", ref s.llmModel, "RWL_Tip_LlmModel");
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("RWL_Conn_LlmKey"));
            var krow = l.GetRect(26f);
            if (_revealKey) s.llmApiKey = Widgets.TextField(new Rect(krow.x, krow.y, krow.width - 110f, 24f), s.llmApiKey ?? "");
            else Widgets.Label(new Rect(krow.x, krow.y + 2f, krow.width - 110f, 24f), string.IsNullOrEmpty(s.llmApiKey) ? UiUtil.T("RWL_Conn_KeyNone") : Mask(s.llmApiKey));
            if (Widgets.ButtonText(new Rect(krow.xMax - 100f, krow.y, 100f, 26f), UiUtil.T(_revealKey ? "RWL_Conn_Hide" : "RWL_Conn_Edit"))) _revealKey = !_revealKey;
            Tip(l, y0, UiUtil.T("RWL_Tip_LlmKey"));
            Note(l, UiUtil.T("RWL_Conn_KeyNote"));
            TestRow(l, s);
        }

        static void LocalEngine(Listing_Standard l, RimWorldLLMSettings s)
        {
            EngineManager.Touch();
            var st = EngineManager.Status;

            // Status card.
            float y0 = l.CurHeight;
            var row = l.GetRect(28f);
            Color c = EngineManager.ColorOf(EngineManager.CurrentLight());
            Widgets.DrawBoxSolid(new Rect(row.x, row.y + 6f, 16f, 16f), c);
            GUI.color = c;
            Widgets.Label(new Rect(row.x + 24f, row.y + 3f, row.width - 24f, 26f), EngineManager.StatusLine());
            GUI.color = Color.white;
            Tip(l, y0, UiUtil.T("RWL_Tip_StatusCard"));
            string gpuLine = EngineManager.GpuLine();
            if (gpuLine != null) { GUI.color = Color.gray; l.Label(gpuLine); GUI.color = Color.white; }
            if (EngineManager.CustomActive)
            {
                var cu = st.Custom;
                GUI.color = cu.Missing ? Red : Color.gray;
                l.Label(UiUtil.T(cu.Missing ? "RWL_Custom_StatusMissing" : "RWL_Custom_StatusUsing", cu.Name ?? "", cu.Path ?? ""));
                GUI.color = Color.white;
            }
            string fb = EngineManager.FallbackLine();
            if (fb != null) { GUI.color = Amber; l.Label(fb); GUI.color = Color.white; }
            string sw = EngineManager.SwitchLine();
            if (sw != null) { GUI.color = Amber; l.Label(sw); GUI.color = Color.white; }
            string err = EngineManager.ActionError;
            if (!string.IsNullOrEmpty(err)) { GUI.color = Red; l.Label(UiUtil.T("RWL_Setup_ActionError", err)); GUI.color = Color.white; }
            if (st != null && st.Previous != null) PreviousOffer(l, st.Previous);

            l.Gap(4f);
            if (Button(l, "RWL_Model_Change", "RWL_Tip_ChangeModel")) Find.WindowStack.Add(new Window_EngineSetup());
            if (Button(l, "RWL_Model_UseOwnFile", "RWL_Tip_UseOwnFile")) Find.WindowStack.Add(new Window_CustomGguf());

            var trow = l.GetRect(30f);
            TooltipHandler.TipRegion(trow, UiUtil.T("RWL_Tip_Test"));
            bool running = EngineManager.TestRunning;
            if (Widgets.ButtonText(new Rect(trow.x, trow.y, 120f, 28f), UiUtil.T("RWL_Test_Button"), true, true, !running && EngineManager.Client != null))
                EngineManager.RunTest();
            var t = EngineManager.LastTest;
            Color tc = Color.gray;
            string tmsg = running ? UiUtil.T("RWL_Test_Running") : UiUtil.T("RWL_Test_NotRun");
            if (!running && t != null)
            {
                tc = t.Light == "green" ? Green : (t.Light == "amber" ? Amber : Red);
                tmsg = (t.Message ?? "") + (t.LatencyMs > 0 ? " " + UiUtil.T("RWL_Probe_Ms", t.LatencyMs) : "");
            }
            Widgets.DrawBoxSolid(new Rect(trow.x + 130f, trow.y + 6f, 16f, 16f), tc);
            GUI.color = tc;
            Widgets.Label(new Rect(trow.x + 154f, trow.y + 3f, trow.width - 154f, 26f), tmsg);
            GUI.color = Color.white;

            l.Gap(4f);
            Check(l, "RWL_Eng_AutoStart", ref s.engineAutoStart, "RWL_Eng_AutoStartTip");
            Check(l, "RWL_Fallback_HideSetting", ref s.hideFallbackWarning, "RWL_Tip_HideFallback");
            Note(l, UiUtil.T("RWL_Eng_LocalNote"));
        }

        /// <summary>After a model switch the engine keeps the old file: offer to delete it.</summary>
        public static void PreviousOffer(Listing_Standard l, EnginePrevious p)
        {
            string name = !string.IsNullOrEmpty(p.Name) ? p.Name : (p.Id ?? p.File);
            var row = l.GetRect(30f);
            TooltipHandler.TipRegion(row, UiUtil.T("RWL_Tip_DeleteOld"));
            Widgets.Label(new Rect(row.x, row.y + 4f, row.width - 170f, 26f), UiUtil.T("RWL_Prev_Offer", name, Window_EngineSetup.Bytes(p.Bytes)));
            if (Widgets.ButtonText(new Rect(row.xMax - 160f, row.y, 160f, 28f), UiUtil.T("RWL_Prev_Delete"), true, true, !EngineManager.CustomBusy))
            {
                string file = p.File, id = p.Id;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("RWL_Prev_Confirm", name, Window_EngineSetup.Bytes(p.Bytes)),
                    () => { if (!string.IsNullOrEmpty(file)) EngineManager.DeleteModel(file); else EngineManager.DeleteModelById(id); }, true));
            }
        }

        static void AgentLine(Listing_Standard l)
        {
            double secs = RWLBridge.Runtime.Requests.SecondsSinceAgent(DateTime.UtcNow.Ticks);
            string text;
            Color col;
            if (secs < 0) { text = UiUtil.T("RWL_Conn_AgentNever"); col = Color.gray; }
            else
            {
                text = UiUtil.T("RWL_Conn_AgentLast", Ago(secs));
                col = secs < 30 ? Green : (secs < 300 ? Amber : Color.gray);
            }
            GUI.color = col; l.Label(UiUtil.T("RWL_Conn_AgentLabel", text)); GUI.color = Color.white;
        }

        public static string Ago(double secs)
        {
            if (secs < 90) return UiUtil.T("RWL_Ago_Seconds", (int)secs);
            if (secs < 5400) return UiUtil.T("RWL_Ago_Minutes", (int)(secs / 60));
            return UiUtil.T("RWL_Ago_Hours", (int)(secs / 3600));
        }

        static void TestRow(Listing_Standard l, RimWorldLLMSettings s)
        {
            var row = l.GetRect(30f);
            bool running = LlmProbe.IsRunning;
            if (Widgets.ButtonText(new Rect(row.x, row.y, 120f, 28f), UiUtil.T("RWL_Test_Button"), true, true, !running))
            { EngineManager.SettingsChanged(); LlmProbe.RunAsync(s.llmBaseUrl, s.llmModel, s.llmApiKey); }

            var r = LlmProbe.Last;
            Color c = Color.gray;
            string msg;
            if (running) msg = UiUtil.T("RWL_Test_Running");
            else if (r == null) msg = UiUtil.T("RWL_Test_NotRun");
            else
            {
                c = r.Light == ProbeLight.Green ? Green : (r.Light == ProbeLight.Amber ? Amber : Red);
                msg = ProbeMessage(r);
                // The probe found a base URL that works: keep it.
                if (!string.IsNullOrEmpty(r.EffectiveBase) && r.EffectiveBase != (s.llmBaseUrl ?? "").Trim() && r.Light != ProbeLight.Red && r.Code != ProbeCode.Loading && r.Code != ProbeCode.Paused)
                { s.llmBaseUrl = r.EffectiveBase; RWLBridge.RefreshLlm(); }
            }
            Widgets.DrawBoxSolid(new Rect(row.x + 130f, row.y + 6f, 16f, 16f), c);
            GUI.color = c;
            Widgets.Label(new Rect(row.x + 154f, row.y + 3f, row.width - 154f, 26f), msg);
            GUI.color = Color.white;
        }

        public static string ProbeMessage(ProbeResult r)
        {
            string m = UiUtil.T("RWL_Probe_" + r.Code, r.Arg);
            if (r.ElapsedMs > 0 && (r.Code == ProbeCode.Ok || r.Code == ProbeCode.OkFixedBase)) m += " " + UiUtil.T("RWL_Probe_Ms", r.ElapsedMs);
            return m;
        }


        // ------------------------------------------------------------------------------------ Colonists
        static void Colonists(Listing_Standard l, RimWorldLLMSettings s)
        {
            Title(l, "RWL_Talk_Title");
            SliderRow(l, "RWL_Talk_Frequency", s.talkFrequency.ToString("0.0"), ref s.talkFrequency, 0f, 2f, "RWL_Tip_Frequency");
            NumRow(l, "RWL_Talk_PauseAt", ref s.pauseAmbientAtSpeed, ref _speedBuf, 0, 4, "RWL_Talk_PauseHelp");
            l.GapLine();
            l.Label(UiUtil.T("RWL_Effects_Title"));
            Note(l, UiUtil.T("RWL_Effects_Help"));
            Radio(l, "RWL_Effects_off", "RWL_Tip_EffectsOff", s.effects == "off", () => s.effects = "off");
            Radio(l, "RWL_Effects_light", "RWL_Tip_EffectsLight", s.effects == "light", () => s.effects = "light");
            Radio(l, "RWL_Effects_normal", "RWL_Tip_EffectsNormal", s.effects == "normal", () => s.effects = "normal");
            l.GapLine();
            Check(l, "RWL_Quotes_Show", ref s.showQuotes, "RWL_Quotes_ShowTip");
            Check(l, "RWL_Quotes_Vanilla", ref s.voiceVanillaSocial, "RWL_Quotes_VanillaTip");
            Check(l, "RWL_Wants_Enable", ref s.wantsEnabled, "RWL_Wants_EnableTip");
            if (s.wantsEnabled) SliderRow(l, "RWL_Wants_AvoidHours", s.wantsAvoidHours.ToString("0.0"), ref s.wantsAvoidHours, 0.25f, 6f, "RWL_Tip_AvoidHours");
            Check(l, "RWL_Dreams_Enable", ref s.dreamsEnabled, "RWL_Dreams_EnableTip");
            Check(l, "RWL_Recap_Enable", ref s.recapOnLoad, "RWL_Recap_EnableTip");
            Check(l, "RWL_Art_Enable", ref s.artDescriptions, "RWL_Art_EnableTip");

            l.GapLine();
            Title(l, "RWL_Text_Title");
            Note(l, UiUtil.T("RWL_Text_Help"));
            Check(l, "RWL_Text_Enable", ref s.textGen, "RWL_Text_EnableTip");
            if (s.textGen)
            {
                Check(l, "RWL_Text_Art", ref s.textArt, "RWL_Text_ArtTip");
                Check(l, "RWL_Text_Graves", ref s.textGraves, "RWL_Text_GravesTip");
                Check(l, "RWL_Text_Books", ref s.textBooks, "RWL_Text_BooksTip");
                Check(l, "RWL_Text_Social", ref s.textSocial, "RWL_Text_SocialTip");
                Check(l, "RWL_Text_Combat", ref s.textCombat, "RWL_Text_CombatTip");
                Note(l, UiUtil.T("RWL_Text_Vanilla"));
            }

            l.GapLine();
            Title(l, "RWL_Viv_Title");
            Note(l, UiUtil.T("RWL_Viv_Help"));
            Check(l, "RWL_Viv_MayPause", ref s.narratorMayPauseForEmergencies, "RWL_Viv_MayPauseTip");
            Check(l, "RWL_Viv_ControlsSpeed", ref s.narratorControlsSpeed, "RWL_Viv_ControlsSpeedTip");
            Check(l, "RWL_Viv_SlowForCombat", ref s.narratorSlowsForCombat, "RWL_Viv_SlowForCombatTip");
            SliderRow(l, "RWL_Viv_Heartbeat", Mathf.RoundToInt(s.stewardHeartbeatHours), ref s.stewardHeartbeatHours, 1f, 24f, "RWL_Viv_HeartbeatTip");
            s.stewardHeartbeatHours = Mathf.Round(s.stewardHeartbeatHours);
            Check(l, "RWL_Viv_AnswerDialogs", ref s.narratorAnswerDialogs, "RWL_Viv_AnswerDialogsTip");
            float dlgT = s.narratorDialogTimeoutS;
            SliderRow(l, "RWL_Viv_DialogTimeout", s.narratorDialogTimeoutS, ref dlgT, 10f, 300f, "RWL_Viv_DialogTimeoutTip");
            s.narratorDialogTimeoutS = Mathf.RoundToInt(dlgT / 5f) * 5;
            Check(l, "RWL_Viv_Commentary", ref s.narratorCommentary, "RWL_Viv_CommentaryTip");
            Check(l, "RWL_Viv_Autosave", ref s.narratorAutosave, "RWL_Viv_AutosaveTip");
            if (s.narratorAutosave)
            {
                SliderRow(l, "RWL_Viv_AutosaveDays", s.narratorAutosaveDays.ToString("0.#"), ref s.narratorAutosaveDays, 0.25f, 7f, "RWL_Viv_AutosaveDaysTip");
                s.narratorAutosaveDays = Mathf.Round(s.narratorAutosaveDays * 4f) / 4f;
                float keep = s.narratorAutosaveKeep;
                SliderRow(l, "RWL_Viv_AutosaveKeep", s.narratorAutosaveKeep, ref keep, 1f, 20f, "RWL_Viv_AutosaveKeepTip");
                s.narratorAutosaveKeep = Mathf.Clamp(Mathf.RoundToInt(keep), 1, 20);
            }
            s.narratorRestartMode = Mathf.Clamp(s.narratorRestartMode, 0, 2);
            var modeRect = l.GetRect(30f);
            TooltipHandler.TipRegion(modeRect, UiUtil.T("RWL_Viv_RestartModeTip"));
            if (Widgets.ButtonText(modeRect, UiUtil.T("RWL_Viv_RestartMode", UiUtil.T("RWL_Viv_RestartMode_" + s.narratorRestartMode))))
                s.narratorRestartMode = (s.narratorRestartMode + 1) % 3;
            int undoable = Current.Game != null ? VivariumGame.State.Ledger.Count : 0;
            if (Button(l, "RWL_Viv_Undo", "RWL_Viv_UndoTip", undoable > 0)) VivariumChat.UndoNarrator(1);
            Check(l, "RWL_Chat_Show", ref s.chatPanelShow, "RWL_Chat_ShowTip");
            Check(l, "RWL_Chat_Right", ref s.chatPanelRight, "RWL_Chat_RightTip");
            Check(l, "RWL_Chat_Medium", ref s.chatPanelMedium, "RWL_Chat_MediumTip");
            SliderRow(l, "RWL_Chat_Opacity", Mathf.RoundToInt(s.chatPanelOpacity * 100f), ref s.chatPanelOpacity, 0f, 0.8f, "RWL_Chat_OpacityTip");
            s.chatPanelOpacity = Mathf.Round(s.chatPanelOpacity * 20f) / 20f;
            Note(l, UiUtil.T("RWL_Chat_MoveHelp"));
            if (Button(l, "RWL_Chat_ResetPos", "RWL_Chat_ResetPosTip", s.ChatCustom)) s.ChatResetPosition();
            Check(l, "RWL_Chat_FTalk", ref s.chatShowTalk, "RWL_Chat_FTalkTip");
            Check(l, "RWL_Chat_FThoughts", ref s.chatShowThoughts, "RWL_Chat_FThoughtsTip");
            Check(l, "RWL_Chat_FNarrator", ref s.chatShowNarrator, "RWL_Chat_FNarratorTip");
            Check(l, "RWL_Chat_FActions", ref s.chatShowActions, "RWL_Chat_FActionsTip");
            Check(l, "RWL_Chat_FStory", ref s.chatShowStory, "RWL_Chat_FStoryTip");

            l.GapLine();
            Title(l, "RWL_Events_Title");
            Note(l, UiUtil.T("RWL_Events_Help"));
            Check(l, "RWL_Ev_Injury", ref s.evInjury, "RWL_EvTip_Injury");
            Check(l, "RWL_Ev_Death", ref s.evDeath, "RWL_EvTip_Death");
            Check(l, "RWL_Ev_MentalBreak", ref s.evMentalBreak, "RWL_EvTip_MentalBreak");
            Check(l, "RWL_Ev_LevelUp", ref s.evLevelUp, "RWL_EvTip_LevelUp");
            Check(l, "RWL_Ev_Tend", ref s.evTend, "RWL_EvTip_Tend");
            Check(l, "RWL_Ev_Social", ref s.evSocial, "RWL_EvTip_Social");
            Check(l, "RWL_Ev_Raid", ref s.evRaid, "RWL_EvTip_Raid");
            Check(l, "RWL_Ev_Quest", ref s.evQuest, "RWL_EvTip_Quest");
            Check(l, "RWL_Ev_Lodger", ref s.evLodger, "RWL_EvTip_Lodger");
            Check(l, "RWL_Ev_Prisoner", ref s.evPrisoner, "RWL_EvTip_Prisoner");
            Check(l, "RWL_Ev_Funeral", ref s.evFuneral, "RWL_EvTip_Funeral");
            Check(l, "RWL_Ev_Weather", ref s.evWeather, "RWL_EvTip_Weather");
            Check(l, "RWL_Ev_Thought", ref s.evThought, "RWL_EvTip_Thought");
            Check(l, "RWL_Ev_Art", ref s.evArt, "RWL_EvTip_Art");
            Check(l, "RWL_Ev_Sleep", ref s.evSleep, "RWL_EvTip_Sleep");
            Check(l, "RWL_Ev_Firsts", ref s.evFirsts, "RWL_EvTip_Firsts");
            Check(l, "RWL_Ev_Gatherings", ref s.evGatherings, "RWL_EvTip_Gatherings");
        }

        // ------------------------------------------------------------------------------------ Display
        static void Display(Listing_Standard l, RimWorldLLMSettings s)
        {
            Title(l, "RWL_Bubbles_Title");
            Check(l, "RWL_Bubbles_Show", ref s.showBubbles, "RWL_Tip_BubblesShow");
            Check(l, "RWL_Bubbles_Colors", ref s.colorBubbles, "RWL_Tip_BubblesColors");
            bool comic = s.bubbleStyle != "classic";
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("RWL_Bubbles_Style"));
            var br = l.GetRect(30f);
            if (Widgets.ButtonText(br, UiUtil.T(comic ? "RWL_Bubbles_StyleComic" : "RWL_Bubbles_StyleClassic")))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption(UiUtil.T("RWL_Bubbles_StyleComic"), () => s.bubbleStyle = "comic"),
                    new FloatMenuOption(UiUtil.T("RWL_Bubbles_StyleClassic"), () => s.bubbleStyle = "classic")
                }));
            }
            Tip(l, y0, UiUtil.T("RWL_Tip_BubbleStyle"));
            SliderRow(l, "RWL_Bubbles_Opacity", Mathf.RoundToInt(s.bubbleOpacity * 100f), ref s.bubbleOpacity, 0.3f, 1f, "RWL_Tip_BubbleOpacity");
            if (comic) Check(l, "RWL_Bubbles_Lettering", ref s.comicLettering, "RWL_Tip_Lettering");
            NumRow(l, "RWL_Bubbles_Duration", ref s.bubbleDurationSec, ref _durBuf, 1, 120, "RWL_Tip_BubbleDuration");
            if (comic)
            {
                l.GapLine();
                Check(l, "RWL_Thoughts_Show", ref s.showThoughts, "RWL_Thoughts_ShowTip");
                if (s.showThoughts)
                {
                    SliderRow(l, "RWL_Thoughts_Opacity", Mathf.RoundToInt(s.thoughtOpacity * 100f), ref s.thoughtOpacity, 0.2f, 1f, "RWL_Tip_ThoughtOpacity");
                    NumRow(l, "RWL_Thoughts_PerPawn", ref s.thoughtPerPawnSec, ref _thoughtPawnBuf, 5, 3600, "RWL_Tip_ThoughtPerPawn");
                    NumRow(l, "RWL_Thoughts_Global", ref s.thoughtGlobalSec, ref _thoughtGlobalBuf, 1, 600, "RWL_Tip_ThoughtGlobal");
                }
            }
            l.GapLine();
            Check(l, "RWL_Camera_Allowed", ref s.directorCameraAllowed, "RWL_Camera_AllowedTip");
            if (s.directorCameraAllowed)
            {
                Check(l, "RWL_Camera_OnAtStart", ref s.directorCameraOnAtStart, "RWL_Camera_OnAtStartTip");
                SliderRow(l, "RWL_Camera_PanSpeed", s.directorPanSpeed.ToString("0.0"), ref s.directorPanSpeed, 0.5f, 6f, "RWL_Tip_PanSpeed");
            }
            l.GapLine();
            Check(l, "RWL_Watch_Mode", ref s.watchMode, "RWL_Watch_ModeTip");
            if (s.watchMode)
            {
                Check(l, "RWL_Watch_Captions", ref s.watchCaptions, "RWL_Watch_CaptionsTip");
                NumRow(l, "RWL_Watch_PauseSec", ref s.watchPlayerPauseSec, ref _watchPauseBuf, 3, 300, "RWL_Watch_PauseSecTip");
                Check(l, "RWL_Watch_Zoom", ref s.watchZoom, "RWL_Watch_ZoomTip");
                Check(l, "RWL_Watch_SelectFollowed", ref s.watchSelectFollowed, "RWL_Watch_SelectFollowedTip");
                float paceY = l.CurHeight;
                string paceKey = s.watchPace <= 0 ? "RWL_Watch_PaceCalm" : s.watchPace >= 2 ? "RWL_Watch_PaceLively" : "RWL_Watch_PaceNormal";
                if (l.ButtonTextLabeled(UiUtil.T("RWL_Watch_Pace"), UiUtil.T(paceKey))) s.watchPace = (Mathf.Clamp(s.watchPace, 0, 2) + 1) % 3;
                Tip(l, paceY, UiUtil.T("RWL_Watch_PaceTip"));
                l.Label(UiUtil.T("RWL_Watch_Scenes"));
                Check(l, "RWL_Watch_SceneMeals", ref s.watchSceneMeals, "RWL_Watch_ScenesTip");
                Check(l, "RWL_Watch_SceneRecreation", ref s.watchSceneRecreation, "RWL_Watch_ScenesTip");
                Check(l, "RWL_Watch_SceneWork", ref s.watchSceneWork, "RWL_Watch_SceneWorkTip");
                Check(l, "RWL_Watch_SceneNight", ref s.watchSceneNight, "RWL_Watch_ScenesTip");
                Check(l, "RWL_Watch_SceneMedical", ref s.watchSceneMedical, "RWL_Watch_ScenesTip");
                Check(l, "RWL_Watch_SceneChildren", ref s.watchSceneChildren, "RWL_Watch_ScenesTip");
                Check(l, "RWL_Watch_SceneRomance", ref s.watchSceneRomance, "RWL_Watch_SceneRomanceTip");
            }
            l.GapLine();
            Check(l, "RWL_Share_Footer", ref s.shareFooter, "RWL_Share_FooterTip");
        }

        // ------------------------------------------------------------------------------------ Content
        static void Content(Listing_Standard l, RimWorldLLMSettings s)
        {
            Title(l, "RWL_Content_Title");
            bool adult = s.adultContent;
            l.CheckboxLabeled(UiUtil.T("RWL_Adult_Enable"), ref adult, UiUtil.T("RWL_Adult_EnableTip"));
            if (adult != s.adultContent)
            {
                if (!adult) s.adultContent = false;
                else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("RWL_Adult_ConfirmText"), () => s.adultContent = true));
            }
            l.Gap(6f);
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("RWL_Content_AgeGate"));
            Tip(l, y0, UiUtil.T("RWL_Tip_AgeGate"));
        }

        // ------------------------------------------------------------------------------------ Storage
        static string PartLabel(string name)
        {
            switch (name)
            {
                case "models": return UiUtil.T("RWL_Storage_PartModels");
                case "backends": return UiUtil.T("RWL_Storage_PartBackends");
                case "runtime": return UiUtil.T("RWL_Storage_PartRuntime");
                case "downloads": return UiUtil.T("RWL_Storage_PartDownloads");
                case "logs": return UiUtil.T("RWL_Storage_PartLogs");
                default: return name;
            }
        }

        /// <summary>Bytes the "Remove downloaded files" job frees: models, backends, runtime and downloads (not logs).</summary>
        public static long RemovableBytes(EngineStorage st)
        {
            long total = 0;
            if (st != null && st.Parts != null)
                foreach (var p in st.Parts)
                    if (p.Name == "models" || p.Name == "backends" || p.Name == "runtime" || p.Name == "downloads") total += Math.Max(0, p.Bytes);
            return total;
        }

        // Written texts in the current game: per kind, how many are stored and how many wait in the queue.
        static void TextCounts(Listing_Standard l)
        {
            if (Current.Game == null) return;
            var d = TextHook.Desk;
            var parts = new System.Collections.Generic.List<string>();
            var names = new[] { UiUtil.T("RWL_Text_Kind_art"), UiUtil.T("RWL_Text_Kind_grave"), UiUtil.T("RWL_Text_Kind_book"),
                                UiUtil.T("RWL_Text_Kind_social"), UiUtil.T("RWL_Text_Kind_combat") };
            var ids = new[] { "art", "grave", "book", "social", "combat" };
            for (int i = 0; i < ids.Length; i++) parts.Add(names[i] + " " + d.Count(ids[i]) + " (+" + d.Queued(ids[i]) + ")");
            l.Label(UiUtil.T("RWL_Storage_Texts", string.Join(", ", parts)));
            l.GapLine();
        }

        static void Storage(Listing_Standard l, RimWorldLLMSettings s)
        {
            Title(l, "RWL_Storage_Title");
            Note(l, UiUtil.T("RWL_Storage_Help"));
            TextCounts(l);
            EngineManager.Touch();
            if (EngineManager.Client == null) { Note(l, UiUtil.T("RWL_Storage_NoEngine")); return; }
            var st = EngineManager.Storage;
            if (Button(l, "RWL_Storage_Refresh", "RWL_Tip_StorageRefresh", !EngineManager.StorageInFlight)) EngineManager.RefreshStorage();
            if (st == null)
            {
                if (EngineManager.StorageFailed) { GUI.color = Red; l.Label(UiUtil.T("RWL_Storage_Failed")); GUI.color = Color.white; }
                else Note(l, UiUtil.T("RWL_Storage_Loading"));
                return;
            }
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("RWL_Storage_Folder", st.DataDir ?? ""));
            Tip(l, y0, UiUtil.T("RWL_Tip_StorageFolder"));
            if (!string.IsNullOrEmpty(st.DataDir) && Button(l, "RWL_Storage_OpenFolder", "RWL_Tip_OpenFolder")) OpenFolder(st.DataDir);
            l.Gap(4f);
            if (st.Parts != null)
                foreach (var p in st.Parts)
                {
                    float py = l.CurHeight;
                    l.Label(UiUtil.T("RWL_Storage_PartLine", PartLabel(p.Name), Window_EngineSetup.Bytes(Math.Max(0, p.Bytes))));
                    Tip(l, py, p.Path);
                }
            if (st.FreeBytes >= 0) l.Label(UiUtil.T("RWL_Storage_Free", Window_EngineSetup.Bytes(st.FreeBytes)));

            l.GapLine();
            Title(l, "RWL_Storage_ModelsTitle");
            if (st.Models == null || st.Models.Count == 0) Note(l, UiUtil.T("RWL_Storage_NoModels"));
            else
                foreach (var m in st.Models)
                {
                    var row = l.GetRect(28f);
                    TooltipHandler.TipRegion(row, UiUtil.T(m.InUse ? "RWL_Tip_ModelInUse" : "RWL_Tip_ModelDelete"));
                    string line = m.File + "  (" + Window_EngineSetup.Bytes(m.Bytes) + ")";
                    if (m.InUse) line += "  " + UiUtil.T("RWL_Setup_InUse");
                    if (m.Partial) line += "  " + UiUtil.T("RWL_Storage_Partial");
                    Widgets.Label(new Rect(row.x, row.y + 3f, row.width - 120f, 26f), line);
                    if (Widgets.ButtonText(new Rect(row.xMax - 110f, row.y, 110f, 26f), UiUtil.T("RWL_Storage_Delete"), true, true, !m.InUse && !EngineManager.CustomBusy))
                    {
                        string file = m.File;
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("RWL_Storage_DeleteConfirm", file, Window_EngineSetup.Bytes(m.Bytes)), () => EngineManager.DeleteModel(file), true));
                    }
                }

            l.GapLine();
            Note(l, UiUtil.T("RWL_Storage_YourFileNote"));
            if (Button(l, "RWL_Storage_RemoveAll", "RWL_Tip_RemoveAll", !EngineManager.CustomBusy))
                Find.WindowStack.Add(new Window_RemoveAll(RemovableBytes(st)));
            string removeNote = EngineManager.RemoveNote;
            if (removeNote != null) Note(l, removeNote);
        }

        public static void OpenFolder(string dir)
        {
            try { Application.OpenURL(new Uri(System.IO.Path.GetFullPath(dir) + System.IO.Path.DirectorySeparatorChar).AbsoluteUri); }
            catch (Exception ex) { Log.Warning("[RimWorldLLM] could not open " + dir + ": " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------ Advanced
        static void Advanced(Listing_Standard l, RimWorldLLMSettings s)
        {
            Title(l, "RWL_Adv_Title");
            Note(l, UiUtil.T("RWL_Adv_Help"));
            string state;
            Color col;
            if (RWLBridge.IsRunning) { state = UiUtil.T("RWL_Conn_BridgeRunning", RWLBridge.Port); col = Green; }
            else { state = UiUtil.T("RWL_Conn_BridgeStopped"); col = Red; }
            GUI.color = col; l.Label(state); GUI.color = Color.white;
            if (!RWLBridge.IsRunning && !string.IsNullOrEmpty(RWLBridge.LastError)) { GUI.color = Red; l.Label(UiUtil.T("RWL_Conn_LastError", RWLBridge.LastError)); GUI.color = Color.white; }
            AgentLine(l);

            l.Gap(4f);
            NumRow(l, "RWL_Conn_Port", ref s.bridgePort, ref _portBuf, 1, 65535, "RWL_Tip_Port");
            TextRow(l, "RWL_Conn_Bind", ref s.bindAddress, "RWL_Tip_Bind");
            if (!RWLBridge.IsLoopback(s.bindAddress) && string.IsNullOrWhiteSpace(s.sharedToken))
            { GUI.color = Red; l.Label(UiUtil.T("RWL_Adv_OpenWarning")); GUI.color = Color.white; }

            l.Gap(4f);
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("RWL_Conn_Token"));
            var row = l.GetRect(26f);
            string token = RWLBridge.CurrentToken ?? "";
            Widgets.Label(new Rect(row.x, row.y + 2f, row.width - 250f, 24f), _revealToken ? token : Mask(token));
            if (Widgets.ButtonText(new Rect(row.xMax - 244f, row.y, 100f, 26f), UiUtil.T(_revealToken ? "RWL_Conn_Hide" : "RWL_Conn_Reveal"))) _revealToken = !_revealToken;
            if (Widgets.ButtonText(new Rect(row.xMax - 140f, row.y, 140f, 26f), UiUtil.T(_confirmRegen ? "RWL_Conn_RegenConfirm" : "RWL_Conn_Regen")))
            {
                if (_confirmRegen) { RWLBridge.RegenerateToken(); _confirmRegen = false; }
                else _confirmRegen = true;
            }
            Tip(l, y0, UiUtil.T("RWL_Tip_Token"));
            if (_confirmRegen) Note(l, UiUtil.T("RWL_Conn_RegenWarn"));
            TextRow(l, "RWL_Adv_FixedToken", ref s.sharedToken, "RWL_Adv_FixedTokenHelp");

            l.GapLine();
            TextRow(l, "RWL_Events_AgentUrl", ref s.agentUrl, "RWL_Tip_AgentUrl");
            Check(l, "RWL_Events_Push", ref s.sendColonyEvents, "RWL_Tip_Push");
            l.Gap(4f);
            if (Button(l, "RWL_Conn_Restart", "RWL_Tip_Restart")) RWLBridge.Start();
            if (Button(l, "RWL_Setup_OpenPage", "RWL_Tip_OpenPage")) Application.OpenURL(EngineManager.SetupUrl);
            l.GapLine();
            if (Prefs.DevMode)
            {
                if (Button(l, "RWL_Adv_OpenDebug", "RWL_Tip_Debug")) Find.WindowStack.Add(new Window_RWLDebug());
            }
            else Note(l, UiUtil.T("RWL_Adv_DebugNeedsDev"));
        }
    }
}
