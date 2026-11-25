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
        }

        /// <summary>Called when the game ends (return to main menu): stop serving and drop per-game state.</summary>
        public static void OnGameEnd()
        {
            HermesBridge.OpIds.Clear();
            SayEffects.Reset();
            HermesBridge.Minds.Clear();
            SpeechBubbles.Clear();
            Instance = null;
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            SaveEpoch = _sessionEpoch = Math.Max(SaveEpoch, _sessionEpoch) + 1;
            SpeechBubbles.Clear();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SaveEpoch = _sessionEpoch = _sessionEpoch + 1;
            Minds.Clear();
            SpeechBubbles.Clear();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref SaveEpoch, "saveEpoch", 0);
            ExposeMinds();
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

        public void RestartBridge() { HermesBridge.Start(); }
    }

    /// <summary>Save wrapper for one pawn's MindRecord.</summary>
    public class MindSave : IExposable
    {
        public int Id;
        public Bridge.MindRecord Rec = new Bridge.MindRecord();
        List<int> _ticks;
        List<string> _texts;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id", 0);
            Scribe_Values.Look(ref Rec.MindJson, "mind");
            Scribe_Values.Look(ref Rec.Rev, "rev", 0);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                _ticks = Rec.Thoughts.Select(t => t.Key).ToList();
                _texts = Rec.Thoughts.Select(t => t.Value).ToList();
            }
            Scribe_Collections.Look(ref _ticks, "thoughtTicks", LookMode.Value);
            Scribe_Collections.Look(ref _texts, "thoughtTexts", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && _ticks != null && _texts != null)
            {
                Rec.Thoughts.Clear();
                for (int i = 0; i < Math.Min(_ticks.Count, _texts.Count); i++)
                    Rec.Thoughts.Add(new KeyValuePair<int, string>(_ticks[i], _texts[i]));
            }
        }
    }
}
