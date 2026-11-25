using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RimWorld;
using Verse;

namespace HermesVoice
{
    public class HermesVoiceGameComponent : GameComponent
    {
                public static HermesVoiceGameComponent Instance { get; private set; }
        public readonly Bridge.AvoidTable Avoids = new Bridge.AvoidTable();
        public readonly ConversationLog ConversationLog = new ConversationLog();
        public Bridge.MindBook Minds { get { return HermesBridge.Minds; } }

        public HermesVoiceGameComponent(Game game) : base()
        {
            Instance = this;
        }

        static int _sessionEpoch;
        public int SaveEpoch;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // The bridge keeps running from startup; a new game or load only bumps save_epoch (done in LoadedGame / StartedNewGame).
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
        }

        // Runs every frame, even while paused (GameComponentTick does not).
        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            CheckFallbackNotice();
            DirectorCamera.Update();
        }

        public override void GameComponentOnGUI()
        {
            base.GameComponentOnGUI();
            DirectorCamera.OnGUIKey();
        }

        // One per game session: the engine may still be starting when the save loads, so a loaded game arms the notice and
        // this waits (polling status through EngineManager) until the engine says which model is in use.
        public readonly Bridge.RecapGate RecapGate = new Bridge.RecapGate();

        static readonly Bridge.FallbackNotice _fallbackNotice = new Bridge.FallbackNotice();

        static readonly Bridge.StarterWatch _starter = new Bridge.StarterWatch();

        // Fast first run: say once that a starter model is speaking while the full one downloads, and once when it is in.
        static void CheckStarter()
        {
            if (!_starter.KeepPolling && !_fallbackNotice.Armed) return;
            EngineManager.Touch();
            switch (_starter.Update(EngineManager.Status != null, EngineManager.Status != null ? EngineManager.Status.ModelTier : null))
            {
                case Bridge.StarterWatch.Event.Starter:
                    Messages.Message(UiUtil.T("HV_Starter_MessageText"), MessageTypeDefOf.NeutralEvent, false);
                    break;
                case Bridge.StarterWatch.Event.Ready:
                    Messages.Message(UiUtil.T("HV_Starter_ReadyText", EngineManager.TargetName()), MessageTypeDefOf.PositiveEvent, false);
                    break;
            }
        }

        static void CheckFallbackNotice()
        {
            CheckStarter();
            long now = Environment.TickCount64;
            bool hidden = HermesVoiceMod.Settings != null && HermesVoiceMod.Settings.hideFallbackWarning;
            if (!_fallbackNotice.Waiting(now, hidden)) return;
            EngineManager.Touch();
            if (_fallbackNotice.Check(now, EngineManager.Status != null, EngineManager.FallbackInUse && !EngineManager.StarterInUse, hidden))
                Messages.Message(UiUtil.T("HV_Fallback_MessageText"), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Called when the game ends (return to main menu): stop serving and drop per-game state.</summary>
        public static void OnGameEnd()
        {
            HermesBridge.OpIds.Clear();
            SayEffects.Reset();
            HermesBridge.Minds.Clear();
            HermesBridge.Chronicle.Clear();
            HermesBridge.Art.Clear();
            SpeechBubbles.Clear();
            DirectorCamera.Reset();
            _fallbackNotice.Disarm();
            if (Instance != null) Instance.RecapGate.Disarm();
            Instance = null;
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            SaveEpoch = _sessionEpoch = Math.Max(SaveEpoch, _sessionEpoch) + 1;
            SpeechBubbles.Clear();
            _fallbackNotice.Arm(Environment.TickCount64);
            RecapGate.Arm(SaveEpoch, true, Environment.TickCount64);
            DirectorCamera.OnGameStart();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SaveEpoch = _sessionEpoch = _sessionEpoch + 1;
            _fallbackNotice.Arm(Environment.TickCount64);
            RecapGate.Arm(SaveEpoch, false, Environment.TickCount64);
            Minds.Clear();
            HermesBridge.Chronicle.Clear();
            HermesBridge.Art.Clear();
            SpeechBubbles.Clear();
            DirectorCamera.OnGameStart();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref SaveEpoch, "saveEpoch", 0);
            ExposeMinds();
            ExposeChronicle();
            ExposeArt();
            List<int> avoids = null;
            int nowTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (Scribe.mode == LoadSaveMode.Saving) avoids = Avoids.Export(nowTick);
            Scribe_Collections.Look(ref avoids, "hermesAvoids", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Avoids.Import(avoids, Find.TickManager != null ? Find.TickManager.TicksGame : 0);
            Scribe_Collections.Look(ref ConversationLog.Entries, "hermesConversation", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ConversationLog.Entries == null)
                ConversationLog.Entries = new List<ConversationEntry>();
        }

        void ExposeMinds()
        {
            List<MindSave> list = null;
            if (Scribe.mode == LoadSaveMode.Saving)
                list = Minds.Records.Select(kv => new MindSave { Id = kv.Key, Rec = kv.Value }).ToList();
            Scribe_Collections.Look(ref list, "hermesMinds", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Minds.Clear();
                if (list != null) foreach (var m in list) if (m != null && m.Rec != null) Minds.Records[m.Id] = m.Rec;
            }
        }

        void ExposeChronicle()
        {
            var book = HermesBridge.Chronicle;
            List<ChronicleSave> list = null;
            if (Scribe.mode == LoadSaveMode.Saving)
                list = book.Entries.Select(e => new ChronicleSave { Day = e.Day, Kind = e.Kind, Text = e.Text, Tick = e.Tick }).ToList();
            Scribe_Collections.Look(ref list, "hermesChronicle", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // A save without the list (older saves) loads an empty chronicle, not the one from the game just left.
                book.Clear();
                if (list != null)
                    foreach (var e in list)
                        if (e != null && !string.IsNullOrEmpty(e.Text)) book.Add(e.Day, Bridge.ChronicleBook.KnownKind(e.Kind) ? e.Kind : Bridge.ChronicleBook.KindDay, e.Text, e.Tick);
            }
        }

        void ExposeArt()
        {
            var book = HermesBridge.Art;
            List<ArtSave> list = null;
            if (Scribe.mode == LoadSaveMode.Saving)
                list = book.Entries.Select(e => new ArtSave { Thing = e.Thing, Text = e.Text, Tick = e.Tick }).ToList();
            Scribe_Collections.Look(ref list, "hermesArt", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Older saves have no book: they load an empty one, not the one from the game just left.
                book.Clear();
                if (list != null)
                    foreach (var e in list)
                        if (e != null && !string.IsNullOrEmpty(e.Text)) book.Set(e.Thing, e.Text, e.Tick);
            }
        }

        public void RestartBridge() { HermesBridge.Start(); }
    }

    /// <summary>Save wrapper for one pawn's MindRecord.</summary>
    public class MindSave : IExposable
    {
        public int Id;
        public Bridge.MindRecord Rec = new Bridge.MindRecord();
        List<int> _ticks;
        List<string> _texts;
        List<int> _diaryTicks;
        List<string> _diaryTexts;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id", 0);
            Scribe_Values.Look(ref Rec.MindJson, "mind");
            Scribe_Values.Look(ref Rec.Rev, "rev", 0);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                _ticks = Rec.Thoughts.Select(t => t.Key).ToList();
                _texts = Rec.Thoughts.Select(t => t.Value).ToList();
                _diaryTicks = Rec.Diaries.Select(t => t.Key).ToList();
                _diaryTexts = Rec.Diaries.Select(t => t.Value).ToList();
            }
            Scribe_Collections.Look(ref _ticks, "thoughtTicks", LookMode.Value);
            Scribe_Collections.Look(ref _texts, "thoughtTexts", LookMode.Value);
            Scribe_Collections.Look(ref _diaryTicks, "diaryTicks", LookMode.Value);
            Scribe_Collections.Look(ref _diaryTexts, "diaryTexts", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && _diaryTicks != null && _diaryTexts != null)
            {
                Rec.Diaries.Clear();
                for (int i = 0; i < Math.Min(_diaryTicks.Count, _diaryTexts.Count); i++)
                    Rec.Diaries.Add(new KeyValuePair<int, string>(_diaryTicks[i], _diaryTexts[i]));
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit && _ticks != null && _texts != null)
            {
                Rec.Thoughts.Clear();
                for (int i = 0; i < Math.Min(_ticks.Count, _texts.Count); i++)
                    Rec.Thoughts.Add(new KeyValuePair<int, string>(_ticks[i], _texts[i]));
            }
        }
    }

    /// <summary>Save wrapper for one chronicle entry.</summary>
    public class ArtSave : IExposable
    {
        public int Thing, Tick;
        public string Text = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Thing, "thing", 0);
            Scribe_Values.Look(ref Tick, "tick", 0);
            Scribe_Values.Look(ref Text, "text", "");
        }
    }

    public class ChronicleSave : IExposable
    {
        public int Day, Tick;
        public string Kind = "day", Text = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Day, "day", 0);
            Scribe_Values.Look(ref Tick, "tick", 0);
            Scribe_Values.Look(ref Kind, "kind", "day");
            Scribe_Values.Look(ref Text, "text", "");
        }
    }
}
