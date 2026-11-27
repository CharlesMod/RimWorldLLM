// Port of agent/tests/conftest.py World: a real Director over a FakeBridge, a FakeLlm and the RimWorld adapter, with Ana 1, Bram 2, Cy 3.
// Bodies run on the director's loop (Director.RunOnLoop) the way the Python test's asyncio.run did, so tests that await w.D.StepAsync() and
// w.D.DrainAsync() see the same ordering.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.RimWorld;

namespace RimWorldLLM.Tests.Mind.Fakes
{
    /// <summary>A FakeLlm with a hook that runs before each call (Python tests monkeypatch w.llm.complete).</summary>
    public sealed class HookLlm : ILlmClient
    {
        public readonly FakeLlm Inner;
        public Func<Task> Before;
        public HookLlm(FakeLlm inner) { Inner = inner; }
        public async Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                                    IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                                    int? actor = null, JObject jsonSchema = null)
        {
            if (Before != null) await Before();
            return await Inner.CompleteAsync(messages, slot, kind, maxTokens, stop, grammar, temperature, actor, jsonSchema);
        }
    }

    public sealed class World : IDisposable
    {
        public readonly TempDir Tmp = new TempDir();
        public readonly MindConfig Cfg;
        public readonly FakeBridge Fb = new FakeBridge("tok");
        public double Real;
        public readonly RimWorldAdapter Adapter = new RimWorldAdapter();
        public readonly BridgeClient Bridge;
        public readonly FakeLlm Llm = new FakeLlm();
        public readonly HookLlm Hook;
        public readonly Director D;

        public World(Action<MindConfig> tweak = null, IEventSource events = null)
        {
            // the creative-round defaults are off here so the older tests keep pinning the one-call paths
            Cfg = new MindConfig
            {
                DataDir = Tmp.Path, ThoughtEnabled = false, BridgeUrl = "http://fake", BridgeToken = "tok", TurnGapTicks = 0,
                TurnGapFloorRealS = 0.0, AimGround = false, CreativeGroundFor = new string[0], CreativePlanFor = new string[0],
                CreativeBestOfFor = new string[0],
            };
            if (tweak != null) tweak(Cfg);
            Fb.AddPawn(1, "Ana", JObject.Parse("{\"traits\": [{\"def\": \"Kind\", \"label\": \"kind\", \"degree\": 0}]}"));
            Fb.AddPawn(2, "Bram", JObject.Parse("{\"traits\": [{\"def\": \"Abrasive\", \"label\": \"abrasive\", \"degree\": 0}]}"));
            Fb.AddPawn(3, "Cy");
            Bridge = FakeBridge.Client(Fb);
            Hook = new HookLlm(Llm);
            D = new Director(Cfg, Bridge, Hook, Adapter, events, () => Real, new PyRandom(1));
        }

        public async Task<World> GoAsync() { await D.StartAsync(); return this; }

        public void Dispose() { try { D.Dispose(); } catch { } Tmp.Dispose(); }
    }
}
