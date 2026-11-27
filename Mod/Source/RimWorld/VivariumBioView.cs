using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The digest's `bio` block (docs/VIVARIUM.md "Biotech"): mechs, pollution, genes, children. Null when Biotech
    /// is not active. Vanilla reads only, what the colony UI shows; words, never coordinates. Each part is independent
    /// and dropped if it throws.</summary>
    public static class VivariumBioView
    {
        const int MaxMechs = 12, MaxPeople = 12, MaxBuildings = 6, MaxPacks = 8, MaxGestate = 10;
        static readonly HashSet<string> logged = new HashSet<string>();
        // pollution trend: the polluted home-cell count a day ago (not saved: a loaded game starts "steady")
        static int trendPrev = -1, trendNow = -1, trendPrevPacks = -1, trendNowPacks = -1, trendTick = -1, trendMap = -1;

        static void Part(JObject o, string name, Func<JToken> f)
        {
            try { var t = f(); if (t != null) o[name] = t; }
            catch (Exception e) { if (logged.Add(name)) Log.Warning("[RimWorldLLM] digest bio part '" + name + "' failed: " + e.Message); }
        }

        static int Pct(float f) { return (int)Math.Round(Math.Max(0f, Math.Min(1f, f)) * 100f); }

        static List<Pawn> Colonists(Map map)
        {
            return map.mapPawns.FreeColonistsAndPrisonersSpawned.Where(p => p.RaceProps.Humanlike && !p.IsPrisoner).ToList();
        }

        public static JObject Build(Map map)
        {
            if (!ModsConfig.BiotechActive || map == null) return null;
            var o = new JObject();
            var people = Colonists(map);
            var mechs = map.mapPawns.SpawnedColonyMechs ?? new List<Pawn>();
            Part(o, "mechanitors", () => Mechanitors(people));
            Part(o, "mechs", () => Mechs(mechs));
            if (mechs.Count > MaxMechs) o["mechs_total"] = mechs.Count;
            Part(o, "gestators", () => Gestators(map));
            Part(o, "rechargers", () => Rechargers(map));
            Part(o, "can_gestate", () => CanGestate(people));
            Part(o, "pollution", () => Pollution(map, people));
            Part(o, "genes", () => Genes(people));
            Part(o, "hemogen", () => Hemogen(map, people));
            Part(o, "genebank", () => GeneBank(map));
            Part(o, "xenogerms", () => Xenogerms(map));
            Part(o, "children", () => Children(people));
            Part(o, "babies", () => Babies(people));
            Part(o, "pregnancies", () => Pregnancies(people));
            Part(o, "carers", () => Carers(people));
            Part(o, "may_implant", () => VivariumGame.State.Settings.MayImplantGenes);
            Part(o, "missing", () => new JArray(BioPure.Missing(MissingInput(map, people, mechs))));
            return o;
        }

        // ---- mechs ----
        static JToken Mechanitors(List<Pawn> people)
        {
            var a = new JArray();
            foreach (var p in people)
            {
                var t = p.mechanitor;
                if (t == null || !MechanitorUtility.IsMechanitor(p)) continue;
                var groups = new JArray();
                int i = 1;
                foreach (var g in t.controlGroups)
                {
                    groups.Add(new JObject { ["group"] = i, ["mode"] = BioPure.ModeWord(g.WorkMode?.defName), ["mechs"] = g.MechsForReading.Count });
                    i++;
                }
                a.Add(new JObject
                {
                    ["pawn"] = p.thingIDNumber, ["name"] = p.LabelShort,
                    ["bandwidth_used"] = t.UsedBandwidthFromSubjects, ["bandwidth_total"] = t.TotalBandwidth,
                    ["gestating_bandwidth"] = t.UsedBandwidthFromGestation, ["groups"] = groups
                });
            }
            return a.Count == 0 ? null : a;
        }

        static string MechProblem(Pawn m)
        {
            if (m.Downed) return "downed";
            var e = m.needs?.energy;
            if (e != null && e.IsSelfShutdown) return "dormant";
            if (e != null && e.CurLevelPercentage < 0.2f)
                return MechanitorUtility.RechargerForMech(m.def) == null || !m.Map.listerBuildings.allBuildingsColonist.OfType<Building_MechCharger>().Any(c => c.IsCompatibleWithCharger(m.kindDef))
                    ? "no recharger" : "low power";
            var ov = m.GetOverseer();
            if (ov != null && ov.Spawned && ov.Map == m.Map && !MechanitorUtility.InMechanitorCommandRange(m, m)) return "out of command range";
            return null;
        }

        static JToken Mechs(List<Pawn> mechs)
        {
            if (mechs.Count == 0) return null;
            return new JArray(mechs.Take(MaxMechs).Select(m =>
            {
                var g = m.GetMechControlGroup();
                var o = new JObject
                {
                    ["id"] = m.thingIDNumber, ["name"] = m.LabelShort, ["kind"] = (m.kindDef?.label ?? m.def.label ?? "").ToLowerInvariant(),
                    ["overseer"] = m.GetOverseer()?.LabelShort, ["group"] = g?.Index ?? 0,
                    ["mode"] = BioPure.ModeWord(m.GetMechWorkMode()?.defName),
                    ["job"] = SafeReport(m), ["energy"] = m.needs?.energy != null ? Pct(m.needs.energy.CurLevelPercentage) : (int?)null,
                    ["problem"] = MechProblem(m)
                };
                return o;
            }));
        }

        static string SafeReport(Pawn p)
        {
            try { var r = p.jobs?.curDriver?.GetReport(); return string.IsNullOrEmpty(r) ? null : r.TrimEnd('.'); } catch (Exception) { return null; }
        }

        static JToken Gestators(Map map)
        {
            var list = map.listerBuildings.allBuildingsColonist.OfType<Building_MechGestator>().Take(MaxBuildings).ToList();
            if (list.Count == 0) return null;
            return new JArray(list.Select(g =>
            {
                string state;
                var bill = g.ActiveMechBill;
                var mech = g.GestatingMech;
                if (mech != null) state = "gestating " + (mech.kindDef?.label ?? mech.LabelShort) + " (" + Pct(g.CurrentBillFormingPercent) + "%)";
                else if (bill != null) state = "waiting to start " + (bill.recipe.ProducedThingDef?.label ?? bill.recipe.label);
                else if (g.BillStack != null && g.BillStack.Count > 0) state = "queued: " + string.Join(", ", g.BillStack.Bills.Take(3).Select(b => b.recipe.ProducedThingDef?.label ?? b.recipe.label));
                else state = "idle";
                if (!g.PoweredOn) state += ", no power";
                return new JObject { ["id"] = g.thingIDNumber, ["label"] = g.def.label, ["state"] = state };
            }));
        }

        static JToken Rechargers(Map map)
        {
            var list = map.listerBuildings.allBuildingsColonist.OfType<Building_MechCharger>().Take(MaxBuildings).ToList();
            if (list.Count == 0) return null;
            return new JArray(list.Select(c =>
            {
                string state = c.CurrentlyChargingMech != null ? "charging " + c.CurrentlyChargingMech.LabelShort : "empty";
                if (!c.IsPowered) state += ", no power";
                if (c.IsFullOfWaste) state += ", full of waste";
                return new JObject { ["id"] = c.thingIDNumber, ["label"] = c.def.label, ["state"] = state};
            }));
        }

        /// <summary>The mech recipes a gestator could take now (research done), with their bandwidth cost.</summary>
        internal static IEnumerable<RecipeDef> GestateRecipes()
        {
            foreach (var r in MechanitorUtility.MechRecipes)
                if (r != null && r.ProducedThingDef != null && r.AvailableNow) yield return r;
        }

        internal static int BandwidthOf(ThingDef mech)
        {
            try { return (int)Math.Round(mech.GetStatValueAbstract(StatDefOf.BandwidthCost)); } catch (Exception) { return 1; }
        }

        static JToken CanGestate(List<Pawn> people)
        {
            if (!people.Any(p => MechanitorUtility.IsMechanitor(p))) return null;
            var a = new JArray(GestateRecipes().Take(MaxGestate).Select(r => new JObject
            { ["kind"] = (r.ProducedThingDef.label ?? r.defName).ToLowerInvariant(), ["bandwidth"] = BandwidthOf(r.ProducedThingDef) }));
            return a.Count == 0 ? null : a;
        }

        // ---- pollution ----
        static int Wastepacks(IEnumerable<Thing> things) { return things.Sum(t => t.stackCount); }

        internal static bool IsWasteStockpile(Zone_Stockpile z)
        {
            var f = z.settings?.filter;
            if (f == null || ThingDefOf.Wastepack == null || !f.Allows(ThingDefOf.Wastepack)) return false;
            return !f.Allows(ThingDefOf.MealSimple) && !f.Allows(ThingDefOf.Steel);
        }

        static JToken Pollution(Map map, List<Pawn> people)
        {
            var o = new JObject();
            var packs = ThingDefOf.Wastepack != null ? map.listerThings.ThingsOfDef(ThingDefOf.Wastepack).Where(t => t.Spawned).ToList() : new List<Thing>();
            var homeArea = map.areaManager.Home;
            var wasteZones = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Where(IsWasteStockpile).ToList();
            int near = Wastepacks(packs.Where(t => homeArea != null && homeArea[t.Position] && !(t.Position.GetZone(map) is Zone_Stockpile zs && IsWasteStockpile(zs))));
            o["wastepacks"] = Wastepacks(packs);
            o["wastepacks_near_home"] = near;
            int homeCells = 0, homePolluted = 0;
            if (homeArea != null)
                foreach (var c in homeArea.ActiveCells) { homeCells++; if (map.pollutionGrid.IsPolluted(c)) homePolluted++; }
            o["home_polluted_pct"] = homeCells == 0 ? 0 : (int)Math.Round(100.0 * homePolluted / homeCells);
            o["map_polluted_pct"] = Pct(map.pollutionGrid.TotalPollutionPercent);
            o["trend"] = Trend(map, homePolluted, near);
            var tox = new JArray();
            foreach (var p in people)
            {
                var h = p.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.ToxicBuildup);
                if (h != null && h.Severity >= 0.05f) tox.Add(p.LabelShort + " " + Pct(h.Severity) + "%");
            }
            if (tox.Count > 0) o["toxic_buildup"] = tox;
            var atomDef = DefDatabase<ThingDef>.GetNamedSilentFail("WastepackAtomizer");
            o["atomizer"] = atomDef != null && map.listerBuildings.ColonistsHaveBuilding(atomDef);
            o["can_atomize"] = atomDef != null && atomDef.IsResearchFinished;
            o["waste_stockpile"] = wasteZones.Count > 0;
            return o;
        }

        static string Trend(Map map, int cells, int packs)
        {
            int tick = Find.TickManager.TicksGame;
            if (trendMap != map.uniqueID || trendTick < 0 || tick < trendTick)
            { trendMap = map.uniqueID; trendPrev = trendNow = cells; trendPrevPacks = trendNowPacks = packs; trendTick = tick; }
            else if (tick - trendTick >= GenDate.TicksPerDay)
            { trendPrev = trendNow; trendPrevPacks = trendNowPacks; trendTick = tick; }
            trendNow = cells; trendNowPacks = packs;   // the last reading of the day becomes tomorrow's yesterday
            return BioPure.Trend(trendPrev, cells) == "growing" || BioPure.Trend(trendPrevPacks, packs) == "growing" ? "growing" : "steady";
        }

        // ---- genes ----
        internal static List<GeneInfo> GeneInfos(Pawn p)
        {
            var list = new List<GeneInfo>();
            if (p.genes == null) return list;
            foreach (var g in p.genes.GenesListForReading)
            {
                if (g == null || !g.Active || g.def == null) continue;
                var cd = g as Gene_ChemicalDependency;
                list.Add(new GeneInfo
                {
                    DefName = g.def.defName, Label = g.def.label,
                    Chemical = cd != null ? (g.def.chemical?.label ?? g.def.label) : null,
                    Hemogen = g is Gene_Hemogen
                });
            }
            return list;
        }

        static Gene_Hemogen HemogenGene(Pawn p) { return p.genes?.GetFirstGeneOfType<Gene_Hemogen>(); }

        static JToken Genes(List<Pawn> people)
        {
            var a = new JArray();
            foreach (var p in people.Take(MaxPeople * 2))
            {
                if (p.genes == null) continue;
                var effects = BioPure.GeneEffects(GeneInfos(p));
                bool baseliner = p.genes.Xenotype == XenotypeDefOf.Baseliner && !p.genes.UniqueXenotype;
                if (baseliner && effects.Count == 0) continue;
                var o = new JObject { ["pawn"] = p.thingIDNumber, ["name"] = p.LabelShort, ["xenotype"] = p.genes.XenotypeLabelCap.ToString(), ["effects"] = new JArray(effects) };
                var hg = HemogenGene(p);
                if (hg != null) o["hemogen_pct"] = Pct(hg.Value / Math.Max(0.01f, hg.Max));
                a.Add(o);
                if (a.Count >= MaxPeople) break;
            }
            return a.Count == 0 ? null : a;
        }

        static JToken Hemogen(Map map, List<Pawn> people)
        {
            var carriers = people.Where(p => HemogenGene(p) != null).ToList();
            var donors = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.guest != null && PrisonerInteractionModeDefOf.HemogenFarm != null && p.guest.IsInteractionEnabled(PrisonerInteractionModeDefOf.HemogenFarm)).ToList();
            if (carriers.Count == 0 && donors.Count == 0) return null;
            int packs = ThingDefOf.HemogenPack != null ? map.resourceCounter.GetCount(ThingDefOf.HemogenPack) : 0;
            var o = new JObject { ["packs"] = packs };
            if (donors.Count > 0) o["donors"] = new JArray(donors.Take(MaxPeople).Select(p => "prisoner " + p.LabelShort + " (hemogen farm)"));
            var needing = carriers.Select(p => new { p, h = HemogenGene(p) }).Where(x => x.h.Value / Math.Max(0.01f, x.h.Max) < 0.35f)
                                  .Select(x => x.p.LabelShort + " " + Pct(x.h.Value / Math.Max(0.01f, x.h.Max)) + "%").ToList();
            if (needing.Count > 0) o["needing"] = new JArray(needing);
            var prisoners = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.RaceProps.Humanlike && !donors.Contains(p) && p.DevelopmentalStage.Adult() && HemogenGene(p) == null).Take(MaxPeople).ToList();
            if (prisoners.Count > 0) o["prisoners"] = new JArray(prisoners.Select(p => new JObject { ["id"] = p.thingIDNumber, ["name"] = p.LabelShort }));
            return o;
        }

        static IEnumerable<CompGenepackContainer> Banks(Map map)
        {
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                var c = b.TryGetComp<CompGenepackContainer>();
                if (c != null) yield return c;
            }
        }

        internal static List<Genepack> Unstored(Map map)
        {
            return map.listerThings.ThingsOfDef(ThingDefOf.Genepack).OfType<Genepack>().Where(g => g.Spawned && !g.IsForbidden(Faction.OfPlayer)).ToList();
        }

        static string PackWord(Genepack g)
        {
            int cpx = g.GeneSet?.ComplexityTotal ?? 0;
            return g.LabelNoCount + " (complexity " + cpx + ")";
        }

        static JToken GeneBank(Map map)
        {
            var banks = Banks(map).ToList();
            var loose = Unstored(map);
            var stored = banks.SelectMany(b => b.ContainedGenepacks).ToList();
            var assembler = map.listerBuildings.allBuildingsColonist.OfType<Building_GeneAssembler>().FirstOrDefault();
            if (banks.Count == 0 && loose.Count == 0 && assembler == null) return null;
            var o = new JObject
            {
                ["genepacks"] = stored.Count + loose.Count, ["unstored"] = loose.Count, ["banks"] = banks.Count,
                ["bank_space"] = banks.Sum(b => Math.Max(0, b.Props.maxCapacity - b.ContainedGenepacks.Count))
            };
            if (assembler != null)
                o["assembler"] = assembler.Working ? "assembling " + (assembler.xenotypeName ?? "a xenogerm") + " (" + Pct(assembler.ProgressPercent) + "%)"
                    : "idle (max complexity " + assembler.MaxComplexity() + ")";
            var packs = stored.Concat(loose).Take(MaxPacks).Select(PackWord).ToList();
            if (packs.Count > 0) o["packs"] = new JArray(packs);
            return o;
        }

        static JToken Xenogerms(Map map)
        {
            var list = map.listerThings.ThingsOfDef(ThingDefOf.Xenogerm).OfType<Xenogerm>().Where(x => x.Spawned).Take(MaxBuildings).ToList();
            if (list.Count == 0) return null;
            return new JArray(list.Select(x => new JObject { ["id"] = x.thingIDNumber, ["name"] = x.xenotypeName ?? x.LabelNoCount }));
        }

        // ---- children ----
        static bool IsLearningJob(Pawn p)
        {
            var j = p.CurJobDef;
            if (j == null) return false;
            foreach (var d in DefDatabase<LearningDesireDef>.AllDefsListForReading) if (d.jobDef == j) return true;
            return false;
        }

        internal static bool HasSchoolDesk(Map map)
        {
            var desk = DefDatabase<ThingDef>.GetNamedSilentFail("SchoolDesk");
            return desk != null && map.listerBuildings.ColonistsHaveBuilding(desk);
        }

        static JToken Children(List<Pawn> people)
        {
            var kids = people.Where(p => p.DevelopmentalStage.Child()).Take(MaxPeople).ToList();
            if (kids.Count == 0) return null;
            bool desk = HasSchoolDesk(kids[0].Map);
            return new JArray(kids.Select(p =>
            {
                var n = p.needs?.learning;
                return new JObject
                {
                    ["pawn"] = p.thingIDNumber, ["name"] = p.LabelShort, ["age"] = p.ageTracker.AgeBiologicalYears, ["stage"] = "child",
                    ["growth_tier"] = p.ageTracker.GrowthTier, ["learning_pct"] = n != null ? Pct(n.CurLevelPercentage) : (int?)null,
                    ["learning"] = IsLearningJob(p) ? SafeReport(p) : null, ["desk"] = desk
                };
            }));
        }

        static bool IsBaby(Pawn p) { var s = p.DevelopmentalStage; return s.Baby() || s.Newborn(); }

        static JToken Babies(List<Pawn> people)
        {
            var babies = people.Where(IsBaby).Take(MaxPeople).ToList();
            if (babies.Count == 0) return null;
            return new JArray(babies.Select(b =>
            {
                var food = b.needs?.food;
                Pawn feeder = null; AutofeedMode mode = AutofeedMode.Never;
                foreach (var f in b.mindState.Autofeeders())   // the player's explicit choices (the baby's feeding tab)
                {
                    var m = b.mindState.AutofeedSetting(f);
                    if (m != AutofeedMode.Never && (feeder == null || m > mode)) { feeder = f; mode = m; }
                }
                // no explicit choice: whoever does Childcare feeds the baby (the game's default), so name the carers' rule
                bool carers = WorkTypeDefOf.Childcare != null && people.Any(x => x != b && x.DevelopmentalStage.Adult() && x.workSettings != null && x.workSettings.EverWork && x.workSettings.WorkIsActive(WorkTypeDefOf.Childcare));
                string feeding = feeder == null ? (carers ? "carers at childcare" : "no one assigned") : (ChildcareUtility.CanBreastfeed(feeder, out _) ? "breastfed" : "baby food");
                long ageTicks = b.ageTracker.AgeBiologicalTicks;
                return new JObject
                {
                    ["pawn"] = b.thingIDNumber, ["name"] = b.LabelShort, ["age_days"] = (int)(ageTicks / GenDate.TicksPerDay),
                    ["food_pct"] = food != null ? Pct(food.CurLevelPercentage) : (int?)null,
                    ["hungry"] = food != null && food.CurLevelPercentage < 0.3f,
                    ["feeder"] = feeder?.LabelShort, ["feeding"] = feeding
                };
            }));
        }

        static JToken Pregnancies(List<Pawn> people)
        {
            var a = new JArray();
            foreach (var p in people)
            {
                if (p.ageTracker.AgeBiologicalYears < 18) continue;   // the age gate: only grown women
                var h = p.health?.hediffSet?.GetFirstHediff<Hediff_Pregnant>();
                if (h == null || !h.Visible) continue;
                float total = p.RaceProps.gestationPeriodDays;
                a.Add(new JObject
                {
                    ["pawn"] = p.thingIDNumber, ["name"] = p.LabelShort, ["progress_pct"] = Pct(h.GestationProgress),
                    ["days_left"] = (int)Math.Ceiling(Math.Max(0f, (1f - h.GestationProgress) * total))
                });
            }
            return a.Count == 0 ? null : a;
        }

        static JToken Carers(List<Pawn> people)
        {
            var w = WorkTypeDefOf.Childcare;
            if (w == null) return null;
            var a = new JArray(people.Where(p => !p.DevelopmentalStage.Juvenile() && p.workSettings != null && p.workSettings.EverWork && p.workSettings.WorkIsActive(w)).Take(MaxPeople).Select(p => p.LabelShort));
            return a.Count == 0 ? null : a;
        }

        static BioMissingInput MissingInput(Map map, List<Pawn> people, List<Pawn> mechs)
        {
            var bld = map.listerBuildings.allBuildingsColonist;
            var crib = DefDatabase<ThingDef>.GetNamedSilentFail("Crib");
            var babySpot = DefDatabase<ThingDef>.GetNamedSilentFail("BabySleepingSpot");
            return new BioMissingInput
            {
                Mechs = mechs.Count,
                Rechargers = bld.OfType<Building_MechCharger>().Count(),
                Mechanitors = people.Count(p => MechanitorUtility.IsMechanitor(p)),
                Gestators = bld.OfType<Building_MechGestator>().Count(),
                BasicMechtech = ResearchProjectDefOf.BasicMechtech != null && ResearchProjectDefOf.BasicMechtech.IsFinished,
                Children = people.Count(p => p.DevelopmentalStage.Child()),
                SchoolDesks = HasSchoolDesk(map) ? 1 : 0,
                Babies = people.Count(IsBaby),
                Cribs = bld.Count(b => b.def == crib || b.def == babySpot),
                Wastepacks = ThingDefOf.Wastepack != null ? Wastepacks(map.listerThings.ThingsOfDef(ThingDefOf.Wastepack).Where(t => t.Spawned)) : 0,
                WasteStockpile = map.zoneManager.AllZones.OfType<Zone_Stockpile>().Any(IsWasteStockpile),
                HemogenCarriers = people.Count(p => HemogenGene(p) != null),
                HemogenPacks = ThingDefOf.HemogenPack != null ? map.resourceCounter.GetCount(ThingDefOf.HemogenPack) : 0,
                UnstoredGenepacks = ThingDefOf.Genepack != null ? Unstored(map).Count : 0,
                GeneBanks = Banks(map).Count()
            };
        }
    }
}
