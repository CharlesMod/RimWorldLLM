using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Vivarium phase 1 contract (docs/VIVARIUM.md). Verse-free: the routes, argument checks and the
// saved state live here; the game bodies sit behind IVivariumHands in Mod/Source/RimWorld.
namespace RimWorldLLM.Bridge
{
    /// <summary>What a hand did: ok with a short human note, or the reason it could not.</summary>
    public sealed class HandResult
    {
        public bool Ok;
        public string Note;    // ok: plain English past tense, the player reads it: "set Ana's Cooking to 1st"
        public string Reason;  // not ok: "Ana is incapable of Cooking"
        public string What;    // not ok: plain English of what was attempted ("set Ana's Cooking"), for "couldn't {What}: {Reason}"; may be null
        public JObject Extra;  // optional fields merged into the reply
        /// <summary>Phase 2: what the hand put into the world (blueprints, designations, zones, bills), for the
        /// per-cycle caps and the player's undo. Null or empty for hands that place nothing.</summary>
        public List<NarratorMark> Marks;

        public static HandResult Done(string note, JObject extra = null) { return new HandResult { Ok = true, Note = note, Extra = extra }; }
        public static HandResult No(string reason, string what = null) { return new HandResult { Ok = false, Reason = reason, What = what }; }
        public static HandResult Placed(string note, List<NarratorMark> marks, JObject extra = null) { return new HandResult { Ok = true, Note = note, Extra = extra, Marks = marks }; }
    }

    /// <summary>The game bodies of the hands. Game thread. Arguments are already checked for shape
    /// (ids positive, enums known, ranges valid) by VivariumRoutes; game-state checks belong here.</summary>
    public interface IVivariumHands
    {
        /// <summary>The colony digest (docs/BRIDGE-API.md "GET /digest").</summary>
        JObject Digest();
        /// <summary>pause: null = leave; speed: null = leave, else 1..4.</summary>
        HandResult SetTime(bool? pause, int? speed);
        /// <summary>work type (defName or label, case-insensitive) → priority 0..4 (0 = off).</summary>
        HandResult SetWork(int pawn, IDictionary<string, int> priorities);
        /// <summary>Hours fromHour..toHour-1 (wrapping past 24) get the assignment: anything, work, joy, sleep, meditate.</summary>
        HandResult SetSchedule(int pawn, int fromHour, int toHour, string assignment);
        /// <summary>kind: outfit, food, drug; name: the policy's label.</summary>
        HandResult SetPolicy(int pawn, string kind, string name);
        HandResult AssignBed(int pawn, int bed);
        /// <summary>project: defName or label.</summary>
        HandResult SetResearch(string project);
        /// <summary>choice: an option label, "#&lt;index&gt;" (0-based), or "dismiss".</summary>
        HandResult AnswerLetter(int letter, string choice);
        /// <summary>Unattended play: answer an open dialog or choice letter (a scanned DialogRecord id). option is the
        /// record's option index (-1 for naming); texts are the naming fields by name. Game thread.</summary>
        HandResult Choose(DialogRecord dialog, int option, IDictionary<string, string> texts);
        HandResult Draft(IList<int> pawns, bool drafted);
        /// <summary>order: move (x,z), attack (target thing id), hold (hold true/false).</summary>
        HandResult Order(int pawn, string order, int x, int z, int target, bool hold);

        // ---- phase 2: the builder's hands (docs/VIVARIUM.md "Phase 2"). Each returns HandResult.Placed with marks
        // for everything it put into the world, tagged so the player's undo can take it back. "left" is what the
        // per-cycle caps still allow; a hand refuses (with the reason) rather than go over it.

        /// <summary>Mine, chop, harvest, haul, deconstruct or cancel designations.</summary>
        HandResult Designate(DesignateArgs a, BuildBudget left);
        /// <summary>One blueprint of a thing, with stuff, at a cell / in a room / near the base.</summary>
        HandResult Build(BuildArgs a, BuildBudget left);
        /// <summary>A rectangle of wall blueprints with one door, at a given corner or at a site the hand finds.</summary>
        HandResult Room(RoomArgs a, BuildBudget left);
        /// <summary>A growing zone, stockpile or dumping stockpile.</summary>
        HandResult Zone(ZoneArgs a, BuildBudget left);
        /// <summary>Add or remove cells of the home area or an allowed area; optionally restrict pawns to it.</summary>
        HandResult Area(AreaArgs a, BuildBudget left);
        /// <summary>Add, remove, suspend or resume a workbench bill.</summary>
        HandResult Bill(BillArgs a, BuildBudget left);
        /// <summary>GET /benches: every workbench with its bills and the recipes it can take.</summary>
        JObject Benches();
        /// <summary>Takes back what the marks recorded, where it still exists: cancels blueprints and frames, removes
        /// designations, deletes zones/zone cells/area cells and bills. Finished buildings stay.</summary>
        HandResult Undo(IList<NarratorMark> marks);

        // ---- phase 3: combat and defense (docs/VIVARIUM.md "Phase 3"). The CombatState holds the squad (the hand updates it)
        // and the last raid side; orders place nothing; defenses return blueprint marks like phase 2.

        /// <summary>GET /combat: threats, fighters, others, downed, fires, defenses in words, positions. "active" false when quiet.</summary>
        JObject Combat(CombatState state);
        /// <summary>A squad order: draft, move (to a named position the hand finds), hold, fire_at_will, focus, retreat, rescue,
        /// extinguish, flee, undraft.</summary>
        HandResult Squad(SquadArgs a, CombatState state);
        /// <summary>Turrets, sandbags, barricades, a killbox, traps or a wall segment, as blueprints within the cycle caps.</summary>
        HandResult Defend(DefendArgs a, BuildBudget left, CombatState state);
        /// <summary>The fight's speed rule. restore false: set speed (1) and return Extra "before" (the speed it was, 1..4); refuses
        /// when paused or already at 1x. restore true: put speed (the remembered one, 2..4) back, refusing when the game is not at 1x (the
        /// player changed it).</summary>
        HandResult CombatSpeed(int speed, bool restore);
    }

}
