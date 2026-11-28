using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>A fake hands object that also takes the rezone route's calls.</summary>
    sealed class ZonesFakeHands : FakeHands, IZonesHands
    {
        public RezoneArgs Last;
        public HandResult ZonesNext = HandResult.Done("set the food stockpile (#4) to important");
        public HandResult Rezone(RezoneArgs a) { Calls.Add("rezone"); Last = a; return ZonesNext; }
    }

    public class VivariumZonesTests
    {
        static readonly ZoneRef[] Zones =
        {
            new ZoneRef { Id = 3, Kind = "grow", Label = "Growing zone 1", Crop = "potatoes" },
            new ZoneRef { Id = 4, Kind = "stockpile", Label = "Stockpile zone 1", Preset = "food" },
            new ZoneRef { Id = 5, Kind = "stockpile", Label = "Stockpile zone 2", Preset = "meds" },
            new ZoneRef { Kind = "hydroponics", Label = "hydroponics", Crop = "rice" },
        };
        static readonly NamedDef[] Cats =
        {
            new NamedDef("Foods", "foods"), new NamedDef("FoodMeals", "prepared meals"), new NamedDef("FoodRaw", "raw food"), new NamedDef("Medicine", "medicine"),
            new NamedDef("ResourcesRaw", "resources"), new NamedDef("Weapons", "weapons"), new NamedDef("Corpses", "corpses"), new NamedDef("StoneBlocks", "stone blocks"),
        };
        static readonly NamedDef[] Things =
        {
            new NamedDef("Steel", "steel"), new NamedDef("ComponentIndustrial", "component"), new NamedDef("MedicineHerbal", "herbal medicine"),
        };
        static readonly CropInfo[] Crops =
        {
            new CropInfo { Def = "Plant_Potato", Label = "potato plant", Harvest = "potatoes", Ground = true, Hydro = true },
            new CropInfo { Def = "Plant_Rice", Label = "rice plant", Harvest = "rice", Ground = true, Hydro = true },
            new CropInfo { Def = "Plant_Devilstrand", Label = "devilstrand", Harvest = "devilstrand", Ground = true, Research = "Devilstrand" },
            new CropInfo { Def = "Plant_Cocoa", Label = "cocoa plant", Harvest = "cocoa", Hydro = true },
            new CropInfo { Def = "Plant_Haygrass", Label = "haygrass", Harvest = "hay", Ground = true },
        };

        // ---------------------------------------------------------------- ZonesPure
        [Theory]
        [InlineData("important", "important")] [InlineData(" Critical ", "critical")] [InlineData("LOW", "low")] [InlineData("high", null)] [InlineData("", null)] [InlineData(null, null)]
        public void PriorityWords(string s, string want) => Assert.Equal(want, ZonesPure.ParsePriority(s));

        [Fact]
        public void WordLists()
        {
            Assert.Equal(new[] { "meals", "steel", "weapons" }, ZonesPure.SplitWords("Meals, steel and weapons"));
            Assert.Equal(new[] { "meals" }, ZonesPure.SplitWords("meals; meals, none, "));
            Assert.Empty(ZonesPure.SplitWords(null));
        }

        [Theory]
        [InlineData("meals", true, "FoodMeals")] [InlineData("raw food", true, "FoodRaw")] [InlineData("medicine", true, "Medicine")] [InlineData("weapon", true, "Weapons")]
        [InlineData("stone blocks", true, "StoneBlocks")] [InlineData("food", true, "Foods")] [InlineData("meds", true, "Medicine")] [InlineData("raw", true, "ResourcesRaw")]
        [InlineData("everything", true, "Root")] [InlineData("steel", false, "Steel")] [InlineData("components", false, "ComponentIndustrial")] [InlineData("herbal medicine", false, "MedicineHerbal")]
        public void FilterWords(string word, bool category, string def)
        {
            FilterHit hit; string refusal;
            Assert.True(ZonesPure.ResolveFilterWord(word, Cats, Things, out hit, out refusal), refusal);
            Assert.Equal(category, hit.Category); Assert.Equal(def, hit.Def);
        }

        [Fact]
        public void UnknownFilterWordListsExamples()
        {
            FilterHit hit; string refusal;
            Assert.False(ZonesPure.ResolveFilterWord("unobtainium", Cats, Things, out hit, out refusal));
            Assert.Contains("no item word 'unobtainium'", refusal); Assert.Contains("meals", refusal.Replace("prepared meals", "meals")); Assert.Contains("try: food, meds, raw, everything", refusal);
        }

        [Theory]
        [InlineData("potatoes", "Plant_Potato")] [InlineData("potato plant", "Plant_Potato")] [InlineData("Plant_Rice", "Plant_Rice")] [InlineData("hay", "Plant_Haygrass")] [InlineData("rice", "Plant_Rice")]
        public void CropWords(string word, string def)
        {
            CropInfo c; string refusal;
            Assert.True(ZonesPure.ResolveCrop(word, Crops, false, out c, out refusal), refusal);
            Assert.Equal(def, c.Def);
        }

        [Fact]
        public void CropRefusals()
        {
            CropInfo c; string r;
            Assert.False(ZonesPure.ResolveCrop("devilstrand", Crops, false, out c, out r)); Assert.Equal("devilstrand needs research Devilstrand first", r);
            Assert.False(ZonesPure.ResolveCrop("cocoa", Crops, false, out c, out r)); Assert.Contains("not sowable in soil", r);
            Assert.False(ZonesPure.ResolveCrop("haygrass", Crops, true, out c, out r)); Assert.Contains("can't grow in a hydroponics basin", r);
            Assert.False(ZonesPure.ResolveCrop("moonflower", Crops, false, out c, out r)); Assert.Contains("no crop called 'moonflower'; try: potatoes", r);
            Assert.True(ZonesPure.ResolveCrop("cocoa", Crops, true, out c, out r)); Assert.Equal("Plant_Cocoa", c.Def);
        }

        [Fact]
        public void CropWordPrefersTheAlias()
        {
            Assert.Equal("potatoes", ZonesPure.CropWord("Plant_Potato", "potato plant"));
            Assert.Equal("cocoa", ZonesPure.CropWord("Plant_Cocoa", "cocoa plant"));
        }

        [Theory]
        [InlineData("#4", 4)] [InlineData("4", 4)] [InlineData("zone 3", 3)] [InlineData("Stockpile zone 2", 5)] [InlineData("meds stockpile", 5)] [InlineData("food stockpile", 4)] [InlineData("potatoes", 3)] [InlineData("grow", 3)]
        public void ZoneWords(string word, int id)
        {
            ZoneRef z; string r;
            Assert.True(ZonesPure.ResolveZone(word, Zones, out z, out r), r);
            Assert.Equal(id, z.Id);
        }

        [Fact]
        public void ZoneRefusals()
        {
            ZoneRef z; string r;
            Assert.True(ZonesPure.ResolveZone("hydroponics", Zones, out z, out r)); Assert.Equal("hydroponics", z.Kind);
            Assert.False(ZonesPure.ResolveZone("stockpile", Zones, out z, out r)); Assert.Contains("fits several zones", r); Assert.Contains("#4 stockpile food", r);
            Assert.False(ZonesPure.ResolveZone("#9", Zones, out z, out r)); Assert.Contains("no zone #9; the zones are: #3 grow potatoes", r);
            Assert.False(ZonesPure.ResolveZone("hydroponics", Zones.Take(3).ToList(), out z, out r)); Assert.Equal("no hydroponics basins to change", r);
        }

        [Fact]
        public void NotesAndWrongFields()
        {
            Assert.Equal("set the food stockpile (#4) to important, allow meals and steel, stop taking weapons",
                ZonesPure.SetNote(Zones[1], "important", new[] { "meals", "steel" }, new[] { "weapons" }, null));
            Assert.Equal("changed what the meds stockpile (#5) takes: allow steel", ZonesPure.SetNote(Zones[2], null, new[] { "steel" }, new string[0], null));
            Assert.Equal("set the potatoes field (#3) to grow rice", ZonesPure.SetNote(Zones[0], null, new string[0], new string[0], "rice"));
            Assert.Equal("set the hydroponics basins to grow rice", ZonesPure.SetNote(Zones[3], null, new string[0], new string[0], "rice"));
            Assert.Equal("a field has no priority or filter; give plant", ZonesPure.WrongField("grow", new RezoneArgs { Priority = "low" }));
            Assert.Equal("hydroponics has no priority or filter; give plant", ZonesPure.WrongField("hydroponics", new RezoneArgs { Deny = { "steel" } }));
            Assert.Contains("grows nothing", ZonesPure.WrongField("stockpile", new RezoneArgs { Plant = "rice" }));
            Assert.Null(ZonesPure.WrongField("stockpile", new RezoneArgs { Priority = "low" }));
        }

        [Fact]
        public void UndoDiffRoundTrips()
        {
            string d = ZonesPure.EncodeDiff(new[] { "A", "B" }, new[] { "B", "C", "D" });
            Assert.Equal("+C,D|-A", d);
            List<string> on, off;
            ZonesPure.DecodeDiff(d, out on, out off);
            Assert.Equal(new[] { "C", "D" }, on); Assert.Equal(new[] { "A" }, off);
            Assert.Equal("+|-", ZonesPure.EncodeDiff(new[] { "A" }, new[] { "A" }));
            ZonesPure.DecodeDiff(null, out on, out off); Assert.Empty(on); Assert.Empty(off);
        }

        [Fact]
        public void MarkKeepsItsExtra()
        {
            var m = new NarratorMark { Kind = "zone_settings", Map = 1, Id = 4, Sub = 3, Extra = "+A|-B" };
            var back = NarratorMark.FromJson(m.ToJson());
            Assert.Equal("+A|-B", back.Extra); Assert.Equal(3, back.Sub); Assert.Equal(0, BuildBudget.CostCells(new[] { m }));
        }

        // ---------------------------------------------------------------- the route
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly ZonesFakeHands Hands = new ZonesFakeHands();
        IVivariumHands Current;
        int op;

        public VivariumZonesTests() { Current = Hands; VivariumRoutes.Register(H.Router, () => V, () => Current); }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "zop" + (++op);
            return H.Call("POST", path, j.ToString());
        }

        [Fact]
        public void RezoneParses()
        {
            var r = Post("/act/rezone", new { zone = "#4", priority = "Important", allow = "meals, steel", deny = "weapons", why = "since meals pile up" });
            Assert.True(r.Ok, r.Error + " " + r.Body);
            Assert.Equal("#4", Hands.Last.Zone); Assert.Equal("important", Hands.Last.Priority);
            Assert.Equal(new[] { "meals", "steel" }, Hands.Last.Allow); Assert.Equal(new[] { "weapons" }, Hands.Last.Deny); Assert.Null(Hands.Last.Plant);
            Assert.Equal("set the food stockpile (#4) to important", (string)r.Body["done"]);
            Assert.Contains("since meals pile up", V.Chat.Since(0).ToString());
        }

        [Fact]
        public void RezoneCropAndNoneWords()
        {
            Assert.True(Post("/act/rezone", new { zone = "hydroponics", plant = "rice", priority = "none", allow = "none" }).Ok);
            Assert.Equal("rice", Hands.Last.Plant); Assert.Null(Hands.Last.Priority); Assert.Empty(Hands.Last.Allow);
        }

        [Fact]
        public void RezoneShapeErrors()
        {
            Action<object, string> bad = (b, c) => { var r = Post("/act/rezone", b); Assert.False(r.Ok); Assert.Equal("bad_request", r.Error); Assert.Contains(c, (string)r.Body["detail"]); };
            bad(new { zone = "#4" }, "give priority, allow, deny or plant");
            bad(new { zone = "#4", priority = "urgent" }, "priority must be one of low, normal, preferred, important, critical");
            bad(new { zone = "#4", allow = "a,b,c,d,e,f,g,h,i" }, "at most 8 words");
            bad(new { priority = "low" }, "zone");
            Assert.DoesNotContain("rezone", Hands.Calls);
        }

        [Fact]
        public void RezoneRefusalAndMissingHands()
        {
            Hands.ZonesNext = HandResult.No("no zone #9; the zones are: #3 grow potatoes", "change a zone");
            var r = Post("/act/rezone", new { zone = "#9", priority = "low" });
            Assert.False(r.Ok); Assert.Equal("refused", r.Error); Assert.Equal(409, r.Status);
            Current = new FakeHands();
            r = Post("/act/rezone", new { zone = "#4", priority = "low" });
            Assert.False(r.Ok); Assert.Contains("zone hands are not available", (string)r.Body["detail"]);
        }

        [Fact]
        public void RezoneMarksGoToTheLedgerAndCostNoCells()
        {
            Hands.ZonesNext = HandResult.Placed("set the food stockpile (#4) to important", new List<NarratorMark> { new NarratorMark { Kind = "zone_settings", Id = 4, Sub = 3 } });
            int before = V.Budget.Left().Cells;
            var r = Post("/act/rezone", new { zone = "#4", priority = "important" });
            Assert.True(r.Ok); Assert.Equal(1, V.Ledger.Count); Assert.NotNull(r.Body["undo_seq"]);
            Assert.Equal(before, V.Budget.Left().Cells);
        }

        [Fact]
        public void ZoneRoutePassesPriority()
        {
            Assert.True(Post("/act/zone", new { kind = "stockpile", preset = "meds", priority = "critical" }).Ok);
            var z = (ZoneArgs)Hands.LastArgs;
            Assert.Equal("critical", z.Priority); Assert.Equal("meds", z.Preset);
            var r = Post("/act/zone", new { kind = "stockpile", priority = "urgent" });
            Assert.False(r.Ok); Assert.Equal("bad_request", r.Error);
        }
    }
}
