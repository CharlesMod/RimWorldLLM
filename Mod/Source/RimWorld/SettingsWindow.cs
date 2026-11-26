using System;
using System.Collections.Generic;
using HermesVoice.Bridge;
using UnityEngine;
using Verse;

namespace HermesVoice
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
        static string _portBuf, _speedBuf, _durBuf, _thoughtPawnBuf, _thoughtGlobalBuf;

        static readonly Color Green = new Color(0.4f, 0.85f, 0.4f), Amber = new Color(0.95f, 0.75f, 0.25f), Red = new Color(0.9f, 0.3f, 0.3f);

        public static void Draw(Rect inRect, HermesVoiceSettings s)
        {
            float bw = inRect.width / Sections.Length;
            for (int i = 0; i < Sections.Length; i++)
            {
                var r = new Rect(inRect.x + i * bw, inRect.y, bw - 4f, 28f);
                var prev = GUI.color;
                if (i == _section) GUI.color = new Color(0.6f, 0.85f, 1f);
                if (Widgets.ButtonText(r, UiUtil.T("HV_Section_" + Sections[i])))
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
        static void Model(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Model_Title");
            Check(l, "HV_Conn_Enabled", ref s.enabled, "HV_Tip_Enabled");
            l.Gap(4f);
            l.Label(UiUtil.T("HV_Eng_SourceLabel"));
            bool local = s.llmSource != "own";
            Radio(l, "HV_Eng_SourceLocal", "HV_Eng_SourceLocalTip", local, () => { s.llmSource = "local"; EngineManager.SettingsChanged(); });
            Radio(l, "HV_Eng_SourceOwn", "HV_Eng_SourceOwnTip", !local, () => { s.llmSource = "own"; EngineManager.SettingsChanged(); });
            l.Gap(4f);

            if (local) { LocalEngine(l, s); return; }
            Note(l, UiUtil.T("HV_Conn_EndpointHelp"));
            TextRow(l, "HV_Conn_LlmUrl", ref s.llmBaseUrl, "HV_Tip_LlmUrl");
            TextRow(l, "HV_Conn_LlmModel", ref s.llmModel, "HV_Tip_LlmModel");
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("HV_Conn_LlmKey"));
            var krow = l.GetRect(26f);
            if (_revealKey) s.llmApiKey = Widgets.TextField(new Rect(krow.x, krow.y, krow.width - 110f, 24f), s.llmApiKey ?? "");
            else Widgets.Label(new Rect(krow.x, krow.y + 2f, krow.width - 110f, 24f), string.IsNullOrEmpty(s.llmApiKey) ? UiUtil.T("HV_Conn_KeyNone") : Mask(s.llmApiKey));
            if (Widgets.ButtonText(new Rect(krow.xMax - 100f, krow.y, 100f, 26f), UiUtil.T(_revealKey ? "HV_Conn_Hide" : "HV_Conn_Edit"))) _revealKey = !_revealKey;
            Tip(l, y0, UiUtil.T("HV_Tip_LlmKey"));
            Note(l, UiUtil.T("HV_Conn_KeyNote"));
            TestRow(l, s);
        }

        static void LocalEngine(Listing_Standard l, HermesVoiceSettings s)
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
            Tip(l, y0, UiUtil.T("HV_Tip_StatusCard"));
            if (EngineManager.CustomActive)
            {
                var cu = st.Custom;
                GUI.color = cu.Missing ? Red : Color.gray;
                l.Label(UiUtil.T(cu.Missing ? "HV_Custom_StatusMissing" : "HV_Custom_StatusUsing", cu.Name ?? "", cu.Path ?? ""));
                GUI.color = Color.white;
            }
            string fb = EngineManager.FallbackLine();
            if (fb != null) { GUI.color = Amber; l.Label(fb); GUI.color = Color.white; }
            string sw = EngineManager.SwitchLine();
            if (sw != null) { GUI.color = Amber; l.Label(sw); GUI.color = Color.white; }
            string err = EngineManager.ActionError;
            if (!string.IsNullOrEmpty(err)) { GUI.color = Red; l.Label(UiUtil.T("HV_Setup_ActionError", err)); GUI.color = Color.white; }
            if (st != null && st.Previous != null) PreviousOffer(l, st.Previous);

            l.Gap(4f);
            if (Button(l, "HV_Model_Change", "HV_Tip_ChangeModel")) Find.WindowStack.Add(new Window_EngineSetup());
            if (Button(l, "HV_Model_UseOwnFile", "HV_Tip_UseOwnFile")) Find.WindowStack.Add(new Window_CustomGguf());

            var trow = l.GetRect(30f);
            TooltipHandler.TipRegion(trow, UiUtil.T("HV_Tip_Test"));
            bool running = EngineManager.TestRunning;
            if (Widgets.ButtonText(new Rect(trow.x, trow.y, 120f, 28f), UiUtil.T("HV_Test_Button"), true, true, !running && EngineManager.Client != null))
                EngineManager.RunTest();
            var t = EngineManager.LastTest;
            Color tc = Color.gray;
            string tmsg = running ? UiUtil.T("HV_Test_Running") : UiUtil.T("HV_Test_NotRun");
            if (!running && t != null)
            {
                tc = t.Light == "green" ? Green : (t.Light == "amber" ? Amber : Red);
                tmsg = (t.Message ?? "") + (t.LatencyMs > 0 ? " " + UiUtil.T("HV_Probe_Ms", t.LatencyMs) : "");
            }
            Widgets.DrawBoxSolid(new Rect(trow.x + 130f, trow.y + 6f, 16f, 16f), tc);
            GUI.color = tc;
            Widgets.Label(new Rect(trow.x + 154f, trow.y + 3f, trow.width - 154f, 26f), tmsg);
            GUI.color = Color.white;

            l.Gap(4f);
            Check(l, "HV_Eng_AutoStart", ref s.engineAutoStart, "HV_Eng_AutoStartTip");
            Check(l, "HV_Fallback_HideSetting", ref s.hideFallbackWarning, "HV_Tip_HideFallback");
            Note(l, UiUtil.T("HV_Eng_LocalNote"));
        }

        /// <summary>After a model switch the engine keeps the old file: offer to delete it.</summary>
        public static void PreviousOffer(Listing_Standard l, EnginePrevious p)
        {
            string name = !string.IsNullOrEmpty(p.Name) ? p.Name : (p.Id ?? p.File);
            var row = l.GetRect(30f);
            TooltipHandler.TipRegion(row, UiUtil.T("HV_Tip_DeleteOld"));
            Widgets.Label(new Rect(row.x, row.y + 4f, row.width - 170f, 26f), UiUtil.T("HV_Prev_Offer", name, Window_EngineSetup.Bytes(p.Bytes)));
            if (Widgets.ButtonText(new Rect(row.xMax - 160f, row.y, 160f, 28f), UiUtil.T("HV_Prev_Delete"), true, true, !EngineManager.CustomBusy))
            {
                string file = p.File, id = p.Id;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("HV_Prev_Confirm", name, Window_EngineSetup.Bytes(p.Bytes)),
                    () => { if (!string.IsNullOrEmpty(file)) EngineManager.DeleteModel(file); else EngineManager.DeleteModelById(id); }, true));
            }
        }

        static void AgentLine(Listing_Standard l)
        {
            double secs = HermesBridge.Runtime.Requests.SecondsSinceAgent(DateTime.UtcNow.Ticks);
            string text;
            Color col;
            if (secs < 0) { text = UiUtil.T("HV_Conn_AgentNever"); col = Color.gray; }
            else
            {
                text = UiUtil.T("HV_Conn_AgentLast", Ago(secs));
                col = secs < 30 ? Green : (secs < 300 ? Amber : Color.gray);
            }
            GUI.color = col; l.Label(UiUtil.T("HV_Conn_AgentLabel", text)); GUI.color = Color.white;
        }

        public static string Ago(double secs)
        {
            if (secs < 90) return UiUtil.T("HV_Ago_Seconds", (int)secs);
            if (secs < 5400) return UiUtil.T("HV_Ago_Minutes", (int)(secs / 60));
            return UiUtil.T("HV_Ago_Hours", (int)(secs / 3600));
        }

        static void TestRow(Listing_Standard l, HermesVoiceSettings s)
        {
            var row = l.GetRect(30f);
            bool running = LlmProbe.IsRunning;
            if (Widgets.ButtonText(new Rect(row.x, row.y, 120f, 28f), UiUtil.T("HV_Test_Button"), true, true, !running))
            { EngineManager.SettingsChanged(); LlmProbe.RunAsync(s.llmBaseUrl, s.llmModel, s.llmApiKey); }

            var r = LlmProbe.Last;
            Color c = Color.gray;
            string msg;
            if (running) msg = UiUtil.T("HV_Test_Running");
            else if (r == null) msg = UiUtil.T("HV_Test_NotRun");
            else
            {
                c = r.Light == ProbeLight.Green ? Green : (r.Light == ProbeLight.Amber ? Amber : Red);
                msg = ProbeMessage(r);
                // The probe found a base URL that works: keep it.
                if (!string.IsNullOrEmpty(r.EffectiveBase) && r.EffectiveBase != (s.llmBaseUrl ?? "").Trim() && r.Light != ProbeLight.Red && r.Code != ProbeCode.Loading)
                { s.llmBaseUrl = r.EffectiveBase; HermesBridge.RefreshLlm(); }
            }
            Widgets.DrawBoxSolid(new Rect(row.x + 130f, row.y + 6f, 16f, 16f), c);
            GUI.color = c;
            Widgets.Label(new Rect(row.x + 154f, row.y + 3f, row.width - 154f, 26f), msg);
            GUI.color = Color.white;
        }

        public static string ProbeMessage(ProbeResult r)
        {
            string m = UiUtil.T("HV_Probe_" + r.Code, r.Arg);
            if (r.ElapsedMs > 0 && (r.Code == ProbeCode.Ok || r.Code == ProbeCode.OkFixedBase)) m += " " + UiUtil.T("HV_Probe_Ms", r.ElapsedMs);
            return m;
        }


        // ------------------------------------------------------------------------------------ Colonists
        static void Colonists(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Talk_Title");
            SliderRow(l, "HV_Talk_Frequency", s.talkFrequency.ToString("0.0"), ref s.talkFrequency, 0f, 2f, "HV_Tip_Frequency");
            NumRow(l, "HV_Talk_PauseAt", ref s.pauseAmbientAtSpeed, ref _speedBuf, 0, 4, "HV_Talk_PauseHelp");
            l.GapLine();
            l.Label(UiUtil.T("HV_Effects_Title"));
            Note(l, UiUtil.T("HV_Effects_Help"));
            Radio(l, "HV_Effects_off", "HV_Tip_EffectsOff", s.effects == "off", () => s.effects = "off");
            Radio(l, "HV_Effects_light", "HV_Tip_EffectsLight", s.effects == "light", () => s.effects = "light");
            Radio(l, "HV_Effects_normal", "HV_Tip_EffectsNormal", s.effects == "normal", () => s.effects = "normal");
            l.GapLine();
            Check(l, "HV_Quotes_Show", ref s.showQuotes, "HV_Quotes_ShowTip");
            Check(l, "HV_Quotes_Vanilla", ref s.voiceVanillaSocial, "HV_Quotes_VanillaTip");
            Check(l, "HV_Wants_Enable", ref s.wantsEnabled, "HV_Wants_EnableTip");
            if (s.wantsEnabled) SliderRow(l, "HV_Wants_AvoidHours", s.wantsAvoidHours.ToString("0.0"), ref s.wantsAvoidHours, 0.25f, 6f, "HV_Tip_AvoidHours");
            Check(l, "HV_Dreams_Enable", ref s.dreamsEnabled, "HV_Dreams_EnableTip");
            Check(l, "HV_Recap_Enable", ref s.recapOnLoad, "HV_Recap_EnableTip");
            Check(l, "HV_Art_Enable", ref s.artDescriptions, "HV_Art_EnableTip");

            l.GapLine();
            Title(l, "HV_Events_Title");
            Note(l, UiUtil.T("HV_Events_Help"));
            Check(l, "HV_Ev_Injury", ref s.evInjury, "HV_EvTip_Injury");
            Check(l, "HV_Ev_Death", ref s.evDeath, "HV_EvTip_Death");
            Check(l, "HV_Ev_MentalBreak", ref s.evMentalBreak, "HV_EvTip_MentalBreak");
            Check(l, "HV_Ev_LevelUp", ref s.evLevelUp, "HV_EvTip_LevelUp");
            Check(l, "HV_Ev_Tend", ref s.evTend, "HV_EvTip_Tend");
            Check(l, "HV_Ev_Social", ref s.evSocial, "HV_EvTip_Social");
            Check(l, "HV_Ev_Raid", ref s.evRaid, "HV_EvTip_Raid");
            Check(l, "HV_Ev_Quest", ref s.evQuest, "HV_EvTip_Quest");
            Check(l, "HV_Ev_Lodger", ref s.evLodger, "HV_EvTip_Lodger");
            Check(l, "HV_Ev_Prisoner", ref s.evPrisoner, "HV_EvTip_Prisoner");
            Check(l, "HV_Ev_Funeral", ref s.evFuneral, "HV_EvTip_Funeral");
            Check(l, "HV_Ev_Weather", ref s.evWeather, "HV_EvTip_Weather");
            Check(l, "HV_Ev_Thought", ref s.evThought, "HV_EvTip_Thought");
            Check(l, "HV_Ev_Art", ref s.evArt, "HV_EvTip_Art");
            Check(l, "HV_Ev_Sleep", ref s.evSleep, "HV_EvTip_Sleep");
            Check(l, "HV_Ev_Firsts", ref s.evFirsts, "HV_EvTip_Firsts");
            Check(l, "HV_Ev_Gatherings", ref s.evGatherings, "HV_EvTip_Gatherings");
        }

        // ------------------------------------------------------------------------------------ Display
        static void Display(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Bubbles_Title");
            Check(l, "HV_Bubbles_Show", ref s.showBubbles, "HV_Tip_BubblesShow");
            Check(l, "HV_Bubbles_Colors", ref s.colorBubbles, "HV_Tip_BubblesColors");
            bool comic = s.bubbleStyle != "classic";
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("HV_Bubbles_Style"));
            var br = l.GetRect(30f);
            if (Widgets.ButtonText(br, UiUtil.T(comic ? "HV_Bubbles_StyleComic" : "HV_Bubbles_StyleClassic")))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption(UiUtil.T("HV_Bubbles_StyleComic"), () => s.bubbleStyle = "comic"),
                    new FloatMenuOption(UiUtil.T("HV_Bubbles_StyleClassic"), () => s.bubbleStyle = "classic")
                }));
            }
            Tip(l, y0, UiUtil.T("HV_Tip_BubbleStyle"));
            SliderRow(l, "HV_Bubbles_Opacity", Mathf.RoundToInt(s.bubbleOpacity * 100f), ref s.bubbleOpacity, 0.3f, 1f, "HV_Tip_BubbleOpacity");
            if (comic) Check(l, "HV_Bubbles_Lettering", ref s.comicLettering, "HV_Tip_Lettering");
            NumRow(l, "HV_Bubbles_Duration", ref s.bubbleDurationSec, ref _durBuf, 1, 120, "HV_Tip_BubbleDuration");
            if (comic)
            {
                l.GapLine();
                Check(l, "HV_Thoughts_Show", ref s.showThoughts, "HV_Thoughts_ShowTip");
                if (s.showThoughts)
                {
                    SliderRow(l, "HV_Thoughts_Opacity", Mathf.RoundToInt(s.thoughtOpacity * 100f), ref s.thoughtOpacity, 0.2f, 1f, "HV_Tip_ThoughtOpacity");
                    NumRow(l, "HV_Thoughts_PerPawn", ref s.thoughtPerPawnSec, ref _thoughtPawnBuf, 5, 3600, "HV_Tip_ThoughtPerPawn");
                    NumRow(l, "HV_Thoughts_Global", ref s.thoughtGlobalSec, ref _thoughtGlobalBuf, 1, 600, "HV_Tip_ThoughtGlobal");
                }
            }
            l.GapLine();
            Check(l, "HV_Camera_Allowed", ref s.directorCameraAllowed, "HV_Camera_AllowedTip");
            if (s.directorCameraAllowed)
            {
                Check(l, "HV_Camera_OnAtStart", ref s.directorCameraOnAtStart, "HV_Camera_OnAtStartTip");
                SliderRow(l, "HV_Camera_PanSpeed", s.directorPanSpeed.ToString("0.0"), ref s.directorPanSpeed, 0.5f, 6f, "HV_Tip_PanSpeed");
            }
            l.GapLine();
            Check(l, "HV_Share_Footer", ref s.shareFooter, "HV_Share_FooterTip");
        }

        // ------------------------------------------------------------------------------------ Content
        static void Content(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Content_Title");
            bool adult = s.adultContent;
            l.CheckboxLabeled(UiUtil.T("HV_Adult_Enable"), ref adult, UiUtil.T("HV_Adult_EnableTip"));
            if (adult != s.adultContent)
            {
                if (!adult) s.adultContent = false;
                else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("HV_Adult_ConfirmText"), () => s.adultContent = true));
            }
            l.Gap(6f);
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("HV_Content_AgeGate"));
            Tip(l, y0, UiUtil.T("HV_Tip_AgeGate"));
        }

        // ------------------------------------------------------------------------------------ Storage
        static string PartLabel(string name)
        {
            switch (name)
            {
                case "models": return UiUtil.T("HV_Storage_PartModels");
                case "backends": return UiUtil.T("HV_Storage_PartBackends");
                case "runtime": return UiUtil.T("HV_Storage_PartRuntime");
                case "downloads": return UiUtil.T("HV_Storage_PartDownloads");
                case "logs": return UiUtil.T("HV_Storage_PartLogs");
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

        static void Storage(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Storage_Title");
            Note(l, UiUtil.T("HV_Storage_Help"));
            EngineManager.Touch();
            if (EngineManager.Client == null) { Note(l, UiUtil.T("HV_Storage_NoEngine")); return; }
            var st = EngineManager.Storage;
            if (Button(l, "HV_Storage_Refresh", "HV_Tip_StorageRefresh", !EngineManager.StorageInFlight)) EngineManager.RefreshStorage();
            if (st == null)
            {
                if (EngineManager.StorageFailed) { GUI.color = Red; l.Label(UiUtil.T("HV_Storage_Failed")); GUI.color = Color.white; }
                else Note(l, UiUtil.T("HV_Storage_Loading"));
                return;
            }
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("HV_Storage_Folder", st.DataDir ?? ""));
            Tip(l, y0, UiUtil.T("HV_Tip_StorageFolder"));
            if (!string.IsNullOrEmpty(st.DataDir) && Button(l, "HV_Storage_OpenFolder", "HV_Tip_OpenFolder")) OpenFolder(st.DataDir);
            l.Gap(4f);
            if (st.Parts != null)
                foreach (var p in st.Parts)
                {
                    float py = l.CurHeight;
                    l.Label(UiUtil.T("HV_Storage_PartLine", PartLabel(p.Name), Window_EngineSetup.Bytes(Math.Max(0, p.Bytes))));
                    Tip(l, py, p.Path);
                }
            if (st.FreeBytes >= 0) l.Label(UiUtil.T("HV_Storage_Free", Window_EngineSetup.Bytes(st.FreeBytes)));

            l.GapLine();
            Title(l, "HV_Storage_ModelsTitle");
            if (st.Models == null || st.Models.Count == 0) Note(l, UiUtil.T("HV_Storage_NoModels"));
            else
                foreach (var m in st.Models)
                {
                    var row = l.GetRect(28f);
                    TooltipHandler.TipRegion(row, UiUtil.T(m.InUse ? "HV_Tip_ModelInUse" : "HV_Tip_ModelDelete"));
                    string line = m.File + "  (" + Window_EngineSetup.Bytes(m.Bytes) + ")";
                    if (m.InUse) line += "  " + UiUtil.T("HV_Setup_InUse");
                    if (m.Partial) line += "  " + UiUtil.T("HV_Storage_Partial");
                    Widgets.Label(new Rect(row.x, row.y + 3f, row.width - 120f, 26f), line);
                    if (Widgets.ButtonText(new Rect(row.xMax - 110f, row.y, 110f, 26f), UiUtil.T("HV_Storage_Delete"), true, true, !m.InUse && !EngineManager.CustomBusy))
                    {
                        string file = m.File;
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("HV_Storage_DeleteConfirm", file, Window_EngineSetup.Bytes(m.Bytes)), () => EngineManager.DeleteModel(file), true));
                    }
                }

            l.GapLine();
            Note(l, UiUtil.T("HV_Storage_YourFileNote"));
            if (Button(l, "HV_Storage_RemoveAll", "HV_Tip_RemoveAll", !EngineManager.CustomBusy))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(UiUtil.T("HV_Storage_RemoveConfirm", Window_EngineSetup.Bytes(RemovableBytes(st))),
                    () => EngineManager.RemoveDownloads(), true));
            }
        }

        public static void OpenFolder(string dir)
        {
            try { Application.OpenURL(new Uri(System.IO.Path.GetFullPath(dir) + System.IO.Path.DirectorySeparatorChar).AbsoluteUri); }
            catch (Exception ex) { Log.Warning("[HermesVoice] could not open " + dir + ": " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------ Advanced
        static void Advanced(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Adv_Title");
            Note(l, UiUtil.T("HV_Adv_Help"));
            string state;
            Color col;
            if (HermesBridge.IsRunning) { state = UiUtil.T("HV_Conn_BridgeRunning", HermesBridge.Port); col = Green; }
            else { state = UiUtil.T("HV_Conn_BridgeStopped"); col = Red; }
            GUI.color = col; l.Label(state); GUI.color = Color.white;
            if (!HermesBridge.IsRunning && !string.IsNullOrEmpty(HermesBridge.LastError)) { GUI.color = Red; l.Label(UiUtil.T("HV_Conn_LastError", HermesBridge.LastError)); GUI.color = Color.white; }
            AgentLine(l);

            l.Gap(4f);
            NumRow(l, "HV_Conn_Port", ref s.bridgePort, ref _portBuf, 1, 65535, "HV_Tip_Port");
            TextRow(l, "HV_Conn_Bind", ref s.bindAddress, "HV_Tip_Bind");
            if (!HermesBridge.IsLoopback(s.bindAddress) && string.IsNullOrWhiteSpace(s.sharedToken))
            { GUI.color = Red; l.Label(UiUtil.T("HV_Adv_OpenWarning")); GUI.color = Color.white; }

            l.Gap(4f);
            float y0 = l.CurHeight;
            l.Label(UiUtil.T("HV_Conn_Token"));
            var row = l.GetRect(26f);
            string token = HermesBridge.CurrentToken ?? "";
            Widgets.Label(new Rect(row.x, row.y + 2f, row.width - 250f, 24f), _revealToken ? token : Mask(token));
            if (Widgets.ButtonText(new Rect(row.xMax - 244f, row.y, 100f, 26f), UiUtil.T(_revealToken ? "HV_Conn_Hide" : "HV_Conn_Reveal"))) _revealToken = !_revealToken;
            if (Widgets.ButtonText(new Rect(row.xMax - 140f, row.y, 140f, 26f), UiUtil.T(_confirmRegen ? "HV_Conn_RegenConfirm" : "HV_Conn_Regen")))
            {
                if (_confirmRegen) { HermesBridge.RegenerateToken(); _confirmRegen = false; }
                else _confirmRegen = true;
            }
            Tip(l, y0, UiUtil.T("HV_Tip_Token"));
            if (_confirmRegen) Note(l, UiUtil.T("HV_Conn_RegenWarn"));
            TextRow(l, "HV_Adv_FixedToken", ref s.sharedToken, "HV_Adv_FixedTokenHelp");

            l.GapLine();
            TextRow(l, "HV_Events_AgentUrl", ref s.agentUrl, "HV_Tip_AgentUrl");
            Check(l, "HV_Events_Push", ref s.sendColonyEvents, "HV_Tip_Push");
            l.Gap(4f);
            if (Button(l, "HV_Conn_Restart", "HV_Tip_Restart")) HermesBridge.Start();
            if (Button(l, "HV_Setup_OpenPage", "HV_Tip_OpenPage")) Application.OpenURL(EngineManager.SetupUrl);
            l.GapLine();
            if (Prefs.DevMode)
            {
                if (Button(l, "HV_Adv_OpenDebug", "HV_Tip_Debug")) Find.WindowStack.Add(new Window_HermesDebug());
            }
            else Note(l, UiUtil.T("HV_Adv_DebugNeedsDev"));
        }
    }
}
