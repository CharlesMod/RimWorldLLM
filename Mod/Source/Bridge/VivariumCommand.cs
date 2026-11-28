using System;
using System.Collections.Generic;
using System.Linq;

// Vivarium command tools: job, medical, prisoner, animal, social. The hands interface (separate from IVivariumHands)
// and the pure vocabulary. Verse-free; the game side is Mod/Source/RimWorld/VivariumCommand*Hands.cs.
namespace RimWorldLLM.Bridge
{
    /// <summary>One command, as the route hands it to the game. Targets are words (names, room labels, "#id"), never cells.</summary>
    public sealed class CmdArgs
    {
        public string Op;       // the tool's op (see CommandPure)
        public string Who;      // the pawn the order is for: colonist (job), patient, prisoner, animal, organiser. Empty = none
        public string Target;   // what to act on: name, room label/id, "#id" thing id, bench/bed id; "" = none
        public string Value;    // the second choice: surgery label, care level, interaction mode, training, area name, gathering; "" = none
        /// <summary>Set by the route gate from the player's settings: slaughter may take named or bonded animals.</summary>
        public bool AllowNamed;
    }

    /// <summary>The command hands. Game thread. A hands object that does not implement this answers "command hands are not available".
    /// Refusals say why (incapable, unreachable, forbidden, reserved, already doing it) and what to do instead.</summary>
    public interface IVivariumCommandHands
    {
        /// <summary>Op haul|rescue|tend|clean|repair|refuel|equip|wear|drop|consume|go|stay. Who = a colonist.</summary>
        HandResult Job(CmdArgs a);
        /// <summary>Op operate|rest|care. Who = the patient. Value = surgery label (operate; empty = list the choices) or care level (care).</summary>
        HandResult Medical(CmdArgs a);
        /// <summary>Op mode|free. Who = the prisoner. Value = a CommandPure.PrisonerModes word (mode).</summary>
        HandResult Prisoner(CmdArgs a);
        /// <summary>Op tame|train|slaughter|release|area. Who = the animal (a wild kind for tame, e.g. "wild boar"). Value = training label or area name.</summary>
        HandResult Animal(CmdArgs a);
        /// <summary>Op party|wedding|other. Who = the organiser or one of the couple. Value = a gathering label (other).</summary>
        HandResult Social(CmdArgs a);
    }

    public static class CommandPure
    {
        public const int MaxOrdersPerCycle = 20;
        public static readonly string[] JobOps = { "haul", "rescue", "tend", "clean", "repair", "refuel", "equip", "wear", "drop", "consume", "go", "stay" };
        /// <summary>Ops that need a target.</summary>
        public static readonly string[] JobNeedsTarget = { "haul", "rescue", "tend", "clean", "repair", "refuel", "equip", "wear", "drop", "consume", "go" };
        public static readonly string[] MedicalOps = { "operate", "rest", "care" };
        public static readonly string[] CareLevels = { "none", "no_meds", "herbal", "normal", "best" };
        public static readonly string[] PrisonerOps = { "mode", "free" };
        public static readonly string[] PrisonerModes = { "none", "chat", "recruit", "convert", "enslave", "release", "execute" };
        public static readonly string[] AnimalOps = { "tame", "train", "slaughter", "release", "area" };
        public static readonly string[] SocialOps = { "party", "wedding", "other" };

        public static string JobWhat(string op, string who, string target)
        {
            string w = string.IsNullOrEmpty(who) ? "a colonist" : who;
            switch (op)
            {
                case "haul": return "have " + w + " haul " + target;
                case "rescue": return "have " + w + " rescue " + target;
                case "tend": return "have " + w + " tend " + target;
                case "clean": return "have " + w + " clean " + target;
                case "repair": return "have " + w + " repair " + target;
                case "refuel": return "have " + w + " refuel " + target;
                case "equip": return "have " + w + " equip " + target;
                case "wear": return "have " + w + " wear " + target;
                case "drop": return "have " + w + " drop " + target;
                case "consume": return "have " + w + " use " + target;
                case "go": return "send " + w + " to " + target;
                default: return "have " + w + " stand still";
            }
        }

        public static string MedicalWhat(string op, string who, string value)
        {
            switch (op)
            {
                case "operate": return "plan " + (string.IsNullOrEmpty(value) ? "an operation" : value) + " for " + who;
                case "rest": return "put " + who + " to bed rest";
                default: return "set " + who + "'s medical care to " + (value ?? "").Replace('_', ' ');
            }
        }

        public static string PrisonerWhat(string op, string who, string value)
        {
            return op == "free" ? "free " + who : "set " + who + " to " + (value ?? "") ;
        }

        public static string AnimalWhat(string op, string who, string value)
        {
            switch (op)
            {
                case "tame": return "mark " + who + " to be tamed";
                case "train": return "train " + who + " in " + value;
                case "slaughter": return "mark " + who + " for slaughter";
                case "release": return "release " + who + " to the wild";
                default: return "keep " + who + " to the " + value + " area";
            }
        }

        public static string SocialWhat(string op, string who, string value)
        {
            switch (op)
            {
                case "party": return "throw a party";
                case "wedding": return "hold a wedding" + (string.IsNullOrEmpty(who) ? "" : " for " + who);
                default: return "start a " + (string.IsNullOrEmpty(value) ? "gathering" : value);
            }
        }
    }
}
