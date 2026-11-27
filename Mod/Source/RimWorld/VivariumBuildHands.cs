using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Phase 2 game bodies: designations, blueprints, rooms, zones, areas, bills and undo
    /// (docs/VIVARIUM.md "Phase 2"). Game thread only.</summary>
    public sealed partial class VivariumHands
    {
        public HandResult Designate(DesignateArgs a, BuildBudget left) { return HandResult.No("not built yet"); }
        public HandResult Build(BuildArgs a, BuildBudget left) { return HandResult.No("not built yet"); }
        public HandResult Room(RoomArgs a, BuildBudget left) { return HandResult.No("not built yet"); }
        public HandResult Zone(ZoneArgs a, BuildBudget left) { return HandResult.No("not built yet"); }
        public HandResult Area(AreaArgs a, BuildBudget left) { return HandResult.No("not built yet"); }
        public HandResult Bill(BillArgs a, BuildBudget left) { return HandResult.No("not built yet"); }
        public JObject Benches() { return new JObject { ["benches"] = new JArray() }; }
        public HandResult Undo(IList<NarratorMark> marks) { return HandResult.No("not built yet"); }
    }
}
