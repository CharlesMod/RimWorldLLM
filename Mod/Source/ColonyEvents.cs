using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using HermesVoice.Core;
using Newtonsoft.Json.Linq;

namespace HermesVoice
{
    /// <summary>Shared payload helpers for colony events. Game thread only.</summary>
    public static class EventPayload
    {
        public static JObject PawnObj(Pawn p)
        {
            return new JObject { ["id"] = p.thingIDNumber, ["name"] = p.LabelShort };
        }

        public static string Label(Thing t)
        {
            return t == null ? null : (t is Pawn p ? p.LabelShort : t.LabelShort);
        }

        public static float Round(float f) { return (float)Math.Round(f, 2); }

        /// <summary>Run a patch body so that a bad event can never break the game.</summary>
        public static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Warning("[HermesVoice] event '" + what + "' failed: " + e.Message); }
        }
    }


    /// <summary>Flushes merged injuries once their cooldown window has passed, polls slow-moving facts, and holds the raid tracks saved with the game.</summary>
    public class ColonyEventsComponent : GameComponent
    {
        public static ColonyEventsComponent Instance;
        public List<RaidTrack> Raids = new List<RaidTrack>();
        bool _primed;

        public ColonyEventsComponent(Game game) : base()
        {
            Instance = this;
            EventGate.Reset();
            InjuryEvents.Clear();
            EventBus.Reset();
            Pollers.Reset();
            Routes.TrendBook.Clear();
            TalkQueue.Clear();
        }

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 60 == 0) EventPayload.Safe("injury-flush", InjuryEvents.Flush);
            if (now % 500 == 0) EventPayload.Safe("trends", Routes.SampleTrends);
            if (now % 120 == 0)
            {
                bool quiet = !_primed;
                _primed = true;
                EventPayload.Safe("pollers", () => Pollers.Run(quiet));
            }
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            EventBus.Ledger.Counter = Math.Max(EventBus.Ledger.Counter, _ledgerCounter);
            Pollers.Reset();
            _primed = false; // the first poll after a load only learns what is already there
            foreach (var r in Raids) r.Reopen();
        }

        int _ledgerCounter;

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) _ledgerCounter = EventBus.Ledger.Counter;
            Scribe_Values.Look(ref _ledgerCounter, "ledgerCounter", 0);
            Scribe_Collections.Look(ref Raids, "raids", LookMode.Deep);
            if (Raids == null) Raids = new List<RaidTrack>();
        }
    }

    /// <summary>A raid in progress, saved with the game so raid_end still fires after a reload.</summary>
    public class RaidTrack : IExposable
    {
        public string LedgerId;
        public int MapIndex;
        public string Faction;
        public int StartTick;
        public string Summary;
        public List<Pawn> Raiders = new List<Pawn>();
        public int Total;

        public void ExposeData()
        {
            Scribe_Values.Look(ref LedgerId, "ledgerId");
            Scribe_Values.Look(ref MapIndex, "mapIndex");
            Scribe_Values.Look(ref Faction, "faction");
            Scribe_Values.Look(ref StartTick, "startTick");
            Scribe_Values.Look(ref Summary, "summary");
            Scribe_Values.Look(ref Total, "total");
            Scribe_Collections.Look(ref Raiders, "raiders", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && Raiders == null) Raiders = new List<Pawn>();
        }

        public void Reopen()
        {
            Raiders.RemoveAll(p => p == null);
            EventBus.Ledger.OpenEntry("raid", "raid:" + LedgerId, StartTick, MapIndex, Summary, null,
                new JObject { ["faction"] = Faction, ["raider_count"] = Total }, LedgerId);
        }

        public bool Active { get { return Raiders.Any(p => p != null && !p.Dead && p.Spawned && !p.Downed && p.HostileTo(Faction_Player())); } }
        static Faction Faction_Player() { return RimWorld.Faction.OfPlayer; }
    }

    /// <summary>Slow polls: lodgers, prisoners, quests, funerals, weather, and the end of raids. Game thread.</summary>
    public static class Pollers
    {
        static readonly Dictionary<int, Pawn> Lodgers = new Dictionary<int, Pawn>();
        static readonly Dictionary<int, Pawn> Prisoners = new Dictionary<int, Pawn>();
        static readonly Dictionary<int, QuestState> Quests = new Dictionary<int, QuestState>();
        static readonly Dictionary<int, string> Weather = new Dictionary<int, string>(); // map index -> ledger key label
        static readonly HashSet<int> Funerals = new HashSet<int>();

        public static void Reset()
        {
            Lodgers.Clear(); Prisoners.Clear(); Quests.Clear(); Weather.Clear(); Funerals.Clear();
        }

        static bool Want(EventKind k) { return EventGate.GameLive() && EventGate.KindEnabled(k); }

        public static void Run(bool quiet)
        {
            if (!EventGate.GameLive()) return;
            int now = Find.TickManager.TicksGame;
            PollLodgers(now, quiet);
            PollPrisoners(now, quiet);
            PollQuests(now, quiet);
            PollWeather(now, quiet);
            PollFunerals(now, quiet);
            PollRaids(now);
        }

        static IEnumerable<Pawn> HomePawns()
        {
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (var p in map.mapPawns.AllPawnsSpawned) yield return p;
            }
        }

        static void PollLodgers(int now, bool quiet)
        {
            var current = new Dictionary<int, Pawn>();
            foreach (var p in HomePawns())
                if (p.RaceProps.Humanlike && p.Faction != null && p.Faction.IsPlayer && p.IsQuestLodger()) current[p.thingIDNumber] = p;
            foreach (var kv in current)
            {
                if (Lodgers.ContainsKey(kv.Key)) continue;
                var p = kv.Value;
                var le = EventBus.Ledger.OpenEntry("lodger", "lodger:" + kv.Key, now, p.Map.Index, p.LabelShort + " is staying as a lodger.", new[] { kv.Key });
                if (!quiet && Want(EventKind.Lodger))
                    EventBus.Publish(new EventSpec { Type = "lodger_arrive", Map = p.Map, AllOnMap = true, Severity = 0.3f, LedgerId = le.Id,
                        Summary = p.LabelShort + " arrived as a lodger." }.Involve(p, "lodger"));
            }
            foreach (var kv in Lodgers.ToList())
            {
                if (current.ContainsKey(kv.Key)) continue;
                var p = kv.Value;
                var le = EventBus.Ledger.Close("lodger:" + kv.Key);
                string why = p.Dead ? "died" : "left";
                if (!quiet && Want(EventKind.Lodger))
                    EventBus.Publish(new EventSpec { Type = "lodger_leave", Map = p.MapHeld ?? Find.AnyPlayerHomeMap, AllOnMap = true, Severity = p.Dead ? 0.5f : 0.3f, LedgerId = le?.Id,
                        Summary = p.LabelShort + (p.Dead ? " died while a lodger." : " left the colony."),
                        Data = new JObject { ["reason"] = why } }.Involve(p, "lodger", false));
            }
            Lodgers.Clear();
            foreach (var kv in current) Lodgers[kv.Key] = kv.Value;
        }

        static void PollPrisoners(int now, bool quiet)
        {
            var current = new Dictionary<int, Pawn>();
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (var p in map.mapPawns.PrisonersOfColony) current[p.thingIDNumber] = p;
            }
            foreach (var kv in current)
            {
                if (Prisoners.ContainsKey(kv.Key)) continue;
                var p = kv.Value;
                var le = EventBus.Ledger.OpenEntry("prisoner", "prisoner:" + kv.Key, now, p.Map?.Index, p.LabelShort + " is a prisoner.", new[] { kv.Key });
                if (!quiet && Want(EventKind.Prisoner))
                    EventBus.Publish(new EventSpec { Type = "prisoner", Map = p.Map, AllOnMap = true, Severity = 0.3f, LedgerId = le.Id,
                        Summary = p.LabelShort + " was taken prisoner.", Data = new JObject { ["phase"] = "captured" } }.Involve(p, "prisoner"));
            }
            foreach (var kv in Prisoners.ToList())
            {
                if (current.ContainsKey(kv.Key)) continue;
                var p = kv.Value;
                var le = EventBus.Ledger.Close("prisoner:" + kv.Key);
                string phase = p.Dead ? "died" : (p.Faction != null && p.Faction.IsPlayer ? "recruited" : "released");
                if (!quiet && Want(EventKind.Prisoner))
                    EventBus.Publish(new EventSpec { Type = "prisoner", Map = p.MapHeld ?? Find.AnyPlayerHomeMap, AllOnMap = true, Severity = phase == "died" ? 0.5f : 0.3f, LedgerId = le?.Id,
                        Summary = p.LabelShort + " is no longer a prisoner (" + phase + ").", Data = new JObject { ["phase"] = phase } }.Involve(p, "prisoner", false));
            }
            Prisoners.Clear();
            foreach (var kv in current) Prisoners[kv.Key] = kv.Value;
        }

        static void PollQuests(int now, bool quiet)
        {
            var list = Find.QuestManager.QuestsListForReading;
            foreach (var q in list)
            {
                if (q.hidden) continue;
                QuestState prev;
                bool known = Quests.TryGetValue(q.id, out prev);
                var st = q.State;
                if (known && prev == st) continue;
                Quests[q.id] = st;
                string key = "quest:" + q.id;
                var map = Find.AnyPlayerHomeMap;
                if (st == QuestState.Ongoing)
                {
                    var le = EventBus.Ledger.OpenEntry("quest", key, now, map?.Index, "Quest: " + q.name, null, new JObject { ["name"] = q.name });
                    if (!quiet && Want(EventKind.Quest))
                        EventBus.Publish(new EventSpec { Type = "quest", Map = map, AllOnMap = true, Severity = 0.4f, LedgerId = le.Id,
                            Summary = "Quest accepted: " + q.name + ".", Data = new JObject { ["phase"] = "accepted", ["name"] = q.name } });
                }
                else if (st == QuestState.NotYetAccepted)
                {
                    if (!known && !quiet && Want(EventKind.Quest))
                        EventBus.Publish(new EventSpec { Type = "quest", Map = map, AllOnMap = true, Severity = 0.3f,
                            Summary = "Quest offered: " + q.name + ".", Data = new JObject { ["phase"] = "offered", ["name"] = q.name } });
                }
                else if (known || EventBus.Ledger.Find(key) != null)
                {
                    var le = EventBus.Ledger.Close(key);
                    if (le != null && !quiet && Want(EventKind.Quest))
                    {
                        string outcome = st == QuestState.EndedSuccess ? "success" : (st == QuestState.EndedFailed ? "failed" : "ended");
                        EventBus.Publish(new EventSpec { Type = "quest_end", Map = map, AllOnMap = true, Severity = st == QuestState.EndedFailed ? 0.5f : 0.35f, LedgerId = le.Id,
                            Summary = "Quest " + outcome + ": " + q.name + ".", Data = new JObject { ["name"] = q.name, ["outcome"] = outcome } });
                    }
                }
            }
        }

        static void PollWeather(int now, bool quiet)
        {
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                string what = null;
                var w = map.weatherManager?.curWeather;
                if (w != null && w.isBad) what = w.label;
                var conds = map.gameConditionManager?.ActiveConditions;
                if (conds != null && conds.Count > 0) what = (what != null ? what + ", " : "") + string.Join(", ", conds.Select(c => c.def.label));
                string prev;
                bool had = Weather.TryGetValue(map.Index, out prev);
                string key = "weather:" + map.Index;
                if (what != null && (!had || prev != what))
                {
                    EventBus.Ledger.Close(key);
                    var le = EventBus.Ledger.OpenEntry("weather", key, now, map.Index, "Extreme weather: " + what + ".", null, new JObject { ["what"] = what });
                    Weather[map.Index] = what;
                    if (!quiet && Want(EventKind.Weather))
                        EventBus.Publish(new EventSpec { Type = "weather_extreme", Map = map, AllOnMap = true, Severity = 0.5f, LedgerId = le.Id,
                            Summary = "Extreme weather: " + what + ".", Data = new JObject { ["what"] = what } });
                }
                else if (what == null && had)
                {
                    Weather.Remove(map.Index);
                    var le = EventBus.Ledger.Close(key);
                    if (le != null && !quiet && Want(EventKind.Weather))
                        EventBus.Publish(new EventSpec { Type = "weather_extreme_end", Map = map, AllOnMap = true, Severity = 0.2f, LedgerId = le.Id,
                            Summary = "The extreme weather has passed.", Data = new JObject { ["what"] = prev } });
                }
            }
        }

        static void PollFunerals(int now, bool quiet)
        {
            if (!ModsConfig.IdeologyActive) return;
            var seen = new HashSet<int>();
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (var lord in map.lordManager.lords)
                {
                    var job = lord.LordJob as LordJob_Ritual;
                    if (job == null || job.Ritual == null) continue;
                    var d = job.Ritual.def;
                    if (d != PreceptDefOf.Funeral && d != PreceptDefOf.FuneralNoCorpse) continue;
                    seen.Add(lord.loadID);
                    string key = "funeral:" + lord.loadID;
                    if (Funerals.Contains(lord.loadID)) continue;
                    Funerals.Add(lord.loadID);
                    var le = EventBus.Ledger.OpenEntry("funeral", key, now, map.Index, "A funeral is being held.");
                    if (!quiet && Want(EventKind.Funeral))
                        EventBus.Publish(new EventSpec { Type = "funeral", Map = map, AllOnMap = true, Severity = 0.5f, LedgerId = le.Id,
                            Summary = "A funeral is starting.", Data = new JObject { ["phase"] = "start" } });
                }
            }
            foreach (int id in Funerals.ToList())
            {
                if (seen.Contains(id)) continue;
                Funerals.Remove(id);
                var le = EventBus.Ledger.Close("funeral:" + id);
                if (le != null && !quiet && Want(EventKind.Funeral))
                    EventBus.Publish(new EventSpec { Type = "funeral", Map = Find.AnyPlayerHomeMap, AllOnMap = true, Severity = 0.3f, LedgerId = le.Id,
                        Summary = "The funeral is over.", Data = new JObject { ["phase"] = "end" } });
            }
        }

        static void PollRaids(int now)
        {
            var comp = ColonyEventsComponent.Instance;
            if (comp == null) return;
            foreach (var r in comp.Raids.ToList())
            {
                if (r.Active) continue;
                comp.Raids.Remove(r);
                var le = EventBus.Ledger.Close("raid:" + r.LedgerId);
                if (!Want(EventKind.Raid)) continue;
                int dead = r.Raiders.Count(p => p != null && p.Dead);
                var map = Find.Maps.ElementAtOrDefault(r.MapIndex);
                EventBus.Publish(new EventSpec { Type = "raid_end", Map = map, AllOnMap = true, Severity = 0.3f, LedgerId = r.LedgerId,
                    Summary = "The raid by " + r.Faction + " is over (" + dead + " of " + r.Total + " raiders killed).",
                    Data = new JObject { ["faction"] = r.Faction, ["raiders_killed"] = dead, ["raider_count"] = r.Total, ["started_tick"] = r.StartTick } });
            }
        }
    }

    // ---------------------------------------------------------------- injuries
    public static class InjuryEvents
    {
        class Entry { public string Label; public string Part; public float Severity; public string Damage; public string Attacker; public Pawn AttackerPawn; }
        class Pending { public Pawn Pawn; public List<Entry> Entries = new List<Entry>(); }

        static readonly Dictionary<int, Pending> Merged = new Dictionary<int, Pending>();

        public static void Clear() { Merged.Clear(); }

        public static void OnInjury(Pawn pawn, Hediff_Injury inj, DamageInfo? dinfo)
        {
            if (!EventGate.Wanted(EventKind.Injury, pawn)) return;
            var e = new Entry
            {
                Label = inj.def.label,
                Part = inj.Part?.Label,
                Severity = EventPayload.Round(inj.Severity),
                Damage = dinfo?.Def?.label,
                Attacker = EventPayload.Label(dinfo?.Instigator),
                AttackerPawn = dinfo?.Instigator as Pawn
            };
            int id = pawn.thingIDNumber;
            if (EventGate.CooldownOk(EventKind.Injury, id, EventGate.CooldownFor(EventKind.Injury)))
            {
                Send(pawn, new List<Entry> { e });
                return;
            }
            Pending p;
            if (!Merged.TryGetValue(id, out p)) Merged[id] = p = new Pending { Pawn = pawn };
            if (p.Entries.Count < 30) p.Entries.Add(e);
        }

        public static void Flush()
        {
            if (Merged.Count == 0) return;
            foreach (int id in Merged.Keys.ToList())
            {
                var p = Merged[id];
                if (!EventGate.CooldownOk(EventKind.Injury, id, EventGate.CooldownFor(EventKind.Injury))) continue;
                Merged.Remove(id);
                if (EventGate.Wanted(EventKind.Injury, p.Pawn)) Send(p.Pawn, p.Entries);
            }
        }

        static void Send(Pawn pawn, List<Entry> entries)
        {
            var parts = entries.Select(x => x.Label + (x.Part != null ? " on " + x.Part : "")).ToList();
            var attackers = entries.Select(x => x.Attacker).Where(a => a != null).Distinct().ToList();
            string summary = pawn.LabelShort + " was hurt: " + string.Join(", ", parts.Take(5))
                + (parts.Count > 5 ? " and " + (parts.Count - 5) + " more" : "")
                + (attackers.Count > 0 ? " (by " + string.Join(", ", attackers) + ")" : "") + ".";
            float total = EventPayload.Round(entries.Sum(x => x.Severity));
            var spec = new EventSpec
            {
                Type = "injury", Map = pawn.MapHeld, At = pawn.PositionHeld, Summary = summary,
                Severity = Rules.InjurySeverity(total, pawn.HealthScale),
                Data = new JObject
                {
                    ["count"] = entries.Count,
                    ["total_severity"] = total,
                    ["injuries"] = new JArray(entries.Select(x => new JObject { ["part"] = x.Part, ["hediff"] = x.Label, ["severity"] = x.Severity, ["damage_def"] = x.Damage, ["attacker"] = x.Attacker })),
                    ["attackers"] = new JArray(attackers)
                }
            };
            spec.Involve(pawn, "victim");
            var ap = entries.Select(x => x.AttackerPawn).FirstOrDefault(a => a != null && a != pawn);
            if (ap != null) spec.Involve(ap, "attacker");
            EventBus.Publish(spec);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
        new[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
    public static class Patch_Injury
    {
        [HarmonyPostfix]
        public static void Postfix(Hediff hediff, DamageInfo? dinfo)
        {
            EventPayload.Safe("injury", () =>
            {
                var inj = hediff as Hediff_Injury;
                if (inj == null || !dinfo.HasValue || inj.pawn == null) return;
                InjuryEvents.OnInjury(inj.pawn, inj, dinfo);
            });
        }
    }

    // ------------------------------------------------------------------ death
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Death
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance, out bool __state) { __state = __instance.Dead; }

        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, bool __state, DamageInfo? dinfo, Hediff exactCulprit)
        {
            EventPayload.Safe("death", () =>
            {
                if (__state || !__instance.Dead) return;
                if (!EventGate.Wanted(EventKind.Death, __instance, true)) return;
                string cause = dinfo?.Def?.label ?? exactCulprit?.def?.label;
                string killer = EventPayload.Label(dinfo?.Instigator);
                string summary = __instance.LabelShort + " died"
                    + (cause != null ? " from " + cause : "")
                    + (killer != null ? ", killed by " + killer : "") + ".";
                var spec = new EventSpec
                {
                    Type = "death", Map = __instance.MapHeld, At = __instance.PositionHeld, Summary = summary, Severity = 1f,
                    Data = new JObject
                    {
                        ["animal"] = !__instance.RaceProps.Humanlike, ["cause"] = cause, ["culprit"] = exactCulprit?.LabelCap, ["killer"] = killer
                    }
                };
                spec.Involve(__instance, "victim", false);
                var kp = dinfo?.Instigator as Pawn;
                if (kp != null && kp != __instance) spec.Involve(kp, "attacker");
                EventGate.Emit(EventKind.Death, __instance, spec);
            });
        }
    }

    // ----------------------------------------------------------- mental break
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class Patch_MentalBreak
    {
        static AccessTools.FieldRef<MentalStateHandler, Pawn> _pawn;

        static Pawn PawnOf(MentalStateHandler h)
        {
            if (_pawn == null) _pawn = AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");
            return _pawn(h);
        }

        [HarmonyPostfix]
        public static void Postfix(MentalStateHandler __instance, bool __result, MentalStateDef stateDef, string reason)
        {
            EventPayload.Safe("mental break", () =>
            {
                if (!__result) return;
                var pawn = PawnOf(__instance);
                if (!EventGate.Wanted(EventKind.MentalBreak, pawn)) return;
                string label = stateDef.label;
                var spec = new EventSpec
                {
                    Type = "mental_break", Map = pawn.MapHeld, At = pawn.PositionHeld,
                    Severity = Rules.MentalBreakSeverity(stateDef.IsAggro, stateDef.IsExtreme),
                    Summary = pawn.LabelShort + " is having a mental break (" + label + ")" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason),
                    Data = new JObject { ["state"] = stateDef.defName, ["state_label"] = label, ["reason"] = reason }
                };
                spec.Involve(pawn, "subject");
                EventGate.Emit(EventKind.MentalBreak, pawn, spec);
            });
        }
    }

    // ---------------------------------------------------------------- level-up
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Learn))]
    public static class Patch_LevelUp
    {
        [HarmonyPrefix]
        public static void Prefix(SkillRecord __instance, out int __state) { __state = __instance.Level; }

        [HarmonyPostfix]
        public static void Postfix(SkillRecord __instance, int __state)
        {
            // Learn runs every tick for every working pawn: bail before allocating the Safe closure.
            if (__instance.Level <= __state) return;
            EventPayload.Safe("level up", () =>
            {
                var pawn = __instance.Pawn;
                if (!EventGate.Wanted(EventKind.LevelUp, pawn)) return;
                string skill = __instance.def.label;
                var spec = new EventSpec
                {
                    Type = "level_up", Map = pawn.MapHeld, At = pawn.PositionHeld,
                    Severity = Rules.LevelUpSeverity(__instance.Level),
                    Summary = pawn.LabelShort + " reached " + skill + " level " + __instance.Level + ".",
                    Data = new JObject { ["skill"] = skill, ["old_level"] = __state, ["new_level"] = __instance.Level, ["passion"] = __instance.passion.ToString() }
                };
                spec.Involve(pawn, "subject");
                EventGate.Emit(EventKind.LevelUp, pawn, spec);
            });
        }
    }

    // ----------------------------------------------------------------- tending
    public static class TendContext
    {
        public static float Quality = -1f;
    }

    [HarmonyPatch(typeof(HediffComp_TendDuration), nameof(HediffComp_TendDuration.CompTended))]
    public static class Patch_TendQuality
    {
        [HarmonyPostfix]
        public static void Postfix(float quality) { TendContext.Quality = Math.Max(TendContext.Quality, quality); }
    }

    [HarmonyPatch(typeof(TendUtility), nameof(TendUtility.DoTend))]
    public static class Patch_Tend
    {
        [HarmonyPrefix]
        public static void Prefix() { TendContext.Quality = -1f; }

        [HarmonyPostfix]
        public static void Postfix(Pawn doctor, Pawn patient, Medicine medicine)
        {
            EventPayload.Safe("tend", () =>
            {
                float q = TendContext.Quality;
                TendContext.Quality = -1f;
                if (doctor == null || patient == null || doctor == patient) return; // self-tend is noise
                if (!EventGate.Wanted(EventKind.Tend, patient)) return;
                string med = medicine?.def?.label;
                string qText = q >= 0f ? Math.Round(q * 100) + "% quality" : "unknown quality";
                var spec = new EventSpec
                {
                    Type = "tend", Map = patient.MapHeld, At = patient.PositionHeld, Severity = 0.2f,
                    Summary = doctor.LabelShort + " tended " + patient.LabelShort + " (" + qText + (med != null ? ", " + med : "") + ").",
                    Data = new JObject { ["quality"] = q >= 0f ? (JToken)EventPayload.Round(q) : JValue.CreateNull(), ["medicine"] = med }
                };
                spec.Involve(patient, "patient").Involve(doctor, "doctor");
                EventGate.Emit(EventKind.Tend, patient, spec);
            });
        }
    }

    // ------------------------------------------------------------------ social
    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Patch_Social
    {
        static AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> _pawn;

        [HarmonyPostfix]
        public static void Postfix(Pawn_InteractionsTracker __instance, bool __result, Pawn recipient, InteractionDef intDef)
        {
            EventPayload.Safe("social", () =>
            {
                if (!__result || recipient == null || intDef == null) return;
                if (_pawn == null) _pawn = AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");
                var initiator = _pawn(__instance);
                if (!EventGate.Wanted(EventKind.Social, initiator)) return;
                var spec = new EventSpec
                {
                    Type = "social", Map = initiator.MapHeld, At = initiator.PositionHeld, Severity = 0.1f,
                    Summary = initiator.LabelShort + " -> " + recipient.LabelShort + ": " + intDef.label + ".",
                    Data = new JObject { ["interaction"] = intDef.defName, ["interaction_label"] = intDef.label }
                };
                spec.Involve(initiator, "initiator").Involve(recipient, "recipient");
                EventGate.Emit(EventKind.Social, initiator, spec);
            });
        }
    }

    // ----------------------------------------------------------------- thoughts
    /// <summary>Notable new mood thoughts: absolute mood offset of at least 5. Our own HV_ thoughts never count.</summary>
    [HarmonyPatch(typeof(MemoryThoughtHandler), nameof(MemoryThoughtHandler.TryGainMemory),
        new[] { typeof(Thought_Memory), typeof(Pawn) })]
    public static class Patch_Thought
    {
        public const float MinOffset = 5f;

        [HarmonyPostfix]
        public static void Postfix(Thought_Memory newThought, Pawn otherPawn)
        {
            if (newThought == null || newThought.pawn == null) return;
            EventPayload.Safe("thought", () =>
            {
                var pawn = newThought.pawn;
                if (newThought.def == null || newThought.def.defName.StartsWith("HV_")) return;
                if (newThought.def.IsSocial) return;
                float off = newThought.MoodOffset();
                if (Math.Abs(off) < MinOffset) return;
                if (!EventGate.Wanted(EventKind.Thought, pawn)) return;
                if (!pawn.needs.mood.thoughts.memories.Memories.Contains(newThought)) return; // merged into an older memory
                var spec = new EventSpec
                {
                    Type = "thought", Map = pawn.MapHeld, At = pawn.PositionHeld, Severity = Rules.ThoughtSeverity(off),
                    Summary = pawn.LabelShort + " got a thought: " + newThought.LabelCap + " (" + (off > 0 ? "+" : "") + Math.Round(off, 1) + " mood).",
                    Data = new JObject { ["def"] = newThought.def.defName, ["label"] = newThought.LabelCap, ["offset"] = EventPayload.Round(off),
                        ["cause_pawn"] = otherPawn != null ? (JToken)otherPawn.thingIDNumber : JValue.CreateNull() }
                };
                spec.Involve(pawn, "subject");
                if (otherPawn != null) spec.Involve(otherPawn, "cause", false);
                EventGate.Emit(EventKind.Thought, pawn, spec);
            });
        }
    }

    // -------------------------------------------------------------------- raids
    public static class RaidContext
    {
        public static int RaiderCount = -1;
        public static List<Pawn> Raiders;
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class Patch_RaidInfo
    {
        [HarmonyPostfix]
        public static void Postfix(bool __result, List<Pawn> pawns)
        {
            if (__result && pawns != null) { RaidContext.RaiderCount = pawns.Count; RaidContext.Raiders = new List<Pawn>(pawns); }
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), "TryExecuteWorker")]
    public static class Patch_Raid
    {
        [HarmonyPrefix]
        public static void Prefix() { RaidContext.RaiderCount = -1; RaidContext.Raiders = null; }

        [HarmonyPostfix]
        public static void Postfix(bool __result, IncidentParms parms)
        {
            EventPayload.Safe("raid", () =>
            {
                int count = RaidContext.RaiderCount;
                var raiders = RaidContext.Raiders;
                RaidContext.RaiderCount = -1; RaidContext.Raiders = null;
                if (!__result || parms == null) return;
                if (!EventGate.GameLive() || !EventGate.KindEnabled(EventKind.Raid)) return;
                var map = parms.target as Map;
                if (map == null || !map.IsPlayerHome) return;
                if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer)) return;
                string fac = parms.faction.Name;
                string strat = parms.raidStrategy?.label;
                string arrive = parms.raidArrivalMode?.label;
                string summary = "Raid by " + fac + (count >= 0 ? " (" + count + " raiders)" : "")
                    + (strat != null ? ", strategy: " + strat : "") + (arrive != null ? ", arriving: " + arrive : "") + ".";
                int now = Find.TickManager.TicksGame;
                string lid = EventBus.Ledger.NextId("raid");
                var le = EventBus.Ledger.OpenEntry("raid", "raid:" + lid, now, map.Index, summary, null,
                    new JObject { ["faction"] = fac, ["raider_count"] = count >= 0 ? (JToken)count : JValue.CreateNull() }, lid);
                var comp = ColonyEventsComponent.Instance;
                if (comp != null)
                    comp.Raids.Add(new RaidTrack { LedgerId = lid, MapIndex = map.Index, Faction = fac, StartTick = now, Summary = summary,
                        Raiders = raiders ?? new List<Pawn>(), Total = raiders != null ? raiders.Count : Math.Max(0, count) });
                EventBus.Publish(new EventSpec
                {
                    Type = "raid_start", Map = map, AllOnMap = true, Severity = Rules.RaidSeverity(parms.points), LedgerId = le.Id, Summary = summary,
                    Data = new JObject
                    {
                        ["faction"] = fac, ["strategy"] = strat, ["arrival"] = arrive,
                        ["points"] = EventPayload.Round(parms.points), ["raider_count"] = count >= 0 ? (JToken)count : JValue.CreateNull()
                    }
                });
            });
        }
    }
}
