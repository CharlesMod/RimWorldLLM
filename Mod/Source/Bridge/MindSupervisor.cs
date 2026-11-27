// Pure (Verse-free) lifecycle of the mind inside the mod: start when a game is loaded and the mod is enabled, restart when what it was started
// with changes, stop when the game ends or the mod is disabled, hold off during a memory removal. Tick is cheap and never blocks or throws;
// stopping and starting happen on the thread pool. MindHost wires the real MindRunner; tests use a fake IMindProc.
using System;
using System.Threading.Tasks;
using RimWorldLLM.Mind;

namespace RimWorldLLM.Bridge
{
    /// <summary>The slice of MindRunner the supervisor drives.</summary>
    public interface IMindProc
    {
        bool Running { get; }
        string LastError { get; }
        int Restarts { get; }
        void Start();
        Task StopAsync();
    }

    public sealed class MindSupervisor
    {
        public const double RetryAfterFailureS = 15.0;
        readonly Func<MindInputs, IMindProc> _factory;
        readonly Func<double> _clock;
        readonly Action<string> _log;
        readonly object _gate = new object();
        IMindProc _proc;
        string _runKey;
        Task _stopping;
        bool _held;
        double _retryAt = double.NegativeInfinity;
        string _error;
        double _errorAt = double.NegativeInfinity;
        int _seenRestarts;
        string _lastLogged;
        public int Starts { get; private set; }

        public MindSupervisor(Func<MindInputs, IMindProc> factory, Func<double> clock, Action<string> log = null)
        {
            _factory = factory; _clock = clock; _log = log;
        }

        public bool IsRunning { get { lock (_gate) return _proc != null; } }
        public string LastError { get { lock (_gate) return _error; } }
        public double ErrorAgeS { get { lock (_gate) return _clock() - _errorAt; } }

        void Note(string msg)
        {
            _error = msg; _errorAt = _clock();
            if (msg != _lastLogged) { _lastLogged = msg; try { if (_log != null) _log(msg); } catch (Exception) { } }
        }

        /// <summary>Called every frame (or at any rate) from the game thread. Never throws, never blocks.</summary>
        public void Tick(MindInputs i)
        {
            try
            {
                lock (_gate)
                {
                    if (_stopping != null)
                    {
                        if (!_stopping.IsCompleted) return;
                        _stopping = null;
                    }
                    bool wants = MindPlan.Wants(i) && !_held;
                    if (_proc != null)
                    {
                        if (_proc.Restarts != _seenRestarts) { _seenRestarts = _proc.Restarts; if (!string.IsNullOrEmpty(_proc.LastError)) Note(_proc.LastError); }
                        if (!wants || _runKey != MindPlan.Key(i)) StopLocked();
                        return;
                    }
                    if (!wants || _clock() < _retryAt) return;
                    string key = MindPlan.Key(i);
                    try
                    {
                        var p = _factory(i);
                        p.Start();
                        _proc = p; _runKey = key; _seenRestarts = 0; _error = null; Starts++;
                    }
                    catch (Exception e)
                    {
                        _retryAt = _clock() + RetryAfterFailureS;
                        Note((e is ConfigError ? "config: " : "") + e.Message);
                    }
                }
            }
            catch (Exception) { }
        }

        void StopLocked()
        {
            var p = _proc; _proc = null; _runKey = null;
            _stopping = Task.Run(async () => { try { await p.StopAsync().ConfigureAwait(false); } catch (Exception) { } });
        }

        /// <summary>Stops the mind now (game ended): without waiting.</summary>
        public void StopNow()
        {
            try { lock (_gate) { if (_proc != null) StopLocked(); } } catch (Exception) { }
        }

        /// <summary>Stops the mind and keeps it off until <see cref="Release"/>; completes when it has stopped (a memory removal may then delete its files).</summary>
        public Task HoldAsync()
        {
            Task t;
            lock (_gate)
            {
                _held = true;
                if (_proc != null) StopLocked();
                t = _stopping ?? Task.CompletedTask;
            }
            return t;
        }

        public void Release() { lock (_gate) { _held = false; _retryAt = double.NegativeInfinity; } }
        public bool Held { get { lock (_gate) return _held; } }
    }
}
