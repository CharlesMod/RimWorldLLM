using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RimWorldLLM.Tests
{
    sealed class FakeBioHands : IVivariumHands, IVivariumBioHands
    {
        public List<string> Calls = new List<string>();
        public HandResult Next = HandResult.Done("did it");
        public XenogermArgs LastXeno;
        public string[] LastHours = new string[0];
        public JObject Digest() { return new JObject(); }
        public HandResult SetTime(bool? p, int? s) { return Next; }
        public HandResult SetWork(int p, IDictionary<string, int> d) { return Next; }
        public HandResult SetSchedule(int p, int f, int t, string a) { return Next; }
        public HandResult SetPolicy(int p, string k, string n) { return Next; }
        public HandResult AssignBed(int p, int b) { return Next; }
        public HandResult SetResearch(string p) { return Next; }
        public HandResult AnswerLetter(int l, string c) { return Next; }
        public HandResult Draft(IList<int> p, bool d) { return Next; }
        public HandResult Choose(DialogRecord d, int o, IDictionary<string, string> t) { return Next; }
        public HandResult Order(int p, string o, int x, int z, int t, bool h) { return Next; }
        public HandResult Designate(DesignateArgs a, BuildBudget l) { return Next; }
        public HandResult Build(BuildArgs a, BuildBudget l) { return Next; }
        public HandResult Room(RoomArgs a, BuildBudget l) { return Next; }
        public HandResult Zone(ZoneArgs a, BuildBudget l) { Calls.Add("zone " + a.Kind + " " + a.Preset); return Next; }
        public HandResult Area(AreaArgs a, BuildBudget l) { return Next; }
        public HandResult Bill(BillArgs a, BuildBudget l) { return Next; }
        public JObject Benches() { return new JObject(); }
        public HandResult Undo(IList<NarratorMark> m) { return Next; }
        public JObject Combat(CombatState s) { return null; }
        public HandResult Squad(SquadArgs a, CombatState s) { return Next; }
        public HandResult Defend(DefendArgs a, BuildBudget left, CombatState s) { return Next; }
        public HandResult CombatSpeed(int speed, bool restore) { return Next; }
        public JObject TradeOffer(string with, TradeLimits l) { return null; }
        public HandResult Trade(TradeArgs a, TradeLimits l, TradeDay d) { return Next; }
        public JObject CaravanPlan(CaravanArgs a, TradeLimits l) { return null; }
        public HandResult Caravan(CaravanArgs a, int maxPawns) { return Next; }

        public HandResult MechMode(int m, int g, string mode) { Calls.Add("mech_mode " + m + " " + g + " " + mode); return Next; }
        public HandResult MechGroup(int m, int g) { Calls.Add("mech_group " + m + " " + g); return Next; }
        public HandResult MechGestate(string k, int g, int c, int hr) { Calls.Add("mech_gestate " + k + " " + g + " " + c + " " + hr); return Next; }
        public HandResult Genepack(int p, int b) { Calls.Add("genepack " + p + " " + b); return Next; }
        public HandResult Xenogerm(XenogermArgs a) { LastXeno = a; Calls.Add("xenogerm " + a.Op); return Next; }
        public HandResult Hemogen(string op, int p) { Calls.Add("hemogen " + op + " " + p); return Next; }
        public HandResult ChildSchedule(int p, string preset, string[] hours) { LastHours = hours; Calls.Add("child_schedule " + p + " " + preset); return Next; }
        public HandResult Feed(int b, int f, string m) { Calls.Add("feed " + b + " " + f + " " + m); return Next; }
        public HandResult ChildCare(int p, int pr) { Calls.Add("child_care " + p + " " + pr); return Next; }
    }

    public class VivariumBioRouteTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeBioHands Hands = new FakeBioHands();
        IVivariumHands Cur;
        int op;

        public VivariumBioRouteTests()
        {
            Cur = Hands;
            VivariumRoutes.Register(H.Router, () => V, () => Cur);
        }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "op" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        static void Bad(ApiResult r, string contains = null)
        {
            Assert.False(r.Ok); Assert.Equal("bad_request", r.Error); Assert.Equal(400, r.Status);
            if (contains != null) Assert.Contains(contains, (string)r.Body["detail"]);
        }
        void Calls(params string[] c) { Assert.Equal(c, Hands.Calls); }

        [Fact]
        public void MechModeShapeAndCall()
        {
            Bad(Post("/act/mech_mode", new { mode = "work" }), "group");
            Bad(Post("/act/mech_mode", new { group = 0, mode = "work" }));
            Bad(Post("/act/mech_mode", new { group = 13, mode = "work" }));
            Bad(Post("/act/mech_mode", new { group = 1, mode = "dance" }), "mode must be one of");
            Bad(Post("/act/mech_mode", new { group = 1 }), "mode");
            Bad(Post("/act/mech_mode", new { group = 1, mode = "work", mechanitor = -1 }));
            Assert.Empty(Hands.Calls);
            Assert.True(Post("/act/mech_mode", new { group = 2, mode = "Work" }).Ok);
            Assert.True(Post("/act/mech_mode", new { group = 3, mode = "dormant", mechanitor = 101 }).Ok);
            Assert.True(Post("/act/mech_mode", new { group = 3, mode = "self-shutdown" }).Ok);
            Calls("mech_mode 0 2 work", "mech_mode 101 3 shutdown", "mech_mode 0 3 shutdown");
        }

        [Fact]
        public void MechGroupShapeAndCall()
        {
            Bad(Post("/act/mech_group", new { mech = 0, group = 1 }));
            Bad(Post("/act/mech_group", new { mech = 5, group = 13 }));
            Bad(Post("/act/mech_group", new { group = 1 }), "mech");
            Assert.True(Post("/act/mech_group", new { mech = 501, group = 2 }).Ok);
            Calls("mech_group 501 2");
        }

        [Fact]
        public void MechGestateShapeAndCall()
        {
            Bad(Post("/act/mech_gestate", new { }), "kind");
            Bad(Post("/act/mech_gestate", new { kind = "" }));
            Bad(Post("/act/mech_gestate", new { kind = new string('x', 41) }));
            Bad(Post("/act/mech_gestate", new { kind = "lifter", count = 4 }));
            Bad(Post("/act/mech_gestate", new { kind = "lifter", count = 0 }));
            Bad(Post("/act/mech_gestate", new { kind = "lifter", gestator = -3 }));
            Assert.True(Post("/act/mech_gestate", new { kind = "lifter" }).Ok);
            Assert.True(Post("/act/mech_gestate", new { kind = "Mech_Agrihand", gestator = 610, count = 2 }).Ok);
            Calls("mech_gestate lifter 0 1 1", "mech_gestate Mech_Agrihand 610 2 1");
        }

        [Fact]
        public void GenepackCall()
        {
            Bad(Post("/act/genepack", new { genepack = -1 }));
            Assert.True(Post("/act/genepack", new { }).Ok);
            Assert.True(Post("/act/genepack", new { genepack = 7, bank = 9 }).Ok);
            Calls("genepack 0 0", "genepack 7 9");
        }

        [Fact]
        public void XenogermShapes()
        {
            string plan = "make Ana faster for hauling";
            Bad(Post("/act/xenogerm", new { }), "op");
            Bad(Post("/act/xenogerm", new { op = "brew", plan }));
            Bad(Post("/act/xenogerm", new { op = "assemble", name = "Fast", plan }), "genepacks");
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = new string[0], name = "Fast", plan }), "1..12");
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = Enumerable.Range(1, 13).Select(i => i.ToString()).ToArray(), name = "Fast", plan }), "1..12");
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = new[] { "a" }, plan }), "name");
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = new[] { "a" }, name = new string('n', 41), plan }));
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = new[] { "a" }, name = "Fast" }), "plan");
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = new[] { "a" }, name = "Fast", plan = "short" }));
            Bad(Post("/act/xenogerm", new { op = "assemble", genepacks = new[] { "a" }, name = "Fast", plan = new string('p', 201) }));
            Bad(Post("/act/xenogerm", new { op = "implant", pawn = 101, plan }), "xenogerm");
            Bad(Post("/act/xenogerm", new { op = "implant", xenogerm = 700, plan }), "pawn");
            Assert.Empty(Hands.Calls);
        }

        [Fact]
        public void XenogermAssembleDoesNotNeedPermissionAndMixesIdsAndLabels()
        {
            var r = Post("/act/xenogerm", new { op = "assemble", genepacks = new object[] { "robust", 12 }, name = "Tough", plan = "a sturdy line for the mine crew" });
            Assert.True(r.Ok);
            Assert.Equal(new[] { "robust", "12" }, Hands.LastXeno.Genepacks.ToArray());
            Assert.Equal("Tough", Hands.LastXeno.Name);
        }

        [Fact]
        public void XenogermImplantGate()
        {
            var body = new { op = "implant", xenogerm = 700, pawn = 101, plan = "give Ana the tough gene for mining" };
            var r = Post("/act/xenogerm", body);
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Assert.Contains("has not let the narrator implant genes", (string)r.Body["detail"]);
            Assert.Empty(Hands.Calls);
            Assert.Contains(V.Chat.Entries.Select(e => e.Text), t => t.StartsWith("couldn't implant a xenogerm: "));
            V.Settings.MayImplantGenes = true;
            Assert.True(Post("/act/xenogerm", body).Ok);
            Assert.Equal(700, Hands.LastXeno.Xenogerm); Assert.Equal(101, Hands.LastXeno.Pawn);
        }

        [Fact]
        public void SettingsJsonCarriesMayImplantGenes()
        {
            Assert.False((bool)V.Settings.ToJson()["may_implant_genes"]);
            V.Settings.MayImplantGenes = true;
            Assert.True((bool)V.Settings.ToJson()["may_implant_genes"]);
        }

        [Fact]
        public void HemogenShapesAndCall()
        {
            Bad(Post("/act/hemogen", new { op = "donate" }), "prisoner");
            Bad(Post("/act/hemogen", new { op = "drain", prisoner = 3 }));
            Bad(Post("/act/hemogen", new { op = "donate", prisoner = 0 }));
            Assert.True(Post("/act/hemogen", new { op = "donate", prisoner = 3 }).Ok);
            Assert.True(Post("/act/hemogen", new { op = "stop", prisoner = 3 }).Ok);
            Calls("hemogen donate 3", "hemogen stop 3");
        }

        [Fact]
        public void ChildScheduleShapesAndCall()
        {
            Bad(Post("/act/child_schedule", new { pawn = 103 }), "preset");
            Bad(Post("/act/child_schedule", new { pawn = 103, preset = "party" }));
            Bad(Post("/act/child_schedule", new { preset = "school" }), "pawn");
            Assert.True(Post("/act/child_schedule", new { pawn = 103, preset = "School" }).Ok);
            Calls("child_schedule 103 school");
            Assert.Null(Hands.LastHours);
        }

        [Fact]
        public void FeedShapesAndCall()
        {
            Bad(Post("/act/feed", new { }), "baby");
            Bad(Post("/act/feed", new { baby = 104, mode = "sometimes" }));
            Bad(Post("/act/feed", new { baby = 104, feeder = -1 }));
            Assert.True(Post("/act/feed", new { baby = 104, feeder = 101 }).Ok);
            Assert.True(Post("/act/feed", new { baby = 104, mode = "urgent" }).Ok);
            Assert.True(Post("/act/feed", new { baby = 104, feeder = 0, mode = "never" }).Ok);
            Calls("feed 104 101 childcare", "feed 104 0 urgent", "feed 104 0 never");
        }

        [Fact]
        public void ChildCareShapesAndCall()
        {
            Bad(Post("/act/child_care", new { }), "pawn");
            Bad(Post("/act/child_care", new { pawn = 101, priority = 5 }));
            Bad(Post("/act/child_care", new { pawn = 101, priority = -1 }));
            Assert.True(Post("/act/child_care", new { pawn = 101 }).Ok);
            Assert.True(Post("/act/child_care", new { pawn = 101, priority = 0 }).Ok);
            Calls("child_care 101 1", "child_care 101 0");
        }

        [Fact]
        public void PlainHandsGiveBiotechNotAvailable()
        {
            Cur = new Plain();
            var r = Post("/act/mech_group", new { mech = 5, group = 1 });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error);
            Assert.Equal("Biotech hands are not available", (string)r.Body["detail"]);
            Assert.Contains(V.Chat.Entries.Select(e => e.Text), t => t.StartsWith("couldn't move a mech to group 1: "));
        }

        [Fact]
        public void RefusalGreentext()
        {
            Hands.Next = HandResult.No("no gestator has room");
            var r = Post("/act/mech_gestate", new { kind = "lifter" });
            Assert.False(r.Ok); Assert.Equal(409, r.Status);
            Assert.Contains(V.Chat.Entries.Select(e => e.Text), t => t.Contains("couldn't start gestating a lifter: no gestator has room"));
            Assert.Equal(0, V.Actions.LastSeq);
        }

        [Fact]
        public void SuccessLogsAnAction()
        {
            Hands.Next = HandResult.Done("started gestating a lifter");
            var r = Post("/act/mech_gestate", new { kind = "lifter", why = "labor" });
            Assert.True(r.Ok); Assert.Equal("started gestating a lifter", (string)r.Body["done"]);
            Assert.Equal(1, V.Actions.LastSeq);
        }

        [Fact]
        public void EachRouteWhatString()
        {
            Hands.Next = HandResult.No("nope");
            Post("/act/mech_mode", new { group = 2, mode = "recharge" });
            Post("/act/genepack", new { });
            Post("/act/xenogerm", new { op = "assemble", genepacks = new[] { "a" }, name = "N", plan = "ten chars plan" });
            Post("/act/hemogen", new { op = "stop", prisoner = 3 });
            Post("/act/child_schedule", new { pawn = 1, preset = "play" });
            Post("/act/feed", new { baby = 1 });
            Post("/act/child_care", new { pawn = 1 });
            var t = V.Chat.Entries.Select(e => e.Text).ToList();
            foreach (var w in new[] { "set control group 2 to recharge", "store genepacks", "assemble a xenogerm", "set up blood donation",
                                      "set a child's schedule", "set who feeds a baby", "set child care" })
                Assert.Contains(t, x => x.StartsWith("couldn't " + w + ": nope"));
        }

        // zone waste preset and aliases
        [Fact]
        public void ZoneWastePresetParses()
        {
            Assert.True(Post("/act/zone", new { kind = "stockpile", preset = "waste" }).Ok);
            Calls("zone stockpile waste");
            Bad(Post("/act/zone", new { kind = "stockpile", preset = "slag" }));
        }

        [Fact]
        public void BiotechAliasesResolve()
        {
            Assert.Equal(new[] { "MechGestator" }, BuildPure.ThingDefs("mech gestator"));
            Assert.Equal(new[] { "LargeMechGestator" }, BuildPure.ThingDefs("large mech gestator"));
            Assert.Equal(new[] { "BasicRecharger", "StandardRecharger" }, BuildPure.ThingDefs("mech recharger"));
            Assert.Equal(new[] { "BasicRecharger" }, BuildPure.ThingDefs("basic recharger"));
            Assert.Equal(new[] { "StandardRecharger" }, BuildPure.ThingDefs("Standard Recharger"));
            Assert.Equal(new[] { "SchoolDesk" }, BuildPure.ThingDefs("school desk"));
            Assert.Equal(new[] { "SchoolDesk" }, BuildPure.ThingDefs("school desks"));
            Assert.Equal(new[] { "Blackboard" }, BuildPure.ThingDefs("blackboard"));
            Assert.Equal(new[] { "Crib" }, BuildPure.ThingDefs("crib"));
            Assert.Equal(new[] { "BabySleepingSpot" }, BuildPure.ThingDefs("baby sleeping spot"));
            Assert.Equal(new[] { "ToyBox" }, BuildPure.ThingDefs("toy box"));
            Assert.Equal(new[] { "GeneBank" }, BuildPure.ThingDefs("gene bank"));
            Assert.Equal(new[] { "GeneAssembler" }, BuildPure.ThingDefs("gene assembler"));
            Assert.Equal(new[] { "GeneExtractor" }, BuildPure.ThingDefs("gene extractor"));
            Assert.Equal(new[] { "GrowthVat" }, BuildPure.ThingDefs("growth vat"));
            Assert.Equal(new[] { "WastepackAtomizer" }, BuildPure.ThingDefs("wastepack atomizer"));
            Assert.Equal(new[] { "BandNode" }, BuildPure.ThingDefs("band node"));
            Assert.Equal(new[] { "DeathrestCasket" }, BuildPure.ThingDefs("deathrest casket"));
        }

        sealed class Plain : IVivariumHands
        {
            public JObject Digest() { return new JObject(); }
            public HandResult SetTime(bool? p, int? s) { return null; }
            public HandResult SetWork(int p, IDictionary<string, int> d) { return null; }
            public HandResult SetSchedule(int p, int f, int t, string a) { return null; }
            public HandResult SetPolicy(int p, string k, string n) { return null; }
            public HandResult AssignBed(int p, int b) { return null; }
            public HandResult SetResearch(string p) { return null; }
            public HandResult AnswerLetter(int l, string c) { return null; }
            public HandResult Draft(IList<int> p, bool d) { return null; }
            public HandResult Choose(DialogRecord d, int o, IDictionary<string, string> t) { return null; }
            public HandResult Order(int p, string o, int x, int z, int t, bool h) { return null; }
            public HandResult Designate(DesignateArgs a, BuildBudget l) { return null; }
            public HandResult Build(BuildArgs a, BuildBudget l) { return null; }
            public HandResult Room(RoomArgs a, BuildBudget l) { return null; }
            public HandResult Zone(ZoneArgs a, BuildBudget l) { return null; }
            public HandResult Area(AreaArgs a, BuildBudget l) { return null; }
            public HandResult Bill(BillArgs a, BuildBudget l) { return null; }
            public JObject Benches() { return null; }
            public HandResult Undo(IList<NarratorMark> m) { return null; }
            public JObject Combat(CombatState s) { return null; }
            public HandResult Squad(SquadArgs a, CombatState s) { return null; }
            public HandResult Defend(DefendArgs a, BuildBudget left, CombatState s) { return null; }
            public HandResult CombatSpeed(int speed, bool restore) { return null; }
            public JObject TradeOffer(string with, TradeLimits l) { return null; }
            public HandResult Trade(TradeArgs a, TradeLimits l, TradeDay d) { return null; }
            public JObject CaravanPlan(CaravanArgs a, TradeLimits l) { return null; }
            public HandResult Caravan(CaravanArgs a, int maxPawns) { return null; }
        }
    }

    public class BioPureTests
    {
        [Theory]
        [InlineData(0, 0, 6, 1, 1, 1, true)]
        [InlineData(4, 0, 6, 1, 1, 1, true)]    // 4+1+1 = 6 exactly
        [InlineData(5, 0, 6, 1, 1, 1, false)]
        [InlineData(3, 1, 6, 1, 1, 1, true)]
        [InlineData(3, 2, 6, 1, 1, 1, false)]
        [InlineData(2, 0, 6, 1, 3, 1, true)]    // 2+3+1 = 6
        [InlineData(3, 0, 6, 1, 3, 1, false)]
        [InlineData(0, 0, 6, 2, 2, 0, true)]
        [InlineData(0, 0, 0, 1, 1, 0, false)]
        public void BandwidthEdges(int used, int gest, int total, int need, int count, int headroom, bool ok)
        {
            string reason;
            Assert.Equal(ok, BioPure.BandwidthOk(used, gest, total, need, count, headroom, "lifter", out reason));
            Assert.Equal(ok, reason == null);
        }

        [Fact]
        public void BandwidthReasonInWords()
        {
            string r;
            BioPure.BandwidthOk(5, 0, 6, 1, 1, 1, "lifter", out r);
            Assert.Equal("not enough bandwidth: 5 of 6 used, a lifter needs 1 and 1 stays free", r);
            BioPure.BandwidthOk(4, 1, 6, 2, 2, 1, "Militor", out r);
            Assert.Equal("not enough bandwidth: 5 of 6 used, 2 militors need 4 and 1 stays free", r);
        }

        [Fact]
        public void FreeBandwidthNeverBelowZero()
        {
            Assert.Equal(2, BioPure.FreeBandwidth(3, 1, 6));
            Assert.Equal(0, BioPure.FreeBandwidth(6, 2, 6));
            Assert.Equal(1, BioPure.DefaultHeadroom);
            Assert.Equal(1, BioPure.HeadroomFor(6));
        }

        [Fact]
        public void ModeWordAndDef()
        {
            Assert.Equal("work", BioPure.ModeWord("Work"));
            Assert.Equal("escort", BioPure.ModeWord("Escort"));
            Assert.Equal("recharge", BioPure.ModeWord("Recharge"));
            Assert.Equal("shutdown", BioPure.ModeWord("SelfShutdown"));
            Assert.Equal("fancy", BioPure.ModeWord("Fancy"));
            Assert.Equal("Work", BioPure.ModeDef("work"));
            Assert.Equal("Escort", BioPure.ModeDef("ESCORT"));
            Assert.Equal("Recharge", BioPure.ModeDef("recharge"));
            Assert.Equal("SelfShutdown", BioPure.ModeDef("shutdown"));
            Assert.Equal("SelfShutdown", BioPure.ModeDef("dormant"));
            Assert.Equal("SelfShutdown", BioPure.ModeDef("self-shutdown"));
            Assert.Equal("SelfShutdown", BioPure.ModeDef("selfshutdown"));
            Assert.Null(BioPure.ModeDef("dance"));
            Assert.Null(BioPure.ModeDef(null));
        }

        static GeneInfo G(string def, string chem = null, bool hemo = false) { return new GeneInfo { DefName = def, Label = def, Chemical = chem, Hemogen = hemo }; }

        [Fact]
        public void GeneEffectsTableOrder()
        {
            var w = BioPure.GeneEffects(new[] { G("Aggression_Aggressive"), G("MoveSpeed_Quick"), G("Hemogenic") });
            Assert.Equal(new[] { "needs hemogen", "fast", "aggressive" }, w.ToArray());
        }

        [Fact]
        public void GeneEffectsChemicalHemogenUnknownDistinct()
        {
            var w = BioPure.GeneEffects(new[] { G("Zzz_Unknown"), G("Chemical_Go", "Go-juice"), G("WoundHealing_Fast"), G("WoundHealing_SuperFast"), G("Odd", null, true), G("Hemogenic") });
            Assert.Equal(new[] { "needs hemogen", "heals fast", "needs go-juice" }, w.ToArray());
            Assert.Empty(BioPure.GeneEffects(new[] { G("Beard_Bushy") }));
            Assert.Empty(BioPure.GeneEffects(null));
            Assert.Equal(new[] { "won't fight", "never sleeps" }, BioPure.GeneEffects(new[] { G("ViolenceDisabled"), G("Neversleep") }).ToArray());
        }

        [Fact]
        public void GeneEffectsCapsAtSix()
        {
            var w = BioPure.GeneEffects(new[] { G("Hemogenic"), G("Deathrest"), G("MoveSpeed_Quick"), G("Aggression_Aggressive"), G("KillThirst"),
                G("Robust"), G("Ageless"), G("FireTerror"), G("C", "Beer") });
            Assert.Equal(6, w.Count);
            Assert.Equal("needs hemogen", w[0]);
        }

        [Theory]
        [InlineData("default", 3)] [InlineData("default", 6)] [InlineData("default", 7)] [InlineData("default", 12)]
        [InlineData("school", 3)] [InlineData("school", 6)] [InlineData("school", 7)] [InlineData("school", 12)]
        [InlineData("play", 3)] [InlineData("play", 6)] [InlineData("play", 7)] [InlineData("play", 12)]
        [InlineData("school", 0)] [InlineData("play", 2)]
        public void EachPresetPassesItsOwnCheck(string preset, int age)
        {
            var h = BioPure.ChildHours(preset, age);
            Assert.Equal(24, h.Length);
            Assert.Null(BioPure.CheckChildHours(age, h));
        }

        [Fact]
        public void ChildHoursShapes()
        {
            var b = BioPure.ChildHours("school", 1);
            Assert.Equal(11, b.Count(x => x == "S"));
            Assert.Equal(13, b.Count(x => x == "A"));
            Assert.All(Enumerable.Range(19, 5), i => Assert.Equal("S", b[i]));
            Assert.Equal("S", b[0]); Assert.Equal("S", b[5]); Assert.Equal("A", b[6]);

            var d6 = BioPure.ChildHours("default", 6);
            Assert.Equal(9, d6.Count(x => x == "S")); Assert.Equal(2, d6.Count(x => x == "J")); Assert.DoesNotContain("W", d6);
            var d8 = BioPure.ChildHours("default", 8);
            Assert.Equal(2, d8.Count(x => x == "W")); Assert.Equal("W", d8[13]); Assert.Equal("A", d8[15]);
            var s = BioPure.ChildHours("school", 8);
            Assert.DoesNotContain("W", s); Assert.Equal("J", s[17]); Assert.Equal("J", s[18]); Assert.Equal("A", s[16]);
            var p = BioPure.ChildHours("play", 8);
            Assert.Equal(6, p.Count(x => x == "J")); Assert.Equal("J", p[9]); Assert.Equal("J", p[11]); Assert.Equal("A", p[12]); Assert.Equal("J", p[15]);
            Assert.Null(BioPure.ChildHours("default", 13));
        }

        static string[] Day(string s) { return s.Select(c => c.ToString()).ToArray(); }

        [Fact]
        public void CheckChildHoursViolations()
        {
            Assert.Equal("not a child", BioPure.CheckChildHours(13, BioPure.ChildHours("default", 12)));
            Assert.Equal("a child of 6 can't have work hours", BioPure.CheckChildHours(6, Day("SSSSSSAAAAAAAWAAAAAAASSS")));
            Assert.Equal("children need at least 9 hours of sleep", BioPure.CheckChildHours(8, Day("SSSSSAAAAAAAAAAAAAAAAASS")));
            Assert.Contains("at most 3 work hours", BioPure.CheckChildHours(10, Day("SSSSSSAAAAAWWWWAAAAAASSS")));
            Assert.Null(BioPure.CheckChildHours(10, Day("SSSSSSAAAAAWWWAAAAAAASSS")));
            Assert.Contains("at least 4 free hours", BioPure.CheckChildHours(10, Day("SSSSSSSSSSSSSSSSSSSSSSSW")));
            Assert.Equal("babies need at least 10 hours of sleep", BioPure.CheckChildHours(1, Day("SSSSSSSSSAAAAAAAAAAAAAAA")));
            Assert.Equal("a baby can't have work hours", BioPure.CheckChildHours(1, Day("SSSSSSSSSSSSSSSSSSSSSSSW")));
            Assert.Equal("a baby can't have joy hours", BioPure.CheckChildHours(1, Day("SSSSSSSSSSSSSSSSSSSSSSSJ")));
            Assert.NotNull(BioPure.CheckChildHours(5, new[] { "S" }));
            Assert.NotNull(BioPure.CheckChildHours(5, null));
            Assert.NotNull(BioPure.CheckChildHours(5, Day("SSSSSSSSSXAAAAAAAAAAAAAA")));
        }

        [Fact]
        public void MissingOrderAndCap()
        {
            Assert.Empty(BioPure.Missing(new BioMissingInput()));
            Assert.Empty(BioPure.Missing(null));
            var all = new BioMissingInput { Mechs = 2, Mechanitors = 1, BasicMechtech = true, Children = 1, Babies = 1, Wastepacks = 4, HemogenCarriers = 1, UnstoredGenepacks = 2 };
            var m = BioPure.Missing(all);
            Assert.Equal(new[] { "mech recharger", "mech gestator", "school desk", "crib", "waste stockpile", "hemogen packs" }, m.ToArray());   // capped at 6; gene bank dropped
            all.Rechargers = 1; all.Gestators = 1; all.SchoolDesks = 1; all.Cribs = 1; all.WasteStockpile = true; all.HemogenPacks = 3;
            Assert.Equal(new[] { "gene bank" }, BioPure.Missing(all).ToArray());
            all.GeneBanks = 1;
            Assert.Empty(BioPure.Missing(all));
        }

        [Fact]
        public void MissingGestatorNeedsResearch()
        {
            Assert.Empty(BioPure.Missing(new BioMissingInput { Mechanitors = 1, BasicMechtech = false }));
            Assert.Equal(new[] { "mech gestator" }, BioPure.Missing(new BioMissingInput { Mechanitors = 1, BasicMechtech = true }).ToArray());
        }

        [Fact]
        public void TrendWords()
        {
            Assert.Equal("growing", BioPure.Trend(10, 11));
            Assert.Equal("steady", BioPure.Trend(10, 10));
            Assert.Equal("steady", BioPure.Trend(10, 4));
        }

        [Fact]
        public void BioWordsHoursNamesTheRunsAcrossMidnight()
        {
            Assert.Equal("sleep 21-6; recreation 17-19", BioWords.Hours(BioPure.ChildHours("school", 6)));
            Assert.Equal("sleep 21-6; recreation 16-18; work 13-15", BioWords.Hours(BioPure.ChildHours("default", 8)));
            Assert.Equal("sleep 19-6", BioWords.Hours(BioPure.ChildHours("play", 1)));
            Assert.Equal("1st", BioWords.Ordinal(1));
            Assert.Equal("4th", BioWords.Ordinal(4));
        }

        [Fact]
        public void MissingCribPerBaby()
        {
            Assert.Contains("crib", BioPure.Missing(new BioMissingInput { Babies = 2, Cribs = 1 }));
            Assert.DoesNotContain("crib", BioPure.Missing(new BioMissingInput { Babies = 2, Cribs = 2 }));
        }
    }
}
