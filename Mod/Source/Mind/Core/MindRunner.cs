// The `run` loop of core/cli.py (_run + make_tool_backend) as a library: builds the HTTP LLM client, the tool backend, the bridge client over a given
// transport and the Director, and runs them on a background loop until stopped. Never throws out of the loop: a failure is logged once and
// retried after a back-off. The transport belongs to the caller: it is never closed here. The other cli commands (test-endpoint, argument parsing, bridge.json waiting) belong to the host (docs/csmind/parity-director.md).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    public sealed class MindRunner : IDisposable
    {
        public readonly MindConfig Cfg;
        public readonly IGameAdapter Adapter;
        private readonly IBridgeTransport _transport;
        private readonly string _token;
        private readonly Func<double> _clock;
        private readonly double _backoffS;
        private CancellationTokenSource _cts;
        private Task _loop;
        /// <summary>The running director (null before the first start and after a stop).</summary>
        public Director Director { get; private set; }
        /// <summary>The last failure that ended a run attempt (null when none).</summary>
        public string LastError { get; private set; }
        public int Restarts { get; private set; }

        public MindRunner(MindConfig cfg, IGameAdapter adapter, IBridgeTransport transport, string token = "", Func<double> clock = null, double backoffS = 5.0)
        {
            Cfg = cfg; Adapter = adapter; _transport = transport; _token = token ?? ""; _clock = clock; _backoffS = backoffS;
        }

        /// <summary>The tool-loop backend (wants, the vivarium steward): the engine endpoint of the line calls, or null when both are off.
        /// Sampler fields are forced only where the config forces them.</summary>
        public static OpenAIBackend MakeToolBackend(MindConfig cfg)
        {
            if (!cfg.Wants && !cfg.Steward) return null;
            var forced = new JObject();
            var l = cfg.Llm;
            if (l.Temperature != null) forced["temperature"] = l.Temperature;
            if (l.TopK != null) forced["top_k"] = l.TopK;
            if (l.TopP != null) forced["top_p"] = l.TopP;
            if (l.MinP != null) forced["min_p"] = l.MinP;
            if (l.RepeatPenalty != null) forced["repeat_penalty"] = l.RepeatPenalty;
            return new OpenAIBackend(l.BaseUrl, l.Model, l.ApiKey, null, null, l.Engine, forced);
        }

        public bool Running { get { return _loop != null && !_loop.IsCompleted; } }

        /// <summary>Starts the loop on the thread pool (a second call while running does nothing).</summary>
        public void Start()
        {
            if (Running) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(ct));
        }

        /// <summary>Stops the loop and waits for it (the director's tasks are drained).</summary>
        public async Task StopAsync()
        {
            var c = _cts; var t = _loop;
            if (c == null) return;
            c.Cancel();
            try { if (t != null) await t.ConfigureAwait(false); } catch (Exception) { }
            _cts = null; _loop = null;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunOnceAsync(ct).ConfigureAwait(false); return; }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    string msg = PyNames.Of(e) + ": " + e.Message;
                    if (msg != LastError) LlmLog.Warning("rimworldllm", "mind run failed: " + msg);      // logged once per distinct failure
                    LastError = msg;
                    Restarts++;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(_backoffS), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(Cfg.DataDir);
            var bridge = new BridgeClient(_transport, Adapter.Protocol, _token);
            var llm = new HttpLlm(Cfg.Llm, new CallLog(Path.Combine(Cfg.DataDir, "calls.jsonl")));
            OpenAIBackend tools = MakeToolBackend(Cfg);
            var d = new Director(Cfg, bridge, llm, Adapter, null, _clock, null, true);
            d.ToolBackend = tools; d.ToolMode = "auto";
            Director = d;
            try { await d.RunAsync(ct).ConfigureAwait(false); }
            finally
            {
                try { await d.DrainAsync().ConfigureAwait(false); } catch (Exception) { }
                Director = null;
                if (tools != null) tools.Dispose();
                llm.Dispose();
            }
        }

        public void Dispose() { try { StopAsync().GetAwaiter().GetResult(); } catch (Exception) { } }
    }
}
