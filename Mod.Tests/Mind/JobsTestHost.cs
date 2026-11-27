// A stand-in for the Director (wave 3 of the port): one object that implements every host interface of the "jobs" modules
// (IArtHost, ILoreHost, IRecapHost, IDreamHost, IStewardHost) on a FakeBridge, a FakeLlm and the real RimWorldAdapter.
// It mirrors what the Python `world` fixture gives the tests (Ana 1, Bram 2, Cy 3) and Director.step for the steward.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using MindT = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>The grounding view of the adapter (what the Director's adapter exposes to grounding.py).</summary>
    internal sealed class GroundAdapter : IGroundingAdapter
    {
        private readonly IGameAdapter _a;
        public GroundAdapter(IGameAdapter a) { _a = a; }
        public bool HasCreativeCard => true;
        public JObject CreativeCard(JObject snapshot) => _a.CreativeCard(snapshot);
        public IReadOnlyList<KeyValuePair<string, int>> RecapImportance => _a.RecapImportance.Select(p => new KeyValuePair<string, int>(p.Pattern, p.Weight)).ToList();
    }

    internal sealed class CreativeView : ICreativeHost
    {
        private readonly JobsWorld _w;
        public CreativeView(JobsWorld w) { _w = w; }
        public ILlmClient Llm => _w.Llm;
        public CreativeSettings Cfg => CreativeSettings.From(_w.Cfg);
        public List<PieceStats> CreativeLog { get; } = new List<PieceStats>();
    }

    internal sealed class GroundView : IGroundingHost
    {
        private readonly JobsWorld _w;
        public GroundView(JobsWorld w) { _w = w; Adapter = new GroundAdapter(w.Adapter); }
        public IBridge Bridge => _w.Bridge;
        public IGroundingAdapter Adapter { get; }
        public LeaseManager Leases => _w.Leases;
        public IIdleYield Idle => null;
    }

    internal sealed class StubIdle : IStewardIdle
    {
        public bool IsActive; public string KindName = ""; public List<string> Preempted = new List<string>();
        public bool Active() => IsActive;
        public string Kind => KindName;
        public void Preempt(string who) { Preempted.Add(who); IsActive = false; }
    }

    internal sealed class JobsWorld : IArtHost, ILoreHost, IRecapHost, IDreamHost, IStewardHost, IDisposable
    {
        public readonly TempDir Tmp = new TempDir();
        public readonly FakeBridge Fb = new FakeBridge("tok");
        public readonly RimWorldAdapter Adapter = new RimWorldAdapter();
        public readonly BridgeClient Client;
        public MindConfig Cfg;
        public ILlmClient Llm { get; set; } = new FakeLlm();
        public FakeLlm Fake => (FakeLlm)Llm;
        public double Real = 0.0;
        public JObject BridgeCfg { get; set; } = new JObject();
        public readonly Dictionary<int, string> NamesD = new Dictionary<int, string>();
        public readonly Dictionary<int, MindT> MindsD = new Dictionary<int, MindT>();
        public readonly Dictionary<int, HashSet<string>> TokensD = new Dictionary<int, HashSet<string>>();
        public readonly Dictionary<int, object> Ages = new Dictionary<int, object>();
        public Chronicle ChronicleD;
        public readonly ArtQueue ArtQ = new ArtQueue();
        public readonly GateStats GateD = new GateStats();
        public LeaseManager Leases { get; }
        public readonly LoreBook LoreD = new LoreBook();
        public readonly DreamBook DreamD = new DreamBook();
        public readonly List<(int Actor, string Reason)> DroppedD = new List<(int, string)>();
        public readonly HashSet<int> DirtyD = new HashSet<int>();
        public readonly List<JObject> IdleLogD = new List<JObject>();
        public readonly StubIdle IdleD = new StubIdle();
        public readonly CreativeView CreativeD;
        public readonly GroundView GroundD;
        public int RecapEpoch { get; set; }
        public IBackend ToolBackend { get; set; }
        public string ToolMode { get; set; } = "grammar";
        public Steward Steward;
        public readonly List<Task> Spawned = new List<Task>();
        private int _eventSeq;

        public JobsWorld(bool creativeOff = true)
        {
            Cfg = new MindConfig { DataDir = Tmp.Path };
            if (creativeOff)      // the Python conftest's CLASSIC_CREATIVE: the one-call paths
            {
                Cfg.AimGround = false; Cfg.CreativeGroundFor = new string[0]; Cfg.CreativePlanFor = new string[0]; Cfg.CreativeBestOfFor = new string[0];
            }
            Client = FakeBridge.Client(Fb);
            ChronicleD = new Chronicle(Tmp.Path);
            Leases = new LeaseManager(Cfg.ActorSlots, Cfg.NarratorSlot);
            CreativeD = new CreativeView(this);
            GroundD = new GroundView(this);
            Fb.AddPawn(1, "Ana"); Fb.AddPawn(2, "Bram"); Fb.AddPawn(3, "Cy");
            foreach (var (id, nm) in new[] { (1, "Ana"), (2, "Bram"), (3, "Cy") }) AddActor(id, nm);
            Steward = new Steward(this);
        }

        public void AddActor(int id, string name, int? age = 30)
        {
            NamesD[id] = name;
            TokensD[id] = new HashSet<string> { name, name.ToLowerInvariant() };
            var m = new MindT(id, name);
            m.Fixed.Text = name + " is a colonist.";
            MindsD[id] = m;
            Ages[id] = age;
        }

        public void SetAge(int id, int age)
        {
            Ages[id] = age;
            if (Fb.Pawns.TryGetValue(id, out var p)) p["age"] = age;
        }

        public async Task StartAsync()
        {
            BridgeCfg = await Client.ConfigAsync();
            await Client.HealthAsync();
        }

        public async Task RefreshConfigAsync() { BridgeCfg = await Client.ConfigAsync(); }

        // ---- the host members ---------------------------------------------------------
        public ICreativeHost Creative => CreativeD;
        public IGroundingHost Ground => GroundD;
        public IBridge Bridge => Client;
        MindConfig IArtHost.Cfg => Cfg;
        MindConfig ILoreHost.Cfg => Cfg;
        MindConfig IRecapHost.Cfg => Cfg;
        MindConfig IDreamHost.Cfg => Cfg;
        MindConfig IStewardHost.Cfg => Cfg;
        IGameAdapter IArtHost.Adapter => Adapter;
        IGameAdapter ILoreHost.Adapter => Adapter;
        IGameAdapter IRecapHost.Adapter => Adapter;
        IGameAdapter IDreamHost.Adapter => Adapter;
        IGameAdapter IStewardHost.Adapter => Adapter;
        public ModelTier Tier => new ModelTier(Cfg, () => Real);
        public ArtQueue Art => ArtQ;
        public Chronicle Chronicle => ChronicleD;
        public LoreBook Lore => LoreD;
        public DreamBook DreamBook => DreamD;
        public IDictionary<int, string> Names => NamesD;
        public IDictionary<int, MindT> Minds => MindsD;
        public IDictionary<int, HashSet<string>> NameTokens => TokensD;
        public GateStats Gate => GateD;
        public int Day => 60000;
        public IList<(int Actor, string Reason)> Dropped => DroppedD;
        public ISet<int> Dirty => DirtyD;
        public IList<JObject> IdleLog => IdleLogD;
        public IStewardIdle Idle => IdleD;
        public double Clock() => Real;
        public Task Spawn(Func<Task> work) { var t = Task.Run(work); Spawned.Add(t); return t; }

        public bool IsMinor(int actorId) => AgeGate.IsMinor(Ages.TryGetValue(actorId, out var a) ? a : null);
        public HashSet<string> MinorTokens()
        {
            var o = new HashSet<string>();
            foreach (int id in NamesD.Keys) if (IsMinor(id) && TokensD.TryGetValue(id, out var t)) o.UnionWith(t);
            return o;
        }

        // ---- Director.step for the steward: poll the events, observe them, then steward.step ----------
        public async Task PollEventsAsync()
        {
            var b = await Client.EventsAsync(_eventSeq);
            foreach (var e in (b["events"] as JArray ?? new JArray()).OfType<JObject>())
            {
                _eventSeq = Math.Max(_eventSeq, (int)e["seq"]);
                Steward.Observe(e);
            }
            if (b["next_seq"] != null) _eventSeq = Math.Max(_eventSeq, (int)b["next_seq"]);
        }

        public async Task StepAsync() { await PollEventsAsync(); await Steward.StepAsync(); }

        /// <summary>Director.drain: wait for the spawned cycle tasks.</summary>
        public async Task DrainAsync()
        {
            while (true)
            {
                var pending = Spawned.Where(t => !t.IsCompleted).ToList();
                if (pending.Count == 0) break;
                await Task.WhenAll(pending);
            }
        }

        public void Dispose()
        {
            try { DrainAsync().Wait(2000); } catch (Exception) { }
            Tmp.Dispose();
        }
    }
}
