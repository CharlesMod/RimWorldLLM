using System;

namespace HermesVoice.Bridge
{
    /// <summary>What the bridge needs from a host game. A RimWorld implementation lives in the mod; another game supplies its own.</summary>
    public interface IGameAdapter
    {
        /// <summary>Current tick, pause, speed and save epoch; GameState.Menu when no game is loaded. Game thread.</summary>
        GameState BuildState();
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    /// <summary>The game-agnostic bridge: server, router, op-id store, event ring, mind book and talk queue, driven once per frame.</summary>
    public sealed class BridgeRuntime
    {
        public readonly OpIdStore OpIds;
        public readonly ApiRouter Router;
        public readonly EventRing Events = new EventRing();
        public readonly MindBook Minds = new MindBook();
        public readonly TalkQueue Talk = new TalkQueue();
        public readonly IGameAdapter Game;
        public BridgeServer Server { get; private set; }

        public BridgeRuntime(IGameAdapter game, Func<double> nowSeconds)
        {
            Game = game;
            OpIds = new OpIdStore(nowSeconds);
            Router = new ApiRouter(OpIds) { LogWarning = game.Warn };
        }

        public bool IsRunning { get { return Server != null && Server.IsRunning; } }
        public int Port { get { return Server != null ? Server.Port : Const.DefaultPort; } }
        public string LastError { get { return Server != null ? Server.LastError : ""; } }

        public bool Start(BridgeOptions opt)
        {
            Stop();
            Server = new BridgeServer(opt, Router) { LogInfo = Game.Info, LogError = Game.Error };
            Server.SetState(Game.BuildState());
            return Server.Start();
        }

        public void Stop()
        {
            var s = Server;
            Server = null;
            if (s != null) s.Stop();
        }

        /// <summary>Once per frame, game thread: publish the state and answer queued requests within the budget.</summary>
        public void FrameUpdate(double budgetMs = Const.FrameBudgetMs)
        {
            var s = Server;
            if (s == null) return;
            s.SetState(Game.BuildState());
            s.Pump(budgetMs);
        }
    }
}
