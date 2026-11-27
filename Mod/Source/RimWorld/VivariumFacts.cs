using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest "facts" block: flat scalars and lists a rules engine can test. Every key is computed in its
    /// own guard, so one failing fact drops only itself; a key that cannot be computed cheaply is omitted, never guessed.
    /// Runs on the game thread; each source collection is walked once.</summary>
    public static class VivariumFacts
    {
        static readonly HashSet<string> logged = new HashSet<string>();

        static void F(JObject o, string key, Func<JToken> f)
        {
            try { var t = f(); if (t != null) o[key] = t; }
            catch (Exception e)
            {
                if (logged.Add(key)) Log.Warning("[RimWorldLLM] digest fact '" + key + "' failed: " + e.Message);
            }
        }

        static float R(float f) { return (float)Math.Round(f, 2); }

        public static JObject Build(Map map)
        {
            var o = new JObject();
            F(o, "day", () => GenDate.DaysPassed);
            F(o, "season", () => { string k = FactsPure.SeasonKey(GenLocalDate.Season(map).ToString()); return k == null ? null : (JToken)k; });
            F(o, "biome", () => FactsPure.SnakeCase(map.Biome.defName));
            F(o, "temp_c", () => R(map.mapTemperature.OutdoorTemp));
            F(o, "storyteller", () => FactsPure.StorytellerKey(Find.Storyteller.def.defName));
            F(o, "difficulty_threat_scale", () => R(Find.Storyteller.difficulty.threatScale));
            F(o, "wealth", () => (int)Math.Round(map.wealthWatcher.WealthTotal));
            F(o, "food_days", () => R(Routes.FoodDays(map)));
            F(o, "dlc", Dlc);

            List<Pawn> cols = null;
            try { cols = map.mapPawns.FreeColonistsSpawned.Where(p => p.DevelopmentalStage.Adult()).ToList(); }
            catch (Exception e) { if (logged.Add("colonists_list")) Log.Warning("[RimWorldLLM] digest facts colonists failed: " + e.Message); }
            if (cols != null) Colonists(o, map, cols);

            F(o, "prisoners", () => map.mapPawns.PrisonersOfColonySpawnedCount);
            F(o, "animals", () => map.mapPawns.SpawnedColonyAnimals.Count);
            Beds(o, map);
            Hostiles(o, map);
            F(o, "turrets", () => Turrets(map));
            F(o, "events", () => Events(map));
            F(o, "fires", () => Fires(map));
            Power(o, map);
            try { VivariumFood.AddFacts(o, map); } catch (Exception e) { if (logged.Add("food_facts")) Log.Warning("[RimWorldLLM] digest food facts failed: " + e.Message); }
            try { VivariumDeadlines.AddFacts(o, map); } catch (Exception e) { if (logged.Add("deadline_facts")) Log.Warning("[RimWorldLLM] digest deadline facts failed: " + e.Message); }

            var rc = map.resourceCounter;
            F(o, "medicine_total", () => rc.GetCount(ThingDefOf.MedicineHerbal) + rc.GetCount(ThingDefOf.MedicineIndustrial) + rc.GetCount(ThingDefOf.MedicineUltratech));
            F(o, "medicine_industrial", () => rc.GetCount(ThingDefOf.MedicineIndustrial) + rc.GetCount(ThingDefOf.MedicineUltratech));
            F(o, "steel", () => VivariumLoose.Usable(map, ThingDefOf.Steel));
            F(o, "wood", () => VivariumLoose.Usable(map, ThingDefOf.WoodLog));
            F(o, "components", () => VivariumLoose.Usable(map, ThingDefOf.ComponentIndustrial) + VivariumLoose.Usable(map, ThingDefOf.ComponentSpacer));
            F(o, "silver", () => VivariumLoose.Usable(map, ThingDefOf.Silver));

            F(o, "research_current", () => { var p = Find.ResearchManager.GetProject(); return p == null ? JValue.CreateNull() : (JToken)p.defName; });
            F(o, "research_done", () => new JArray(DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(d => d.IsFinished).Select(d => d.defName)));
            // Every research defName this game defines, so the agent can switch off playbook rules naming research that
            // does not exist (the playbook's names are not all confirmed). Never rendered into the prompt.
            F(o, "research_known", () => new JArray(DefDatabase<ResearchProjectDef>.AllDefsListForReading.Select(d => d.defName)));
            return o;
        }

        static JToken Dlc()
        {
            var a = new JArray();
            if (ModsConfig.RoyaltyActive) a.Add("royalty");
            if (ModsConfig.IdeologyActive) a.Add("ideology");
            if (ModsConfig.BiotechActive) a.Add("biotech");
            if (ModsConfig.AnomalyActive) a.Add("anomaly");
            if (ModsConfig.OdysseyActive) a.Add("odyssey");
            return a;
        }

        static bool IsArmed(Pawn p)
        {
            var w = p.equipment != null ? p.equipment.Primary : null;
            if (w == null || !w.def.IsWeapon) return false;
            if (w.def.IsRangedWeapon) return true;
            return w.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS) >= 8f; // "good melee": a knife is ~4, a longsword ~13
        }

        static bool HasDisease(Pawn p)
        {
            var hs = p.health != null ? p.health.hediffSet : null;
            if (hs == null) return false;
            foreach (var h in hs.hediffs)
            {
                if (h is Hediff_Injury || h is Hediff_MissingPart || !h.Visible) continue;
                if (h.def.makesSickThought || (h.def.tendable && h.def.isBad && !h.def.chronic && h.def.lethalSeverity > 0f)) return true;
            }
            return false;
        }

        static void Colonists(JObject o, Map map, List<Pawn> cols)
        {
            F(o, "colonists", () => cols.Count);
            F(o, "colonists_capable_fighters", () => cols.Count(p => !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent)));
            F(o, "colonists_armed", () => cols.Count(IsArmed));
            F(o, "colonists_downed", () => cols.Count(p => p.Downed));
            F(o, "colonists_bleeding", () => cols.Count(p => p.health != null && p.health.hediffSet.BleedRateTotal > 0.01f));
            F(o, "colonists_sick", () => cols.Count(HasDisease));
            F(o, "mental_breaks_active", () => cols.Count(p => p.InMentalState));
            F(o, "colonists_below_minor", () => cols.Count(p => p.needs != null && p.needs.mood != null && p.mindState != null && p.needs.mood.CurLevel < p.mindState.mentalBreaker.BreakThresholdMinor));
            F(o, "colonists_below_major", () => cols.Count(p => p.needs != null && p.needs.mood != null && p.mindState != null && p.needs.mood.CurLevel < p.mindState.mentalBreaker.BreakThresholdMajor));

            var moods = cols.Where(p => p.needs != null && p.needs.mood != null).Select(p => p.needs.mood.CurLevelPercentage * 100f).ToList();
            if (moods.Count > 0)
            {
                F(o, "mood_avg", () => R(moods.Average()));
                F(o, "mood_min", () => R(moods.Min()));
            }
            var joys = cols.Where(p => p.needs != null && p.needs.joy != null).Select(p => p.needs.joy.CurLevelPercentage * 100f).ToList();
            if (joys.Count > 0) F(o, "recreation_avg", () => R(joys.Average()));

            F(o, "colonists_without_bedroom", () => cols.Count(p =>
            {
                var bed = p.ownership != null ? p.ownership.OwnedBed : null;
                return bed == null || bed.GetRoom() == null || bed.GetRoom().Role != RoomRoleDefOf.Bedroom;
            }));

            F(o, "best_cook_skill", () => BestSkill(cols, SkillDefOf.Cooking));
            F(o, "best_doctor_skill", () => BestSkill(cols, SkillDefOf.Medicine));
            F(o, "doctors_capable", () => cols.Count(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)));
        }

        static int BestSkill(List<Pawn> cols, SkillDef def)
        {
            int best = 0;
            foreach (var p in cols)
            {
                var s = p.skills != null ? p.skills.GetSkill(def) : null;
                if (s != null && !s.TotallyDisabled && s.Level > best) best = s.Level;
            }
            return best;
        }

        static void Beds(JObject o, Map map)
        {
            int free = 0, prisonerFree = 0, hospital = 0;
            try
            {
                foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
                {
                    if (bed.Medical)
                    {
                        var r = bed.GetRoom();
                        if (r != null && r.Role == RoomRoleDefOf.Hospital) hospital++;
                        continue;
                    }
                    if (bed.OwnersForReading.Count > 0 || bed.AnyOccupants) continue;
                    if (bed.ForPrisoners) prisonerFree++;
                    else if (!bed.ForSlaves) free++;
                }
            }
            catch (Exception e)
            {
                if (logged.Add("beds")) Log.Warning("[RimWorldLLM] digest facts beds failed: " + e.Message);
                return;
            }
            o["beds_free"] = free;
            o["prisoner_beds_free"] = prisonerFree;
            o["hospital_beds"] = hospital;
        }

        static void Hostiles(JObject o, Map map)
        {
            int enemies = 0, raiders = 0;
            try
            {
                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p.Dead || p.Position.Fogged(map) || !p.HostileTo(Faction.OfPlayer)) continue;
                    enemies++;
                    if (p.RaceProps.Humanlike || p.RaceProps.IsMechanoid) raiders++;
                }
            }
            catch (Exception e)
            {
                if (logged.Add("hostiles")) Log.Warning("[RimWorldLLM] digest facts hostiles failed: " + e.Message);
                return;
            }
            o["enemies_count"] = enemies;
            o["raid_active"] = raiders > 0;
        }

        static JToken Turrets(Map map)
        {
            int n = 0;
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_Turret)) continue;
                var pt = b.GetComp<CompPowerTrader>();
                if (pt != null && pt.PowerOn) n++;
            }
            return n;
        }

        static JToken Events(Map map)
        {
            var list = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, list);
            var a = new JArray();
            foreach (var d in list.Select(c => FactsPure.SnakeCase(c.def.defName)).Distinct()) a.Add(d);
            return a;
        }

        static JToken Fires(Map map)
        {
            var home = map.areaManager.Home;
            int n = 0;
            foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
                if (home[t.Position]) n++;
            return n;
        }

        static void Power(JObject o, Map map)
        {
            float net = 0, stored = 0, cap = 0;
            try
            {
                foreach (var pn in map.powerNetManager.AllNetsListForReading)
                {
                    foreach (var c in pn.powerComps) net += c.PowerOutput;
                    foreach (var b in pn.batteryComps) { stored += b.StoredEnergy; cap += b.Props.storedEnergyMax; }
                }
            }
            catch (Exception e)
            {
                if (logged.Add("power")) Log.Warning("[RimWorldLLM] digest facts power failed: " + e.Message);
                return;
            }
            o["power_net_w"] = (int)Math.Round(net);
            o["battery_wd"] = (int)Math.Round(stored);
            o["battery_capacity_wd"] = (int)Math.Round(cap);
            F(o, "unroofed_electrics", () =>
            {
                int n = 0;
                foreach (var b in map.listerBuildings.allBuildingsColonist)
                {
                    var pt = b.GetComp<CompPowerTrader>();
                    if (pt != null && pt.PowerOn && pt.PowerOutput < 0f && !b.Position.Roofed(map)) n++;
                }
                return n;
            });
        }
    }
}
