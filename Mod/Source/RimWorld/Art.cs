using System;
using HarmonyLib;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace HermesVoice
{
    /// <summary>Art descriptions: the event when a colonist finishes a piece, the lookup behind POST /art, and the info-card text.</summary>
    public static class ArtSupport
    {
        public const int EventCooldownTicks = 2500;   // one game hour per maker
        public const int MaxTaleChars = 400;

        /// <summary>The artwork with this thing id, spawned on a map or inside a minified piece. Null when gone or never seen.</summary>
        public static Thing Find(int id)
        {
            foreach (var map in Verse.Find.Maps)
                foreach (var t in map.listerThings.AllThings)
                {
                    if (t.thingIDNumber == id) return t;
                    var mini = t as MinifiedThing;
                    if (mini != null && mini.InnerThing != null && mini.InnerThing.thingIDNumber == id) return mini.InnerThing;
                }
            return null;
        }

        public static CompArt ArtOf(Thing t)
        {
            var art = t == null ? null : t.TryGetComp<CompArt>();
            return art != null && art.Active ? art : null;
        }

        /// <summary>The tale the art depicts as a JSON object, or null when none or unreadable (private TaleReference.tale read by reflection).</summary>
        public static JObject TaleObject(CompArt art)
        {
            try
            {
                var tref = art.TaleRef;
                if (tref == null || tref.IsNullTale) return null;
                var tale = AccessTools.Field(typeof(TaleReference), "tale").GetValue(tref) as Tale;
                if (tale == null) return null;
                var pawns = new System.Collections.Generic.List<string>();
                var single = tale as Tale_SinglePawn;
                var dbl = tale as Tale_DoublePawn;
                if (single != null) AddName(pawns, single.pawnData);
                if (dbl != null) { AddName(pawns, dbl.firstPawnData); AddName(pawns, dbl.secondPawnData); }
                int day = GenDate.DaysPassed - tale.AgeTicks / GenDate.TicksPerDay;
                string summary = "";
                try { summary = tale.ShortSummary; } catch (Exception) { }
                return ArtText.Tale(tale.def == null ? "" : tale.def.label, summary, day, pawns);
            }
            catch (Exception) { return null; }
        }

        static void AddName(System.Collections.Generic.List<string> l, TaleData_Pawn d)
        {
            if (d == null) return;
            string n = d.pawn != null ? d.pawn.LabelShort : (d.name != null ? d.name.ToStringShort : null);
            if (!string.IsNullOrEmpty(n)) l.Add(n);
        }

        public static string QualityLabel(Thing t)
        {
            QualityCategory q;
            return t.TryGetQuality(out q) ? q.GetLabel() : "";
        }
    }

    public static partial class Routes
    {
        static void RegisterArt(ApiRouter r)
        {
            r.Add("POST", "/art", true, ArtPost);
        }

        static ApiResult ArtPost(RouteContext c)
        {
            var b = c.Body;
            if (b["thing"] == null || b["thing"].Type != JTokenType.Integer)
                return ApiResult.Fail("bad_request", "thing (the artwork's id, an integer) is required");
            string text = ((string)b["text"] ?? "").Trim();
            if (text.Length == 0) return ApiResult.Fail("bad_request", "text is empty");
            int id = b.Value<int>("thing");
            var thing = ArtSupport.Find(id);
            if (thing == null || thing.Destroyed) return ApiResult.Fail("conflict", "no artwork " + id + " (destroyed, or not on a map)");
            if (ArtSupport.ArtOf(thing) == null) return ApiResult.Fail("bad_request", "thing " + id + " is not an artwork");
            int count = HermesBridge.Art.Set(id, text, Verse.Find.TickManager.TicksGame);
            return ApiResult.Success(new JObject { ["stored"] = true, ["count"] = count });
        }
    }

    // A finished piece: JustCreatedBy runs once when a worker makes art (after quality is set, before the piece is placed).
    // Art that spawns with the map, comes from traders, or is minified/reinstalled never runs it.
    [HarmonyPatch(typeof(CompArt), nameof(CompArt.JustCreatedBy))]
    public static class Patch_ArtCreated
    {
        static readonly System.Text.RegularExpressions.Regex Tags = new System.Text.RegularExpressions.Regex("<[^>]*>");
        static string Strip(string s) { return Tags.Replace(s ?? "", ""); }

        [HarmonyPostfix]
        public static void Postfix(CompArt __instance, Pawn pawn)
        {
            if (pawn == null || !__instance.Active || !EventGate.Wanted(EventKind.Art, pawn)) return;
            EventPayload.Safe("art", () =>
            {
                var thing = __instance.parent;
                if (thing == null || !EventGate.CooldownPassed(EventKind.Art, pawn.thingIDNumber, ArtSupport.EventCooldownTicks)) return;
                string title = Strip(__instance.Title ?? "").Trim();
                string tale = "";
                try { tale = __instance.GenerateImageDescription().ToString(); } catch (Exception) { }
                tale = Strip(tale).Trim();
                if (tale.Length > ArtSupport.MaxTaleChars) tale = tale.Substring(0, ArtSupport.MaxTaleChars);
                string quality = ArtSupport.QualityLabel(thing);
                string material = "";
                try { material = ArtText.Material(thing.Stuff == null ? "" : thing.Stuff.label); } catch (Exception) { }
                JObject taleObj = ArtSupport.TaleObject(__instance);
                var spec = new EventSpec
                {
                    Type = "art_created", Map = pawn.MapHeld, At = pawn.PositionHeld, Severity = 0.2f,
                    Summary = pawn.LabelShort + " finished " + thing.LabelNoCount + (title.Length > 0 ? " titled \"" + title + "\"" : "") + ".",
                    Data = new JObject
                    {
                        ["thing_id"] = thing.thingIDNumber, ["def_label"] = thing.def.label, ["quality"] = quality,
                        ["title"] = title, ["description"] = tale, ["material"] = material
                    }
                };
                if (taleObj != null) spec.Data["tale"] = taleObj;
                spec.Involve(pawn, "maker");
                EventGate.Emit(EventKind.Art, pawn, spec);
            });
        }
    }

    // The info card's art text: ours replaces the game's generated tale text when the book has one and the setting is on.
    [HarmonyPatch(typeof(CompArt), nameof(CompArt.GetDescriptionPart))]
    public static class Patch_ArtDescription
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance, ref string __result)
        {
            try
            {
                var s = HermesVoiceMod.Settings;
                if (s == null || !s.enabled || !s.artDescriptions || __instance.parent == null) return;
                string text = HermesBridge.Art.Get(__instance.parent.thingIDNumber);
                if (!string.IsNullOrEmpty(text)) __result = text;
            }
            catch (Exception) { }
        }
    }
}
