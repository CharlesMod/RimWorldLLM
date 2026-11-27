using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Finding an easyEngine on the network and pairing with it, inside "My own endpoint" (docs/SETTINGS.md). Network work
    /// runs on the thread pool; the UI only reads results each frame, and results are copied into the settings on the
    /// game thread (Draw or Tick). Pairing only fills llmBaseUrl / llmModel / llmApiKey; the token is never logged.
    /// </summary>
    public static class EnginePairingUi
    {
        static readonly Color Green = new Color(0.4f, 0.85f, 0.4f), Amber = new Color(0.95f, 0.75f, 0.25f), Red = new Color(0.9f, 0.3f, 0.3f);

        static PairStore _store;
        static string _storeJson;
        static volatile List<FoundEngine> _found;
        static volatile bool _finding, _findRan;
        static volatile FoundEngine _typedResult;
        static volatile bool _typedRunning;
        static string _typed = "";
        static PairingSession _session;
        static bool _outcomeApplied;
        static volatile ReconnectResult _reconnect;
        static volatile bool _checking;
        static string _lastBaseUrl;
        static bool _pairAgainMessageShown;
        static GUIStyle _bigStyle;

        static RimWorldLLMSettings S { get { return RimWorldLLMMod.Settings; } }

        static PairStore Store(RimWorldLLMSettings s)
        {
            string j = s.pairedEngines ?? "";
            if (_store == null || _storeJson != j) { _store = PairStore.FromJson(j); _storeJson = j; }
            return _store;
        }

        static EndpointFields Fields(RimWorldLLMSettings s)
        {
            return new EndpointFields { Source = s.llmSource ?? "local", BaseUrl = s.llmBaseUrl ?? "", Model = s.llmModel ?? "", ApiKey = s.llmApiKey ?? "", EngineId = s.llmEngineId ?? "" };
        }

        /// <summary>Copies fields and the store back into the settings and saves; pushes the change on when the endpoint moved.</summary>
        static void Save(RimWorldLLMSettings s, EndpointFields f, PairStore store, bool endpointChanged)
        {
            s.llmSource = f.Source; s.llmBaseUrl = f.BaseUrl; s.llmModel = f.Model; s.llmApiKey = f.ApiKey; s.llmEngineId = f.EngineId;
            s.pairedEngines = store.ToJson();
            _storeJson = s.pairedEngines;
            _lastBaseUrl = s.llmBaseUrl;
            try { RimWorldLLMMod.Instance.WriteSettings(); } catch { }
            if (endpointChanged) EngineManager.SettingsChanged();
        }

        static string BinaryPath()
        {
            try { return EngineLauncher.BinaryPath(RimWorldLLMMod.Instance.Content.RootDir, EnginePlatform.Detect()); } catch { return null; }
        }

        // ------------------------------------------------------------------------------------ start-up and every frame
        /// <summary>At game start: when the endpoint is a paired engine, find it again by engine_id and check its token.</summary>
        public static void StartupCheck()
        {
            var s = S;
            if (s == null || s.llmSource != "own" || string.IsNullOrEmpty(s.llmEngineId) || _checking) return;
            var store = Store(s).Clone();
            string id = s.llmEngineId, root = EngineAddress.RootOfBase(s.llmBaseUrl);
            _checking = true;
            Task.Run(async () =>
            {
                try { _reconnect = await EngineReconnect.CheckAsync(store, id, root, new PairingApi(), (w, c) => Mdns.BrowseAsync(w, c)).ConfigureAwait(false); }
                catch (Exception e) { Log.Warning("[RimWorldLLM] Engine check failed: " + e.GetType().Name); }
                finally { _checking = false; }
            });
        }

        /// <summary>Game thread, every frame: applies a finished check or pairing, and any 401 seen elsewhere.</summary>
        public static void Tick()
        {
            var s = S;
            if (s == null) return;
            var r = _reconnect;
            if (r != null)
            {
                _reconnect = null;
                var f = Fields(s);
                var store = Store(s);
                if (f.EngineId.Length > 0 && r.Hello != null && r.Hello.EngineId == f.EngineId)
                {
                    bool changed = PairingApply.Reconnect(f, r, store);
                    Save(s, f, store, changed);
                    if (r.Moved) Log.Message("[RimWorldLLM] Paired engine " + r.Hello.Name + " found at " + EngineAddress.Display(r.Root));
                }
            }
            if (!string.IsNullOrEmpty(s.llmEngineId) && s.llmSource == "own")
            {
                var a = EngineAuthStatus.Current;
                if (EngineAuthCodes.PairAgain(a))
                {
                    var f = Fields(s);
                    var store = Store(s);
                    if (PairingApply.Auth(f, a, store)) Save(s, f, store, true);
                    if (!_pairAgainMessageShown && Current.ProgramState == ProgramState.Playing)
                    {
                        _pairAgainMessageShown = true;
                        Messages.Message(UiUtil.T("RWL_Pair_MsgPairAgain"), RimWorld.MessageTypeDefOf.CautionInput, false);
                    }
                }
            }
            ApplySession(s);
        }

        static void ApplySession(RimWorldLLMSettings s)
        {
            var se = _session;
            if (se == null || _outcomeApplied) return;
            var p = se.Phase;
            if (p != PairPhase.Paired && p != PairPhase.Open) return;
            var o = se.Outcome;
            if (o == null) return;
            _outcomeApplied = true;
            var f = Fields(s);
            var store = Store(s);
            PairingApply.Outcome(f, o, store);
            EngineAuthStatus.Set(p == PairPhase.Paired ? EngineAuth.Paired : EngineAuth.Open);
            _pairAgainMessageShown = false;
            Save(s, f, store, true);
            Log.Message("[RimWorldLLM] " + (p == PairPhase.Paired ? "Paired with " : "Connected to ") + o.EngineName + " at " + EngineAddress.Display(o.Root));
        }

        // ------------------------------------------------------------------------------------ actions
        static void StartFind(RimWorldLLMSettings s)
        {
            if (_finding) return;
            _finding = true;
            var store = Store(s).Clone();
            string bin = BinaryPath();
            Task.Run(async () =>
            {
                try { _found = await EngineFinder.FindAsync(store, bin).ConfigureAwait(false); }
                catch { _found = new List<FoundEngine>(); }
                finally { _finding = false; _findRan = true; }
            });
        }

        static void HelloTyped(RimWorldLLMSettings s, string typed)
        {
            if (_typedRunning) return;
            _typedRunning = true;
            _typedResult = null;
            var store = Store(s).Clone();
            Task.Run(async () =>
            {
                try { _typedResult = await EngineFinder.HelloAtAsync(typed, store).ConfigureAwait(false); }
                catch { _typedResult = new FoundEngine { Name = typed }; }
                finally { _typedRunning = false; }
            });
        }

        static void Connect(RimWorldLLMSettings s, FoundEngine e)
        {
            if (e == null || string.IsNullOrEmpty(e.Root)) return;
            if (_session != null && _session.Busy) return;
            var pe = Store(s).Get(e.EngineId);
            _outcomeApplied = false;
            _session = new PairingSession(new PairingApi(), e.Root, PairingSession.ClientNameFor(Environment.MachineName), pe != null ? pe.Token : null,
                line => Log.Message("[RimWorldLLM] " + line));
            _session.Start();
        }

        static void Cancel()
        {
            var se = _session;
            if (se == null) return;
            try { se.CancelAsync(); } catch { }
        }

        // ------------------------------------------------------------------------------------ drawing
        /// <summary>The finding and pairing block inside "My own endpoint". Game thread.</summary>
        public static void Draw(Listing_Standard l, RimWorldLLMSettings s)
        {
            ApplySession(s);
            // A URL typed by hand lets go of the paired engine (its token stays stored for later).
            if (_lastBaseUrl != null && _lastBaseUrl != s.llmBaseUrl && !string.IsNullOrEmpty(s.llmEngineId))
            {
                var f = Fields(s);
                if (PairingApply.Typed(f, Store(s))) { s.llmEngineId = f.EngineId; }
            }
            _lastBaseUrl = s.llmBaseUrl;

            l.Label(UiUtil.T("RWL_Pair_Title"));
            StatusRow(l, s);

            var se = _session;
            if (se != null && se.Phase != PairPhase.Idle && se.Phase != PairPhase.Paired && se.Phase != PairPhase.Open) { SessionPanel(l, se); return; }

            var row = l.GetRect(30f);
            TooltipHandler.TipRegion(row, UiUtil.T("RWL_Pair_FindTip"));
            if (Widgets.ButtonText(new Rect(row.x, row.y, 260f, 28f), UiUtil.T(_finding ? "RWL_Pair_Finding" : "RWL_Pair_Find"), true, true, !_finding)) StartFind(s);

            var found = _found;
            if (found != null && found.Count > 0)
                foreach (var e in found) EngineRow(l, s, e);
            else if (_findRan && !_finding) { GUI.color = Color.gray; l.Label(UiUtil.T("RWL_Pair_NoneFound")); GUI.color = Color.white; }

            // A typed address.
            var trow = l.GetRect(30f);
            TooltipHandler.TipRegion(trow, UiUtil.T("RWL_Pair_TypedTip"));
            Widgets.Label(new Rect(trow.x, trow.y + 4f, 150f, 24f), UiUtil.T("RWL_Pair_Typed"));
            _typed = Widgets.TextField(new Rect(trow.x + 155f, trow.y + 2f, trow.width - 155f - 130f, 24f), _typed ?? "");
            if (Widgets.ButtonText(new Rect(trow.xMax - 120f, trow.y, 120f, 28f), UiUtil.T("RWL_Pair_Look"), true, true, !_typedRunning && !string.IsNullOrWhiteSpace(_typed)))
                HelloTyped(s, _typed);
            var tr = _typedResult;
            if (_typedRunning) { GUI.color = Color.gray; l.Label(UiUtil.T("RWL_Pair_Looking")); GUI.color = Color.white; }
            else if (tr != null)
            {
                if (tr.Answered) EngineRow(l, s, tr);
                else { GUI.color = Red; l.Label(UiUtil.T("RWL_Pair_NoEngineAt", tr.Name ?? "")); GUI.color = Color.white; }
            }
            l.Gap(6f);
        }

        static void StatusRow(Listing_Standard l, RimWorldLLMSettings s)
        {
            string id = s.llmEngineId ?? "";
            if (id.Length == 0) { GUI.color = Color.gray; l.Label(UiUtil.T("RWL_Pair_StatusTyped")); GUI.color = Color.white; return; }
            var pe = Store(s).Get(id);
            string name = pe != null && pe.Name.Length > 0 ? pe.Name : id.Substring(0, Math.Min(6, id.Length));
            string addr = EngineAddress.Display(EngineAddress.RootOfBase(s.llmBaseUrl));
            var a = EngineAuthStatus.Current;
            if (_checking) { GUI.color = Color.gray; l.Label(UiUtil.T("RWL_Pair_StatusChecking", name)); }
            else if (EngineAuthCodes.PairAgain(a) || a == EngineAuth.TokenRequired)
            {
                GUI.color = Red;
                var row = l.GetRect(30f);
                Widgets.Label(new Rect(row.x, row.y + 4f, row.width - 170f, 26f), UiUtil.T(a == EngineAuth.TokenRevoked ? "RWL_Pair_StatusRevoked" : "RWL_Pair_StatusPairAgain", name));
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(row.xMax - 160f, row.y, 160f, 28f), UiUtil.T("RWL_Pair_PairAgain"), true, true, _session == null || !_session.Busy))
                    Connect(s, new FoundEngine { EngineId = id, Name = name, Root = EngineAddress.RootOfBase(s.llmBaseUrl) ?? "" });
            }
            else if (a == EngineAuth.Open) { GUI.color = Green; l.Label(UiUtil.T("RWL_Pair_StatusOpen", name, addr)); }
            else { GUI.color = Green; l.Label(UiUtil.T("RWL_Pair_StatusPaired", name, addr)); }
            GUI.color = Color.white;
        }

        static void EngineRow(Listing_Standard l, RimWorldLLMSettings s, FoundEngine e)
        {
            var row = l.GetRect(30f);
            string what = e.Answered
                ? UiUtil.T("RWL_Pair_Row", e.Name, string.IsNullOrEmpty(e.ModelName) ? "-" : e.ModelName, EngineManager.PhaseLabel(e.Phase), e.Address)
                : UiUtil.T("RWL_Pair_RowSilent", e.Name, e.Address);
            if (e.HaveToken) what += "  " + UiUtil.T("RWL_Pair_HaveToken");
            bool current = !string.IsNullOrEmpty(e.EngineId) && e.EngineId == s.llmEngineId;
            GUI.color = e.Answered ? Color.white : Color.gray;
            Widgets.Label(new Rect(row.x + 8f, row.y + 4f, row.width - 150f, 26f), what);
            GUI.color = Color.white;
            string btn = current ? "RWL_Pair_Reconnect" : (e.HaveToken || !e.NeedsToken ? "RWL_Pair_Use" : "RWL_Pair_Connect");
            if (Widgets.ButtonText(new Rect(row.xMax - 130f, row.y, 130f, 28f), UiUtil.T(btn), true, true, e.Answered && (_session == null || !_session.Busy)))
                Connect(s, e);
        }

        static void SessionPanel(Listing_Standard l, PairingSession se)
        {
            var p = se.Phase;
            string name = se.EngineName.Length > 0 ? se.EngineName : EngineAddress.Display(se.Root);
            switch (p)
            {
                case PairPhase.Checking:
                case PairPhase.Asking:
                    GUI.color = Color.gray; l.Label(UiUtil.T("RWL_Pair_Asking", name)); GUI.color = Color.white;
                    CancelButton(l, se);
                    break;
                case PairPhase.ShowingCode:
                    {
                        l.Label(UiUtil.T("RWL_Pair_CodeIntro", name));
                        var big = l.GetRect(64f);
                        Widgets.DrawBoxSolid(big, new Color(0f, 0f, 0f, 0.35f));
                        if (_bigStyle == null) _bigStyle = new GUIStyle(Text.CurFontStyle) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
                        GUI.Label(big, PairingSession.FormatCode(se.Code), _bigStyle);
                        l.Label(UiUtil.T("RWL_Pair_CodeCompare"));
                        if (se.Reconnecting) { GUI.color = Amber; l.Label(UiUtil.T("RWL_Pair_Reconnecting")); GUI.color = Color.white; }
                        CancelButton(l, se);
                        break;
                    }
                case PairPhase.Denied: Final(l, "RWL_Pair_Denied", name); break;
                case PairPhase.Gone:
                    GUI.color = Amber; l.Label(UiUtil.T("RWL_Pair_Gone", name)); GUI.color = Color.white;
                    var row = l.GetRect(30f);
                    if (se.CanTryAgain && Widgets.ButtonText(new Rect(row.x, row.y, 160f, 28f), UiUtil.T("RWL_Pair_TryAgain"))) se.TryAgain();
                    if (Widgets.ButtonText(new Rect(row.x + (se.CanTryAgain ? 170f : 0f), row.y, 120f, 28f), UiUtil.T("RWL_Pair_Close"))) _session = null;
                    break;
                case PairPhase.NotAnEngine: Final(l, "RWL_Pair_NotAnEngine", name); break;
                case PairPhase.NoPairing: Final(l, "RWL_Pair_NoPairing", name); break;
                case PairPhase.Unreachable: Final(l, "RWL_Pair_Unreachable", name); break;
                case PairPhase.Failed: Final(l, "RWL_Pair_Failed", name, se.Detail); break;
                case PairPhase.Cancelled: _session = null; break;
            }
            l.Gap(6f);
        }

        static void CancelButton(Listing_Standard l, PairingSession se)
        {
            var row = l.GetRect(30f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, 120f, 28f), UiUtil.T("RWL_Pair_Cancel"))) { Cancel(); _session = null; }
        }

        static void Final(Listing_Standard l, string key, string name, string detail = "")
        {
            GUI.color = Red; l.Label(UiUtil.T(key, name, detail ?? "")); GUI.color = Color.white;
            var row = l.GetRect(30f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, 120f, 28f), UiUtil.T("RWL_Pair_Close"))) _session = null;
        }
    }
}
