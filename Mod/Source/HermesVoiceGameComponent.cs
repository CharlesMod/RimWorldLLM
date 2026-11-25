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
            SpeechBubbles.Clear();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref SaveEpoch, "saveEpoch", 0);
            Scribe_Collections.Look(ref ConversationLog.Entries, "hermesConversation", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ConversationLog.Entries == null)
                ConversationLog.Entries = new List<ConversationEntry>();
        }

        public void RestartBridge() { HermesBridge.Start(); }
    }
}
