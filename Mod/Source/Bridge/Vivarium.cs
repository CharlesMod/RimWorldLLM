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

    /// <summary>Mod settings the steward and the time hand obey; the game side refreshes it from RimWorldLLMSettings.</summary>
    public sealed class VivariumSettings
    {
        public bool MayPauseForEmergencies = true;
        public bool ControlsSpeed = false;
        public float HeartbeatHours = 6f;
        public JObject ToJson() { throw new NotImplementedException(); }
    }

    /// <summary>One chat line. Who: player, narrator, note, colonist. To: a pawn id for "@Name" lines and colonist answers, else 0.</summary>
    public sealed class ChatEntry
    {
        public int Seq;
        public int Tick;
        public string Who;
        public int To;
        public string Name;   // speaker or addressee label for display ("Ana")
        public string Text;
        public JObject ToJson() { throw new NotImplementedException(); }
    }

    /// <summary>The conversation, capped (300). Game thread.</summary>
    public sealed class ChatLog
    {
        public const int Max = 300;
        public int LastSeq { get { throw new NotImplementedException(); } }
        public IReadOnlyList<ChatEntry> Entries { get { throw new NotImplementedException(); } }
        /// <summary>Appends; text trimmed and capped at 500 chars; returns the entry (null for empty text).</summary>
        public ChatEntry Add(string who, string text, int tick, int to = 0, string name = null) { throw new NotImplementedException(); }
        public JObject Since(int seq) { throw new NotImplementedException(); }   // {entries:[...], next_seq}
        public JArray Export() { throw new NotImplementedException(); }
        public void Import(JArray saved) { throw new NotImplementedException(); }
        public void Clear() { throw new NotImplementedException(); }
    }

    /// <summary>The goals board (docs/BRIDGE-API.md "GET /goals"). Game thread. Rev bumps on every change.</summary>
    public sealed class GoalsBoard
    {
        public int Rev { get { throw new NotImplementedException(); } }
        public JObject ToJson() { throw new NotImplementedException(); }
        /// <summary>Replaces goals/plan/orders from a PUT body, appends log_add; returns null or an error detail.</summary>
        public string Replace(JObject body, int tick) { throw new NotImplementedException(); }
        /// <summary>Player edits from the UI.</summary>
        public void AddGoal(string text, string horizon) { throw new NotImplementedException(); }
        public void SetGoalStatus(string id, string status) { throw new NotImplementedException(); }
        public void RemoveGoal(string id) { throw new NotImplementedException(); }
        public void SetPlan(string text) { throw new NotImplementedException(); }
        public void AddOrder(string text) { throw new NotImplementedException(); }
        public void RemoveOrder(string id) { throw new NotImplementedException(); }
        public void Log(string text, int tick) { throw new NotImplementedException(); }
        public JObject Export() { throw new NotImplementedException(); }
        public void Import(JObject saved) { throw new NotImplementedException(); }
        public void Clear() { throw new NotImplementedException(); }
    }

    /// <summary>Every action a hand took (honesty log), capped (500). Game thread.</summary>
    public sealed class ActionLog
    {
        public int LastSeq { get { throw new NotImplementedException(); } }
        public void Add(string verb, string note, int tick, string by) { throw new NotImplementedException(); }
        public JObject Since(int seq) { throw new NotImplementedException(); }   // {actions:[...], next_seq}
        public JArray Export() { throw new NotImplementedException(); }
        public void Import(JArray saved) { throw new NotImplementedException(); }
        public void Clear() { throw new NotImplementedException(); }
    }

    /// <summary>All vivarium state for one game. BridgeRuntime holds one; the GameComponent saves it.</summary>
    public sealed class Vivarium
    {
        public bool Playing;
        public bool PausedByNarrator;   // set when the steward paused for an emergency; cleared on any unpause
        public readonly VivariumSettings Settings = new VivariumSettings();
        public readonly ChatLog Chat = new ChatLog();
        public readonly GoalsBoard Goals = new GoalsBoard();
        public readonly ActionLog Actions = new ActionLog();
        public JObject StatusJson() { throw new NotImplementedException(); }      // GET /vivarium body
        public JObject Export() { throw new NotImplementedException(); }          // whole state for the save
        public void Import(JObject saved) { throw new NotImplementedException(); }
        public void Clear() { throw new NotImplementedException(); }
    }

    /// <summary>Registers the vivarium routes. hands may be null in tests of the non-game routes.</summary>
    public static class VivariumRoutes
    {
        public static void Register(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands) { throw new NotImplementedException(); }
    }
}
