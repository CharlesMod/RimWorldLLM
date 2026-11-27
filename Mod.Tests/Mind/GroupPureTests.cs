// The pure parts of core/group.py (keys, rolls, text helpers, GroupState, story picking, speaker planning), checked against
// the Python's own outputs (captured with csmind/director-group/gen_group.py). The 19 test_group.py tests need the Director
// (`world` fixture) and wait for it; see docs/csmind/parity-group.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class GroupPureTests
    {
        static Gathering Mk(string kind, int? lead = 1, int[] couple = null, string label = "", string ideo = "", string room = "",
            string deceased = "", string story = "", int[] speakers = null)
        {
            return new Gathering("g-1", kind, "k", lead, (couple ?? new int[0]).ToList(), null, new List<int> { 1, 2, 3 },
                (speakers ?? new[] { 1, 2, 3 }).ToList(), new Dictionary<int, string> { [1] = "Ana", [2] = "Bram", [3] = "Cy" },
                label, ideo, room, deceased, story);
        }

        [Fact]
        public void TierAndTurnCapFollowTheSpeed()
        {
            Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 3, 3 }, new double[] { 30, 60, 61, 180, 181, 360, 361, 1000 }.Select(Group.Tier));
            var rates = new double[] { 30, 100, 300, 500 };
            var gt = new[] { 3, 2, 1, 0 };
            Assert.Equal(new[] { 3, 2, 1, 0 }, rates.Select(r => Group.TurnCap("meal", r, gt, 5)));
            Assert.Equal(new[] { 3, 2, 1, 1 }, rates.Select(r => Group.TurnCap("funeral", r, gt, 5)));
            Assert.Equal(new[] { 2, 2, 2, 1 }, rates.Select(r => Group.TurnCap("wedding", r, new[] { 3, 3, 3, 3 }, 2)));
            Assert.Equal(0, Group.TurnCap("meal", 30, new int[0], 3));
        }

        [Fact]
        public void SceneClassIsReactiveForCeremoniesOnly()
        {
            foreach (var k in new[] { "wedding", "funeral", "speech", "ritual" }) Assert.Equal(Cls.Reactive, Group.SceneClass(k));
            foreach (var k in new[] { "meal", "party" }) Assert.Equal(Cls.Social, Group.SceneClass(k));
        }

        [Fact]
        public void SentencesKeepOnlyTheLongOnes()
        {
            Assert.Equal(new[]
            {
                "A long enough sentence is here!", "And another sufficiently long question?", "Tail without stop that is long enough",
            }, Group.Sentences("Short one. A long enough sentence is here! And another sufficiently long question? ok.  Tail without stop that is long enough"));
        }

        [Fact]
        public void TheGatherRollMatchesThePython()
        {
            var want = new Dictionary<string, double> { ["meal-1"] = 0.994, ["wed:7"] = 0.968, ["k"] = 0.442 };
            foreach (var kv in want)
            {
                byte[] h;
                using (var sha = SHA1.Create()) h = sha.ComputeHash(Encoding.UTF8.GetBytes("gather:" + kv.Key));
                var v = new System.Numerics.BigInteger(h.Reverse().Concat(new byte[] { 0 }).ToArray());
                Assert.Equal(kv.Value, (int)(v % 1000) / 1000.0);
            }
        }

        public static IEnumerable<object[]> TextCases()
        {
            yield return new object[] { "meal", Mk("meal"), 1, "You are eating together with Bram, Cy.", "Say something to the others that fits the moment. One short line." };
            yield return new object[] { "meal_room_story", Mk("meal", room: "kitchen", story: "Ana once fed the whole colony."), 2,
                "You are eating together with Ana, Cy in the kitchen. A story the group knows: Ana once fed the whole colony.",
                "You all know the story in the scene. Bring it up to the others in your own words, one short line." };
            yield return new object[] { "party", Mk("party", label: "birthday party"), 3, "A birthday party is under way, with Ana, Bram.", "Say something to the others that fits the moment. One short line." };
            yield return new object[] { "party_default", Mk("party"), 3, "A party is under way, with Ana, Bram.", "Say something to the others that fits the moment. One short line." };
            yield return new object[] { "wedding_me", Mk("wedding", couple: new[] { 1, 2 }), 1, "You are marrying Bram; everyone has gathered.",
                "It is the wedding. Say your vow to Bram aloud: one sincere line, in your own voice." };
            yield return new object[] { "wedding_other", Mk("wedding", couple: new[] { 1, 2 }), 3, "Ana and Bram are getting married, and everyone has gathered.",
                "The vows have been said. React in one short, warm line, in your own voice." };
            yield return new object[] { "funeral_lead", Mk("funeral", deceased: "Dot"), 1, "A funeral is being held for Dot. You are leading it.",
                "Give a short eulogy for Dot to everyone gathered, from what you remember of them. One or two plain sentences, in your own voice." };
            yield return new object[] { "funeral_other", Mk("funeral", deceased: "Dot"), 2, "A funeral is being held for Dot. Ana leads it.",
                "Add one quiet line of your own about Dot, or answer what was just said. Keep it brief." };
            yield return new object[] { "funeral_none", Mk("funeral", lead: null), 2, "A funeral is being held for someone who died.",
                "Add one quiet line of your own about the one who died, or answer what was just said. Keep it brief." };
            yield return new object[] { "speech_lead", Mk("speech", ideo: "the Founders' creed"), 1, "You are giving a speech: the Founders' creed.",
                "Say one line to the people gathered, about what this occasion is for, in your own voice." };
            yield return new object[] { "speech_other", Mk("speech", label: "a toast"), 2, "Ana is holding a speech: a toast.",
                "React in one short line to what was just said, in your own voice." };
            yield return new object[] { "ritual_plain", Mk("ritual"), 2, "Ana is holding a ritual: a ritual.",
                "React in one short line to what was just said, in your own voice." };
        }

        [Theory, MemberData(nameof(TextCases))]
        public void TriggerAndRequestMatchThePython(string label, Gathering g, int me, string trigger, string request)
        {
            Assert.Equal(trigger, Group.Trigger(g, me));
            Assert.False(string.IsNullOrEmpty(label));
            var (req, max) = Group.Request(g, me);
            Assert.Equal("=== REQUEST ===\n" + request, req);
            Assert.Null(max);
        }

        [Fact]
        public void RequestFollowsTheTurnsSoFar()
        {
            var g = Mk("wedding", couple: new[] { 1, 2 });
            g.Turns.Add(new Turn(1, "Ana", "I do."));
            Assert.Equal("=== REQUEST ===\nIt is the wedding. Say your vow to Ana aloud: one sincere line, in your own voice.", Group.Request(g, 2).Request);
            g.Turns.Add(new Turn(2, "Bram", "So do I."));
            Assert.Equal("=== REQUEST ===\nThe vows have been said. React in one short, warm line, in your own voice.", Group.Request(g, 3).Request);
            var m = Mk("meal", story: "A tale.");
            Assert.Equal("=== REQUEST ===\nYou all know the story in the scene. Bring it up to the others in your own words, one short line.", Group.Request(m, 1).Request);
            m.Story = "";
            m.Turns.Add(new Turn(1, "Ana", "Hi"));
            Assert.Equal("=== REQUEST ===\nReact to what was just said: add a detail, laugh, or answer. One short line.", Group.Request(m, 2).Request);
        }

        [Fact]
        public void CeremonyTurnsAreTheEulogyAndTheVows()
        {
            var f = Mk("funeral");
            Assert.True(Group.CeremonyTurn(f, 1)); Assert.False(Group.CeremonyTurn(f, 2));
            f.Turns.Add(new Turn(1, "Ana", "x")); Assert.False(Group.CeremonyTurn(f, 1));
            var w = Mk("wedding", couple: new[] { 1, 2 });
            Assert.True(Group.CeremonyTurn(w, 2)); Assert.False(Group.CeremonyTurn(w, 3));
            w.Turns.Add(new Turn(1, "Ana", "x")); w.Turns.Add(new Turn(2, "Bram", "y"));
            Assert.False(Group.CeremonyTurn(w, 1));
            Assert.False(Group.CeremonyTurn(Mk("meal"), 1));
        }

        [Fact]
        public void TemplateMemoryMatchesThePython()
        {
            var g = Mk("meal");
            Assert.Equal("I was at a shared meal.", Group.TemplateMemory(g, 1));
            g.Turns.Add(new Turn(1, "Ana", "Hello all."));
            Assert.Equal("I was at a shared meal. I spoke first: \"Hello all.\"", Group.TemplateMemory(g, 1));
            Assert.Equal("I was at a shared meal. Ana spoke first: \"Hello all.\"", Group.TemplateMemory(g, 2));
            g.Turns.Add(new Turn(2, "Bram", "Pass the salt."));
            Assert.Equal("I was at a shared meal. Ana spoke first: \"Hello all.\" I said: \"Pass the salt.\"", Group.TemplateMemory(g, 2));
            Assert.Equal("I was at a shared meal. Ana spoke first: \"Hello all.\"", Group.TemplateMemory(g, 3));
            var w = Mk("wedding", couple: new[] { 1, 2 }); w.Turns.Add(new Turn(1, "Ana", "I do."));
            Assert.Equal("I was at the wedding of Ana and Bram. Ana spoke first: \"I do.\"", Group.TemplateMemory(w, 3));
            var w1 = Mk("wedding", couple: new[] { 1 }); w1.Turns.Add(new Turn(1, "Ana", "I do."));
            Assert.Equal("I was at a wedding. I spoke first: \"I do.\"", Group.TemplateMemory(w1, 1));
            var f = Mk("funeral", deceased: "Dot"); f.Turns.Add(new Turn(1, "Ana", "She was kind."));
            Assert.Equal("I was at the funeral of Dot. Ana spoke first: \"She was kind.\"", Group.TemplateMemory(f, 2));
            var f2 = Mk("funeral"); f2.Turns.Add(new Turn(1, "Ana", "She was kind."));
            Assert.Equal("I was at the funeral of one of ours. Ana spoke first: \"She was kind.\"", Group.TemplateMemory(f2, 2));
            var s = Mk("speech", ideo: "the creed"); s.Turns.Add(new Turn(1, "Ana", "Hear me."));
            Assert.Equal("I was at a speech (the creed). I spoke first: \"Hear me.\"", Group.TemplateMemory(s, 1));
            var r = Mk("ritual"); r.Turns.Add(new Turn(1, "Ana", "Hear me."));
            Assert.Equal("I was at a ritual. I spoke first: \"Hear me.\"", Group.TemplateMemory(r, 1));
        }

        [Fact]
        public void NextSpeakerCyclesAMealAndFollowsTheCeremonyPlanOnce()
        {
            var g = Mk("meal");
            var seq = new List<int?>();
            for (int i = 0; i < 7; i++) { var s = Group.NextSpeaker(g); seq.Add(s); g.Turns.Add(new Turn(s.Value, "x", "t")); }
            Assert.Equal(new int?[] { 1, 2, 3, 1, 2, 3, 1 }, seq);
            var one = Mk("meal", speakers: new[] { 1 }); one.Turns.Add(new Turn(1, "x", "t"));
            Assert.Null(Group.NextSpeaker(one));
            var two = Mk("meal", speakers: new[] { 1, 2 }); two.Turns.Add(new Turn(2, "x", "t"));
            Assert.Equal(1, Group.NextSpeaker(two));
            Assert.Null(Group.NextSpeaker(Mk("meal", speakers: new int[0])));
            var f = Mk("funeral", speakers: new[] { 5, 6 });
            Assert.Equal(5, Group.NextSpeaker(f));
            f.Turns.Add(new Turn(5, "a", "b")); f.Turns.Add(new Turn(6, "a", "b"));
            Assert.Null(Group.NextSpeaker(f));
        }

        static Dictionary<int, MindM> Minds(params (int a, int b, int op)[] rels)
        {
            var m = new Dictionary<int, MindM>();
            for (int i = 1; i <= 5; i++) m[i] = new MindM(i) { Name = "P" + i };
            foreach (var (a, b, op) in rels) m[a].Relations[b.ToString()] = new Relation { Name = "P" + b, Opinion = op };
            return m;
        }

        [Fact]
        public void PlanSpeakersMatchesThePython()
        {
            var w = new Dictionary<int, double> { [1] = 1.0, [2] = 3.0, [3] = 2.0, [4] = 0.5 };
            var pool = new[] { 1, 2, 3, 4 };
            Assert.Equal(new[] { 2, 3, 1, 4 }, Group.PlanSpeakers("meal", null, new int[0], pool, Minds(), w, 6));
            Assert.Equal(new[] { 2, 3 }, Group.PlanSpeakers("meal", null, new int[0], pool, Minds(), w, 2));
            Assert.Equal(new[] { 2 }, Group.PlanSpeakers("meal", null, new int[0], pool, Minds(), w, 0));
            Assert.Equal(new[] { 4, 2, 3, 1 }, Group.PlanSpeakers("meal", null, new int[0], pool, Minds(), w, 6, 4));
            Assert.Equal(new[] { 4, 2, 1, 3 }, Group.PlanSpeakers("meal", null, new int[0], pool, Minds((2, 4, 80), (3, 4, -50), (1, 4, 100)), w, 6, 4));
            Assert.Equal(new[] { 3, 2, 1, 4 }, Group.PlanSpeakers("party", 3, new int[0], pool, Minds(), w, 6));
            Assert.Equal(new[] { 2, 3, 1, 4 }, Group.PlanSpeakers("party", 9, new int[0], pool, Minds(), w, 6));
            Assert.Equal(new[] { 4, 1, 2, 3 }, Group.PlanSpeakers("wedding", 3, new[] { 4, 1 }, pool, Minds(), w, 6));
            Assert.Equal(new[] { 3, 2, 1, 4 }, Group.PlanSpeakers("funeral", 3, new int[0], pool, Minds(), w, 6));
            Assert.Equal(new[] { 2, 3, 1, 4 }, Group.PlanSpeakers("funeral", 9, new int[0], pool, Minds(), w, 6));
            Assert.Equal(new[] { 2, 1, 4 }, Group.PlanSpeakers("meal", null, new int[0], new[] { 4, 4, 2, 2, 1 }, Minds(), w, 6));
            Assert.Equal(new[] { 2, 1, 3, 5 }, Group.PlanSpeakers("ritual", 2, new int[0], new[] { 1, 2, 3, 5 }, Minds(), new Dictionary<int, double>(), 6));
        }

        // ---- test_group.py, the two tests that need no world ----
        [Fact]
        public void CapBySpeedAndCeremonyFloor()
        {
            var gt = new[] { 5, 4, 2, 0 };
            var rates = new double[] { 60, 180, 360, 900 };
            Assert.Equal(new[] { 5, 4, 2, 0 }, rates.Select(r => Group.TurnCap("meal", r, gt, 4)));
            Assert.Equal(new[] { 3, 3, 2, 1 }, rates.Select(r => Group.TurnCap("funeral", r, gt, 4)));
            Assert.Equal(2, Group.TurnCap("funeral", 60, gt, 2));
        }

        [Fact]
        public void PlanOrderAndNoRepeatInARow()
        {
            var minds = new Dictionary<int, MindM>();
            foreach (var i in new[] { 1, 2, 3, 4 }) minds[i] = new MindM(i);
            var none = new Dictionary<int, double>();
            Assert.Equal(3, Group.PlanSpeakers("funeral", 3, new int[0], new[] { 1, 2, 3, 4 }, minds, none, 4)[0]);
            Assert.Equal(new[] { 4, 2 }, Group.PlanSpeakers("wedding", null, new[] { 4, 2 }, new[] { 1, 2, 3, 4 }, minds, none, 4).Take(2));
            Assert.Equal(new[] { 1, 2, 3 }, Group.PlanSpeakers("meal", null, new int[0], new[] { 1, 2, 3, 4 }, minds,
                new Dictionary<int, double> { [1] = 0.2, [2] = 2.0, [3] = 1.0 }, 3, 1));
            var g = new Gathering("g", "meal", "k", null, new List<int>(), null, new List<int> { 1, 2, 3 }, new List<int> { 1, 2, 3 },
                new Dictionary<int, string> { [1] = "A", [2] = "B", [3] = "C" });
            var seq = new List<int>();
            for (int i = 0; i < 7; i++) { int s = Group.NextSpeaker(g).Value; seq.Add(s); g.Turns.Add(new Turn(s, "x", "y")); }
            Assert.True(seq.Zip(seq.Skip(1), (a, b) => a != b).All(x => x));
            Assert.Equal(new HashSet<int> { 1, 2, 3 }, new HashSet<int>(seq));
        }

        // ---- pick_story over a small host (only the members it reads are live) ----
        sealed class StoryHost : IGroupHost
        {
            public MindConfig Cfg { get { throw new NotImplementedException(); } }
            public JObject BridgeCfg { get { throw new NotImplementedException(); } }
            public IBridge Bridge { get { throw new NotImplementedException(); } }
            public IGameAdapter Adapter { get { throw new NotImplementedException(); } }
            public ILlmClient Llm { get { throw new NotImplementedException(); } }
            public LeaseManager Leases { get { throw new NotImplementedException(); } }
            public Chronicle Chronicle { get; set; }
            public LoreBook Lore { get; set; }
            public long Day { get; set; }
            public IDictionary<int, string> Names { get { throw new NotImplementedException(); } }
            public IDictionary<int, HashSet<string>> NameTokens { get; set; }
            public Dictionary<int, MindM> Minds { get { throw new NotImplementedException(); } }
            public ISet<int> Dirty { get { throw new NotImplementedException(); } }
            public IList<(int Actor, string Reason)> Dropped { get { throw new NotImplementedException(); } }
            public GateStats Gate { get { throw new NotImplementedException(); } }
            public IDictionary<int, double> Weights { get { throw new NotImplementedException(); } }
            public IDictionary<int, int> LineCount { get { throw new NotImplementedException(); } }
            public GroupState Group { get; set; }
            public int ConvN { get { throw new NotImplementedException(); } set { throw new NotImplementedException(); } }
            public ICreativeHost Creative { get { throw new NotImplementedException(); } }
            public IGroundingHost Ground { get { throw new NotImplementedException(); } }
            public Now Now() { throw new NotImplementedException(); }
            public bool IsMinor(int actorId) { throw new NotImplementedException(); }
            public Task LearnAgesAsync(IEnumerable<int> ids) { throw new NotImplementedException(); }
            public Task<(SanitizeCtx Ctx, JObject Scene, string Sig)> PrepareAsync(int aid, string trigger, JObject ev, Lease lease, int? convWith = null) { throw new NotImplementedException(); }
            public Task<bool> SayAsync(int aid, Spoken sp, int? listener, int? expires, string conv = null, string talkId = null, bool combat = false, JObject ev = null, string vanillaRef = null, bool minor = false) { throw new NotImplementedException(); }
            public Task PaceAsync(double lastReal, IEnumerable<Lease> leases) { throw new NotImplementedException(); }
            public (LineCheck Check, Action<CheckEvent> OnCheck) RepeatCheck(IList<Turn> turns, int speaker) { throw new NotImplementedException(); }
            public void RememberOwn(int aid, string text) { throw new NotImplementedException(); }
            public IList<string> LabelsFor(bool minor) { throw new NotImplementedException(); }
            public int? WireExpiry(WorkItem item) { throw new NotImplementedException(); }
        }

        static StoryHost Story()
        {
            var c = new Chronicle(null);
            c.State.Recent.Add(new ChronicleEntry { Day = 1, Game = 100, Text = "Ana rescued Bram from the collapsed cave last winter. Short. Cy planted a field of rice by the river." });
            c.Note(120, "Dot built a great tower on the hill, and Ana carried every stone herself.", 0.3, false);
            var lore = new LoreBook();
            lore.Add("The Long Winter", "the year the colony nearly starved, when Bram hunted alone", 1, 1);
            return new StoryHost
            {
                Chronicle = c, Lore = lore, Day = 60000, Group = new GroupState(),
                NameTokens = new Dictionary<int, HashSet<string>>
                {
                    [1] = new HashSet<string> { "Ana" }, [2] = new HashSet<string> { "Bram", "Hale" }, [3] = new HashSet<string> { "Cy" }, [4] = new HashSet<string> { "Dot" },
                },
            };
        }

        [Fact]
        public void PickStoryPrefersTwoNamedAndSkipsOneToldInTheLastDay()
        {
            var h = Story();
            var p = Group.PickStory(h, new[] { 1, 2 }, 5000).Value;
            Assert.Equal("Ana rescued Bram from the collapsed cave last winter.", p.Text); Assert.Equal(1, p.Opener);
            p = Group.PickStory(h, new[] { 1, 4 }, 5000).Value;
            Assert.Equal("Dot built a great tower on the hill, and Ana carried every stone herself.", p.Text); Assert.Equal(1, p.Opener);
            Assert.Null(Group.PickStory(h, new[] { 3 }, 5000));
            h.Group.Told["Ana rescued Bram from the collapsed cave last winter."] = 4000;
            p = Group.PickStory(h, new[] { 1, 2 }, 5000).Value;
            Assert.Equal("The Long Winter: the year the colony nearly starved, when Bram hunted alone", p.Text); Assert.Equal(2, p.Opener);
            Assert.Equal(1, Group.PickStory(h, new[] { 1, 2 }, 64000).Value.Opener);          // exactly a day later it may be told again
            Assert.Equal("The Long Winter: the year the colony nearly starved, when Bram hunted alone", Group.PickStory(h, new[] { 1, 2 }, 63999).Value.Text);
        }

        [Fact]
        public void GroupStateStartsEmptyAndTheGatheringCarriesItsPayload()
        {
            var gs = new GroupState();
            Assert.Empty(gs.Seen); Assert.Empty(gs.Over); Assert.Empty(gs.Told); Assert.Empty(gs.Ends); Assert.Empty(gs.Log);
            Assert.Equal(0, gs.Started + gs.Skipped + gs.Lines + gs.Rotations);
            var g = Mk("meal");
            var payload = Group.PayloadOf(g);
            var item = new WorkItem(1, 1, Cls.Social, "gathering", new Now(0, 0), null, null, null, null, payload);
            Assert.Same(g, Group.GatheringOf(item));
            Assert.Equal("", g.Ended); Assert.Equal(0, g.Cap);
        }
    }
}
