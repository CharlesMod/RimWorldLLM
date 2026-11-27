using System;

namespace RimWorldLLM.Bridge
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
        public readonly ChronicleBook Chronicle = new ChronicleBook();
        public readonly ArtBook Art = new ArtBook();
        public readonly TalkQueue Talk = new TalkQueue();
        public readonly Vivarium Vivarium = new Vivarium();
        public readonly IGameAdapter Game;
        volatile BridgeServer _server;
        bool _inProcess;
        public BridgeServer Server { get { return _server; } private set { _server = value; } }

        public BridgeRuntime(IGameAdapter game, Func<double> nowSeconds)
        {
            Game = game;
            OpIds = new OpIdStore(nowSeconds);
            Router = new ApiRouter(OpIds) { LogWarning = game.Warn };
        }

        public RequestLog Requests { get { return Server != null ? Server.Requests : _noRequests; } }
        readonly RequestLog _noRequests = new RequestLog();
        public void RefreshInfo() { var s = Server; if (s != null) s.RefreshInfo(); }

        public bool IsRunning { get { return Server != null && Server.IsRunning; } }
        public int Port { get { return Server != null ? Server.Port : Const.DefaultPort; } }
        public string LastError { get { return Server != null ? Server.LastError : ""; } }

        /// <summary>True once the in-process door was opened (EnableInProcess); it stays open across Start/Stop of the HTTP side.</summary>
        public bool InProcessEnabled { get { return _inProcess; } }

        /// <summary>
        /// Opens the in-process door (the mind inside the mod submits requests without HTTP; see InProcessTransport).
        /// Makes a listener-less server when none exists yet, so FrameUpdate pumps whether or not HTTP is on.
        /// Start/Stop of the HTTP listener afterwards keep the door open. Call it once, from the game thread.
        /// </summary>
        public void EnableInProcess()
        {
            _inProcess = true;
            if (Server == null) Server = NewServer(new BridgeOptions());
            Server.EnableInProcess();
        }

        BridgeServer NewServer(BridgeOptions opt)
        {
            var s = new BridgeServer(opt, Router) { LogInfo = Game.Info, LogError = Game.Error };
            s.SetState(Game.BuildState());
            if (_inProcess) s.EnableInProcess();
            return s;
        }

        public bool Start(BridgeOptions opt)
        {
            Stop();
            Server = NewServer(opt);
            return Server.Start();
        }

        /// <summary>Stops the HTTP listener. With the in-process door open, a listener-less server takes its place.</summary>
        public void Stop()
        {
            var s = Server;
            Server = _inProcess ? NewServer(new BridgeOptions()) : null;   // swapped at once: the transport never sees a gap
            if (s != null) s.Stop();
        }

        /// <summary>Once per frame, game thread: publish the state and answer queued requests within the budget.</summary>
        public void FrameUpdate(double budgetMs = Const.FrameBudgetMs)
        {
            if (Vivarium.Live.HasFinals) Vivarium.Live.Pump(Vivarium.Chat, Game.BuildState().Tick);   // finals ended off the game thread
            var s = Server;
            if (s == null) return;
            s.SetState(Game.BuildState());
            s.Pump(budgetMs);
        }
    }
}
