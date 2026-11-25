using System.Collections.Generic;
using System.Linq;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HermesVoice
{
    /// <summary>
    /// "gathering" / "gathering_end" events: parties, weddings, rituals (funerals included), speeches, bestowing ceremonies
    /// (all lords) and ordinary shared meals (a poll of who is eating in which room). Game thread only; polled from
    /// <see cref="Pollers.Run"/> every 120 ticks.
    /// </summary>
    public static class GatheringEvents
    {
        sealed class Track
        {
            public string Kind, Key, Label, Ideo, Room, DeceasedName;
            public Pawn Lead, Deceased, CoupleA, CoupleB;
            public int MapIndex;
            public bool Started;
            public string LedgerId;
            public List<Pawn> Participants = new List<Pawn>();
            public float Severity;
        }

        static readonly Dictionary<int, Track> Lords = new Dictionary<int, Track>();
        static readonly Dictionary<int, Track> Meals = new Dictionary<int, Track>();   // by meal room key
        static readonly MealDetector Detector = new MealDetector();

        public static void Reset() { Lords.Clear(); Meals.Clear(); Detector.Clear(); }

        /// <summary>A free colonist or slave of the player faction (no prisoners, guests or lodgers).</summary>
        static bool Eligible(Pawn p)
        {
            if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Humanlike) return false;
            if (p.Faction == null || !p.Faction.IsPlayer || p.IsQuestLodger()) return false;
            return p.IsColonist || p.IsSlaveOfColony;
        }

        public static void Poll(int now, bool quiet)
        {
            if (!EventGate.GameLive()) return;
            PollLords(now, quiet);
            PollMeals(now, quiet);
        }

        // ------------------------------------------------------------ lords
        static void PollLords(int now, bool quiet)
        {
            var seen = new HashSet<int>();
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (var lord in map.lordManager.lords)
                {
                    var job = lord.LordJob;
                    if (!(job is LordJob_Joinable_Party || job is LordJob_Joinable_MarriageCeremony
                          || job is LordJob_Ritual || job is LordJob_BestowingCeremony)) continue;
                    seen.Add(lord.loadID);
                    Track t;
                    if (!Lords.TryGetValue(lord.loadID, out t))
                    {
                        t = Describe(lord, job, map);
                        if (t == null) continue;
                        Lords[lord.loadID] = t;
                    }
                    Refresh(lord, job, t);
                    if (!t.Started && t.Participants.Count >= 2) Start(t, now, quiet);
                }
            }
            foreach (int id in Lords.Keys.ToList())
            {
                if (seen.Contains(id)) continue;
                var t = Lords[id];
                Lords.Remove(id);
                if (t.Started) End(t, now, quiet);
            }
        }

        static Track Describe(Lord lord, LordJob job, Map map)
        {
            var t = new Track { MapIndex = map.Index, Severity = 0.6f };
            string key = "lord:" + lord.loadID;
            t.Key = key;
            var marriage = job as LordJob_Joinable_MarriageCeremony;
            if (job is LordJob_Joinable_Party)
            {
                t.Kind = "party"; t.Severity = 0.45f;
                var party = (LordJob_Joinable_Party)job;
                var gdef = HarmonyLib.Traverse.Create(party).Field("gatheringDef").GetValue<GatheringDef>();   // protected field
                t.Label = gdef != null ? gdef.label : "party";
                t.Lead = party.Organizer;
            }
            else if (marriage != null)
            {
                t.Kind = "wedding"; t.Label = "wedding";
                t.CoupleA = marriage.firstPawn; t.CoupleB = marriage.secondPawn;
            }
            else if (job is LordJob_BestowingCeremony)
            {
                var b = (LordJob_BestowingCeremony)job;
                t.Kind = "speech"; t.Label = job.GetType() == typeof(LordJob_BestowingCeremony) ? "bestowing ceremony" : "ceremony";
                t.Lead = b.bestower;
            }
            else
            {
                var rit = (LordJob_Ritual)job;
                var def = rit.Ritual != null ? rit.Ritual.def : null;
                bool funeral = def != null && (def == PreceptDefOf.Funeral || def == PreceptDefOf.FuneralNoCorpse);
                t.Kind = funeral ? "funeral" : (job is LordJob_Joinable_Speech ? "speech" : "ritual");
                t.Label = rit.RitualLabel;
                if (rit.Ritual != null) t.Ideo = rit.Ritual.Label;
                t.Lead = rit.Organizer;
                if (t.Lead == null && rit.assignments != null)
                    t.Lead = rit.assignments.FirstAssignedPawn("leader") ?? rit.assignments.FirstAssignedPawn("moralist");
                if (funeral)
                {
                    var target = rit.selectedTarget.Thing;
                    var corpse = target as Corpse;
                    t.Deceased = corpse != null ? corpse.InnerPawn : target as Pawn;
                    if (t.Deceased != null) t.DeceasedName = t.Deceased.LabelShort;
                }
            }
            if (t.Label != null && t.Label.Length > 60) t.Label = t.Label.Substring(0, 60);
            return t;
        }

        static void Refresh(Lord lord, LordJob job, Track t)
        {
            var set = new HashSet<Pawn>();
            foreach (var p in lord.ownedPawns) if (Eligible(p)) set.Add(p);
            var rit = job as LordJob_Ritual;
            if (rit != null && rit.assignments != null)
                foreach (var p in rit.assignments.Participants) if (Eligible(p)) set.Add(p);
            var bes = job as LordJob_BestowingCeremony;
            if (bes != null && bes.colonistParticipants != null)
                foreach (var p in bes.colonistParticipants) if (Eligible(p)) set.Add(p);
            foreach (var p in t.Participants) if (Eligible(p) && p.MapHeld != null) set.Add(p);   // keep who was there, for the end event
            if (t.CoupleA != null && Eligible(t.CoupleA)) set.Add(t.CoupleA);
            if (t.CoupleB != null && Eligible(t.CoupleB)) set.Add(t.CoupleB);
            if (t.Lead != null && !Eligible(t.Lead)) t.Lead = null;
            t.Participants = set.OrderBy(p => p.thingIDNumber).ToList();
        }

        // ------------------------------------------------------------ meals
        static void PollMeals(int now, bool quiet)
        {
            var samples = new List<MealSample>();
            var byId = new Dictionary<int, Pawn>();
            var roomLabel = new Dictionary<int, string>();
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (!Eligible(p) || p.Downed || !p.Awake()) continue;
                    var room = p.GetRoom();
                    if (room == null || room.PsychologicallyOutdoors) continue;
                    int rid = map.Index * 1000000 + room.ID;
                    byId[p.thingIDNumber] = p;
                    var lord = p.GetLord();
                    bool inLord = lord != null && (lord.LordJob is LordJob_Ritual || lord.LordJob is LordJob_Joinable_Party || lord.LordJob is LordJob_Joinable_MarriageCeremony);
                    bool eating = !inLord && p.CurJobDef == JobDefOf.Ingest;
                    samples.Add(new MealSample(p.thingIDNumber, rid, eating));
                    if (eating && !roomLabel.ContainsKey(rid)) roomLabel[rid] = room.Role != null ? room.Role.label : null;
                }
            }
            List<MealGroup> starts, ends;
            Detector.Observe(samples, now, out starts, out ends);
            foreach (var g in ends)
            {
                Track t;
                if (!Meals.TryGetValue(g.RoomId, out t)) continue;
                Meals.Remove(g.RoomId);
                if (t.Started) End(t, now, quiet);
            }
            foreach (var g in starts)
            {
                var t = new Track { Kind = "meal", Key = g.Key, Severity = 0.45f, MapIndex = g.RoomId / 1000000, Label = "shared meal" };
                t.Room = roomLabel.TryGetValue(g.RoomId, out var rl) ? rl : null;
                foreach (int id in g.Pawns) { Pawn p; if (byId.TryGetValue(id, out p)) t.Participants.Add(p); }
                Meals[g.RoomId] = t;
                if (t.Participants.Count >= MealDetector.MinStart) Start(t, now, quiet);
            }
        }

        // ------------------------------------------------------------ events
        static void Start(Track t, int now, bool quiet)
        {
            t.Started = true;
            var map = Find.Maps.ElementAtOrDefault(t.MapIndex);
            var le = EventBus.Ledger.OpenEntry("gathering", "gathering:" + t.Key, now, t.MapIndex, Text(t, true),
                t.Participants.Select(p => p.thingIDNumber), new JObject { ["kind"] = t.Kind, ["key"] = t.Key }, null);
            t.LedgerId = le.Id;
            if (quiet || !EventGate.KindEnabled(EventKind.Gatherings)) return;
            if (t.Kind != "meal" && !EventGate.GatheringCooldownOk(t.Key)) return;
            Publish(t, "start", "gathering", map, le.Id, Text(t, true));
        }

        static void End(Track t, int now, bool quiet)
        {
            var le = EventBus.Ledger.Close("gathering:" + t.Key);
            if (le == null || quiet || !EventGate.KindEnabled(EventKind.Gatherings)) return;
            var map = Find.Maps.ElementAtOrDefault(t.MapIndex);
            Publish(t, "end", "gathering_end", map, le.Id, Text(t, false));
        }

        static string Text(Track t, bool start)
        {
            string names = string.Join(" and ", new[] { t.CoupleA, t.CoupleB }.Where(p => p != null).Select(p => p.LabelShort).ToArray());
            switch (t.Kind)
            {
                case "wedding": return start ? "A wedding is starting" + (names.Length > 0 ? ": " + names + "." : ".") : "The wedding is over.";
                case "party": return start ? "A " + t.Label + " is starting." : "The " + t.Label + " is over.";
                case "funeral": return start ? "A funeral gathering begins" + (t.DeceasedName != null ? " for " + t.DeceasedName + "." : ".") : "The funeral gathering is over.";
                case "speech": return start ? (t.Lead != null ? t.Lead.LabelShort + " is giving a speech." : "A speech is starting.") : "The speech is over.";
                case "ritual": return start ? "A ritual is starting: " + t.Label + "." : "The ritual (" + t.Label + ") is over.";
                default: return start ? "Colonists are eating together" + (t.Room != null ? " in the " + t.Room + "." : ".") : "The shared meal is over.";
            }
        }

        static void Publish(Track t, string phase, string type, Map map, string ledgerId, string summary)
        {
            var data = new JObject { ["phase"] = phase, ["kind"] = t.Kind, ["key"] = t.Key };
            if (t.Lead != null) data["lead"] = t.Lead.thingIDNumber;
            if (!string.IsNullOrEmpty(t.Label)) data["label"] = t.Label;
            if (!string.IsNullOrEmpty(t.Room)) data["room"] = t.Room;
            if (!string.IsNullOrEmpty(t.Ideo)) data["ideo"] = t.Ideo;
            if (t.DeceasedName != null) data["deceased_name"] = t.DeceasedName;
            if (t.CoupleA != null && t.CoupleB != null) data["couple"] = new JArray(t.CoupleA.thingIDNumber, t.CoupleB.thingIDNumber);
            var spec = new EventSpec { Type = type, Map = map, Severity = t.Severity, LedgerId = ledgerId, Summary = summary, Data = data };
            var listed = new HashSet<Pawn>();
            if (t.Lead != null && listed.Add(t.Lead)) spec.Involve(t.Lead, "lead");
            if (t.CoupleA != null && listed.Add(t.CoupleA)) spec.Involve(t.CoupleA, "couple");
            if (t.CoupleB != null && listed.Add(t.CoupleB)) spec.Involve(t.CoupleB, "couple");
            foreach (var p in t.Participants) if (listed.Add(p)) spec.Involve(p, "participant");
            if (t.Deceased != null && listed.Add(t.Deceased)) spec.Involve(t.Deceased, "subject", false);
            EventBus.Publish(spec);
        }
    }
}
