using System;
using System.Collections.Generic;
using System.Linq;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace HermesVoice
{
    public static partial class Routes
    {
        public static readonly Trends TrendBook = new Trends(3, 15000);

        static void RegisterSnapshots(ApiRouter r)
        {
            r.Add("GET", "/config", false, Config);
            r.Add("GET", "/colony", true, Colony);
            r.Add("GET", "/pawn/{id}", true, PawnRoute);
            r.Add("GET", "/scene/{id}", true, SceneRoute);
        }

        // ------------------------------------------------------------ helpers
        public static Pawn FindPawn(int id)
        {
            foreach (var map in Find.Maps)
                foreach (var p in map.mapPawns.AllPawns)
                    if (p.thingIDNumber == id) return p;
            return PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.FirstOrDefault(p => p.thingIDNumber == id);
        }

        static ApiResult ResolvePawn(RouteContext c, out Pawn pawn)
        {
            pawn = null;
            int id;
            if (!int.TryParse(c.Args["id"], out id)) return ApiResult.Fail("bad_request", "pawn id must be an integer");
            pawn = FindPawn(id);
            if (pawn == null) return ApiResult.Fail("not_found", "no pawn " + id);
            return null;
        }

        static JToken N(string s) { return s != null ? (JToken)s : JValue.CreateNull(); }
        static float R(float f) { return (float)Math.Round(f, 2); }

        public static List<string> ActiveDlc()
        {
            var l = new List<string>();
            if (ModsConfig.RoyaltyActive) l.Add("Royalty");
            if (ModsConfig.IdeologyActive) l.Add("Ideology");
            if (ModsConfig.BiotechActive) l.Add("Biotech");
            if (ModsConfig.AnomalyActive) l.Add("Anomaly");
            if (ModsConfig.OdysseyActive) l.Add("Odyssey");
            return l;
        }

        // ------------------------------------------------------------- config
        public static JObject EventToggles(HermesVoiceSettings s)
        {
            return new JObject
            {
                ["injury"] = s.evInjury, ["death"] = s.evDeath, ["mental_break"] = s.evMentalBreak, ["level_up"] = s.evLevelUp,
                ["tend"] = s.evTend, ["social"] = s.evSocial, ["raid"] = s.evRaid, ["quest"] = s.evQuest, ["lodger"] = s.evLodger,
                ["prisoner"] = s.evPrisoner, ["funeral"] = s.evFuneral, ["gathering"] = s.evGatherings, ["weather_extreme"] = s.evWeather, ["thought"] = s.evThought, ["art"] = s.evArt, ["sleep"] = s.evSleep, ["first_cue"] = s.evFirsts
            };
        }

        static ApiResult Config(RouteContext c)
        {
            var s = HermesVoiceMod.Settings;
            return ApiResult.Success(new JObject
            {
                ["effects"] = s.effects,
                ["events"] = EventToggles(s),
                ["talk_frequency"] = s.talkFrequency,
                ["wants_enabled"] = s.wantsEnabled,
                ["recap"] = s.recapOnLoad,
                ["adult_content"] = s.adultContent,
                ["recap_open"] = HermesVoiceGameComponent.Instance != null
                    && HermesVoiceGameComponent.Instance.RecapGate.IsOpen(Environment.TickCount64, s.recapOnLoad),
                ["dreams"] = s.dreamsEnabled,
                ["art_descriptions"] = s.artDescriptions,
                ["pause_ambient_at_speed"] = s.pauseAmbientAtSpeed,
                ["dlc"] = new JArray(ActiveDlc()),
                ["llm"] = LlmConfig(s)
            });
        }

        /// <summary>The agent's LLM endpoint for GET /config. Never contains the key, only whether one is set.</summary>
        public static JObject LlmConfig(HermesVoiceSettings s)
        {
            return new JObject
            {
                ["base_url"] = (s.llmBaseUrl ?? "").Trim(),
                ["model"] = (s.llmModel ?? "").Trim(),
                ["api_key_set"] = !string.IsNullOrWhiteSpace(s.llmApiKey)
            };
        }

        // ------------------------------------------------------------- colony
        public static float FoodDays(Map map)
        {
            int n = Math.Max(1, map.mapPawns.FreeColonistsAndPrisonersSpawnedCount);
            return map.resourceCounter.TotalHumanEdibleNutrition / (1.6f * n);
        }

        public static int MedicineCount(Map map)
        {
            int n = 0;
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine)) n += t.stackCount;
            return n;
        }

        /// <summary>Sampled from the game component; one sample per game quarter-day per series.</summary>
        public static void SampleTrends()
        {
            if (!EventGate.GameLive()) return;
            int now = Find.TickManager.TicksGame;
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                TrendBook.Sample("food_days." + map.Index, now, R(FoodDays(map)));
                TrendBook.Sample("medicine." + map.Index, now, MedicineCount(map));
            }
        }

        static JObject TrendsFor(Map map)
        {
            return new JObject
            {
                ["food_days"] = new JArray(TrendBook.Get("food_days." + map.Index)),
                ["medicine"] = new JArray(TrendBook.Get("medicine." + map.Index))
            };
        }

        static ApiResult Colony(RouteContext c)
        {
            var maps = new JArray();
            foreach (var map in Find.Maps)
            {
                var o = new JObject
                {
                    ["index"] = map.Index,
                    ["home"] = map.IsPlayerHome,
                    ["name"] = N(map.Parent?.LabelCap),
                    ["colonists"] = new JArray(map.mapPawns.FreeColonistsSpawned.Select(p => new JObject { ["id"] = p.thingIDNumber, ["name"] = p.LabelShort })),
                    ["prisoners"] = new JArray(map.mapPawns.PrisonersOfColony.Select(p => p.thingIDNumber)),
                    ["weather"] = N(map.weatherManager?.curWeather?.label),
                    ["season"] = GenLocalDate.Season(map).Label(),
                    ["food_days"] = R(FoodDays(map)),
                    ["medicine"] = MedicineCount(map),
                    ["trends"] = TrendsFor(map)
                };
                maps.Add(o);
            }
            return ApiResult.Success(new JObject { ["maps"] = maps, ["ledger"] = EventBus.Ledger.ToJson(), ["dlc"] = new JArray(ActiveDlc()) });
        }

        // --------------------------------------------------------------- pawn
        public static string StatusOf(Pawn p)
        {
            if (p.IsPrisonerOfColony) return "prisoner";
            if (p.IsSlaveOfColony) return "slave";
            if (p.Faction != null && p.Faction.IsPlayer) return p.IsQuestLodger() ? "lodger" : "colonist";
            if (p.IsQuestLodger()) return "lodger";
            if (p.HostFaction != null && p.HostFaction.IsPlayer) return "guest";
            return p.Faction != null ? "other" : "none";
        }

        static JObject RoomObj(Pawn p)
        {
            if (!p.Spawned) return null;
            var room = p.GetRoom();
            if (room == null || room.PsychologicallyOutdoors)
                return new JObject { ["role"] = "outdoors", ["impressiveness"] = JValue.CreateNull(), ["clean"] = JValue.CreateNull(), ["temp"] = R(p.AmbientTemperature) };
            return new JObject
            {
                ["role"] = N(room.Role?.label),
                ["impressiveness"] = R(room.GetStat(RoomStatDefOf.Impressiveness)),
                ["clean"] = R(room.GetStat(RoomStatDefOf.Cleanliness)),
                ["temp"] = R(room.Temperature)
            };
        }

        static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch (Exception) { return ""; }
        }

        static JToken BackstoryObj(Pawn p, BackstoryDef b)
        {
            if (b == null) return JValue.CreateNull();
            var o = new JObject { ["title"] = b.title };
            string d = Safe(() => PawnText.Cap(b.FullDescriptionFor(p).Resolve(), PawnText.BackstoryMax));
            if (d.Length > 0) o["desc"] = d;
            return o;
        }

        static string TraitDesc(Pawn p, Trait t)
        {
            return Safe(() => PawnText.Cap(t.CurrentData.description.Formatted(p.Named("PAWN")).AdjustedFor(p).Resolve(), PawnText.TraitMax));
        }

        static JArray MemesArr(Ideo ideo)
        {
            var a = new JArray();
            foreach (var m in ideo.memes)
            {
                var mo = new JObject { ["def"] = m.defName, ["label"] = m.label };
                string d = Safe(() => PawnText.Cap(m.description, PawnText.MemeMax));
                if (d.Length > 0) mo["desc"] = d;
                a.Add(mo);
            }
            return a;
        }

        static IEnumerable<string> PreceptLabels(Ideo ideo)
        {
            try
            {
                var list = ideo.PreceptsListForReading.Where(x => x.def.visible)
                    .OrderByDescending(x => x.def.issue != null ? 1 : 0)
                    .Select(x => x.Label.CapitalizeFirst().ToString());
                return PawnText.CapList(list, PawnText.MaxPrecepts, 60);
            }
            catch (Exception) { return new List<string>(); }
        }

        static JObject HistoryObj(Pawn p)
        {
            try
            {
                var tales = Find.TaleManager?.AllTalesListForReading;
                if (tales == null) return null;
                var mine = tales.Where(t => t.Concerns(p)).ToList();
                string arrival = "";
                foreach (var t in mine)
                {
                    string dn = t.def.defName.ToLowerInvariant();
                    if (dn.Contains("recruit") || dn.Contains("rescue") || dn.Contains("wanderer") || dn.Contains("joined"))
                    { arrival = PawnText.Cap(t.ShortSummary, PawnText.ArrivalMax); if (arrival.Length > 0) break; }
                }
                var notable = mine.OrderByDescending(t => t.def.baseInterest).ThenBy(t => t.AgeTicks)
                    .Select(t => Safe(() => t.ShortSummary));
                var list = PawnText.CapList(notable, PawnText.MaxTales, PawnText.TaleMax);
                if (arrival.Length == 0 && list.Count == 0) return null;
                var o = new JObject();
                if (arrival.Length > 0) o["arrival"] = arrival;
                o["tales"] = new JArray(list);
                return o;
            }
            catch (Exception) { return null; }
        }

        static string AppearanceLine(Pawn p)
        {
            if (p.story == null) return "";
            var c = p.story.SkinColor;
            float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            string skin = lum > 0.8f ? "very light" : lum > 0.6f ? "light" : lum > 0.4f ? "medium" : lum > 0.25f ? "brown" : "dark";
            var worn = (p.apparel?.WornApparel ?? new List<Apparel>())
                .OrderByDescending(a => a.TryGetQuality(out QualityCategory q) ? (int)q : (int)QualityCategory.Normal).Select(a => a.def.label);
            return PawnText.Appearance(p.story.hairDef?.label, skin, worn);
        }

        static JObject PawnSnapshot(Pawn p)
        {
            var o = new JObject();
            o["id"] = p.thingIDNumber;
            var nt = p.Name as NameTriple;
            o["name"] = new JObject { ["first"] = N(nt?.First ?? p.Name?.ToStringShort), ["nick"] = N(nt?.Nick), ["last"] = N(nt?.Last) };
            o["age"] = p.ageTracker != null ? (JToken)p.ageTracker.AgeBiologicalYears : JValue.CreateNull();
            o["gender"] = p.gender.ToString().ToLowerInvariant();
            o["body_type"] = N(p.story?.bodyType?.defName);
            o["backstory"] = new JObject
            {
                ["child"] = BackstoryObj(p, p.story?.Childhood),
                ["adult"] = BackstoryObj(p, p.story?.Adulthood)
            };
            o["traits"] = new JArray((p.story?.traits?.allTraits ?? new List<Trait>()).Select(t =>
            {
                var to = new JObject { ["def"] = t.def.defName, ["label"] = t.LabelCap, ["degree"] = t.Degree };
                string d = TraitDesc(p, t);
                if (d.Length > 0) to["desc"] = d;
                return to;
            }));
            o["title"] = N(p.story?.TitleCap);
            o["skills"] = new JArray((p.skills?.skills ?? new List<SkillRecord>()).Select(s => new JObject
            {
                ["skill"] = s.def.defName, ["level"] = s.TotallyDisabled ? 0 : s.Level, ["passion"] = s.passion.ToString().ToLowerInvariant(),
                ["disabled"] = s.TotallyDisabled
            }));

            // health
            var hs = p.health?.hediffSet;
            var conds = new JArray();
            var missing = new JArray();
            if (hs != null)
            {
                foreach (var h in hs.hediffs)
                {
                    if (!h.Visible || h is Hediff_MissingPart) continue;
                    conds.Add(new JObject { ["def"] = h.def.defName, ["label"] = h.LabelCap, ["part"] = N(h.Part?.Label), ["severity"] = R(h.Severity), ["tended"] = h.IsTended() });
                }
                foreach (var m in hs.GetMissingPartsCommonAncestors()) missing.Add(m.Part.Label);
            }
            o["health"] = new JObject
            {
                ["conditions"] = conds, ["missing_parts"] = missing,
                ["pain"] = R(hs?.PainTotal ?? 0f),
                ["consciousness"] = R(p.health?.capacities?.GetLevel(PawnCapacityDefOf.Consciousness) ?? 0f),
                ["talking"] = R(p.health?.capacities?.GetLevel(PawnCapacityDefOf.Talking) ?? 0f),
                ["downed"] = p.Downed
            };

            // needs and mood
            var needs = new JObject();
            if (p.needs != null)
                foreach (var n in p.needs.AllNeeds) needs[n.def.defName.ToLowerInvariant()] = R(n.CurLevelPercentage);
            o["needs"] = needs;
            var thoughts = new JArray();
            float mood = 0f;
            if (p.needs?.mood != null)
            {
                mood = p.needs.mood.CurLevelPercentage;
                var groups = new List<Thought>();
                p.needs.mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                foreach (var t in groups.OrderByDescending(t => Math.Abs(p.needs.mood.thoughts.MoodOffsetOfGroup(t))).Take(12))
                {
                    var mem = t as Thought_Memory;
                    thoughts.Add(new JObject
                    {
                        ["label"] = t.LabelCap, ["offset"] = R(p.needs.mood.thoughts.MoodOffsetOfGroup(t)),
                        ["cause_pawn"] = mem != null && mem.otherPawn != null ? (JToken)mem.otherPawn.thingIDNumber : JValue.CreateNull()
                    });
                }
            }
            o["mood"] = new JObject { ["value"] = R(mood), ["thoughts"] = thoughts };

            o["asleep"] = p.Spawned && !p.Dead && !p.Awake();
            o["job"] = p.CurJob != null ? new JObject { ["def"] = p.CurJob.def.defName, ["label"] = N(p.CurJob.GetReport(p)?.TrimEnd('.')) } : null;
            o["gear"] = new JObject
            {
                ["worn"] = new JArray((p.apparel?.WornApparel ?? new List<Apparel>()).Select(a => a.LabelCap)),
                ["held"] = N(p.equipment?.Primary?.LabelCap)
            };

            var rels = new JArray();
            if (p.relations != null && !p.Dead)
            {
                var others = new List<Pawn>();
                foreach (var map in Find.Maps)
                    foreach (var q in map.mapPawns.AllPawns)
                        if (q != p && Witnesses.IsSpeaker(q)) others.Add(q);
                foreach (var q in others.Take(40))
                {
                    string kind = string.Join(", ", PawnRelationUtility.GetRelations(p, q).Select(d => d.label));
                    rels.Add(new JObject { ["pawn"] = q.thingIDNumber, ["name"] = q.LabelShort, ["kind"] = kind.Length > 0 ? kind : "acquaintance", ["opinion"] = p.relations.OpinionOf(q) });
                }
            }
            o["relations"] = rels;

            o["status"] = StatusOf(p);
            o["faction"] = N(p.Faction?.Name);
            o["map"] = p.MapHeld != null ? (JToken)p.MapHeld.Index : JValue.CreateNull();
            o["pos"] = p.Spawned ? new JObject { ["x"] = p.Position.x, ["z"] = p.Position.z } : null;
            o["room"] = RoomObj(p);
            o["alive"] = !p.Dead;
            o["spawned"] = p.Spawned;

            // DLC fields only when active
            if (ModsConfig.IdeologyActive && p.ideo != null)
            {
                var ideo = p.ideo.Ideo;
                var role = ideo?.GetRole(p);
                o["ideo"] = ideo == null ? null : new JObject
                {
                    ["name"] = ideo.name, ["memes"] = MemesArr(ideo),
                    ["precepts"] = new JArray(PreceptLabels(ideo)),
                    ["certainty"] = R(p.ideo.Certainty)
                };
                o["ideo_role"] = N(role?.LabelCap);
            }
            if (ModsConfig.BiotechActive && p.genes != null)
            {
                o["xenotype"] = N(p.genes.XenotypeLabel);
                o["genes"] = new JArray(p.genes.GenesListForReading.Where(g => g.Active).Select(g =>
                    new JObject { ["def"] = g.def.defName, ["label"] = g.def.label }));
                string xd = Safe(() => PawnText.FirstSentence(p.genes.Xenotype?.description, PawnText.XenotypeMax));
                if (!string.IsNullOrEmpty(xd)) o["xenotype_desc"] = xd;
            }
            if (ModsConfig.RoyaltyActive && p.royalty != null)
            {
                var t = p.royalty.MostSeniorTitle;
                o["royal_title"] = N(t?.def?.GetLabelFor(p));
            }

            var hist = HistoryObj(p);
            if (hist != null) o["history"] = hist;
            string app = Safe(() => AppearanceLine(p));
            if (!string.IsNullOrEmpty(app)) o["appearance"] = app;

            // other mods' context (hook API)
            var extra = HermesVoiceAPI.CollectContext(p);
            o["extra"] = extra;
            return o;
        }

        static ApiResult PawnRoute(RouteContext c)
        {
            Pawn p;
            var err = ResolvePawn(c, out p);
            if (err != null) return err;
            var o = PawnSnapshot(p);
            return ApiResult.Success(o);
        }

        // -------------------------------------------------------------- scene
        static string TimeOfDay(int hour)
        {
            if (hour < 5) return "night";
            if (hour < 8) return "dawn";
            if (hour < 12) return "morning";
            if (hour < 17) return "afternoon";
            if (hour < 21) return "evening";
            return "night";
        }

        static ApiResult SceneRoute(RouteContext c)
        {
            Pawn p;
            var err = ResolvePawn(c, out p);
            if (err != null) return err;
            var map = p.MapHeld;
            var o = new JObject();
            o["room"] = RoomObj(p);
            var present = new JArray();
            if (p.Spawned && map != null)
            {
                float myHearing = p.health?.capacities?.GetLevel(PawnCapacityDefOf.Hearing) ?? 0f;
                var myRoom = p.GetRoom();
                foreach (var q in map.mapPawns.AllPawnsSpawned)
                {
                    if (q == p || q.Dead || q.RaceProps == null || !q.RaceProps.Humanlike) continue;
                    float dist = p.Position.DistanceTo(q.Position);
                    if (dist > 25f) continue;
                    var qRoom = q.GetRoom();
                    bool same = myRoom != null && myRoom == qRoom;
                    bool los = same || GenSight.LineOfSight(p.Position, q.Position, map);
                    if (!los && !same) continue;
                    present.Add(new JObject
                    {
                        ["pawn"] = q.thingIDNumber, ["name"] = q.LabelShort, ["status"] = StatusOf(q),
                        ["job"] = N(q.CurJob?.GetReport(q)?.TrimEnd('.')), ["distance"] = R(dist),
                        ["can_hear"] = Rules.Witnessed(dist, myHearing, same, los)
                    });
                }
            }
            o["present"] = present;
            if (map != null)
            {
                int hour = GenLocalDate.HourOfDay(map);
                o["time_of_day"] = new JObject { ["hour"] = hour, ["label"] = TimeOfDay(hour) };
                o["weather"] = N(map.weatherManager?.curWeather?.label);
                o["danger"] = map.dangerWatcher.DangerRating.ToString().ToLowerInvariant();
                o["trends"] = TrendsFor(map);
            }
            else
            {
                o["time_of_day"] = null; o["weather"] = null; o["danger"] = null; o["trends"] = new JObject();
            }
            return ApiResult.Success(o);
        }
    }
}
