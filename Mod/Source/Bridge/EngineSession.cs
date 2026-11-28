using System;
using System.Threading.Tasks;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// The attachment to the local engine: its control client and the status/plan/hardware last read from it. Game-agnostic so
    /// it can be tested. In the player's own-endpoint mode there is no local engine to talk to: <see cref="SourceChanged"/>
    /// drops the client and the cached reads, and nothing polls, discovers, attaches or acts until the source is local again.
    /// </summary>
    public sealed class EngineSession
    {
        readonly Func<bool> _isLocal;
        readonly Func<Task<EngineClient>> _discover;
        volatile EngineClient _client;
        volatile EngineStatus _status;
        volatile EnginePlan _plan;
        volatile EngineHardware _hardware;
        volatile string _actionError;
        volatile bool _statusInFlight, _planInFlight, _hardwareInFlight;
        volatile bool _planStale = true;

        public EngineSession(Func<bool> isLocal, Func<Task<EngineClient>> discover)
        {
            _isLocal = isLocal;
            _discover = discover;
        }

        public EngineClient Client { get { return _client; } }
        public EngineStatus Status { get { return _status; } }
        public EnginePlan Plan { get { return _plan; } }
        public EngineHardware Hardware { get { return _hardware; } }
        public string ActionError { get { return _actionError; } set { _actionError = value; } }
        /// <summary>Set when status reports "not installed" so the game shows the first-run prompt; the owner clears it.</summary>
        public volatile bool FirstRunWanted;

        public void MarkPlanStale() { _planStale = true; }

        /// <summary>Takes a client the launcher or discovery found. Refused (false) in own mode.</summary>
        public bool Attach(EngineClient c)
        {
            if (c == null || !_isLocal()) return false;
            _client = c;
            return true;
        }

        /// <summary>The source setting changed: in own mode drop the client and everything read from it.</summary>
        public void SourceChanged()
        {
            if (_isLocal()) return;
            _client = null;
            _status = null;
            _plan = null;
            _hardware = null;
            _actionError = null;
            _planStale = true;
        }

        /// <summary>One poll tick: nothing in own mode; discovers an engine when none is attached (unless a launch is running); else reads status.</summary>
        public async Task PollAsync(bool launching)
        {
            if (!_isLocal()) return;
            if (_client == null)
            {
                if (launching) return;
                try
                {
                    var c = await _discover().ConfigureAwait(false);
                    if (c != null && Attach(c)) await FetchStatusAsync().ConfigureAwait(false);
                }
                catch { }
                return;
            }
            await FetchStatusAsync().ConfigureAwait(false);
        }

        public async Task FetchStatusAsync()
        {
            var c = _client;
            if (c == null || _statusInFlight) return;
            _statusInFlight = true;
            try
            {
                var r = await c.GetStatusAsync().ConfigureAwait(false);
                if (!ReferenceEquals(c, _client)) return;   // dropped or replaced while the call was out: its answer is stale
                if (r.Ok && r.Value != null)
                {
                    string before = _status != null ? _status.Phase : null;
                    _status = r.Value;
                    if (before != r.Value.Phase) _planStale = true;     // installed flags change when a job ends
                    if (r.Value.Phase == EnginePhase.NotInstalled) FirstRunWanted = true;
                }
                else if (r.Unreachable) { _status = null; _client = null; }
            }
            finally { _statusInFlight = false; }
            if (_planStale) await FetchPlanAsync().ConfigureAwait(false);
        }

        /// <summary>Refreshes plan and (once) hardware for the setup window.</summary>
        public async Task FetchPlanAsync()
        {
            var c = _client;
            if (c == null || _planInFlight) return;
            _planInFlight = true;
            try
            {
                _planStale = false;
                var r = await c.GetPlanAsync().ConfigureAwait(false);
                if (r.Ok && ReferenceEquals(c, _client)) _plan = r.Value;
                if (_hardware == null && !_hardwareInFlight && ReferenceEquals(c, _client))
                {
                    _hardwareInFlight = true;
                    try { var h = await c.GetHardwareAsync().ConfigureAwait(false); if (h.Ok && ReferenceEquals(c, _client)) _hardware = h.Value; }
                    finally { _hardwareInFlight = false; }
                }
            }
            finally { _planInFlight = false; }
        }

        /// <summary>Re-probe hardware (the "refresh" in the setup window). Does nothing without a local client.</summary>
        public Task RefreshHardwareAsync()
        {
            var c = _client;
            if (c == null) return Task.CompletedTask;
            return Task.Run(async () => { var h = await c.GetHardwareAsync(true).ConfigureAwait(false); if (h.Ok && ReferenceEquals(c, _client)) _hardware = h.Value; });
        }

        /// <summary>A button action against the local engine, then a status read. Refused with "engine not running" when no local client is attached (always so in own mode).</summary>
        public Task Act<T>(Func<EngineClient, Task<EngineResult<T>>> call)
        {
            var c = _client;
            if (c == null) { _actionError = "engine not running"; return Task.CompletedTask; }
            _actionError = null;
            return Task.Run(async () =>
            {
                var r = await call(c).ConfigureAwait(false);
                if (!r.Ok) _actionError = r.Error ?? ("HTTP " + r.HttpStatus);
                await FetchStatusAsync().ConfigureAwait(false);
            });
        }
    }
}
