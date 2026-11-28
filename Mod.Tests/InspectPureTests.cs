using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class InspectPureTests
    {
        static PawnFacts Sample()
        {
            var p = new PawnFacts { Name = "Mara", Id = 7, Age = 31, Xenotype = "Sanguophage", Role = "moral guide", Mood = 48, Job = "hauling steel", Room = "the kitchen", Dist = 14, DirKey = "n", Weapon = "revolver" };
            p.Injuries.Add(new InjuryFact { Part = "left leg", Label = "gunshot wound", Severity = 0.6, Bleeding = true });
            p.Injuries.Add(new InjuryFact { Part = "arm", Label = "bruise", Severity = 0.1 });
            p.Injuries.Add(new InjuryFact { Part = "torso", Label = "cut", Severity = 0.3, Infected = true });
            p.Needs.Add(new NeedFact { Label = "food", Pct = 12 }); p.Needs.Add(new NeedFact { Label = "rest", Pct = 80 });
            p.Thoughts.Add(new ThoughtFact { Label = "ate raw food", Mood = -12 }); p.Thoughts.Add(new ThoughtFact { Label = "nice room", Mood = 5 });
            p.Thoughts.Add(new ThoughtFact { Label = "bored", Mood = -3 }); p.Thoughts.Add(new ThoughtFact { Label = "tiny", Mood = 1 });
            p.Skills.Add(new SkillFact { Label = "shooting", Level = 12, Passion = 2 }); p.Skills.Add(new SkillFact { Label = "cooking", Level = 3 });
            p.Apparel.Add("parka");
            p.Relations.Add("wife Jo");
            return p;
        }

        [Fact]
        public void PawnPriorityOrderAndOmitsEmpty()
        {
            string s = PawnFacts.Format(Sample());
            Assert.StartsWith("Mara #7, colonist, age 31, Sanguophage, moral guide", s);
            Assert.True(s.IndexOf("bleeding") < s.IndexOf("infected"));
            Assert.True(s.IndexOf("left leg") < s.IndexOf("torso"));
            Assert.True(s.IndexOf("torso") < s.IndexOf("arm: bruise"));
            Assert.True(s.IndexOf("injuries") < s.IndexOf("needs: food 12%"));
            Assert.True(s.IndexOf("needs") < s.IndexOf("mood 48%"));
            Assert.Contains("-12 ate raw food", s); Assert.Contains("+5 nice room", s); Assert.Contains("-3 bored", s);
            Assert.DoesNotContain("tiny", s);       // only top 3 thoughts
            Assert.DoesNotContain("rest 80", s);    // satisfied needs left out
            Assert.Contains("14 cells north of home (in the kitchen)", s);
            Assert.Contains("shooting 12 (major passion)", s);
            Assert.DoesNotContain("illness", s); Assert.DoesNotContain("DOWNED", s); Assert.DoesNotContain("resistance", s);
            Assert.True(s.Length <= 600);
        }

        [Fact]
        public void PawnBreakAndDownedComeFirstAfterTheHead()
        {
            var p = Sample(); p.Downed = true; p.MentalBreak = "berserk";
            string s = PawnFacts.Format(p);
            Assert.True(s.IndexOf("DOWNED") < s.IndexOf("MENTAL BREAK: berserk"));
            Assert.True(s.IndexOf("MENTAL BREAK") < s.IndexOf("injuries"));
        }

        [Fact]
        public void PawnPrisonerAndSlaveStates()
        {
            var p = new PawnFacts { Name = "Rex", Kind = "prisoner", RecruitResistance = 4.25, Will = 2, InteractionMode = "convert", Suppression = 40 };
            string s = PawnFacts.Format(p);
            Assert.Contains("recruit resistance 4.", s);
            Assert.Contains("will 2", s); Assert.Contains("mode convert", s); Assert.Contains("suppression 40%", s);
            Assert.DoesNotContain("mood", s);
        }

        [Fact]
        public void PawnDiseasePainAndCapacities()
        {
            var p = new PawnFacts { Name = "A", Pain = 0.4 };
            p.Diseases.Add(new DiseaseFact { Label = "flu", Severity = 0.6, Immunity = 0.3 });
            p.Diseases.Add(new DiseaseFact { Label = "plague", Severity = 0.2, Lethal = true });
            p.LowCapacities.Add("moving 60%");
            string s = PawnFacts.Format(p);
            Assert.True(s.IndexOf("plague") < s.IndexOf("flu"));
            Assert.Contains("severity 60%, immunity 30%", s);
            Assert.Contains("pain 40%; low: moving 60%", s);
        }

        [Fact]
        public void PawnCappedWhenHuge()
        {
            var p = Sample();
            for (int i = 0; i < 40; i++) p.Injuries.Add(new InjuryFact { Part = "part" + i, Label = "wound number " + i, Severity = 0.9 });
            for (int i = 0; i < 20; i++) p.Apparel.Add("very long apparel name " + i);
            string s = PawnFacts.Format(p);
            Assert.True(s.Length <= 600);
            Assert.Contains("+", s.Substring(0, System.Math.Min(s.Length, 600)));
            Assert.StartsWith("Mara #7", s);
        }

        [Fact]
        public void InjuriesCapAtFiveWithMore()
        {
            var l = Enumerable.Range(0, 8).Select(i => new InjuryFact { Part = "p" + i, Label = "cut", Severity = 0.1 * i }).ToList();
            string s = PawnFacts.InjuryText(l);
            Assert.Contains("+3 more", s);
            Assert.Equal("", PawnFacts.InjuryText(new List<InjuryFact>()));
        }

        [Fact]
        public void CapTrimsAtAWord()
        {
            Assert.Equal("short", InspectText.Cap("short"));
            Assert.Equal("", InspectText.Cap(null));
            string s = InspectText.Cap(string.Join(" ", Enumerable.Repeat("alpha", 200)));
            Assert.True(s.Length <= 600); Assert.EndsWith("alpha...", s);
            Assert.True(InspectText.Cap(new string('x', 2000), 5000).Length <= 700);
            Assert.True(InspectText.Cap(new string('x', 2000)).Length <= 600);
        }

        [Fact]
        public void WhereWords()
        {
            Assert.Equal("at home", InspectText.Where(2, "n"));
            Assert.Equal("14 cells north of home", InspectText.Where(14, "n"));
            Assert.Equal("9 cells south-east of home", InspectText.Where(9, "se"));
            Assert.Equal("at home", InspectText.Where(100, 100, 101, 101));
            Assert.Equal("10 cells east of home", InspectText.Where(100, 100, 110, 100));
            Assert.Equal("5 cells north of home (in the barn)", InspectText.WhereIn(5, "n", "the barn"));
        }

        [Fact]
        public void RoomFormat()
        {
            var r = new RoomFacts { Id = 12, Label = "the kitchen", Role = "kitchen", ImpressiveLabel = "decent", Impressiveness = 33.4, Beauty = 1.5, Cleanliness = -0.3, Cells = 30, SpaceLabel = "roomy", Temperature = 21.2 };
            r.Contents.Add("stove #44"); r.Owners.Add("Mara");
            string s = RoomFacts.Format(r);
            Assert.StartsWith("the kitchen (room 12)", s);
            Assert.Contains("decent 33.4", s); Assert.Contains("beauty 1.5", s); Assert.Contains("cleanliness -0.3", s);
            Assert.Contains("roomy, 30 cells", s); Assert.Contains("21.2 C", s); Assert.Contains("holds stove #44", s); Assert.Contains("owners: Mara", s);
            string e = RoomFacts.Format(new RoomFacts { Label = "the hall" });
            Assert.Equal("the hall", e);
        }

        [Fact]
        public void BuildingFormat()
        {
            var b = new BuildingFacts { Label = "electric stove", Id = 44, HasPower = true, PowerOn = false, NetWatts = -350, FuelPct = 40, HpPct = 70, Forbidden = true, Room = "the kitchen" };
            b.Bills.Add("simple meal x5"); b.Storage.Add("rice x20");
            string s = BuildingFacts.Format(b);
            Assert.StartsWith("electric stove #44", s);
            Assert.Contains("power OFF, -350 W", s); Assert.Contains("fuel 40%", s); Assert.Contains("hit points 70%", s);
            Assert.Contains("forbidden", s); Assert.Contains("bills: simple meal x5", s); Assert.Contains("holds rice x20", s); Assert.EndsWith("in the kitchen", s);
            Assert.Equal("wall #3", BuildingFacts.Format(new BuildingFacts { Label = "wall", Id = 3 }));
        }

        [Fact]
        public void StackFormat()
        {
            var s = new StackFacts { Label = "steel", Id = 12, Count = 400, Quality = "", HpPct = 100, RotDays = -1, Dist = 14, DirKey = "n", Room = "the stockpile" };
            Assert.Equal("steel x400 #12. 14 cells north of home (in the stockpile)", StackFacts.Format(s));
            s = new StackFacts { Label = "meat", Id = 5, Count = 1, RotDays = 0.5, Holder = "Mara", Quality = "good" };
            Assert.Equal("meat #5. good quality. rots within a day. held by Mara", StackFacts.Format(s));
        }

        [Fact]
        public void ZoneFormat()
        {
            var z = new ZoneFacts { Label = "Growing zone 1", Id = 2, Kind = "grow", Crop = "rice", GrowthPct = 45, Cells = 60 };
            Assert.Equal("Growing zone 1 (zone 2), grow, 60 cells. growing rice, 45% grown", ZoneFacts.Format(z));
            var p = new ZoneFacts { Label = "Stockpile 1", Kind = "stockpile", Cells = 20 };
            Assert.EndsWith("empty", ZoneFacts.Format(p));
            p.Contents.Add("steel x200");
            Assert.EndsWith("holds steel x200", ZoneFacts.Format(p));
        }

        [Fact]
        public void FindKindsParse()
        {
            Assert.Equal(17, FindKinds.Words.Length);
            foreach (var w in FindKinds.Words) { FindKind k; Assert.True(FindKinds.Parse(w, out k)); Assert.Equal(w, FindKinds.Word(k)); }
            FindKind x;
            Assert.True(FindKinds.Parse(" Bed_Free ", out x)); Assert.Equal(FindKind.BedFree, x);
            Assert.True(FindKinds.Parse("door out", out x)); Assert.Equal(FindKind.DoorOut, x);
            Assert.False(FindKinds.Parse("caravan", out x));
            Assert.False(FindKinds.Parse("", out x)); Assert.False(FindKinds.Parse(null, out x));
            Assert.StartsWith("item, wounded, idle", FindKinds.List());
            Assert.Equal("steel", FindKinds.Label(FindKind.Item, "steel"));
        }

        [Fact]
        public void FormatFindRowsAndTotals()
        {
            var rows = new List<FindRow> { new FindRow("steel", 400, 12, 14, "n", "the stockpile"), new FindRow("steel", 1, 15, 1, "", "") };
            string s = InspectFind.FormatFind("steel", rows, 2);
            Assert.Equal("steel: steel x400 #12, 14 cells north of home (in the stockpile); steel #15, at home. 2 total; showing 2", s);
            Assert.Equal("No fires found.", InspectFind.FormatFind("fires", new List<FindRow>(), 0));
        }

        [Fact]
        public void FormatFindCapsRowsAndMentionsTotal()
        {
            var rows = Enumerable.Range(1, 30).Select(i => new FindRow("meat", i, i, i + 3, "se", "the freezer")).ToList();
            string s = InspectFind.FormatFind("meat", rows, 30);
            Assert.True(s.Length <= 600);
            Assert.Contains("30 total; showing 8", s);
            string big = InspectFind.FormatFind("some long label here", Enumerable.Range(1, 8).Select(i => new FindRow("an extremely long item label number " + i, 2, i, 20, "nw", "the very large dining hall")).ToList(), 99);
            Assert.True(big.Length <= 600);
            Assert.Contains("99 total; showing ", big);
            Assert.DoesNotContain("showing 8", big);   // dropped whole rows to fit
        }

        [Fact]
        public void AmbiguityText()
        {
            var c = new List<(string, int, string)> { ("Hay", 12, "the kitchen"), ("Hay", 15, ""), ("Hay", 19, "the stockpile") };
            Assert.Equal("3 match \"hay\": Hay #12 (the kitchen), Hay #15, Hay #19 (the stockpile). say #id", InspectFind.Ambiguity("hay", c));
            var many = Enumerable.Range(1, 9).Select(i => ("Hay", i, "")).ToList();
            Assert.Contains("+3 more", InspectFind.Ambiguity("hay", many));
        }

        static readonly IList<(string name, int id)> Names = new List<(string name, int id)> { ("Mara", 1), ("Maria", 2), ("Tom", 3), ("Marathon Jim", 4), ("the kitchen", 12), ("Tomás", 5) };

        [Fact]
        public void MatchNameTiers()
        {
            var m = InspectNames.MatchName("mara", Names);
            Assert.Equal("exact", m.Tier); Assert.True(m.Single); Assert.Equal(1, m.Matches[0].id);
            m = InspectNames.MatchName("mar", Names);
            Assert.Equal("prefix", m.Tier); Assert.Equal(3, m.Matches.Count);
            m = InspectNames.MatchName("ria", Names);
            Assert.Equal("substring", m.Tier); Assert.Equal(2, m.Matches[0].id);
            m = InspectNames.MatchName("tom", Names);
            Assert.Equal("exact", m.Tier); Assert.Single(m.Matches);
            m = InspectNames.MatchName("kitchen", Names);
            Assert.Equal("exact", m.Tier);   // "the " is ignored
            m = InspectNames.MatchName("itche", Names);
            Assert.Equal("substring", m.Tier);
            m = InspectNames.MatchName("The Kitchen", Names);
            Assert.Equal("exact", m.Tier);
            Assert.Equal("none", InspectNames.MatchName("zzz", Names).Tier);
            Assert.Equal("none", InspectNames.MatchName("  ", Names).Tier);
        }

        [Fact]
        public void MatchNameIds()
        {
            int id;
            Assert.True(InspectNames.TryId("#12", out id)); Assert.Equal(12, id);
            Assert.True(InspectNames.TryId("room 12", out id)); Assert.Equal(12, id);
            Assert.False(InspectNames.TryId("room", out id)); Assert.False(InspectNames.TryId("#x", out id)); Assert.False(InspectNames.TryId("#0", out id));
            var m = InspectNames.MatchName("#12", Names);
            Assert.Equal("id", m.Tier); Assert.Equal("the kitchen", m.Matches[0].name);
            Assert.Equal("none", InspectNames.MatchName("#99", Names).Tier);
            Assert.Equal("id", InspectNames.MatchName("room 12", Names).Tier);
        }

        [Fact]
        public void DaysLeftMaths()
        {
            Assert.Equal(-1, InspectStock.DaysLeft(50, 0));
            Assert.Equal(0, InspectStock.DaysLeft(0, 3));
            Assert.Equal(5, InspectStock.DaysLeft(16, 3.2), 6);
            Assert.Equal(2.0, InspectStock.MedicinePerDay(6, 0), 6);
            Assert.Equal(5.0, InspectStock.MedicinePerDay(6, 10), 6);
            Assert.Equal(1.0, InspectStock.ComponentsPerDay(5), 6);
            Assert.Equal("no use", InspectStock.DaysWord(-1));
            Assert.Equal("none", InspectStock.DaysWord(0));
            Assert.Equal("under a day", InspectStock.DaysWord(0.5));
            Assert.Equal("2.5 days", InspectStock.DaysWord(2.5));
            Assert.Equal("12 days", InspectStock.DaysWord(12.2));
            Assert.Equal("100+ days", InspectStock.DaysWord(900));
        }

        [Fact]
        public void StockFormat()
        {
            var s = new StockFacts { Colonists = 4, Prisoners = 1, FoodNutritionStored = 40, Medicine = 6, Components = 2, Injured = 0 };
            Assert.Equal(5.0, s.FoodDays, 6);           // 40 / (5 * 1.6)
            s.Rows.Add(new StockRow("steel", 350, 50)); s.Rows.Add(new StockRow("wood", 0, 120)); s.Rows.Add(new StockRow("gold", 0, 0));
            string t = StockFacts.Format(s);
            Assert.StartsWith("Days left: food 5 days (40 nutrition, 5 mouths); medicine 6 (4.5 days); components 2 (2.5 days)", t);
            Assert.Contains("steel 400 (350 stored, 50 loose), wood 120 (loose)", t);
            Assert.DoesNotContain("gold", t);
            s.FoodNutritionStored = 8;
            Assert.Contains("food 1 days LOW", InspectStock.FormatStock(s));
            Assert.True(InspectStock.FormatStock(s).Length <= 600);
        }

        [Fact]
        public void StockWithNoColonists()
        {
            string t = InspectStock.FormatStock(new StockFacts());
            Assert.DoesNotContain("food", t);
            Assert.Contains("medicine 0", t);
        }

        [Fact]
        public void ThreatsCalm()
        {
            Assert.Equal("Calm: nothing hostile", ThreatFacts.Format(new ThreatFacts()));
        }

        [Fact]
        public void ThreatsOrderAndParts()
        {
            var t = new ThreatFacts { Fires = 2, NearestFireDist = 6, NearestFireDirKey = "w", Insects = 5, InsectDist = 30, InsectDirKey = "s", Unknown = "2 raiders signalled but not seen" };
            t.Groups.Add(new ThreatEntry { Kind = "pirates", Count = 6, Dist = 40, DirKey = "ne" });
            t.Groups.Add(new ThreatEntry { Kind = "wargs", Count = 2, Dist = 20, DirKey = "n", Attacking = true, Manhunter = true });
            t.Groups.Add(new ThreatEntry { Kind = "centipedes", Count = 3, Dist = 10, DirKey = "e", Mech = true, Siege = true });
            t.Conditions.Add("toxic fallout");
            string s = ThreatFacts.Format(t);
            Assert.StartsWith("Threats: 2 wargs (attacking, manhunters), 20 cells north of home; 3 centipedes (mechs, siege), 10 cells east of home; 6 pirates, 40 cells north-east of home", s);
            Assert.Contains("2 fires, nearest 6 cells west of home", s);
            Assert.Contains("5 insects, 30 cells south of home", s);
            Assert.Contains("conditions: toxic fallout", s);
            Assert.EndsWith("not seen", s);
            Assert.True(s.Length <= 600);
        }

        [Fact]
        public void ThreatsAtHomeSingularFireAndFromGroup()
        {
            var t = new ThreatFacts { Fires = 1, NearestFireDist = 1 };
            Assert.Equal("Threats: 1 fire, nearest at home", ThreatFacts.Format(t));
            var g = new ThreatGroup { Kind = "raiders", Count = 4, Dist = 30, X = 130, Z = 100 };
            var e = ThreatEntry.From(g, 100, 100, true, false, false, false);
            Assert.Equal("e", e.DirKey); Assert.True(e.Attacking); Assert.Equal(4, e.Count);
            Assert.Equal("", ThreatEntry.From(new ThreatGroup { Dist = 1 }, 0, 0, false, false, false, false).DirKey);
        }

        [Fact]
        public void ThreatsManyGroupsCapped()
        {
            var t = new ThreatFacts();
            for (int i = 0; i < 9; i++) t.Groups.Add(new ThreatEntry { Kind = "raiders", Count = 2, Dist = 20 + i, DirKey = "n" });
            string s = ThreatFacts.Format(t);
            Assert.Contains("+5 more groups", s);
        }
    }
}
