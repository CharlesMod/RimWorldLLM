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
        public string Note;    // ok: "set Cooking 1 for Ana"
        public string Reason;  // not ok: "Ana is incapable of Cooking"
        public JObject Extra;  // optional fields merged into the reply

        public static HandResult Done(string note, JObject extra = null) { return new HandResult { Ok = true, Note = note, Extra = extra }; }
        public static HandResult No(string reason) { return new HandResult { Ok = false, Reason = reason }; }
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
        HandResult Draft(IList<int> pawns, bool drafted);
        /// <summary>order: move (x,z), attack (target thing id), hold (hold true/false).</summary>
        HandResult Order(int pawn, string order, int x, int z, int target, bool hold);
    }

}
