using System;
using System.Collections.Generic;
using System.Linq;
using HermesVoice.Core;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>What a patch hands to the bus. Game thread only.</summary>
    public sealed class EventSpec
    {
        public string Type;
        public Map Map;
        public List<KeyValuePair<Pawn, string>> Pawns = new List<KeyValuePair<Pawn, string>>();
        public string Summary;
        public float Severity;
        public IntVec3? At;               // where it happened, for hearing; null with AllOnMap
        public bool AllOnMap;             // everyone on the map knows (raid, quest, weather)
        public List<Pawn> AlsoWitness = new List<Pawn>();
        public string LedgerId;
        public JObject Data = new JObject();

        public EventSpec Involve(Pawn p, string role, bool witness = true)
        {
            if (p == null) return this;
            Pawns.Add(new KeyValuePair<Pawn, string>(p, role));
            if (witness) AlsoWitness.Add(p);
            return this;
        }
    }

    public static class Witnesses
    {
        /// <summary>People who can speak for themselves: colonists, slaves, colony prisoners and guests of the colony.</summary>
        public static bool IsSpeaker(Pawn p)
        {
            if (p == null || p.Dead || !p.Spawned || p.RaceProps == null || !p.RaceProps.Humanlike) return false;
            if (p.Faction != null && p.Faction.IsPlayer) return true;
            return p.IsPrisonerOfColony || (p.HostFaction != null && p.HostFaction.IsPlayer);
        }

        public static List<int> Compute(EventSpec e)
        {
            var result = new HashSet<int>();
            var map = e.Map;
            if (map != null)
            {
                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (!IsSpeaker(p) || !p.Awake()) continue;
                    if (e.AllOnMap) { result.Add(p.thingIDNumber); continue; }
                    if (e.At == null) continue;
                    try
                    {
                        float level = p.health?.capacities?.GetLevel(PawnCapacityDefOf.Hearing) ?? 0f;
                        var at = e.At.Value;
                        float dist = p.Position.DistanceTo(at);
                        if (dist > Rules.HearingBaseRange * Math.Max(0f, level)) continue;
                        bool sameRoom = false;
                        var room = p.GetRoom();
                        if (room != null && at.InBounds(map)) sameRoom = room == RegionAndRoomQuery.RoomAt(at, map);
                        bool los = !sameRoom && GenSight.LineOfSight(p.Position, at, map);
                        if (Rules.Witnessed(dist, level, sameRoom, los)) result.Add(p.thingIDNumber);
                    }
                    catch (Exception) { }
                }
            }
            foreach (var p in e.AlsoWitness)
                if (p != null && !p.Dead && p.RaceProps != null && p.RaceProps.Humanlike && IsSpeaker(p) && p.Awake()) result.Add(p.thingIDNumber);
            return result.OrderBy(i => i).ToList();
        }
    }

    public static class EventBus
    {
        public static readonly Ledger Ledger = new Ledger();

        public static JObject Build(EventSpec e)
        {
            return new JObject
            {
                ["tick"] = Find.TickManager.TicksGame,
                ["type"] = e.Type,
                ["map"] = e.Map != null ? (JToken)e.Map.Index : JValue.CreateNull(),
                ["pawns"] = new JArray(e.Pawns.Select(p => new JObject { ["id"] = p.Key.thingIDNumber, ["role"] = p.Value })),
                ["summary"] = e.Summary,
                ["severity"] = (float)Math.Round(Math.Max(0f, Math.Min(1f, e.Severity)), 2),
                ["witnesses"] = new JArray(Witnesses.Compute(e)),
                ["ledger_id"] = e.LedgerId != null ? (JToken)e.LedgerId : JValue.CreateNull(),
                ["data"] = e.Data
            };
        }

        /// <summary>Record the event in the ring, then push the same object to the agent when pushing is on.</summary>
        public static JObject Publish(EventSpec spec, bool push = true)
        {
            var ev = Build(spec);
            HermesBridge.Events.Add(ev);
            if (push) AgentDialOut.Push(ev);
            return ev;
        }

        public static void Reset()
        {
            HermesBridge.Events.Clear();
            Ledger.Clear();
        }
    }
}
