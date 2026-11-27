using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Per-instance generated text (docs/TEXT-POOLS.md). An artwork, a grave, a book or a log line asks the desk for
    /// its own text when it is made (or the first time it is shown), with a snapshot of what was going on. The game's own
    /// text shows until the engine writes ours (POST /text/fill); then ours shows, saved with the game. Nothing here waits
    /// on the engine, and every hook falls back to the game's text on any error.</summary>
    public static class TextHook
    {
        public static TextDesk Desk { get { return RWLBridge.Runtime.Texts; } }

        static TextHook() { RWLBridge.Runtime.Texts.Enabled = KindOn; }

        // Instances queued this session, so a fill can be put on them at once (art title cache, book fields, log caches).
        static readonly Dictionary<string, System.WeakReference<object>> Live = new Dictionary<string, System.WeakReference<object>>();
        static readonly HashSet<string> Applied = new HashSet<string>();
        [ThreadStatic] static bool _building;
        static bool _warned;

        // Death causes noted at Pawn.Kill, for the grave's context. Session only.
        static readonly Dictionary<int, JObject> Deaths = new Dictionary<int, JObject>();
        static readonly Queue<int> DeathOrder = new Queue<int>();

        public static void Register(ApiRouter r)
        {
            Desk.Enabled = KindOn;
            TextRoutes.Register(r, () => Desk, () => new Applier(), () => Find.TickManager != null ? Find.TickManager.TicksGame : 0);
        }

        public static bool KindOn(string kind)
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null || !s.enabled || !s.textGen) return false;
            switch (kind)
            {
                case TextKinds.Art: return s.textArt;
                case TextKinds.Grave: return s.textGraves;
                case TextKinds.Book: return s.textBooks;
                case TextKinds.Social: return s.textSocial;
                case TextKinds.Combat: return s.textCombat;
                default: return false;
            }
        }

        public static void Reset()
        {
            Desk.Clear();
            Live.Clear();
            Applied.Clear();
            Deaths.Clear();
            DeathOrder.Clear();
        }

        static int Now { get { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; } }

        /// <summary>Whether an id would be queued now: kind on, never asked for, no text yet. Cheap; safe every frame.</summary>
        public static bool Wants(string id, string kind)
        {
            return !_building && KindOn(kind) && !Desk.Tried(id) && Desk.Get(id) == null;
        }

        /// <summary>Queues one instance. The context and the game's own text are taken now. Never throws.</summary>
        public static void Queue(string id, string kind, object instance, Func<JObject> context, Func<string> example, Func<string> exampleTitle = null)
        {
            if (!Wants(id, kind)) return;
            _building = true;
            try
            {
                var item = new TextItem { Id = id, Kind = kind, Context = context() ?? new JObject() };
                try { item.Example = Plain(example()); } catch (Exception) { }
                try { if (exampleTitle != null) item.ExampleTitle = Plain(exampleTitle()); } catch (Exception) { }
                if (Desk.Enqueue(item, Now) && instance != null) Live[id] = new System.WeakReference<object>(instance);
                if (Live.Count > 4000) Live.Clear();
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Log.Warning("[RimWorldLLM] text queue: " + e.Message); }
            }
            finally { _building = false; }
        }

        public static TextEntry Text(string id) { return _building ? null : Desk.Get(id); }

        public static object LiveOf(string id)
        {
            System.WeakReference<object> w;
            object o;
            return Live.TryGetValue(id, out w) && w.TryGetTarget(out o) ? o : null;
        }

        public static bool NeedsApply(string id) { return !Applied.Contains(id); }
        public static void MarkApplied(string id) { Applied.Add(id); }

        // ---- context ---------------------------------------------------------------------------------------------
        static readonly Regex Tags = new Regex(@"<[^>]*>|\(\*[^)]*\)|\(/[^)]*\)");
        public static string Plain(string s) { return Tags.Replace(s ?? "", "").Trim(); }

        public static JObject Colony(Map map)
        {
            var o = new JObject();
            try
            {
                if (Faction.OfPlayer != null) o["colony"] = Plain(Faction.OfPlayer.Name);
                map = map ?? Find.CurrentMap;
                if (map != null)
                {
                    o["date"] = GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile));
                    if (map.weatherManager != null && map.weatherManager.curWeather != null) o["weather"] = map.weatherManager.curWeather.label;
                    o["outdoor_temp"] = Math.Round(map.mapTemperature.OutdoorTemp) + "C";
                    o["colonists"] = map.mapPawns.FreeColonistsSpawnedCount;
                }
                var letters = Find.LetterStack != null ? Find.LetterStack.LettersListForReading : null;
                if (letters != null && letters.Count > 0)
                    o["recent"] = new JArray(letters.Skip(Math.Max(0, letters.Count - 4)).Select(l => Plain(l.Label.ToString())));
            }
            catch (Exception) { }
            return o;
        }

        public static JObject PawnInfo(Pawn p, bool mood = true)
        {
            var o = new JObject();
            if (p == null) return o;
            try
            {
                o["name"] = p.LabelShort;
                if (p.RaceProps != null && !p.RaceProps.Humanlike) o["animal"] = p.def.label;
                if (p.ageTracker != null) o["age"] = p.ageTracker.AgeBiologicalYears;
                if (p.gender != Gender.None) o["gender"] = p.gender.ToString().ToLowerInvariant();
                if (p.story != null)
                {
                    if (p.story.traits != null) o["traits"] = new JArray(p.story.traits.allTraits.Take(4).Select(t => Plain(t.LabelCap)));
                    if (p.story.Adulthood != null) o["background"] = Plain(p.story.Adulthood.TitleShortFor(p.gender));
                    else if (p.story.Childhood != null) o["background"] = Plain(p.story.Childhood.TitleShortFor(p.gender));
                }
                if (mood && p.needs != null && p.needs.mood != null) o["mood"] = Math.Round(p.needs.mood.CurLevelPercentage * 100) + "%";
                if (p.MentalStateDef != null) o["mental_state"] = p.MentalStateDef.label;
                if (p.jobs != null && p.jobs.curDriver != null) o["doing"] = Plain(p.jobs.curDriver.GetReport());
                if (p.Faction != null && p.Faction != Faction.OfPlayer) o["faction"] = Plain(p.Faction.Name);
                else if (p.IsColonist) o["colonist"] = true;
            }
            catch (Exception) { }
            return o;
        }

        public static JArray Nearby(Map map, IntVec3 at, params Thing[] skip)
        {
            var a = new JArray();
            try
            {
                if (map == null || !at.IsValid) return a;
                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (a.Count >= 5) break;
                    if (p.RaceProps == null || !p.RaceProps.Humanlike || skip.Contains(p) || p.Position.DistanceTo(at) > 9f) continue;
                    a.Add(p.LabelShort);
                }
            }
            catch (Exception) { }
            return a;
        }

        public static string RoomLabel(Thing t)
        {
            try
            {
                var room = t != null && t.Spawned ? t.GetRoom() : null;
                return room != null && room.Role != null && !room.PsychologicallyOutdoors ? room.Role.label : "";
            }
            catch (Exception) { return ""; }
        }

        static bool Minor(Pawn p) { return p != null && p.RaceProps != null && p.RaceProps.Humanlike && p.ageTracker != null && p.ageTracker.AgeBiologicalYears < 18; }

        public static void NoteDeath(Pawn p, DamageInfo? dinfo, Hediff culprit)
        {
            try
            {
                if (p == null || p.RaceProps == null || !p.RaceProps.Humanlike) return;
                var o = new JObject();
                if (dinfo.HasValue)
                {
                    var d = dinfo.Value;
                    if (d.Def != null) o["damage"] = d.Def.label;
                    if (d.Instigator != null) o["by"] = Plain(d.Instigator.LabelShort);
                    if (d.Weapon != null) o["weapon"] = d.Weapon.label;
                }
                if (culprit != null) o["cause"] = Plain(culprit.LabelCap);
                if (p.jobs != null && p.jobs.curDriver != null) o["was_doing"] = Plain(p.jobs.curDriver.GetReport());
                if (p.Spawned) o["where"] = RoomLabel(p);
                o["mood_at_death"] = p.needs != null && p.needs.mood != null ? Math.Round(p.needs.mood.CurLevelPercentage * 100) + "%" : "";
                if (!Deaths.ContainsKey(p.thingIDNumber)) DeathOrder.Enqueue(p.thingIDNumber);
                Deaths[p.thingIDNumber] = o;
                while (DeathOrder.Count > 300) Deaths.Remove(DeathOrder.Dequeue());
            }
            catch (Exception) { }
        }

        public static JObject DeathOf(Pawn p)
        {
            JObject o;
            return p != null && Deaths.TryGetValue(p.thingIDNumber, out o) ? o : new JObject { ["cause"] = "not recorded" };
        }

        // ---- kinds -----------------------------------------------------------------------------------------------
        public static string ArtId(CompArt a) { return "art:" + a.parent.thingIDNumber; }

        public static void QueueArt(CompArt art, Pawn maker, Thing related)
        {
            if (art == null || art.parent == null || !art.Active) return;
            string id = ArtId(art);
            if (!Wants(id, TextKinds.Art)) return;
            Queue(id, TextKinds.Art, art, () =>
            {
                var t = art.parent;
                var map = t.MapHeld ?? (maker != null ? maker.MapHeld : null);
                var o = new JObject { ["piece"] = t.def.label, ["quality"] = ArtSupport.QualityLabel(t) };
                if (t.Stuff != null) o["material"] = ArtText.Material(t.Stuff.label);
                var tale = ArtSupport.TaleObject(art);
                if (tale != null) o["tale_depicted"] = tale;
                if (maker != null)
                {
                    o["maker"] = PawnInfo(maker);
                    if (maker.skills != null) o["maker"]["artistic_skill"] = maker.skills.GetSkill(SkillDefOf.Artistic).Level;
                    o["made_in"] = RoomLabel(maker);
                    o["nearby"] = Nearby(maker.MapHeld, maker.PositionHeld, maker);
                }
                else o["origin"] = "not made in the colony (found, traded, or older than the colony's records)";
                var corpse = related as Corpse;
                if (corpse != null && corpse.InnerPawn != null)
                {
                    o["for_the_grave_of"] = PawnInfo(corpse.InnerPawn, false);
                    o["death"] = DeathOf(corpse.InnerPawn);
                }
                o["colony_now"] = Colony(map);
                if (Minor(maker) || (corpse != null && Minor(corpse.InnerPawn))) o["minor_involved"] = true;
                return o;
            }, () => art.GenerateImageDescription().ToString(), () => art.Title);
        }

        public static string GraveId(Building_Grave g, Pawn dead) { return "grave:" + g.thingIDNumber + ":" + dead.thingIDNumber; }

        public static void QueueGrave(Building_Grave g)
        {
            if (g == null || !g.HasCorpse || g.Corpse == null || g.Corpse.InnerPawn == null) return;
            var art = g.TryGetComp<CompArt>();
            if (art != null && art.Active) return;            // a sarcophagus: its art plate carries the text
            var dead = g.Corpse.InnerPawn;
            if (dead.RaceProps == null || !dead.RaceProps.Humanlike) return;
            Queue(GraveId(g, dead), TextKinds.Grave, g, () => new JObject
            {
                ["buried"] = PawnInfo(dead, false),
                ["death"] = DeathOf(dead),
                ["mourners_nearby"] = Nearby(g.Map, g.Position, dead),
                ["grave"] = g.def.label,
                ["colony_now"] = Colony(g.Map),
                ["minor_involved"] = Minor(dead)
            }, () => "");
        }

        public static string BookId(Book b) { return "book:" + b.thingIDNumber; }

        public static void QueueBook(Book b, Pawn author)
        {
            if (b == null) return;
            Queue(BookId(b), TextKinds.Book, b, () =>
            {
                var o = new JObject { ["book"] = b.def.label, ["quality"] = ArtSupport.QualityLabel(b) };
                if (author != null)
                {
                    o["author"] = PawnInfo(author);
                    o["written_in"] = RoomLabel(author);
                }
                else o["origin"] = "not written in the colony";
                o["colony_now"] = Colony(b.MapHeld ?? (author != null ? author.MapHeld : null));
                return o;
            }, () => b.DescriptionFlavor, () => b.Title);
        }

        public static string LogId(LogEntry e) { return "log:" + e.LogID; }

        static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, InteractionDef> IntDef = AccessTools.FieldRefAccess<PlayLogEntry_Interaction, InteractionDef>("intDef");
        static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, Pawn> Initiator = AccessTools.FieldRefAccess<PlayLogEntry_Interaction, Pawn>("initiator");
        static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, Pawn> Recipient = AccessTools.FieldRefAccess<PlayLogEntry_Interaction, Pawn>("recipient");
        static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, List<RulePackDef>> Extra = AccessTools.FieldRefAccess<PlayLogEntry_Interaction, List<RulePackDef>>("extraSentencePacks");
        static readonly AccessTools.FieldRef<BattleLogEntry_MeleeCombat, RulePackDef> MeleeRule = AccessTools.FieldRefAccess<BattleLogEntry_MeleeCombat, RulePackDef>("ruleDef");
        static readonly AccessTools.FieldRef<BattleLogEntry_MeleeCombat, Pawn> MeleeBy = AccessTools.FieldRefAccess<BattleLogEntry_MeleeCombat, Pawn>("initiator");
        static readonly AccessTools.FieldRef<BattleLogEntry_MeleeCombat, Pawn> MeleeAt = AccessTools.FieldRefAccess<BattleLogEntry_MeleeCombat, Pawn>("recipientPawn");
        static readonly AccessTools.FieldRef<BattleLogEntry_MeleeCombat, ThingDef> MeleeAtThing = AccessTools.FieldRefAccess<BattleLogEntry_MeleeCombat, ThingDef>("recipientThing");
        static readonly AccessTools.FieldRef<BattleLogEntry_RangedFire, Pawn> ShotBy = AccessTools.FieldRefAccess<BattleLogEntry_RangedFire, Pawn>("initiatorPawn");
        static readonly AccessTools.FieldRef<BattleLogEntry_RangedFire, ThingDef> ShotByThing = AccessTools.FieldRefAccess<BattleLogEntry_RangedFire, ThingDef>("initiatorThing");
        static readonly AccessTools.FieldRef<BattleLogEntry_RangedFire, Pawn> ShotAt = AccessTools.FieldRefAccess<BattleLogEntry_RangedFire, Pawn>("recipientPawn");
        static readonly AccessTools.FieldRef<BattleLogEntry_RangedFire, ThingDef> ShotAtThing = AccessTools.FieldRefAccess<BattleLogEntry_RangedFire, ThingDef>("recipientThing");
        static readonly AccessTools.FieldRef<BattleLogEntry_RangedFire, ThingDef> ShotWeapon = AccessTools.FieldRefAccess<BattleLogEntry_RangedFire, ThingDef>("weaponDef");

        /// <summary>The two sides of a combat line: (attacker pawn, attacker name, target pawn, target name).</summary>
        static void Sides(LogEntry e, out Pawn a, out string an, out Pawn b, out string bn)
        {
            a = b = null; an = bn = null;
            var shot = e as BattleLogEntry_RangedFire;
            if (shot != null)
            {
                a = ShotBy(shot); b = ShotAt(shot);
                an = a != null ? a.LabelShort : ShotByThing(shot) != null ? ShotByThing(shot).label : null;
                bn = b != null ? b.LabelShort : ShotAtThing(shot) != null ? ShotAtThing(shot).label : null;
                return;
            }
            var melee = e as BattleLogEntry_MeleeCombat;
            if (melee != null)
            {
                a = MeleeBy(melee); b = MeleeAt(melee);
                an = a != null ? a.LabelShort : null;
                bn = b != null ? b.LabelShort : MeleeAtThing(melee) != null ? MeleeAtThing(melee).label : null;
            }
        }

        /// <summary>The log kind of an entry, or null when ours must not replace it: romance (age gate), entries whose game
        /// text carries an outcome sentence or wound detail, and anything else unknown.</summary>
        public static string LogKind(LogEntry e)
        {
            var inter = e as PlayLogEntry_Interaction;
            if (inter != null)
            {
                var def = IntDef(inter);
                if (def == null || TextLogRules.Unsafe(def.defName)) return null;
                var extra = Extra(inter);
                return extra != null && extra.Count > 0 ? null : TextKinds.Social;
            }
            if (e is BattleLogEntry_RangedFire) return TextKinds.Combat;
            var melee = e as BattleLogEntry_MeleeCombat;
            if (melee != null) { var r = MeleeRule(melee); return r != null && TextLogRules.MeleeOk(r.defName) ? TextKinds.Combat : null; }
            return null;
        }

        public static Dictionary<string, string> LogNames(LogEntry e)
        {
            var d = new Dictionary<string, string>();
            var inter = e as PlayLogEntry_Interaction;
            if (inter != null)
            {
                if (Initiator(inter) != null) d["INITIATOR"] = Initiator(inter).LabelShort;
                if (Recipient(inter) != null) d["RECIPIENT"] = Recipient(inter).LabelShort;
                return d;
            }
            Pawn a, b; string an, bn;
            Sides(e, out a, out an, out b, out bn);
            if (an != null) d["INITIATOR"] = an;
            if (bn != null) d["RECIPIENT"] = bn;
            return d;
        }

        public static void QueueLog(LogEntry e)
        {
            if (e == null) return;
            string id = LogId(e);
            string kind = LogKind(e);
            if (kind == null || !Wants(id, kind)) return;
            Queue(id, kind, e, () =>
            {
                var o = new JObject();
                var inter = e as PlayLogEntry_Interaction;
                Pawn a = null, b = null;
                if (inter != null)
                {
                    a = Initiator(inter); b = Recipient(inter);
                    o["interaction"] = IntDef(inter).label;
                }
                else
                {
                    string an, bn;
                    Sides(e, out a, out an, out b, out bn);
                    var melee = e as BattleLogEntry_MeleeCombat;
                    o["attack"] = melee != null ? "melee, " + MeleeRule(melee).defName.Replace("Combat_", "").ToLowerInvariant() : "ranged shot";
                    var shot = e as BattleLogEntry_RangedFire;
                    if (shot != null && ShotWeapon(shot) != null) o["weapon"] = ShotWeapon(shot).label;
                    if (a == null && an != null) o["attacker"] = an;
                    if (b == null && bn != null) o["target"] = bn;
                }
                if (a != null) o["initiator"] = PawnInfo(a);
                if (b != null) o["recipient"] = PawnInfo(b);
                try
                {
                    if (a != null && b != null && a.relations != null && b.RaceProps.Humanlike && a.RaceProps.Humanlike)
                    {
                        o["opinion_of_each_other"] = a.relations.OpinionOf(b) + " / " + b.relations.OpinionOf(a);
                        var rel = PawnRelationUtility.GetMostImportantRelation(a, b);
                        if (rel != null) o["relation"] = rel.GetGenderSpecificLabel(b);
                    }
                }
                catch (Exception) { }
                if (a != null) o["where"] = RoomLabel(a);
                o["colony_now"] = Colony(a != null ? a.MapHeld : null);
                if (Minor(a) || Minor(b)) o["minor_involved"] = true;
                return o;
            }, () => e.ToGameStringFromPOV(null, false));
        }

        /// <summary>Puts a stored text on its instance when it is alive in this session; else the read hooks apply it.</summary>
        sealed class Applier : ITextApplier
        {
            public string Apply(TextEntry entry)
            {
                var o = LiveOf(entry.Id);
                if (o == null) return "pending";
                try { PostToChat(o, entry); } catch (Exception) { }   // the chat log line never blocks applying
                try
                {
                    var book = o as Book;
                    if (book != null) { ApplyBook(book, entry); return null; }
                    var log = o as LogEntry;
                    if (log != null) { ResetLog(log); return null; }
                    return null;    // art and graves read the desk directly
                }
                catch (Exception e) { Log.Warning("[RimWorldLLM] text apply " + entry.Id + ": " + e.Message); return "failed"; }
            }
        }

        /// <summary>One Story line in the chat panel per stored art/grave/book text.</summary>
        static void PostToChat(object o, TextEntry e)
        {
            if (VivariumGame.State == null) return;
            string subject = null;
            var comp = o as CompArt ?? (o as ThingWithComps)?.GetComp<CompArt>();
            if (e.Kind == TextKinds.Art && comp != null) subject = Plain(comp.AuthorName);
            var grave = o as Building_Grave;
            if (e.Kind == TextKinds.Grave && grave != null && grave.Corpse != null && grave.Corpse.InnerPawn != null) subject = grave.Corpse.InnerPawn.LabelShort;
            ChatFeed.Written(VivariumGame.State.Chat, VivariumGame.NowTick, e.Kind, subject, e.Title, e.Text);
        }

        static readonly AccessTools.FieldRef<Book, string> BookTitle = AccessTools.FieldRefAccess<Book, string>("title");
        static readonly AccessTools.FieldRef<Book, string> BookFlavor = AccessTools.FieldRefAccess<Book, string>("descriptionFlavor");
        static readonly AccessTools.FieldRef<Book, string> BookDesc = AccessTools.FieldRefAccess<Book, string>("description");

        /// <summary>Writes the book's own fields (the game saves them), then rebuilds its full description.</summary>
        public static void ApplyBook(Book b, TextEntry e)
        {
            MarkApplied(e.Id);
            if (!string.IsNullOrEmpty(e.Title)) BookTitle(b) = e.Title;
            BookFlavor(b) = e.Text;
            try { BookDesc(b) = (string)AccessTools.Method(typeof(Book), "GenerateFullDescription").Invoke(b, null); }
            catch (Exception) { BookDesc(b) = e.Text; }
        }

        public static void ResetLog(LogEntry e)
        {
            try { AccessTools.Method(typeof(LogEntry), "ResetCache").Invoke(e, null); } catch (Exception) { }
        }
    }

    // ---- triggers: when a thing is made -------------------------------------------------------------------------
    // Art made by a colonist: JustCreatedBy runs after the art is initialised, with the maker.
    [HarmonyPatch(typeof(CompArt), nameof(CompArt.JustCreatedBy))]
    public static class Patch_TextArtMade
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance, Pawn pawn)
        {
            try { TextHook.QueueArt(__instance, pawn, null); } catch (Exception) { }
        }
    }

    // A sarcophagus plate is initialised with the corpse when the grave fills: queue it with the dead.
    [HarmonyPatch(typeof(CompArt), "InitializeArtInternal")]
    public static class Patch_TextArtInit
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance, Thing relatedThing)
        {
            try { if (relatedThing is Corpse) TextHook.QueueArt(__instance, null, relatedThing); } catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Building_Grave), nameof(Building_Grave.TryAcceptThing))]
    public static class Patch_TextGraveFilled
    {
        [HarmonyPostfix]
        public static void Postfix(Building_Grave __instance, Thing thing, bool __result)
        {
            try { if (__result && thing is Corpse) TextHook.QueueGrave(__instance); } catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_TextDeath
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            try { TextHook.NoteDeath(__instance, dinfo, exactCulprit); } catch (Exception) { }
        }
    }

    // Written by a colonist: queue with the author. Books made for traders and maps (no author) wait until first shown.
    [HarmonyPatch(typeof(Book), nameof(Book.GenerateBook))]
    public static class Patch_TextBookMade
    {
        [HarmonyPostfix]
        public static void Postfix(Book __instance, Pawn author)
        {
            try { if (author != null) TextHook.QueueBook(__instance, author); } catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(PlayLog), nameof(PlayLog.Add))]
    public static class Patch_TextPlayLog
    {
        [HarmonyPostfix]
        public static void Postfix(LogEntry entry)
        {
            try { TextHook.QueueLog(entry); } catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(BattleLog), nameof(BattleLog.Add))]
    public static class Patch_TextBattleLog
    {
        [HarmonyPostfix]
        public static void Postfix(LogEntry entry)
        {
            try { TextHook.QueueLog(entry); } catch (Exception) { }
        }
    }

    // ---- reads: ours once written, else the game's (and a first-shown queue for anything missed) ----------------
    [HarmonyPatch(typeof(CompArt), nameof(CompArt.GenerateImageDescription))]
    public static class Patch_TextArtDescription
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance, ref TaggedString __result)
        {
            try
            {
                if (__instance.parent == null) return;
                var e = TextHook.Text(TextHook.ArtId(__instance));
                if (e != null) __result = e.Text;
            }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(CompArt), nameof(CompArt.Title), MethodType.Getter)]
    public static class Patch_TextArtTitle
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance, ref string __result)
        {
            try
            {
                if (__instance.parent == null) return;
                var e = TextHook.Text(TextHook.ArtId(__instance));
                if (e != null && !string.IsNullOrEmpty(e.Title)) __result = e.Title;
            }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(CompArt), nameof(CompArt.GetDescriptionPart))]
    public static class Patch_TextArtShown
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance)
        {
            try { TextHook.QueueArt(__instance, null, null); } catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(CompArt), nameof(CompArt.CompInspectStringExtra))]
    public static class Patch_TextArtInspected
    {
        [HarmonyPostfix]
        public static void Postfix(CompArt __instance)
        {
            try { if (__instance.parent != null && TextHook.Wants(TextHook.ArtId(__instance), TextKinds.Art)) TextHook.QueueArt(__instance, null, null); } catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Building_Grave), nameof(Building_Grave.GetInspectString))]
    public static class Patch_TextGraveShown
    {
        [HarmonyPostfix]
        public static void Postfix(Building_Grave __instance, ref string __result)
        {
            try
            {
                if (!__instance.HasCorpse || __instance.Corpse == null || __instance.Corpse.InnerPawn == null) return;
                string id = TextHook.GraveId(__instance, __instance.Corpse.InnerPawn);
                var e = TextHook.Text(id);
                if (e != null)
                {
                    string line = UiUtil.T("RWL_Text_Epitaph", e.Text);
                    __result = string.IsNullOrEmpty(__result) ? line : __result.TrimEnd() + "\n" + line;
                }
                else if (TextHook.Wants(id, TextKinds.Grave)) TextHook.QueueGrave(__instance);
            }
            catch (Exception) { }
        }
    }

    // The info card and labels read a book's fields; a text written while the book was out of reach goes on at first read.
    [HarmonyPatch(typeof(Book), nameof(Book.DescriptionDetailed), MethodType.Getter)]
    public static class Patch_TextBookShown
    {
        [HarmonyPrefix]
        public static void Prefix(Book __instance)
        {
            try
            {
                string id = TextHook.BookId(__instance);
                var e = TextHook.Text(id);
                if (e != null) { if (TextHook.NeedsApply(id)) TextHook.ApplyBook(__instance, e); }
                else if (TextHook.Wants(id, TextKinds.Book)) TextHook.QueueBook(__instance, null);
            }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Book), nameof(Book.Title), MethodType.Getter)]
    public static class Patch_TextBookTitle
    {
        [HarmonyPostfix]
        public static void Postfix(Book __instance, ref string __result)
        {
            try
            {
                var e = TextHook.Text(TextHook.BookId(__instance));
                if (e != null && !string.IsNullOrEmpty(e.Title)) __result = e.Title;
            }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(LogEntry), nameof(LogEntry.ToGameStringFromPOV))]
    public static class Patch_TextLogShown
    {
        [HarmonyPostfix]
        public static void Postfix(LogEntry __instance, ref string __result)
        {
            try
            {
                string id = TextHook.LogId(__instance);
                var e = TextHook.Text(id);
                if (e != null)
                {
                    if (TextHook.LogKind(__instance) == null) return;     // a kind that became unsafe or off: the game's line
                    __result = TextTokens.Fill(e.Text, TextHook.LogNames(__instance));
                }
                else TextHook.QueueLog(__instance);
            }
            catch (Exception) { }
        }
    }
}
