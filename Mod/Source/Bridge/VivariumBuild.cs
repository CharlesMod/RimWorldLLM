using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Vivarium phase 2 contract (docs/VIVARIUM.md "Phase 2: the builder's hands"). Verse-free: argument
// records, the narrator's undo ledger, the per-cycle caps and the footprint math the game hands use.
namespace RimWorldLLM.Bridge
{
    /// <summary>A rectangle of cells: (X, Z) is the south-west corner, W along x, H along z.</summary>
    public sealed class CellBox
    {
        public int X, Z, W, H;
        public CellBox() { }
        public CellBox(int x, int z, int w, int h) { X = x; Z = z; W = w; H = h; }
        public int Area { get { return W * H; } }
        public int MaxX { get { return X + W - 1; } }
        public int MaxZ { get { return Z + H - 1; } }
        public bool Contains(int x, int z) { return x >= X && x <= MaxX && z >= Z && z <= MaxZ; }
        public override string ToString() { return W + "x" + H + " at (" + X + "," + Z + ")"; }
    }

    /// <summary>Where the narrator wants something: an exact box or cell, a room, or "find a site near the base".</summary>
    public sealed class Where
    {
        public CellBox Box;       // exact rectangle (zones, areas, designations, rooms)
        public int? X, Z;         // exact cell (build); with Radius: a circle (designations)
        public int Radius;        // 0 = none
        public int Room;          // room id from the digest's build.rooms (0 = none)
        public bool Any { get { return Box != null || X != null || Room != 0; } }
    }

    /// <summary>POST /act/designate. kind: mine, chop, harvest, haul, deconstruct, cancel.</summary>
    public sealed class DesignateArgs
    {
        public string Kind;
        public Where At = new Where();
        public string Ore;         // mine without a place: ore or rock kind ("steel", "components", "granite"...)
        public int Target;         // one thing id (deconstruct, cancel, haul, chop)
        public int Max = 40;       // most things/cells to designate
        public bool PlayerAsked;   // deconstruct of player-made buildings only when true (checked against chat by the route)
    }

    /// <summary>POST /act/build: one blueprint.</summary>
    public sealed class BuildArgs
    {
        public string Thing;       // alias ("stove") or defName ("FueledStove")
        public string Stuff;       // alias ("wood", "steel", "granite") or defName; null = the hand picks the most plentiful allowed
        public int Rot = -1;       // 0 north, 1 east, 2 south, 3 west; -1 = the hand picks
        public Where At = new Where();
    }

    /// <summary>POST /act/room: a rectangle of walls with one door.</summary>
    public sealed class RoomArgs
    {
        public string Purpose;     // bedroom, barracks, kitchen, dining, storage, freezer, hospital, workshop, rec
        public int W, H;           // outer size including walls (3..11); 0 = from Purpose and Size
        public string Size = "medium";
        public string Stuff;
        public string Door = "auto"; // n, e, s, w, auto (faces the base)
        public Where At = new Where(); // At.X/Z = south-west corner (scripts); none = the base planner picks the site
        public string Near;        // a room role/label or a colonist's name the room goes next to (planner)
        public string For;         // who it is for (words only)
        public int Beds;           // barracks/hospital beds; 0 = from the size
        public bool Furnish = true; // lay out the role's furniture inside
    }

    /// <summary>POST /act/zone. kind: grow, stockpile, dump.</summary>
    public sealed class ZoneArgs
    {
        public string Kind;
        public string Plant;       // grow: alias ("potatoes") or defName
        public string Preset = "all"; // stockpile: food, meds, raw, all, waste (wastepacks only)
        public string Priority;    // low, normal, preferred, important, critical; null = leave
        public int W, H;           // 0 = from Size
        public string Size = "medium";
        public Where At = new Where();
    }

    /// <summary>POST /act/area. kind: home, allowed. op: add, remove.</summary>
    public sealed class AreaArgs
    {
        public string Kind, Op, Name; // Name: the allowed area's label (made if missing, on add)
        public CellBox Box;
        public List<int> Assign = new List<int>(); // allowed: pawns to restrict to the area
    }

    /// <summary>POST /act/bill. op: add, remove, suspend, resume.</summary>
    public sealed class BillArgs
    {
        public string Op;
        public int Bench;          // workbench thing id
        public string Recipe;      // add: recipe defName or label; remove/suspend/resume: match by recipe if Index is 0
        public int Index;          // 1-based position in the bench's bill list (remove/suspend/resume)
        public int Count;          // add: do X times (0 = forever unless Until)
        public int Until;          // add: do until you have X
        public int Radius;         // ingredient search radius (0 = leave the default)
    }

    /// <summary>Something the narrator put into the world, so the player can take it back.
    /// Kinds: blueprint (Def = thing defName, Cells = root cells), designation (Def = designation defName,
    /// Things and/or Cells), zone (Id = a zone the narrator made), zone_cells (Id = zone, Cells it added),
    /// area_cells (Def = area label, Cells it set), bill (Id = bench thing id, Sub = the bill's load id).</summary>
    public sealed class NarratorMark
    {
        public string Kind;
        public int Map;            // Map.uniqueID
        public string Def;
        public int Id, Sub;
        public string Extra;       // zone_settings: the filter change as "+turnedOn|-turnedOff" def names
        public List<int> Things = new List<int>();
        public List<int> Cells = new List<int>(); // flat x0, z0, x1, z1, ...

        public int CellCount { get { return Cells.Count / 2; } }
        public void AddCell(int x, int z) { Cells.Add(x); Cells.Add(z); }

        public JObject ToJson()
        {
            var o = new JObject { ["kind"] = Kind, ["map"] = Map };
            if (!string.IsNullOrEmpty(Def)) o["def"] = Def;
            if (Id != 0) o["id"] = Id;
            if (Sub != 0) o["sub"] = Sub;
            if (!string.IsNullOrEmpty(Extra)) o["extra"] = Extra;
            if (Things.Count > 0) o["things"] = new JArray(Things);
            if (Cells.Count > 0) o["cells"] = new JArray(Cells);
            return o;
        }

        public static NarratorMark FromJson(JObject o)
        {
            if (o == null) return null;
            var m = new NarratorMark
            {
                Kind = VivJson.Str(o["kind"]), Map = VivJson.Int(o["map"]), Def = VivJson.Str(o["def"], null),
                Id = VivJson.Int(o["id"]), Sub = VivJson.Int(o["sub"]), Extra = VivJson.Str(o["extra"], null)
            };
            if (string.IsNullOrEmpty(m.Kind)) return null;
            if (o["things"] is JArray t) foreach (var x in t) if (x.Type == JTokenType.Integer) m.Things.Add(x.Value<int>());
            if (o["cells"] is JArray c) foreach (var x in c) if (x.Type == JTokenType.Integer) m.Cells.Add(x.Value<int>());
            if (m.Cells.Count % 2 == 1) m.Cells.RemoveAt(m.Cells.Count - 1);
            return m;
        }
    }

    /// <summary>The narrator's world changes, newest last, so "/undo" can take back the last N actions. Saved.</summary>
    public sealed class NarratorLedger
    {
        public const int Max = 50;
        public sealed class Entry { public int Seq, Tick; public string Verb, Note; public List<NarratorMark> Marks = new List<NarratorMark>(); }
        readonly List<Entry> _entries = new List<Entry>();
        int _seq;

        public int Count { get { return _entries.Count; } }
        public IReadOnlyList<Entry> Entries { get { return _entries; } }

        public Entry Add(string verb, string note, int tick, IList<NarratorMark> marks)
        {
            if (marks == null || marks.Count == 0) return null;
            var e = new Entry { Seq = ++_seq, Tick = tick, Verb = verb ?? "", Note = note ?? "" };
            e.Marks.AddRange(marks);
            _entries.Add(e);
            while (_entries.Count > Max) _entries.RemoveAt(0);
            return e;
        }

        /// <summary>Removes and returns the newest n entries, newest first.</summary>
        public List<Entry> PopLast(int n)
        {
            var r = new List<Entry>();
            while (n-- > 0 && _entries.Count > 0)
            {
                r.Add(_entries[_entries.Count - 1]);
                _entries.RemoveAt(_entries.Count - 1);
            }
            return r;
        }

        /// <summary>Puts entries back (newest-first list, as PopLast returned it) when the undo could not run.</summary>
        public void Restore(List<Entry> popped)
        {
            for (int i = popped.Count - 1; i >= 0; i--) _entries.Add(popped[i]);
        }

        public JArray Export()
        {
            var a = new JArray();
            foreach (var e in _entries)
            {
                var m = new JArray();
                foreach (var k in e.Marks) m.Add(k.ToJson());
                a.Add(new JObject { ["seq"] = e.Seq, ["tick"] = e.Tick, ["verb"] = e.Verb, ["note"] = e.Note, ["marks"] = m });
            }
            return a;
        }

        public void Import(JArray saved)
        {
            Clear();
            if (saved == null) return;
            foreach (var t in saved)
            {
                var o = t as JObject;
                if (o == null) continue;
                var e = new Entry { Seq = VivJson.Int(o["seq"]), Tick = VivJson.Int(o["tick"]), Verb = VivJson.Str(o["verb"]), Note = VivJson.Str(o["note"]) };
                if (o["marks"] is JArray ms) foreach (var x in ms) { var m = NarratorMark.FromJson(x as JObject); if (m != null) e.Marks.Add(m); }
                if (e.Marks.Count == 0) continue;
                _entries.Add(e);
                if (e.Seq > _seq) _seq = e.Seq;
            }
            while (_entries.Count > Max) _entries.RemoveAt(0);
        }

        public JObject ToJson(int last)
        {
            var a = new JArray();
            for (int i = _entries.Count - 1; i >= 0 && a.Count < last; i--)
            {
                var e = _entries[i];
                a.Add(new JObject { ["seq"] = e.Seq, ["tick"] = e.Tick, ["verb"] = e.Verb, ["note"] = e.Note, ["marks"] = e.Marks.Count });
            }
            return new JObject { ["count"] = _entries.Count, ["newest_first"] = a };
        }

        public void Clear() { _entries.Clear(); _seq = 0; }
    }

    /// <summary>What the narrator may still place this cycle. A cycle starts when the steward posts
    /// /chat/status "thinking", or at the latest one game hour (2500 ticks) after the last start.</summary>
    public sealed class BuildBudget
    {
        public const int CycleTicks = 2500;
        public int MaxBlueprints = 50, MaxCells = 200, MaxDesignations = 150, MaxBills = 4;
        public int Blueprints, Cells, Designations, Bills;   // left this cycle
        int _start = int.MinValue;

        public BuildBudget() { Refill(); }

        void Refill() { Blueprints = MaxBlueprints; Cells = MaxCells; Designations = MaxDesignations; Bills = MaxBills; }

        public void NewCycle(int tick) { _start = tick; Refill(); }

        /// <summary>Starts a new cycle if the last one is older than CycleTicks (or the clock went back).</summary>
        public void Tick(int tick)
        {
            if (_start == int.MinValue || tick < _start || tick - _start >= CycleTicks) NewCycle(tick);
        }

        /// <summary>A copy of what is left, handed to the game hand so it can refuse an action that would not fit.</summary>
        public BuildBudget Left()
        {
            return new BuildBudget { MaxBlueprints = MaxBlueprints, MaxCells = MaxCells, MaxDesignations = MaxDesignations, MaxBills = MaxBills,
                Blueprints = Blueprints, Cells = Cells, Designations = Designations, Bills = Bills, _start = _start };
        }

        public static int CostBlueprints(IEnumerable<NarratorMark> ms) { int n = 0; foreach (var m in ms) if (m.Kind == "blueprint") n += m.CellCount; return n; }
        public static int CostCells(IEnumerable<NarratorMark> ms) { int n = 0; foreach (var m in ms) if (m.Kind == "zone" || m.Kind == "zone_cells" || m.Kind == "area_cells") n += m.CellCount; return n; }
        public static int CostDesignations(IEnumerable<NarratorMark> ms) { int n = 0; foreach (var m in ms) if (m.Kind == "designation") n += m.Things.Count + m.CellCount; return n; }
        public static int CostBills(IEnumerable<NarratorMark> ms) { int n = 0; foreach (var m in ms) if (m.Kind == "bill") n++; return n; }

        public void Charge(IList<NarratorMark> ms)
        {
            if (ms == null) return;
            Blueprints = Math.Max(0, Blueprints - CostBlueprints(ms));
            Cells = Math.Max(0, Cells - CostCells(ms));
            Designations = Math.Max(0, Designations - CostDesignations(ms));
            Bills = Math.Max(0, Bills - CostBills(ms));
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["blueprints"] = Blueprints, ["cells"] = Cells, ["designations"] = Designations, ["bills"] = Bills,
                ["max"] = new JObject { ["blueprints"] = MaxBlueprints, ["cells"] = MaxCells, ["designations"] = MaxDesignations, ["bills"] = MaxBills }
            };
        }
    }

    /// <summary>Pure footprint math and the word lists the hands share (tested in Mod.Tests).</summary>
    public static class BuildPure
    {
        public const int MinRoom = 3, MaxRoom = 11, MaxZoneSide = 20;

        public static readonly string[] Purposes = { "bedroom", "barracks", "kitchen", "dining", "storage", "freezer", "hospital", "workshop", "rec", "research" };
        public static readonly string[] Sizes = { "small", "medium", "large" };
        public static readonly string[] Presets = { "food", "meds", "raw", "all", "waste" };
        public static readonly string[] Priorities = { "low", "normal", "preferred", "important", "critical" };
        public static readonly string[] DoorSides = { "n", "e", "s", "w", "auto" };

        /// <summary>Outer size (walls included) for a room purpose and size word.</summary>
        public static void RoomSize(string purpose, string size, out int w, out int h)
        {
            switch (purpose)
            {
                case "bedroom": w = 5; h = 5; break;      // 3x3 inside: bed, end table, dresser
                case "barracks": w = 8; h = 6; break;
                case "kitchen": w = 6; h = 5; break;
                case "dining": w = 7; h = 6; break;
                case "storage": w = 8; h = 7; break;
                case "freezer": w = 6; h = 6; break;
                case "hospital": w = 7; h = 5; break;
                case "workshop": w = 8; h = 6; break;
                default: w = 7; h = 6; break;            // rec and anything else
            }
            int d = size == "small" ? -1 : size == "large" ? 2 : 0;
            w = Clamp(w + d, MinRoom, MaxRoom);
            h = Clamp(h + d, MinRoom, MaxRoom);
        }

        /// <summary>Zone size (w, h) for a kind and size word.</summary>
        public static void ZoneSize(string kind, string size, out int w, out int h)
        {
            int s = kind == "grow" ? (size == "small" ? 5 : size == "large" ? 11 : 8)
                  : kind == "dump" ? (size == "small" ? 3 : size == "large" ? 6 : 4)
                  : (size == "small" ? 4 : size == "large" ? 9 : 6);
            w = s; h = s;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>The perimeter (wall) cells of a room box, clockwise from the south-west corner, without the door cell,
        /// and the door cell: the middle of the given side (n/e/s/w). Corners never hold the door.</summary>
        public static List<int[]> RoomWalls(CellBox b, string side, out int[] door)
        {
            if (b.W < MinRoom || b.H < MinRoom) throw new ArgumentException("a room needs at least 3x3 cells");
            switch (side)
            {
                case "n": door = new[] { b.X + b.W / 2, b.MaxZ }; break;
                case "e": door = new[] { b.MaxX, b.Z + b.H / 2 }; break;
                case "w": door = new[] { b.X, b.Z + b.H / 2 }; break;
                default: door = new[] { b.X + b.W / 2, b.Z }; break;
            }
            var cells = new List<int[]>();
            for (int x = b.X; x <= b.MaxX; x++) cells.Add(new[] { x, b.Z });                  // south row
            for (int z = b.Z + 1; z <= b.MaxZ; z++) cells.Add(new[] { b.MaxX, z });           // east column
            for (int x = b.MaxX - 1; x >= b.X; x--) cells.Add(new[] { x, b.MaxZ });           // north row
            for (int z = b.MaxZ - 1; z > b.Z; z--) cells.Add(new[] { b.X, z });               // west column
            var d = door;
            cells.RemoveAll(c => c[0] == d[0] && c[1] == d[1]);
            return cells;
        }

        /// <summary>The cells inside the walls.</summary>
        public static List<int[]> RoomInside(CellBox b)
        {
            var cells = new List<int[]>();
            for (int z = b.Z + 1; z < b.MaxZ; z++)
                for (int x = b.X + 1; x < b.MaxX; x++) cells.Add(new[] { x, z });
            return cells;
        }

        /// <summary>Which side the door goes on so it faces a point (the base centre): the side whose outward
        /// direction points most toward it. Ties go n, e, s, w.</summary>
        public static string DoorToward(CellBox b, int tx, int tz)
        {
            double cx = b.X + (b.W - 1) / 2.0, cz = b.Z + (b.H - 1) / 2.0;
            double dx = tx - cx, dz = tz - cz;
            if (dx == 0 && dz == 0) return "s";
            if (Math.Abs(dz) >= Math.Abs(dx)) return dz > 0 ? "n" : "s";
            return dx > 0 ? "e" : "w";
        }

        /// <summary>Cells of a box, row by row from the south-west corner.</summary>
        public static List<int[]> BoxCells(CellBox b)
        {
            var cells = new List<int[]>();
            for (int z = b.Z; z <= b.MaxZ; z++)
                for (int x = b.X; x <= b.MaxX; x++) cells.Add(new[] { x, z });
            return cells;
        }

        /// <summary>Candidate south-west corners for a w x h box around a centre, nearest first (square rings),
        /// at most maxRing rings out. The game hand tests each for buildability.</summary>
        public static IEnumerable<int[]> SiteCandidates(int cx, int cz, int w, int h, int maxRing)
        {
            int ox = cx - w / 2, oz = cz - h / 2;
            yield return new[] { ox, oz };
            for (int r = 1; r <= maxRing; r++)
            {
                for (int i = -r; i <= r; i++) yield return new[] { ox + i, oz - r };
                for (int i = -r + 1; i <= r; i++) yield return new[] { ox + r, oz + i };
                for (int i = r - 1; i >= -r; i--) yield return new[] { ox + i, oz + r };
                for (int i = r - 1; i > -r; i--) yield return new[] { ox - r, oz + i };
            }
        }

        /// <summary>Rotation word or number to 0..3 (north, east, south, west); -1 for none, -2 for bad.</summary>
        public static int Rot(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return -1;
            switch (s.Trim().ToLowerInvariant())
            {
                case "0": case "n": case "north": return 0;
                case "1": case "e": case "east": return 1;
                case "2": case "s": case "south": return 2;
                case "3": case "w": case "west": return 3;
                default: return -2;
            }
        }

        // Plain words the steward may use for things, stuff and plants. The hands also accept defNames.
        // Order matters: the first researched def wins, so the no-power version comes first ("electric stove" names the other).
        public static readonly Dictionary<string, string[]> ThingAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["wall"] = new[] { "Wall" },
            ["door"] = new[] { "Door" },
            ["bed"] = new[] { "Bed" },
            ["double bed"] = new[] { "DoubleBed" },
            ["sleeping spot"] = new[] { "SleepingSpot" },
            ["hospital bed"] = new[] { "HospitalBed", "Bed" },
            ["end table"] = new[] { "EndTable" },
            ["dresser"] = new[] { "Dresser" },
            ["table"] = new[] { "Table2x2c", "Table1x2c" },
            ["big table"] = new[] { "Table2x4c", "Table3x3c" },
            ["chair"] = new[] { "DiningChair", "Stool" },
            ["stool"] = new[] { "Stool" },
            ["stove"] = new[] { "FueledStove", "ElectricStove" },
            ["fueled stove"] = new[] { "FueledStove" },
            ["electric stove"] = new[] { "ElectricStove" },
            ["campfire"] = new[] { "Campfire" },
            ["butcher table"] = new[] { "TableButcher", "ButcherSpot" },
            ["butcher spot"] = new[] { "ButcherSpot" },
            ["cooler"] = new[] { "Cooler" },
            ["heater"] = new[] { "Heater" },
            ["passive cooler"] = new[] { "PassiveCooler" },
            ["torch"] = new[] { "TorchLamp" },
            ["lamp"] = new[] { "StandingLamp", "TorchLamp" },
            ["shelf"] = new[] { "Shelf" },
            ["research bench"] = new[] { "SimpleResearchBench", "HiTechResearchBench" },
            ["crafting spot"] = new[] { "CraftingSpot" },
            ["tailoring bench"] = new[] { "HandTailoringBench", "ElectricTailoringBench" },
            ["smithy"] = new[] { "FueledSmithy", "ElectricSmithy" },
            ["machining table"] = new[] { "TableMachining" },
            ["stonecutter"] = new[] { "TableStonecutter" },
            ["sculpting table"] = new[] { "TableSculpting" },
            ["drug lab"] = new[] { "DrugLab" },
            ["brewery"] = new[] { "Brewery" },
            ["fermenting barrel"] = new[] { "FermentingBarrel" },
            ["nutrient paste dispenser"] = new[] { "NutrientPasteDispenser" },
            ["horseshoes"] = new[] { "HorseshoesPin" },
            ["chess table"] = new[] { "ChessTable" },
            ["billiards table"] = new[] { "BilliardsTable" },
            ["generator"] = new[] { "WoodFiredGenerator" },
            ["wood generator"] = new[] { "WoodFiredGenerator" },
            ["solar panel"] = new[] { "SolarGenerator" },
            ["wind turbine"] = new[] { "WindTurbine" },
            ["battery"] = new[] { "Battery" },
            ["conduit"] = new[] { "PowerConduit" },
            ["sandbags"] = new[] { "Sandbags" },
            ["turret"] = new[] { "Turret_MiniTurret" },
            ["tool cabinet"] = new[] { "ToolCabinet" },
            ["vitals monitor"] = new[] { "VitalsMonitor" },
            ["grave"] = new[] { "Grave" },
            // Biotech
            ["mech gestator"] = new[] { "MechGestator" },
            ["large mech gestator"] = new[] { "LargeMechGestator" },
            ["mech recharger"] = new[] { "BasicRecharger", "StandardRecharger" },
            ["basic recharger"] = new[] { "BasicRecharger" },
            ["standard recharger"] = new[] { "StandardRecharger" },
            ["school desk"] = new[] { "SchoolDesk" },
            ["blackboard"] = new[] { "Blackboard" },
            ["crib"] = new[] { "Crib" },
            ["baby sleeping spot"] = new[] { "BabySleepingSpot" },
            ["toy box"] = new[] { "ToyBox" },
            ["gene bank"] = new[] { "GeneBank" },
            ["gene assembler"] = new[] { "GeneAssembler" },
            ["gene extractor"] = new[] { "GeneExtractor" },
            ["growth vat"] = new[] { "GrowthVat" },
            ["wastepack atomizer"] = new[] { "WastepackAtomizer" },
            ["band node"] = new[] { "BandNode" },
            ["deathrest casket"] = new[] { "DeathrestCasket" },
        };

        public static readonly Dictionary<string, string> StuffAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wood"] = "WoodLog", ["steel"] = "Steel", ["plasteel"] = "Plasteel", ["gold"] = "Gold", ["silver"] = "Silver",
            ["uranium"] = "Uranium", ["jade"] = "Jade",
            ["granite"] = "BlocksGranite", ["marble"] = "BlocksMarble", ["limestone"] = "BlocksLimestone",
            ["sandstone"] = "BlocksSandstone", ["slate"] = "BlocksSlate",
            ["cloth"] = "Cloth", ["leather"] = "Leather_Plain",
        };

        public static readonly Dictionary<string, string> PlantAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["potatoes"] = "Plant_Potato", ["potato"] = "Plant_Potato", ["rice"] = "Plant_Rice", ["corn"] = "Plant_Corn",
            ["strawberries"] = "Plant_Strawberry", ["healroot"] = "Plant_Healroot", ["cotton"] = "Plant_Cotton",
            ["haygrass"] = "Plant_Haygrass", ["devilstrand"] = "Plant_Devilstrand", ["smokeleaf"] = "Plant_Smokeleaf",
            ["psychoid"] = "Plant_Psychoid", ["hops"] = "Plant_Hops", ["tree"] = "Plant_TreeOak", ["trees"] = "Plant_TreeOak",
            ["roses"] = "Plant_Rose", ["daylily"] = "Plant_Daylily",
        };

        /// <summary>The defNames to try for a word, in order (alias list, else the word itself as a defName).</summary>
        public static string[] ThingDefs(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return new string[0];
            string w = word.Trim();
            string[] a;
            if (ThingAliases.TryGetValue(w, out a)) return a;
            if (w.EndsWith("s", StringComparison.Ordinal) && ThingAliases.TryGetValue(w.Substring(0, w.Length - 1), out a)) return a;
            return new[] { w };
        }

        public static string StuffDef(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            string w = word.Trim(), d;
            if (StuffAliases.TryGetValue(w, out d)) return d;
            if (w.EndsWith(" blocks", StringComparison.OrdinalIgnoreCase) && StuffAliases.TryGetValue(w.Substring(0, w.Length - 7), out d)) return d;
            return w;
        }

        public static string PlantDef(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            string w = word.Trim(), d;
            return PlantAliases.TryGetValue(w, out d) ? d : w;
        }

        /// <summary>Joins plain notes: "placed 15 wall blueprints and a door".</summary>
        public static string Count(int n, string one, string many) { return n == 1 ? "a " + one : n + " " + many; }
    }
}
