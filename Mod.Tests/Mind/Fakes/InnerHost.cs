// A small test host for the inner modules (the Python tests used the Director via the `world` fixture): it implements IConcernsHost,
// IWantsHost and IGrudgesHost over a FakeBridge, the RimWorld adapter, a temp-dir Chronicle and a LeaseManager. Pawns: Ana (1, kind),
// Bram (2, abrasive), Cy (3), like conftest.World.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Fakes;
using RimWorldLLM.Mind.RimWorld;

namespace RimWorldLLM.Tests.Mind.Fakes
{
    public sealed class InnerHost : IConcernsHost, IWantsHost, IGrudgesHost, IDisposable
    {
        public MindConfig Cfg { get; set; } = new MindConfig(new MindEnv(new Dictionary<string, string>(), false, "/home/u"));
        public Dictionary<int, RimWorldLLM.Mind.Mind> Minds { get; } = new Dictionary<int, RimWorldLLM.Mind.Mind>();
        public IGameAdapter Adapter { get; } = new RimWorldAdapter();
        public IBridge Bridge { get; }
        public FakeBridge Fb;
        public JObject BridgeCfg { get; set; } = new JObject();
        public IDictionary<int, string> Names { get; } = new Dictionary<int, string>();
        public GateStats Gate { get; } = new GateStats();
        public ISet<int> Dirty { get; } = new HashSet<int>();
        public Chronicle Chronicle { get; }
        public LeaseManager Leases { get; }
        public long Day { get; set; } = 60000;
        public WantsStats Wants { get; } = new WantsStats();
        public ConcernEngine Concerns { get; }
        public GrudgeEngine Grudges { get; }
        public double Real = 1000.0;
        public Dictionary<int, int> Ages = new Dictionary<int, int>();
        public List<Task> Spawned = new List<Task>();
        readonly string dir;

        public InnerHost()
        {
            dir = Path.Combine(Path.GetTempPath(), "innerhost-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Fb = new FakeBridge("tok");
            Fb.AddPawn(1, "Ana", JObject.Parse("{\"traits\": [{\"def\": \"Kind\", \"label\": \"kind\", \"degree\": 0, \"description\": \"x\"}]}"));
            Fb.AddPawn(2, "Bram", JObject.Parse("{\"traits\": [{\"def\": \"Abrasive\", \"label\": \"abrasive\", \"degree\": 0, \"description\": \"x\"}]}"));
            Fb.AddPawn(3, "Cy");
            Bridge = FakeBridge.Client(Fb);
            Chronicle = new Chronicle(dir);
            Leases = new LeaseManager(new[] { 1, 2 });
            foreach (var kv in new[] { (1, "Ana"), (2, "Bram"), (3, "Cy") })
            {
                Names[kv.Item1] = kv.Item2;
                Minds[kv.Item1] = new RimWorldLLM.Mind.Mind(kv.Item1, kv.Item2);
                Ages[kv.Item1] = 30;
            }
            Concerns = new ConcernEngine(this);
            Grudges = new GrudgeEngine(this);
            Cfg.Concerns = true;
        }

        public void Sync() { Bridge.Env.Game = Fb.Tick; }
        public Now Now() { return new Now(Bridge.Env.Game, Real); }
        public ISet<int> MinorIds() { return new HashSet<int>(Ages.Where(a => a.Value < 18).Select(a => a.Key)); }
        public bool IsMinor(int actorId) { int a; return !Ages.TryGetValue(actorId, out a) || a < 18; }
        public ISet<string> MinorTokens()
        {
            var s = new HashSet<string>();
            foreach (int id in MinorIds()) if (Names.TryGetValue(id, out var n)) s.Add(n);
            return s;
        }
        public ISet<string> ToolsAllowed(IEnumerable<string> names, bool minorInvolved)
        {
            var r = new HashSet<string>(names);
            if (minorInvolved) r.ExceptWith(Adapter.RomanticIntents);
            return r;
        }
        public void Spawn(Func<Task> work) { Spawned.Add(work()); }
        public Task Drain() { return Task.WhenAll(Spawned.ToArray()); }
        public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
