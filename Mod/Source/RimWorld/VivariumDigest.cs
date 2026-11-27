using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    /// <summary>One alert the UI currently shows.</summary>
    public class AlertInfo
    {
        public string Name;
        public string Label;
        public bool Critical;
    }

    /// <summary>Builds the GET /digest JSON: a compact colony read of what the player could see in the UI.
    /// Runs on the game thread; every section is independent and omitted if it throws.</summary>
    public static class VivariumDigest
    {
        const int MaxPawns = 30, MaxSkills = 4, MaxResources = 25, MaxResearch = 15, MaxAlerts = 10, MaxLetters = 10, MaxBeds = 20, MaxThreats = 10;
        static readonly HashSet<string> logged = new HashSet<string>();
        static FieldInfo activeAlertsField;
        static bool alertFieldLooked;

        static void LogOnce(string section, Exception e)
        {
            if (logged.Add(section)) Log.Warning("[RimWorldLLM] digest section '" + section + "' failed: " + e.Message);
        }

        static void Sec(JObject o, string name, Func<JToken> f)
        {
            try { var t = f(); if (t != null) o[name] = t; }
            catch (Exception e) { LogOnce(name, e); }
        }

        static float R(float f) { return (float)Math.Round(f, 2); }

        static string Trim(string s, int max)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max).TrimEnd() + "..." : s;
        }

        public static JObject Build()
        {
            Map map = null;
            try { map = Find.CurrentMap; if (map == null || !map.IsPlayerHome) map = Find.AnyPlayerHomeMap ?? map; } catch (Exception) { }
            if (map == null || Find.TickManager == null) return new JObject { ["error"] = "no map" };

            var o = new JObject();
            Sec(o, "date", () => Date(map));
            Sec(o, "weather", () => map.weatherManager?.curWeather?.label);
            Sec(o, "temperature", () => R(map.mapTemperature.OutdoorTemp));
            Sec(o, "map", () => map.Parent?.LabelCap.ToString() ?? "");
            Sec(o, "time", () => new JObject { ["paused"] = Find.TickManager.Paused, ["speed"] = Math.Max(1, Math.Min(4, (int)Find.TickManager.CurTimeSpeed)) });
            Sec(o, "food_days", () => R(Routes.FoodDays(map)));
            Sec(o, "resources", () => Resources(map));
            var types = WorkTypes();
            Sec(o, "pawns", () => Pawns(map, types));
            Sec(o, "work_types", () => new JArray(types.Select(w => w.defName)));
            Sec(o, "manual_priorities", () => Current.Game.playSettings.useWorkPriorities);
            Sec(o, "policies", Policies);
            Sec(o, "beds", () => Beds(map));
            Sec(o, "alerts", () => new JArray(ActiveAlerts().Take(MaxAlerts).Select(a => new JObject { ["alert"] = a.Name, ["label"] = a.Label, ["critical"] = a.Critical })));
            Sec(o, "letters", Letters);
            Sec(o, "dialogs", () => { var v = VivariumGame.State; return v.Dialogs.Count == 0 ? null : v.Dialogs.ToJson(v.Settings.DialogTimeoutS, Bridge.Vivarium.NowReal())["dialogs"]; });
            Sec(o, "research", Research);
            Sec(o, "threats", () => Threats(map));
            Sec(o, "rooms", () => Rooms(map));
            Sec(o, "build", () => VivariumBuildView.Build(map));
            Sec(o, "stuck", () => Stuck(map));
            Sec(o, "power", () => Power(map));
            Sec(o, "food", () => VivariumFood.Digest(map));
            Sec(o, "deadlines", () => VivariumDeadlines.Digest(map));
            Sec(o, "facts", () => VivariumFacts.Build(map));
            return o;
        }

        static JToken Date(Map map)
        {
            float lon = Find.WorldGrid.LongLatOf(map.Tile).x;
            return new JObject
            {
                ["day"] = GenLocalDate.DayOfQuadrum(map) + 1,
                ["quadrum"] = GenDate.Quadrum(GenTicks.TicksAbs, lon).Label(),
                ["year"] = GenLocalDate.Year(map),
                ["hour"] = GenLocalDate.HourOfDay(map),
                ["season"] = GenLocalDate.Season(map).Label().ToLowerInvariant()
            };
        }

        static bool IsMeal(ThingDef d)
        {
            var p = d.ingestible != null ? d.ingestible.preferability : FoodPreferability.Undefined;
            return p == FoodPreferability.MealAwful || p == FoodPreferability.MealSimple || p == FoodPreferability.MealFine || p == FoodPreferability.MealLavish;
        }

        static FieldInfo optTextField;
        internal static string OptText(DiaOption o)
        {
            if (optTextField == null) optTextField = typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return optTextField != null ? optTextField.GetValue(o) as string : null;
        }

        static string ResCategory(ThingDef d)
        {
            if (d == ThingDefOf.Silver) return "silver";
            if (d == ThingDefOf.ComponentIndustrial || d == ThingDefOf.ComponentSpacer) return "components";
            if (d.IsMedicine) return "medicine";
            if (d.IsNutritionGivingIngestible && !d.IsDrug) return IsMeal(d) ? "food" : "rawfood";
            if (d.IsStuff || d.IsWithinCategory(ThingCategoryDefOf.ResourcesRaw)) return "materials";
            return "other";
        }

        static JToken Resources(Map map)
        {
            // Stored (the resource counter) plus what lies loose or forbidden on the ground; a def only on the ground still shows.
            var scan = VivariumLoose.Get(map);
            var rows = new Dictionary<ThingDef, int[]>();   // stored, loose, forbidden
            foreach (var kv in map.resourceCounter.AllCountedAmounts)
                if (kv.Value > 0) rows[kv.Key] = new[] { kv.Value, 0, 0 };
            foreach (var kv in scan.Loose) { int[] r; if (!rows.TryGetValue(kv.Key, out r)) rows[kv.Key] = r = new int[3]; r[1] = kv.Value; }
            foreach (var kv in scan.Forbidden) { int[] r; if (!rows.TryGetValue(kv.Key, out r)) rows[kv.Key] = r = new int[3]; r[2] = kv.Value; }
            var arr = new JArray();
            foreach (var kv in rows.OrderByDescending(k => k.Value[0] + k.Value[1]).ThenBy(k => k.Key.defName).Take(MaxResources))
                arr.Add(new JObject { ["category"] = ResCategory(kv.Key), ["label"] = kv.Key.label ?? kv.Key.defName, ["count"] = kv.Value[0], ["loose"] = kv.Value[1], ["forbidden"] = kv.Value[2] });
            return arr;
        }

        static List<WorkTypeDef> WorkTypes()
        {
            try { return WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder.Where(w => w.visible).ToList(); }
            catch (Exception e) { LogOnce("work_types", e); return new List<WorkTypeDef>(); }
        }

        static string Assignment(TimeAssignmentDef d)
        {
            if (d == TimeAssignmentDefOf.Work) return "work";
            if (d == TimeAssignmentDefOf.Joy) return "joy";
            if (d == TimeAssignmentDefOf.Sleep) return "sleep";
            if (d == TimeAssignmentDefOf.Meditate) return "meditate";
            return "anything";
        }

        static JToken Stuck(Map map)
        {
            return new JArray(VivariumStuck.Stuck(map).Rows.Select(r =>
            {
                var j = new JObject { ["what"] = r.What, ["count"] = r.Count, ["reason"] = r.Reason, ["dist"] = r.Dist };
                if (!string.IsNullOrEmpty(r.Dir)) j["dir"] = r.Dir;
                return j;
            }));
        }

        static JToken Pawns(Map map, List<WorkTypeDef> types)
        {
            var arr = new JArray();
            var all = map.mapPawns.FreeColonistsSpawned.Select(p => p)
                .Concat(map.mapPawns.SlavesOfColonySpawned).Take(MaxPawns);
            foreach (var p in all)
            {
                try { arr.Add(OnePawn(map, p, types)); }
                catch (Exception e) { LogOnce("pawn", e); }
            }
            return arr;
        }

        static JObject OnePawn(Map map, Pawn p, List<WorkTypeDef> types)
        {
            var o = new JObject
            {
                ["id"] = p.thingIDNumber,
                ["name"] = p.LabelShort,
                ["role"] = p.IsSlave ? "slave" : "colonist",
                ["age"] = p.ageTracker != null ? p.ageTracker.AgeBiologicalYears : 0
            };
            var skills = new JArray();
            if (p.skills != null)
                foreach (var s in p.skills.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).ThenByDescending(s => (int)s.passion).Take(MaxSkills))
                    skills.Add(new JObject { ["skill"] = s.def.defName, ["level"] = s.Level, ["passion"] = s.passion == Passion.Major ? "major" : s.passion == Passion.Minor ? "minor" : "none" });
            o["skills"] = skills;

            var inc = new JArray();
            var tags = p.CombinedDisabledWorkTags;
            foreach (WorkTags t in Enum.GetValues(typeof(WorkTags)))
                if (t != WorkTags.None && (tags & t) == t && ((int)t & ((int)t - 1)) == 0) inc.Add(t.ToString());
            o["incapable"] = inc;

            var work = new JObject();
            if (p.workSettings != null && p.workSettings.EverWork)
                foreach (var w in types)
                {
                    if (p.WorkTypeIsDisabled(w)) continue;
                    int pr = p.workSettings.GetPriority(w);
                    if (pr > 0) work[w.defName] = pr;
                }
            o["work"] = work;

            o["health"] = Health(p);
            o["downed"] = p.Downed;
            float mood = p.needs?.mood != null ? p.needs.mood.CurLevelPercentage * 100f : 0f;
            o["mood"] = (int)Math.Round(mood);
            o["mental"] = p.MentalState != null ? (p.MentalState.def.label ?? p.MentalState.def.defName) : "";
            string job = "";
            try { job = p.jobs?.curDriver?.GetReport() ?? ""; } catch (Exception) { }
            o["job"] = NarratorWork.CutJob(job);
            o["drafted"] = p.Drafted;
            if (VivariumStuck.IsChild(p)) o["child"] = true;
            var cannot = VivariumStuck.Cannot(p);
            if (cannot.Count > 0) o["cannot"] = new JArray(cannot);
            if (VivariumStuck.IsIdle(p)) o["idle"] = true;
            try { VivariumExtras.AddPawn(o, p); } catch (Exception e) { LogOnce("pawn_extras", e); }

            if (p.timetable != null)
            {
                o["schedule_now"] = Assignment(p.timetable.CurrentAssignment);
                var sb = new System.Text.StringBuilder(24);
                for (int h = 0; h < 24; h++)
                {
                    var a = p.timetable.GetAssignment(h);
                    sb.Append(a == TimeAssignmentDefOf.Work ? 'W' : a == TimeAssignmentDefOf.Joy ? 'J' : a == TimeAssignmentDefOf.Sleep ? 'S' : a == TimeAssignmentDefOf.Meditate ? 'M' : 'A');
                }
                o["schedule"] = sb.ToString();
            }
            o["outfit"] = p.outfits?.CurrentApparelPolicy?.label ?? "";
            o["food"] = p.foodRestriction?.CurrentFoodPolicy?.label ?? "";
            o["drug"] = p.drugs?.CurrentPolicy?.label ?? "";
            var bed = p.ownership?.OwnedBed;
            if (bed != null) o["bed"] = bed.thingIDNumber;
            return o;
        }

        static string Health(Pawn p)
        {
            if (p.Downed) return "downed";
            var hs = p.health?.hediffSet;
            if (hs == null) return "fine";
            if (hs.BleedRateTotal > 0.01f) return "bleeding";
            if (hs.HasImmunizableNotImmuneHediff()) return "sick";
            if (p.health.summaryHealth.SummaryHealthPercent < 0.99f) return "injured";
            return "fine";
        }

        static JToken Policies()
        {
            var g = Current.Game;
            return new JObject
            {
                ["outfit"] = new JArray(g.outfitDatabase.AllOutfits.Select(x => x.label)),
                ["food"] = new JArray(g.foodRestrictionDatabase.AllFoodRestrictions.Select(x => x.label)),
                ["drug"] = new JArray(g.drugPolicyDatabase.AllPolicies.Select(x => x.label))
            };
        }

        static JToken Beds(Map map)
        {
            var arr = new JArray();
            foreach (var bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
            {
                if (arr.Count >= MaxBeds) break;
                string label = bed.LabelNoCount + (bed.Medical ? " (medical)" : "");
                string room = "";
                try { room = bed.GetRoom()?.Role?.label ?? ""; } catch (Exception) { }
                var owner = bed.OwnersForReading.FirstOrDefault();
                arr.Add(new JObject { ["id"] = bed.thingIDNumber, ["label"] = label, ["room"] = room, ["owner"] = owner != null ? owner.LabelShort : "" });
            }
            return arr;
        }

        /// <summary>The alerts the UI currently shows (AlertsReadout.activeAlerts, private).</summary>
        public static List<AlertInfo> ActiveAlerts()
        {
            var res = new List<AlertInfo>();
            try
            {
                if (!alertFieldLooked)
                {
                    alertFieldLooked = true;
                    activeAlertsField = typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                var readout = Find.Alerts;
                if (readout == null || activeAlertsField == null) return res;
                var list = activeAlertsField.GetValue(readout) as IEnumerable;
                if (list == null) return res;
                foreach (var item in list)
                {
                    var a = item as Alert;
                    if (a == null) continue;
                    res.Add(new AlertInfo { Name = a.GetType().Name, Label = (a.Label ?? "").ToString(), Critical = a.Priority == AlertPriority.Critical });
                }
            }
            catch (Exception e) { LogOnce("alerts", e); }
            return res;
        }

        static JToken Letters()
        {
            var arr = new JArray();
            var ls = Find.LetterStack.LettersListForReading;
            int now = Find.TickManager.TicksGame;
            for (int i = ls.Count - 1; i >= 0 && arr.Count < MaxLetters; i--)
            {
                var l = ls[i];
                try
                {
                    var o = new JObject { ["id"] = l.ID, ["label"] = l.Label.Resolve() };
                    var cl = l as ChoiceLetter;
                    o["text"] = cl != null ? Trim(cl.Text.Resolve(), 300) : "";
                    var choices = new JArray();
                    if (cl != null)
                        foreach (var opt in cl.Choices)
                        {
                            string t = OptText(opt);
                            if (!string.IsNullOrEmpty(t)) choices.Add(t);
                        }
                    o["choices"] = choices;
                    o["quest"] = cl != null && cl.quest != null;
                    var lt = l as LetterWithTimeout;
                    if (lt != null && lt.TimeoutActive) o["expires_in_hours"] = Math.Max(0, (lt.disappearAtTick - now) / 2500);
                    arr.Add(o);
                }
                catch (Exception e) { LogOnce("letter", e); }
            }
            return arr;
        }

        static JToken Research()
        {
            var rm = Find.ResearchManager;
            var o = new JObject();
            var cur = rm.GetProject();
            if (cur != null)
                o["current"] = new JObject { ["def"] = cur.defName, ["label"] = cur.label, ["progress"] = R(cur.ProgressPercent) };
            var avail = new JArray();
            foreach (var d in DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(d => d != cur && d.knowledgeCategory == null && d.CanStartNow)
                .OrderBy(d => d.CostApparent).ThenBy(d => d.defName).Take(MaxResearch))
                avail.Add(new JObject { ["def"] = d.defName, ["label"] = d.label, ["cost"] = (int)Math.Round(d.CostApparent) });
            o["available"] = avail;
            return o;
        }

        static JToken Threats(Map map)
        {
            var groups = new Dictionary<string, int>();
            foreach (var p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || p.Downed || p.Position.Fogged(map)) continue;
                if (!p.HostileTo(Faction.OfPlayer)) continue;
                string k = p.KindLabel;
                int n; groups.TryGetValue(k, out n); groups[k] = n + 1;
            }
            var arr = new JArray();
            foreach (var kv in groups.OrderByDescending(g => g.Value).Take(MaxThreats))
                arr.Add(new JObject { ["label"] = kv.Key, ["count"] = kv.Value, ["hostile"] = true });
            return arr;
        }

        static JToken Rooms(Map map)
        {
            int bedrooms = 0, barracks = 0;
            bool hospital = false, freezer = false;
            var seen = new HashSet<Room>();
            foreach (var region in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
            {
                var r = region.Room;
                if (r == null || !seen.Add(r) || r.PsychologicallyOutdoors || r.Role == null) continue;
                if (r.Role == RoomRoleDefOf.Bedroom) bedrooms++;
                else if (r.Role == RoomRoleDefOf.Barracks) barracks++;
                else if (r.Role == RoomRoleDefOf.Hospital) hospital = true;
            }
            // Freezer approximation: an enclosed room at or below 0C that holds a food item (cheap: scans food things once).
            int scanned = 0;
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (++scanned > 300) break;
                var r = t.GetRoom();
                if (r != null && !r.PsychologicallyOutdoors && !r.UsesOutdoorTemperature && r.Temperature <= 0f) { freezer = true; break; }
            }
            return new JObject { ["bedrooms"] = bedrooms, ["barracks"] = barracks, ["hospital"] = hospital, ["freezer"] = freezer };
        }

        static JToken Power(Map map)
        {
            float prod = 0, used = 0, stored = 0;
            foreach (var net in map.powerNetManager.AllNetsListForReading)
            {
                foreach (var c in net.powerComps)
                {
                    float w = c.PowerOutput;
                    if (w > 0) prod += w; else used -= w;
                }
                stored += net.CurrentStoredEnergy();
            }
            return new JObject { ["produced"] = (int)Math.Round(prod), ["used"] = (int)Math.Round(used), ["stored"] = (int)Math.Round(stored) };
        }
    }
}
