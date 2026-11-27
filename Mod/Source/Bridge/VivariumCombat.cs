using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Vivarium phase 3 contract (docs/VIVARIUM.md "Phase 3: combat and defense"). Verse-free: the argument records for
// /act/squad and /act/defend, the combat status the game refreshes every 250 ticks, the squad and the speed memory.
namespace RimWorldLLM.Bridge
{
    /// <summary>POST /act/squad.</summary>
    public sealed class SquadArgs
    {
        public static readonly string[] Ops = { "draft", "move", "hold", "fire_at_will", "focus", "retreat", "rescue", "extinguish", "flee", "undraft" };
        public static readonly string[] Positions = { "cover", "chokepoint", "door", "wall_n", "wall_e", "wall_s", "wall_w", "home", "inside" };
        public string Op;
        public List<int> Pawns = new List<int>();   // empty = the squad (draft: every capable fighter)
        public string At;                            // one of Positions, or null
        public int Room;                             // door/inside: a room id from the digest
        public int? X, Z;                            // an exact cell instead of At
        public int Target;                           // focus: a threat group id or a hostile pawn id
        public float Below;                          // retreat: the squad's auto-retreat health (0 = leave)
    }

    /// <summary>POST /act/defend.</summary>
    public sealed class DefendArgs
    {
        public static readonly string[] Kinds = { "turret", "sandbags", "barricade", "killbox", "traps", "wall" };
        public static readonly string[] Sides = { "n", "e", "s", "w", "threat" };
        public static readonly string[] Ats = { "chokepoint", "entrance" };
        public string Kind;
        public string Side = "threat";
        public string At;          // chokepoint | entrance | null
        public int? X, Z;
        public int Count;          // turret 1..4 (1), traps 1..8 (4)
        public int Length = 7;     // sandbags, barricade, wall 1..20
        public string Stuff;
    }

    /// <summary>The fight as the game last saw it (refreshed every 250 ticks), the squad (saved) and the speed the narrator
    /// slowed from (saved), for GET /vivarium, GET /combat and the combat time rule.</summary>
    public sealed class CombatState
    {
        public bool Active;          // hostiles on the map (or fire in the home area, or a downed colonist)
        public int Hostiles;         // hostile pawns standing on the map
        public int SinceTick = -1;   // when Active turned on
        public string LastSide;      // compass key the last raid came from ("n", "ne", ...), for defend side=threat
        public SquadState Squad = new SquadState();
        /// <summary>The speed (1..4) before the narrator slowed the game for a fight; 0 = it did not.</summary>
        public int SpeedBefore;

        /// <summary>Called by the game every 250 ticks. Returns true when Active flipped.</summary>
        public bool Update(bool active, int hostiles, int tick)
        {
            bool flip = active != Active;
            if (active && !Active) SinceTick = tick;
            if (!active) SinceTick = -1;
            Active = active; Hostiles = Math.Max(0, hostiles);
            return flip;
        }

        public JObject StatusJson()
        {
            return new JObject { ["active"] = Active, ["hostiles"] = Hostiles, ["since_tick"] = SinceTick };
        }

        public JObject SpeedRuleJson()
        {
            return new JObject { ["slowed"] = SpeedBefore > 0, ["before"] = SpeedBefore };
        }

        public JObject Export()
        {
            return new JObject { ["squad"] = Squad.ToJson(), ["speed_before"] = SpeedBefore, ["last_side"] = LastSide ?? "" };
        }

        public void Import(JObject o)
        {
            Clear();
            if (o == null) return;
            Squad = SquadState.FromJson(o["squad"]);
            int sb = VivJson.Int(o["speed_before"]);
            SpeedBefore = sb >= 1 && sb <= 4 ? sb : 0;
            string ls = VivJson.Str(o["last_side"]);
            LastSide = ls.Length > 0 ? ls : null;
        }

        public void Clear()
        {
            Active = false; Hostiles = 0; SinceTick = -1; LastSide = null; Squad = new SquadState(); SpeedBefore = 0;
        }
    }

    /// <summary>Plain-English text for the combat hands (Verse-free, tested).</summary>
    public static class CombatText
    {
        public static string PositionWords(string at, int room)
        {
            switch (at)
            {
                case "cover": return "cover facing the enemy";
                case "chokepoint": return "the chokepoint";
                case "door": return room != 0 ? "inside that room's door" : "inside the main door";
                case "wall_n": return "behind the north wall";
                case "wall_e": return "behind the east wall";
                case "wall_s": return "behind the south wall";
                case "wall_w": return "behind the west wall";
                case "home": return "the middle of the base";
                case "inside": return room != 0 ? "inside that room" : "indoors";
                default: return at ?? "there";
            }
        }

        public static string SideWord(string side)
        {
            switch (side)
            {
                case "n": return "north"; case "e": return "east"; case "s": return "south"; case "w": return "west";
                case "ne": return "north-east"; case "se": return "south-east"; case "sw": return "south-west"; case "nw": return "north-west";
                default: return side ?? "";
            }
        }

        /// <summary>A compass key folded to one of n/e/s/w (diagonals go to the nearer cardinal: ne → n, se → e, sw → s, nw → w).</summary>
        public static string Cardinal(string key)
        {
            switch (key)
            {
                case "n": case "ne": return "n";
                case "e": case "se": return "e";
                case "s": case "sw": return "s";
                case "w": case "nw": return "w";
                default: return null;
            }
        }

        /// <summary>"Ana, Bram and Cy" (at most 4 names, then "and N more").</summary>
        public static string Names(IList<string> names)
        {
            if (names == null || names.Count == 0) return "nobody";
            var l = new List<string>(names);
            string tail = "";
            if (l.Count > 4) { tail = " and " + (l.Count - 4) + " more"; l = l.GetRange(0, 4); return string.Join(", ", l) + tail; }
            if (l.Count == 1) return l[0];
            return string.Join(", ", l.GetRange(0, l.Count - 1)) + " and " + l[l.Count - 1];
        }

        public static string SquadWhat(string op, string at, int room)
        {
            switch (op)
            {
                case "draft": return "draft the squad";
                case "move": return "move the squad to " + PositionWords(at, room);
                case "hold": return "make the squad hold";
                case "fire_at_will": return "let the squad fire at will";
                case "focus": return "focus the squad's fire";
                case "retreat": return "pull the squad back";
                case "rescue": return "rescue the downed";
                case "extinguish": return "put out the fires";
                case "flee": return "send the non-fighters to safety";
                case "undraft": return "undraft everyone";
                default: return op;
            }
        }

        public static string DefendWhat(string kind)
        {
            switch (kind)
            {
                case "turret": return "plan turrets";
                case "sandbags": return "plan sandbags";
                case "barricade": return "plan barricades";
                case "killbox": return "plan a killbox";
                case "traps": return "plan traps";
                case "wall": return "plan a defensive wall";
                default: return "plan defenses";
            }
        }
    }
}
