using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Command routes: job, medical, prisoner, animal, social. Same Act pipeline as the rest; shapes are checked here,
    // game checks (can reach, forbidden, reserved...) are in the hands. A command that leaves no mark costs one order
    // of the cycle's 20 (BuildBudget.Orders); designations (tame, slaughter) and surgery bills are charged as marks.
    public static partial class VivariumRoutes
    {
        const string NoCommandHands = "command hands are not available";

        public static void RegisterCommand(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/job", true, c => Act(c, vivarium, hands, "job", Job));
            router.Add("POST", "/act/medical", true, c => Act(c, vivarium, hands, "medical", Medical));
            router.Add("POST", "/act/prisoner", true, c => Act(c, vivarium, hands, "prisoner", Prisoner));
            router.Add("POST", "/act/animal", true, c => Act(c, vivarium, hands, "animal", Animal));
            router.Add("POST", "/act/social", true, c => Act(c, vivarium, hands, "social", Social));
        }

        static HandResult WithCommand(IVivariumHands h, Func<IVivariumCommandHands, HandResult> f)
        {
            var x = h as IVivariumCommandHands;
            return x != null ? f(x) : HandResult.No(NoCommandHands);
        }

        static string CmdStr(JObject b, string f, int max)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return "";
            return Str(b, f, 0, max);
        }

        /// <summary>The order cap: refuses when the cycle's orders are spent (and starts a new cycle when due).</summary>
        static HandResult OrderGate(Vivarium v, int tick)
        {
            v.Budget.Tick(tick);
            return v.Budget.Orders > 0 ? null
                : HandResult.No("that is " + v.Budget.MaxOrders + " orders this cycle; let the colony carry them out first");
        }

        /// <summary>A result with no marks used up one order.</summary>
        static void OrderSpent(Vivarium v, HandResult r)
        {
            if (r != null && r.Ok && (r.Marks == null || r.Marks.Count == 0)) v.Budget.Orders = Math.Max(0, v.Budget.Orders - 1);
        }

        static Plan Job(JObject b)
        {
            var a = new CmdArgs { Op = OneOf(b, "op", CommandPure.JobOps), Who = Str(b, "who", 1, 60), Target = CmdStr(b, "target", 80) };
            if (a.Target.Length == 0 && Array.IndexOf(CommandPure.JobNeedsTarget, a.Op) >= 0)
                throw new ArgError("target is required for " + a.Op);
            return new Plan
            {
                What = () => CommandPure.JobWhat(a.Op, a.Who, a.Target),
                GateAt = OrderGate,
                OnResult = OrderSpent,
                Run = h => WithCommand(h, x => x.Job(a))
            };
        }

        static Plan Medical(JObject b)
        {
            var a = new CmdArgs { Op = OneOf(b, "op", CommandPure.MedicalOps), Who = Str(b, "who", 1, 60), Value = CmdStr(b, "what", 80) };
            if (a.Op == "care")
            {
                a.Value = a.Value.ToLowerInvariant().Replace(' ', '_');
                if (Array.IndexOf(CommandPure.CareLevels, a.Value) < 0) throw new ArgError("what must be one of " + string.Join(", ", CommandPure.CareLevels));
            }
            return new Plan
            {
                What = () => CommandPure.MedicalWhat(a.Op, a.Who, a.Value),
                GateAt = OrderGate,
                OnResult = OrderSpent,
                Run = h => WithCommand(h, x => x.Medical(a))
            };
        }

        static Plan Prisoner(JObject b)
        {
            var a = new CmdArgs { Op = OneOf(b, "op", CommandPure.PrisonerOps), Who = Str(b, "who", 1, 60) };
            if (a.Op == "mode") a.Value = OneOf(b, "mode", CommandPure.PrisonerModes);
            bool execute = a.Value == "execute";
            return new Plan
            {
                What = () => CommandPure.PrisonerWhat(a.Op, a.Who, a.Value),
                Gate = v => execute && !v.Settings.MayExecutePrisoners ? HandResult.No("the player has not let the narrator execute prisoners; recruit, convert or release instead") : null,
                GateAt = OrderGate,
                OnResult = OrderSpent,
                Run = h => WithCommand(h, x => x.Prisoner(a))
            };
        }

        static Plan Animal(JObject b)
        {
            var a = new CmdArgs { Op = OneOf(b, "op", CommandPure.AnimalOps), Who = Str(b, "who", 1, 60), Value = CmdStr(b, "what", 60) };
            if ((a.Op == "train" || a.Op == "area") && a.Value.Length == 0)
                throw new ArgError("what is required for " + a.Op + (a.Op == "train" ? " (a training: obedience, release, rescue, haul)" : " (an allowed area name)"));
            return new Plan
            {
                What = () => CommandPure.AnimalWhat(a.Op, a.Who, a.Value),
                Gate = v => { a.AllowNamed = v.Settings.MaySlaughterPets; return null; },
                GateAt = OrderGate,
                OnResult = OrderSpent,
                Run = h => WithCommand(h, x => x.Animal(a))
            };
        }

        static Plan Social(JObject b)
        {
            var a = new CmdArgs { Op = OneOf(b, "op", CommandPure.SocialOps), Who = CmdStr(b, "who", 60), Value = CmdStr(b, "what", 60) };
            if (a.Op == "other" && a.Value.Length == 0) throw new ArgError("what is required for other (a gathering label)");
            return new Plan
            {
                What = () => CommandPure.SocialWhat(a.Op, a.Who, a.Value),
                GateAt = OrderGate,
                OnResult = OrderSpent,
                Run = h => WithCommand(h, x => x.Social(a))
            };
        }
    }
}
