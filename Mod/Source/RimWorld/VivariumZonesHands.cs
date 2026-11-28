using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The rezone hand (POST /act/rezone): a stockpile's priority and filter, the crop of a growing zone or of every hydroponics
    /// basin. Game thread. Decisions (words, crops, wording) are in ZonesPure; every change is marked "zone_settings" for undo.</summary>
    public sealed partial class VivariumHands : IZonesHands
    {
        // ---------------------------------------------------------------- the game's defs as ZonesPure's records
        internal static List<CropInfo> Crops()
        {
            var l = new List<CropInfo>();
            foreach (var d in DefDatabase<ThingDef>.AllDefs)
            {
                if (d.plant == null || d.plant.sowTags == null || d.plant.sowTags.Count == 0) continue;
                var miss = d.plant.sowResearchPrerequisites == null ? null : d.plant.sowResearchPrerequisites.FirstOrDefault(r => !r.IsFinished);
                l.Add(new CropInfo
                {
                    Def = d.defName, Label = d.label ?? d.defName, Harvest = d.plant.harvestedThingDef != null ? d.plant.harvestedThingDef.label : null,
                    Ground = d.plant.sowTags.Contains("Ground"), Hydro = d.plant.sowTags.Contains("Hydroponic"),
                    Research = miss == null ? null : (miss.label ?? miss.defName)
                });
            }
            return l;
        }

        /// <summary>The crop def for a word, for a field or the hydroponics basins; null with the refusal.</summary>
        internal static ThingDef ResolveCropDef(string word, bool hydro, out string refusal)
        {
            CropInfo c;
            if (!ZonesPure.ResolveCrop(word, Crops(), hydro, out c, out refusal)) return null;
            return DefDatabase<ThingDef>.GetNamedSilentFail(c.Def);
        }

        internal static List<Building_PlantGrower> HydroBasins(Map map)
        {
            return map.listerBuildings.allBuildingsColonist.OfType<Building_PlantGrower>().Where(b => b.def.building != null && b.def.building.sowTag == "Hydroponic").ToList();
        }

        static List<NamedDef> FilterCategories()
        {
            return DefDatabase<ThingCategoryDef>.AllDefs.Where(c => c != ThingCategoryDefOf.Root && !string.IsNullOrEmpty(c.label)).Select(c => new NamedDef(c.defName, c.label)).ToList();
        }

        static List<NamedDef> FilterThings()
        {
            return DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Item && !string.IsNullOrEmpty(d.label) && d.thingCategories != null && d.thingCategories.Count > 0)
                .Select(d => new NamedDef(d.defName, d.label)).ToList();
        }

        static StoragePriority? PriorityWord(string s)
        {
            switch (s) { case "low": return StoragePriority.Low; case "normal": return StoragePriority.Normal; case "preferred": return StoragePriority.Preferred;
                case "important": return StoragePriority.Important; case "critical": return StoragePriority.Critical; default: return null; }
        }

        /// <summary>The zones as the model sees them, with the game zone behind each id.</summary>
        static List<ZoneRef> ZoneRefs(Map map, Dictionary<int, Zone> byId)
        {
            var l = new List<ZoneRef>();
            foreach (var z in map.zoneManager.AllZones)
            {
                var g = z as Zone_Growing; var s = z as Zone_Stockpile;
                if (g != null)
                {
                    var p = g.GetPlantDefToGrow();
                    l.Add(new ZoneRef { Id = z.ID, Kind = "grow", Label = z.label, Crop = p == null ? "" : ZonesPure.CropWord(p.defName, p.label) });
                    byId[z.ID] = z;
                }
                else if (s != null)
                {
                    l.Add(new ZoneRef { Id = z.ID, Kind = "stockpile", Label = z.label, Preset = VivariumBuildView.Preset(s) });
                    byId[z.ID] = z;
                }
            }
            var basins = HydroBasins(map);
            if (basins.Count > 0)
            {
                var p = basins[0].GetPlantDefToGrow();
                l.Add(new ZoneRef { Kind = "hydroponics", Label = "hydroponics", Crop = p == null ? "" : ZonesPure.CropWord(p.defName, p.label) });
            }
            return l;
        }

        // ---------------------------------------------------------------- rezone
        public HandResult Rezone(RezoneArgs a)
        {
            try
            {
                var map = HomeMap();
                if (map == null) return HandResult.No("no colony map");
                var byId = new Dictionary<int, Zone>();
                var refs = ZoneRefs(map, byId);
                ZoneRef zr; string err;
                if (!ZonesPure.ResolveZone(a.Zone, refs, out zr, out err)) return HandResult.No(err, "change a zone");
                string wrong = ZonesPure.WrongField(zr.Kind, a);
                if (wrong != null) return HandResult.No(wrong, "change " + ZonesPure.ZoneName(zr));
                string what = "change " + ZonesPure.ZoneName(zr);
                if (zr.Kind == "stockpile") return RezoneStockpile((Zone_Stockpile)byId[zr.Id], zr, a, map, what);
                return RezoneCrop(zr, byId.ContainsKey(zr.Id) ? byId[zr.Id] as Zone_Growing : null, a, map, what);
            }
            catch (Exception e) { return Failed(e); }
        }

        HandResult RezoneStockpile(Zone_Stockpile z, ZoneRef zr, RezoneArgs a, Map map, string what)
        {
            var f = z.settings.filter;
            // resolve every word first: a bad word changes nothing
            var cats = FilterCategories(); var things = FilterThings();
            var steps = new List<KeyValuePair<FilterHit, bool>>();
            var allowLabels = new List<string>(); var denyLabels = new List<string>();
            foreach (var pair in a.Allow.Select(w => new KeyValuePair<string, bool>(w, true)).Concat(a.Deny.Select(w => new KeyValuePair<string, bool>(w, false))))
            {
                FilterHit hit; string refusal;
                if (!ZonesPure.ResolveFilterWord(pair.Key, cats, things, out hit, out refusal)) return HandResult.No(refusal, what);
                steps.Add(new KeyValuePair<FilterHit, bool>(hit, pair.Value));
                (pair.Value ? allowLabels : denyLabels).Add(hit.Label);
            }
            var before = f.AllowedThingDefs.Select(d => d.defName).ToList();
            var oldPriority = z.settings.Priority;
            var pr = a.Priority == null ? null : PriorityWord(a.Priority);
            if (pr.HasValue) z.settings.Priority = pr.Value;
            foreach (var st in steps)
            {
                if (st.Key.Category)
                {
                    var cd = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(st.Key.Def);
                    if (cd != null) f.SetAllow(cd, st.Value);
                }
                else
                {
                    var td = DefDatabase<ThingDef>.GetNamedSilentFail(st.Key.Def);
                    if (td != null) f.SetAllow(td, st.Value);
                }
            }
            string diff = ZonesPure.EncodeDiff(before, f.AllowedThingDefs.Select(d => d.defName));
            bool filterChanged = diff != "+|-";
            bool prChanged = pr.HasValue && pr.Value != oldPriority;
            if (!filterChanged && !prChanged)
                return HandResult.Done(ZonesPure.SetNote(zr, a.Priority, allowLabels, denyLabels, null) + " (already so)");
            var mark = new NarratorMark { Kind = "zone_settings", Map = map.uniqueID, Id = z.ID };
            if (prChanged) mark.Sub = (int)oldPriority + 1;
            if (filterChanged) mark.Extra = diff;
            return HandResult.Placed(ZonesPure.SetNote(zr, a.Priority, allowLabels, denyLabels, null), new List<NarratorMark> { mark });
        }

        HandResult RezoneCrop(ZoneRef zr, Zone_Growing z, RezoneArgs a, Map map, string what)
        {
            bool hydro = zr.Kind == "hydroponics";
            string refusal;
            var plant = ResolveCropDef(a.Plant, hydro, out refusal);
            if (plant == null) return HandResult.No(refusal, what);
            string word = ZonesPure.CropWord(plant.defName, plant.label);
            var marks = new List<NarratorMark>();
            string tail = "";
            if (hydro)
            {
                var basins = HydroBasins(map).Where(b => b.GetPlantDefToGrow() != plant).ToList();
                if (basins.Count == 0) return HandResult.Done("the hydroponics basins already grow " + word);
                foreach (var grp in basins.GroupBy(b => b.GetPlantDefToGrow() == null ? "" : b.GetPlantDefToGrow().defName))
                    marks.Add(new NarratorMark { Kind = "zone_settings", Map = map.uniqueID, Def = grp.Key, Things = grp.Select(b => b.thingIDNumber).ToList() });
                foreach (var b in basins) b.SetPlantDefToGrow(plant);
                if (basins.Any(b => b.PlantsOnMe.Any(p => p.def != plant))) tail = " (plants already in the basins stay until harvested)";
            }
            else
            {
                var old = z.GetPlantDefToGrow();
                if (old == plant) return HandResult.Done(ZonesPure.ZoneName(zr) + " already grows " + word);
                marks.Add(new NarratorMark { Kind = "zone_settings", Map = map.uniqueID, Id = z.ID, Def = old == null ? "" : old.defName });
                bool growing = old != null && z.Cells.Any(c => { var p = c.GetPlant(map); return p != null && p.def == old; });
                z.SetPlantDefToGrow(plant);
                var cell = z.Cells.FirstOrDefault();
                if (growing) tail = " (the " + (old.label ?? old.defName) + " already growing stays until harvested)";
                if (z.Cells.Count > 0 && !PlantUtility.GrowthSeasonNow(cell, map, plant)) tail += " (" + word + " can't grow there right now: wrong season or temperature)";
            }
            return HandResult.Placed(ZonesPure.SetNote(zr, null, new string[0], new string[0], word) + tail, marks);
        }

        // ---------------------------------------------------------------- undo
        /// <summary>Puts back what a zone_settings mark recorded. False when the zone or basin is gone.</summary>
        bool UndoZoneSettings(NarratorMark mk, Map map)
        {
            ThingDef old = string.IsNullOrEmpty(mk.Def) ? null : DefDatabase<ThingDef>.GetNamedSilentFail(mk.Def);
            if (mk.Things.Count > 0)
            {
                bool any = false;
                foreach (int id in mk.Things)
                {
                    var g = ThingById(map, id) as IPlantToGrowSettable;
                    if (g == null || old == null) continue;
                    g.SetPlantDefToGrow(old); any = true;
                }
                return any;
            }
            var z = map.zoneManager.AllZones.FirstOrDefault(x => x.ID == mk.Id);
            var s = z as Zone_Stockpile;
            if (s != null)
            {
                if (mk.Sub > 0) s.settings.Priority = (StoragePriority)(mk.Sub - 1);
                List<string> on, off;
                ZonesPure.DecodeDiff(mk.Extra, out on, out off);
                foreach (var n in on) { var d = DefDatabase<ThingDef>.GetNamedSilentFail(n); if (d != null) s.settings.filter.SetAllow(d, false); }
                foreach (var n in off) { var d = DefDatabase<ThingDef>.GetNamedSilentFail(n); if (d != null) s.settings.filter.SetAllow(d, true); }
                return true;
            }
            var gz = z as Zone_Growing;
            if (gz != null && old != null) { gz.SetPlantDefToGrow(old); return true; }
            return false;
        }
    }
}
