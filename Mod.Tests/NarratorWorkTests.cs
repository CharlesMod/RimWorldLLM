using System.Linq;
using RimWorldLLM;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class NarratorWorkTests
    {
        [Theory]
        [InlineData(false, false, 0, "none")]
        [InlineData(false, true, 5, "none")]
        [InlineData(true, false, 0, "full")]
        [InlineData(true, true, 40, "ok")]
        [InlineData(true, true, 41, "far")]
        [InlineData(true, true, 3, "ok")]
        public void StoreVerdict(bool any, bool found, double d, string want) { Assert.Equal(want, NarratorWork.StoreVerdict(any, found, d)); }

        static MaterialNeed Steel(int need, int usable, int forb = 0) { return new MaterialNeed("steel", need, usable, forb); }

        [Fact]
        public void SiteReasonOrder()
        {
            var m = new[] { Steel(60, 0) };
            Assert.Equal("forbidden", NarratorWork.SiteReason(true, true, true, m).Text);
            Assert.Equal("unreachable", NarratorWork.SiteReason(false, true, true, m).Text);
            Assert.Equal(StuckKind.NoBuilder, NarratorWork.SiteReason(false, false, true, m).Kind);
            Assert.Equal("waiting on steel: need 60, have 0", NarratorWork.SiteReason(false, false, false, m).Text);
        }

        [Fact]
        public void SiteReasonForbiddenStuffAndNone()
        {
            var r = NarratorWork.SiteReason(false, false, false, new[] { Steel(60, 20, 40) });
            Assert.Equal(StuckKind.StuffForbidden, r.Kind);
            Assert.Equal("steel is forbidden: unforbid it", r.Text);
            Assert.Equal("waiting on steel: need 60, have 20", NarratorWork.SiteReason(false, false, false, new[] { Steel(60, 20, 30) }).Text);
            Assert.True(NarratorWork.SiteReason(false, false, false, new[] { Steel(60, 60) }).IsNone);
            Assert.True(NarratorWork.SiteReason(false, false, false, null).IsNone);
            // first short material wins
            var two = new[] { Steel(10, 10), new MaterialNeed("components", 2, 0, 0) };
            Assert.Equal("waiting on components: need 2, have 0", NarratorWork.SiteReason(false, false, false, two).Text);
        }

        [Fact]
        public void BillReasons()
        {
            Assert.Equal("bench has no power", NarratorWork.BillReason(true, true, "Cooking", "meat").Text);
            Assert.Equal("no one does Cooking", NarratorWork.BillReason(false, true, "Cooking", "meat").Text);
            Assert.Equal("no ingredients (raw food)", NarratorWork.BillReason(false, false, "Cooking", "raw food").Text);
            Assert.True(NarratorWork.BillReason(false, false, "Cooking", null).IsNone);
        }

        [Fact]
        public void SiteWhat()
        {
            Assert.Equal("steel wall blueprints", NarratorWork.SiteWhat("blueprint", "wall", "steel"));
            Assert.Equal("wall frames", NarratorWork.SiteWhat("frame", "wall", null));
            Assert.Equal("bill: simple meal at stove", NarratorWork.SiteWhat("bill", "bill: simple meal at stove", null));
            Assert.Equal("steel door blueprints", NarratorWork.SiteWhat("blueprint", "steel door", "steel"));
        }

        static StuckItem Bp(string thing, string stuff, StuckReason r, int x, int z, string kind = "blueprint")
        {
            return new StuckItem { Kind = kind, Thing = thing, Stuff = stuff, Reason = r, X = x, Z = z };
        }

        [Fact]
        public void GroupStuckGroupsAndCounts()
        {
            var steel = new StuckReason(StuckKind.NoMaterial, "waiting on steel: need 60, have 0");
            var items = Enumerable.Range(0, 12).Select(i => Bp("wall", "steel", steel, 110, 100 + i % 2 * 0)).ToList();
            items.Add(Bp("wall", "wood", steel, 100, 100));
            items.Add(Bp("door", null, NarratorWork.SiteReason(false, false, true, null), 100, 90, "frame"));
            items.Add(Bp("lamp", null, default(StuckReason), 0, 0));
            items.Add(Bp("bill: simple meal at stove", null, NarratorWork.BillReason(false, false, "Cooking", "raw food"), 100, 100, "bill"));
            var s = NarratorWork.GroupStuck(items, 100, 100);
            Assert.Equal(4, s.Rows.Count);
            Assert.Equal("steel wall blueprints", s.Rows[0].What);
            Assert.Equal(12, s.Rows[0].Count);
            Assert.Equal(10, s.Rows[0].Dist);
            Assert.Equal("E", s.Rows[0].Dir);
            Assert.Equal(14, s.BlueprintsStuck);
            Assert.Equal(2, s.NoMaterialGroups);
            Assert.True(s.NoBuilder);
            Assert.Equal(1, s.BillsStuck);
        }

        [Fact]
        public void GroupStuckCapsAndEmpty()
        {
            var r = new StuckReason(StuckKind.Unreachable, "unreachable");
            var items = Enumerable.Range(0, 20).Select(i => Bp("thing" + i, null, r, i, 0)).ToList();
            var s = NarratorWork.GroupStuck(items, 0, 0);
            Assert.Equal(6, s.Rows.Count);
            Assert.Equal(20, s.BlueprintsStuck);
            Assert.False(s.NoBuilder);
            Assert.Empty(NarratorWork.GroupStuck(null, 0, 0).Rows);
        }

        [Fact]
        public void CannotReasons()
        {
            Assert.Empty(NarratorWork.CannotReasons(1, 1, 1, 1));
            Assert.Equal(new[] { "no manipulation: can't build, cook or craft" }, NarratorWork.CannotReasons(0.1, 1, 1, 1));
            Assert.Equal(new[] { "weak hands" }, NarratorWork.CannotReasons(0.3, 1, 1, 1));
            Assert.Equal(new[] { "can't walk" }, NarratorWork.CannotReasons(1, 0.1, 1, 1));
            Assert.Equal(new[] { "can't see well" }, NarratorWork.CannotReasons(1, 1, 0.2, 1));
            Assert.Equal(3, NarratorWork.CannotReasons(0, 0, 0, 0).Count);
        }

        [Fact]
        public void WorkRefusal()
        {
            Assert.Equal("Cooking: Ana can't (Pyromaniac, too young)", NarratorWork.WorkRefusal("Cooking", "Ana", new[] { "Pyromaniac.", "too young", "third" }));
            Assert.Equal("Cooking: Ana can't", NarratorWork.WorkRefusal("Cooking", "Ana", null));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("Wait_Wander", true)]
        [InlineData("GotoWander", true)]
        [InlineData("Wait", true)]
        [InlineData("Wait_MaintainPosture", true)]
        [InlineData("IdleWhileDespawned", true)]
        [InlineData("Wait_Downed", false)]
        [InlineData("Sow", false)]
        public void IsIdleJob(string job, bool want) { Assert.Equal(want, NarratorWork.IsIdleJob(job)); }

        [Fact]
        public void CutJob()
        {
            Assert.Equal("short", NarratorWork.CutJob("short"));
            var cut = NarratorWork.CutJob(new string('a', 100));
            Assert.Equal(60, cut.Length);
            Assert.EndsWith("...", cut);
        }

        [Fact]
        public void GroupLooseKeepsLargestStackRef()
        {
            var items = new[] { new LooseItem("steel", 1, 1, 10, false, 7), new LooseItem("steel", 2, 2, 50, false, 9), new LooseItem("steel", 3, 3, 5, false, 3) };
            var p = BuildView.GroupLoose(items, 0, 0);
            Assert.Single(p);
            Assert.Equal(9, p[0].Ref);
        }
    }
}
