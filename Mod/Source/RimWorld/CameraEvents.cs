using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimWorldLLM
{
    // Events the watch-mode director needs and the mod did not emit yet: birth, masterwork, ship_part.
    // All three are published through EventBus like the other colony events (the agent sees them too).
    // Each patch's Prepare() returns false when the target method or its parameters are missing (or the DLC is off),
    // so a game without Biotech never patches or runs the birth hook.

    static class CameraEventHooks
    {
        public static bool Has(MethodBase m, params string[] names)
        {
            if (m == null) return false;
            var ps = m.GetParameters().Select(p => p.Name).ToList();
            return names.All(ps.Contains);
        }
    }

    // ------------------------------------------------------------------ birth (Biotech)
    [HarmonyPatch]
    public static class Patch_Birth
    {
        static MethodBase Target() { return AccessTools.Method(typeof(PregnancyUtility), "ApplyBirthOutcome"); }
        public static bool Prepare() { return ModsConfig.BiotechActive && CameraEventHooks.Has(Target(), "birtherThing", "geneticMother"); }
        public static MethodBase TargetMethod() { return Target(); }

        public static void Postfix(Thing birtherThing, Pawn geneticMother)
        {
            EventPayload.Safe("birth", () =>
            {
                if (!EventGate.GameLive()) return;
                var mother = birtherThing as Pawn ?? geneticMother;
                if (mother == null || mother.MapHeld == null || mother.Faction == null || !mother.Faction.IsPlayer) return;
                var spec = new EventSpec
                {
                    Type = "birth", Map = mother.MapHeld, At = mother.PositionHeld, Severity = 0.6f,
                    Summary = mother.LabelShort + " gave birth.",
                    Data = new JObject { ["pawn"] = EventPayload.PawnObj(mother) }
                };
                spec.Involve(mother, "mother");
                EventBus.Publish(spec);
            });
        }
    }

    // ------------------------------------------------------------------ masterwork / legendary
    [HarmonyPatch]
    public static class Patch_Masterwork
    {
        static MethodBase Target() { return AccessTools.Method(typeof(QualityUtility), "SendCraftNotification"); }
        public static bool Prepare() { return CameraEventHooks.Has(Target(), "thing", "worker"); }
        public static MethodBase TargetMethod() { return Target(); }

        public static void Postfix(Thing thing, Pawn worker)
        {
            EventPayload.Safe("masterwork", () =>
            {
                if (thing == null || worker == null || !EventGate.GameLive()) return;
                QualityCategory q;
                if (!thing.TryGetQuality(out q) || q < QualityCategory.Masterwork) return;
                if (worker.Faction == null || !worker.Faction.IsPlayer || worker.MapHeld == null) return;
                string quality = q == QualityCategory.Legendary ? "legendary" : "masterwork";
                var spec = new EventSpec
                {
                    Type = "masterwork", Map = worker.MapHeld, At = worker.PositionHeld, Severity = 0.5f,
                    Summary = worker.LabelShort + " finished a " + quality + " " + thing.def.label + ".",
                    Data = new JObject
                    {
                        ["thing"] = thing.thingIDNumber, ["label"] = thing.def.label, ["quality"] = quality,
                        ["maker"] = EventPayload.PawnObj(worker)
                    }
                };
                spec.Involve(worker, "maker");
                EventBus.Publish(spec);
            });
        }
    }

    // ------------------------------------------------------------------ ship part finished
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class Patch_ShipPart
    {
        public struct State { public ThingDef Def; public Map Map; public IntVec3 Cell; }

        static bool IsShipPart(ThingDef d)
        {
            return d != null && d.comps != null && d.comps.Any(c => c is CompProperties_ShipPart);
        }

        public static void Prefix(Frame __instance, ref State __state)
        {
            try
            {
                var def = __instance.def != null ? __instance.def.entityDefToBuild as ThingDef : null;
                if (IsShipPart(def)) __state = new State { Def = def, Map = __instance.Map, Cell = __instance.Position };
            }
            catch (Exception) { }
        }

        public static void Postfix(State __state, Pawn worker)
        {
            if (__state.Def == null) return;
            EventPayload.Safe("ship_part", () =>
            {
                if (!EventGate.GameLive() || __state.Map == null) return;
                var spec = new EventSpec
                {
                    Type = "ship_part", Map = __state.Map, At = __state.Cell, AllOnMap = true, Severity = 0.7f,
                    Summary = "A ship part was finished: " + __state.Def.label + ".",
                    Data = new JObject { ["thing"] = __state.Def.defName, ["label"] = __state.Def.label }
                };
                if (worker != null) spec.Involve(worker, "builder");
                EventBus.Publish(spec);
            });
        }
    }
}
