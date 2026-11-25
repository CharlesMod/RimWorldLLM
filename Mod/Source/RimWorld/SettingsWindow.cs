using System;
using System.Collections.Generic;
using HermesVoice.Bridge;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>The mod settings page, in sections: Connection, Talk, Effects, Events, Bubbles, Advanced.</summary>
    public static class SettingsWindow
    {
        static readonly string[] Sections = { "Connection", "Talk", "Effects", "Events", "Bubbles", "Advanced" };
        static int _section;
        static Vector2 _scroll;
        static float _viewHeight = 600f;
        static bool _revealToken, _revealKey, _confirmRegen;
        static string _portBuf, _speedBuf, _durBuf, _thoughtPawnBuf, _thoughtGlobalBuf;

        static readonly Color Green = new Color(0.4f, 0.85f, 0.4f), Amber = new Color(0.95f, 0.75f, 0.25f), Red = new Color(0.9f, 0.3f, 0.3f);

        public static void Draw(Rect inRect, HermesVoiceSettings s)
        {
            // Section row.
            float bw = inRect.width / Sections.Length;
            for (int i = 0; i < Sections.Length; i++)
            {
                var r = new Rect(inRect.x + i * bw, inRect.y, bw - 4f, 28f);
                var prev = GUI.color;
                if (i == _section) GUI.color = new Color(0.6f, 0.85f, 1f);
                if (Widgets.ButtonText(r, UiUtil.T("HV_Section_" + Sections[i]))) { _section = i; _scroll = Vector2.zero; }
                GUI.color = prev;
            }

            var outRect = new Rect(inRect.x, inRect.y + 36f, inRect.width, inRect.height - 36f);
            var view = new Rect(0f, 0f, outRect.width - 18f, _viewHeight);
            Widgets.BeginScrollView(outRect, ref _scroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            l.Begin(view);
            switch (_section)
            {
                case 0: Connection(l, s); break;
                case 1: Talk(l, s); break;
                case 2: Effects(l, s); break;
                case 3: Events(l, s); break;
                case 4: Bubbles(l, s); break;
                default: Advanced(l, s); break;
            }
            _viewHeight = l.CurHeight + 12f;
            l.End();
            Widgets.EndScrollView();
        }

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

        // ------------------------------------------------------------------------------------ Connection
        static void Connection(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Conn_Title");
            l.CheckboxLabeled(UiUtil.T("HV_Conn_Enabled"), ref s.enabled);

            // Bridge state.
            string state;
            Color col;
            if (HermesBridge.IsRunning) { state = UiUtil.T("HV_Conn_BridgeRunning", HermesBridge.Port); col = Green; }
            else { state = UiUtil.T("HV_Conn_BridgeStopped"); col = Red; }
            GUI.color = col; l.Label(state); GUI.color = Color.white;
            if (!HermesBridge.IsRunning && !string.IsNullOrEmpty(HermesBridge.LastError)) { GUI.color = Red; l.Label(UiUtil.T("HV_Conn_LastError", HermesBridge.LastError)); GUI.color = Color.white; }
            AgentLine(l);

            l.Gap(4f);
            l.Label(UiUtil.T("HV_Conn_Port"));
            if (_portBuf == null) _portBuf = s.bridgePort.ToString();
            l.TextFieldNumeric(ref s.bridgePort, ref _portBuf, 1, 65535);
            l.Label(UiUtil.T("HV_Conn_Bind"));
            s.bindAddress = Widgets.TextField(l.GetRect(24f), s.bindAddress ?? "");
            if (!HermesBridge.IsLoopback(s.bindAddress) && string.IsNullOrWhiteSpace(s.sharedToken))
            { GUI.color = Red; l.Label(HermesBridge.OpenWarning + "."); GUI.color = Color.white; }

            // Token.
            l.Gap(4f);
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
            if (_confirmRegen) Note(l, UiUtil.T("HV_Conn_RegenWarn"));

            if (l.ButtonText(UiUtil.T("HV_Conn_Restart"))) HermesBridge.Start();

            // LLM source: the local engine (default) or the player's own endpoint.
            l.GapLine();
            Title(l, "HV_Conn_EndpointTitle");
            l.CheckboxLabeled(UiUtil.T("HV_Eng_AutoStart"), ref s.engineAutoStart, UiUtil.T("HV_Eng_AutoStartTip"));
            l.Label(UiUtil.T("HV_Eng_SourceLabel"));
            bool local = s.llmSource != "own";
            if (l.RadioButton(UiUtil.T("HV_Eng_SourceLocal"), local, 0f, UiUtil.T("HV_Eng_SourceLocalTip")) && !local) { s.llmSource = "local"; EngineManager.SettingsChanged(); }
            if (l.RadioButton(UiUtil.T("HV_Eng_SourceOwn"), !local, 0f, UiUtil.T("HV_Eng_SourceOwnTip")) && local) { s.llmSource = "own"; EngineManager.SettingsChanged(); }
            l.Gap(4f);

            if (s.llmSource != "own") LocalEngine(l);
            else
            {
                Note(l, UiUtil.T("HV_Conn_EndpointHelp"));
                l.Label(UiUtil.T("HV_Conn_LlmUrl"));
                s.llmBaseUrl = Widgets.TextField(l.GetRect(24f), s.llmBaseUrl ?? "");
                l.Label(UiUtil.T("HV_Conn_LlmModel"));
                s.llmModel = Widgets.TextField(l.GetRect(24f), s.llmModel ?? "");
                l.Label(UiUtil.T("HV_Conn_LlmKey"));
                var krow = l.GetRect(26f);
                if (_revealKey) s.llmApiKey = Widgets.TextField(new Rect(krow.x, krow.y, krow.width - 110f, 24f), s.llmApiKey ?? "");
                else Widgets.Label(new Rect(krow.x, krow.y + 2f, krow.width - 110f, 24f), string.IsNullOrEmpty(s.llmApiKey) ? UiUtil.T("HV_Conn_KeyNone") : Mask(s.llmApiKey));
                if (Widgets.ButtonText(new Rect(krow.xMax - 100f, krow.y, 100f, 26f), UiUtil.T(_revealKey ? "HV_Conn_Hide" : "HV_Conn_Edit"))) _revealKey = !_revealKey;
                Note(l, UiUtil.T("HV_Conn_KeyNote"));

                TestRow(l, s);
            }
        }

        static void LocalEngine(Listing_Standard l)
        {
            EngineManager.Touch();
            var row = l.GetRect(28f);
            Color c = EngineManager.ColorOf(EngineManager.CurrentLight());
            Widgets.DrawBoxSolid(new Rect(row.x, row.y + 6f, 16f, 16f), c);
            GUI.color = c;
            Widgets.Label(new Rect(row.x + 24f, row.y + 3f, row.width - 24f, 26f), EngineManager.StatusLine());
            GUI.color = Color.white;
            string fb = EngineManager.FallbackLine();
            if (fb != null)
            {
                GUI.color = Amber;
                l.Label(fb);
                GUI.color = Color.white;
            }
            if (l.ButtonText(UiUtil.T("HV_Eng_SetUpButton"))) Find.WindowStack.Add(new Window_EngineSetup());

            var trow = l.GetRect(30f);
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
            Note(l, UiUtil.T("HV_Eng_LocalNote"));
            var hs = HermesVoiceMod.Settings;
            if (hs != null) l.CheckboxLabeled(UiUtil.T("HV_Fallback_HideSetting"), ref hs.hideFallbackWarning);
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

        // ------------------------------------------------------------------------------------ the others
        static void Talk(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Talk_Title");
            l.Label(UiUtil.T("HV_Talk_Frequency", s.talkFrequency.ToString("0.0")));
            s.talkFrequency = l.Slider(s.talkFrequency, 0f, 2f);
            l.Label(UiUtil.T("HV_Talk_PauseAt"));
            if (_speedBuf == null) _speedBuf = s.pauseAmbientAtSpeed.ToString();
            l.TextFieldNumeric(ref s.pauseAmbientAtSpeed, ref _speedBuf, 0, 4);
            Note(l, UiUtil.T("HV_Talk_PauseHelp"));
            l.GapLine();
            l.CheckboxLabeled(UiUtil.T("HV_Dreams_Enable"), ref s.dreamsEnabled, UiUtil.T("HV_Dreams_EnableTip"));
            l.CheckboxLabeled(UiUtil.T("HV_Recap_Enable"), ref s.recapOnLoad, UiUtil.T("HV_Recap_EnableTip"));
            l.CheckboxLabeled(UiUtil.T("HV_Art_Enable"), ref s.artDescriptions, UiUtil.T("HV_Art_EnableTip"));
            l.CheckboxLabeled(UiUtil.T("HV_Wants_Enable"), ref s.wantsEnabled, UiUtil.T("HV_Wants_EnableTip"));
            if (s.wantsEnabled)
            {
                l.Label(UiUtil.T("HV_Wants_AvoidHours", s.wantsAvoidHours.ToString("0.0")));
                s.wantsAvoidHours = l.Slider(s.wantsAvoidHours, 0.25f, 6f);
            }
        }

        static void Effects(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Effects_Title");
            Note(l, UiUtil.T("HV_Effects_Help"));
            foreach (var key in new[] { "off", "light", "normal" })
            {
                bool on = s.effects == key;
                if (l.RadioButton(UiUtil.T("HV_Effects_" + key), on)) s.effects = key;
            }
            l.GapLine();
            l.CheckboxLabeled(UiUtil.T("HV_Quotes_Show"), ref s.showQuotes, UiUtil.T("HV_Quotes_ShowTip"));
            l.CheckboxLabeled(UiUtil.T("HV_Quotes_Vanilla"), ref s.voiceVanillaSocial, UiUtil.T("HV_Quotes_VanillaTip"));
        }

        static void Events(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Events_Title");
            l.Label(UiUtil.T("HV_Events_AgentUrl"));
            s.agentUrl = Widgets.TextField(l.GetRect(24f), s.agentUrl ?? "");
            l.CheckboxLabeled(UiUtil.T("HV_Events_Push"), ref s.sendColonyEvents);
            l.GapLine();
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Injury"), ref s.evInjury);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Death"), ref s.evDeath);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_MentalBreak"), ref s.evMentalBreak);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_LevelUp"), ref s.evLevelUp);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Tend"), ref s.evTend);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Social"), ref s.evSocial);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Raid"), ref s.evRaid);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Quest"), ref s.evQuest);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Lodger"), ref s.evLodger);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Prisoner"), ref s.evPrisoner);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Funeral"), ref s.evFuneral);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Weather"), ref s.evWeather);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Thought"), ref s.evThought);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Art"), ref s.evArt);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Sleep"), ref s.evSleep);
            l.CheckboxLabeled(UiUtil.T("HV_Ev_Firsts"), ref s.evFirsts);
        }

        static void Bubbles(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Bubbles_Title");
            l.CheckboxLabeled(UiUtil.T("HV_Bubbles_Show"), ref s.showBubbles);
            l.CheckboxLabeled(UiUtil.T("HV_Bubbles_Colors"), ref s.colorBubbles);
            bool comic = s.bubbleStyle != "classic";
            l.Label(UiUtil.T("HV_Bubbles_Style"));
            if (l.ButtonText(UiUtil.T(comic ? "HV_Bubbles_StyleComic" : "HV_Bubbles_StyleClassic")))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption(UiUtil.T("HV_Bubbles_StyleComic"), () => s.bubbleStyle = "comic"),
                    new FloatMenuOption(UiUtil.T("HV_Bubbles_StyleClassic"), () => s.bubbleStyle = "classic")
                }));
            }
            l.Label(UiUtil.T("HV_Bubbles_Opacity", Mathf.RoundToInt(s.bubbleOpacity * 100f)));
            s.bubbleOpacity = l.Slider(s.bubbleOpacity, 0.3f, 1f);
            if (comic) l.CheckboxLabeled(UiUtil.T("HV_Bubbles_Lettering"), ref s.comicLettering);
            l.Label(UiUtil.T("HV_Bubbles_Duration"));
            if (_durBuf == null) _durBuf = s.bubbleDurationSec.ToString();
            l.TextFieldNumeric(ref s.bubbleDurationSec, ref _durBuf, 1, 120);
            if (comic)
            {
                l.CheckboxLabeled(UiUtil.T("HV_Thoughts_Show"), ref s.showThoughts, UiUtil.T("HV_Thoughts_ShowTip"));
                if (s.showThoughts)
                {
                    l.Label(UiUtil.T("HV_Thoughts_Opacity", Mathf.RoundToInt(s.thoughtOpacity * 100f)));
                    s.thoughtOpacity = l.Slider(s.thoughtOpacity, 0.2f, 1f);
                    l.Label(UiUtil.T("HV_Thoughts_PerPawn"));
                    if (_thoughtPawnBuf == null) _thoughtPawnBuf = s.thoughtPerPawnSec.ToString();
                    l.TextFieldNumeric(ref s.thoughtPerPawnSec, ref _thoughtPawnBuf, 5, 3600);
                    l.Label(UiUtil.T("HV_Thoughts_Global"));
                    if (_thoughtGlobalBuf == null) _thoughtGlobalBuf = s.thoughtGlobalSec.ToString();
                    l.TextFieldNumeric(ref s.thoughtGlobalSec, ref _thoughtGlobalBuf, 1, 600);
                }
            }
            l.GapLine();
            l.CheckboxLabeled(UiUtil.T("HV_Camera_Allowed"), ref s.directorCameraAllowed, UiUtil.T("HV_Camera_AllowedTip"));
            if (s.directorCameraAllowed)
            {
                l.CheckboxLabeled(UiUtil.T("HV_Camera_OnAtStart"), ref s.directorCameraOnAtStart, UiUtil.T("HV_Camera_OnAtStartTip"));
                l.Label(UiUtil.T("HV_Camera_PanSpeed", s.directorPanSpeed.ToString("0.0")));
                s.directorPanSpeed = l.Slider(s.directorPanSpeed, 0.5f, 6f);
            }
        }

        static void Advanced(Listing_Standard l, HermesVoiceSettings s)
        {
            Title(l, "HV_Adv_Title");
            l.Label(UiUtil.T("HV_Adv_FixedToken"));
            s.sharedToken = Widgets.TextField(l.GetRect(24f), s.sharedToken ?? "");
            Note(l, UiUtil.T("HV_Adv_FixedTokenHelp"));
            l.GapLine();
            if (Prefs.DevMode)
            {
                if (l.ButtonText(UiUtil.T("HV_Adv_OpenDebug"))) Find.WindowStack.Add(new Window_HermesDebug());
            }
            else Note(l, UiUtil.T("HV_Adv_DebugNeedsDev"));
        }
    }
}
