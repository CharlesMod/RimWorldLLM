// Director, part 4: the host interfaces the other core modules take as their `d` (explicit where a type or name clashes).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public sealed partial class Director
    {
        public IDictionary<int, string> Names { get { return _names; } }
        public IDictionary<int, HashSet<string>> NameTokens { get { return _nameTokens; } }
        public ISet<int> Dirty { get { return _dirty; } }
        public IDictionary<int, double> Weights { get { return _weights; } }
        public IDictionary<int, int> LineCount { get { return _lineCount; } }
        public IDictionary<int, int> WantsLast { get { return _wantsLast; } }
        public IDictionary<int, string> Reputations { get { return _reputations; } }
        public int ConvN { get { return _convN; } set { _convN = value; } }
        public ICreativeHost Creative { get { return _creative; } }
        public IGroundingHost Ground { get { return _ground; } }
        public bool IsMinor(int actorId) { return IsMinor((int?)actorId); }

        long IConcernsHost.Day { get { return Day; } }
        long IGrudgesHost.Day { get { return Day; } }
        long IGroupHost.Day { get { return Day; } }
        IDictionary<int, Mind> IArtHost.Minds { get { return Minds; } }
        IDictionary<int, Mind> IDreamHost.Minds { get { return Minds; } }
        ISet<string> IConcernsHost.MinorTokens() { return MinorTokens(); }
        ISet<string> IWantsHost.MinorTokens() { return MinorTokens(); }
        void IGrudgesHost.Spawn(Func<Task> work) { _ = Spawn(work); }
        IList<(int Actor, string Reason)> IDreamHost.Dropped { get { return Dropped; } }
        IList<(int Actor, string Reason)> IStewardHost.Dropped { get { return Dropped; } }
        IList<(int Actor, string Reason)> ICommentaryHost.Dropped { get { return Dropped; } }
        bool ICommentaryHost.IdleActive { get { return Idle.Active(); } }
        JObject ICommentaryHost.StewardVv { get { return Steward.Vv; } }
        IList<(int Actor, string Reason)> IGroupHost.Dropped { get { return Dropped; } }
        IList<JObject> IDreamHost.IdleLog { get { return Idle.Log; } }
        IStewardIdle IStewardHost.Idle { get { return Idle; } }

        /// <summary>d.creative: the llm and settings the creative module reads, and the capped piece log.</summary>
        private sealed class CreativeView : ICreativeHost
        {
            private readonly Director _d;
            public CreativeView(Director d) { _d = d; }
            public ILlmClient Llm { get { return _d.Llm; } }
            public CreativeSettings Cfg { get { return CreativeSettings.From(_d.Cfg); } }
            public List<PieceStats> CreativeLog { get { return _d.CreativeLog; } }
        }

        /// <summary>What grounding reads of the director.</summary>
        private sealed class GroundView : IGroundingHost
        {
            private readonly Director _d;
            private readonly IGroundingAdapter _a;
            public GroundView(Director d) { _d = d; _a = new Ga(d.Adapter); }
            public IBridge Bridge { get { return _d.Bridge; } }
            public IGroundingAdapter Adapter { get { return _a; } }
            public LeaseManager Leases { get { return _d.Leases; } }
            public IIdleYield Idle { get { return _d.Idle; } }

            private sealed class Ga : IGroundingAdapter
            {
                private readonly IGameAdapter _g;
                public Ga(IGameAdapter g) { _g = g; }
                public bool HasCreativeCard { get { return true; } }
                public JObject CreativeCard(JObject snapshot) { return _g.CreativeCard(snapshot); }
                public IReadOnlyList<KeyValuePair<string, int>> RecapImportance
                {
                    get { return _g.RecapImportance.Select(p => new KeyValuePair<string, int>(p.Pattern, p.Weight)).ToList(); }
                }
            }
        }
    }
}
