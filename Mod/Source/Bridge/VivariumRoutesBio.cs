using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Biotech routes. Same Act pipeline as the rest; shapes are checked here, game checks are in the hands.
    public static partial class VivariumRoutes
    {
        static readonly string[] FeedModes = { "never", "childcare", "urgent" };
        static readonly string[] HemogenOps = { "donate", "stop" };
        static readonly string[] XenogermOps = { "assemble", "implant" };
        const string NoBioHands = "Biotech hands are not available";

        public static void RegisterBio(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/mech_mode", true, c => Act(c, vivarium, hands, "mech_mode", MechMode));
            router.Add("POST", "/act/mech_group", true, c => Act(c, vivarium, hands, "mech_group", MechGroup));
            router.Add("POST", "/act/mech_gestate", true, c => Act(c, vivarium, hands, "mech_gestate", MechGestate));
            router.Add("POST", "/act/genepack", true, c => Act(c, vivarium, hands, "genepack", Genepack));
            router.Add("POST", "/act/xenogerm", true, c => Act(c, vivarium, hands, "xenogerm", Xenogerm));
            router.Add("POST", "/act/hemogen", true, c => Act(c, vivarium, hands, "hemogen", Hemogen));
            router.Add("POST", "/act/child_schedule", true, c => Act(c, vivarium, hands, "child_schedule", ChildSchedule));
            router.Add("POST", "/act/feed", true, c => Act(c, vivarium, hands, "feed", Feed));
            router.Add("POST", "/act/child_care", true, c => Act(c, vivarium, hands, "child_care", ChildCare));
        }

        static HandResult WithBio(IVivariumHands h, Func<IVivariumBioHands, HandResult> f)
        {
            var b = h as IVivariumBioHands;
            return b != null ? f(b) : HandResult.No(NoBioHands);
        }

        static int OptId(JObject b, string f) { return OptInt(b, f, 0, int.MaxValue) ?? 0; }

        static Plan MechMode(JObject b)
        {
            int group = Int(b, "group", 1, 12);
            int mechanitor = OptId(b, "mechanitor");
            string raw = Str(b, "mode", 1, 40);
            string def = BioPure.ModeDef(raw);
            if (def == null) throw new ArgError("mode must be one of " + string.Join(", ", BioPure.Modes));
            string mode = BioPure.ModeWord(def);
            return new Plan
            {
                What = () => "set control group " + group + " to " + mode,
                Run = h => WithBio(h, x => x.MechMode(mechanitor, group, mode))
            };
        }

        static Plan MechGroup(JObject b)
        {
            int mech = Pawn(b, "mech"), group = Int(b, "group", 1, 12);
            return new Plan
            {
                What = () => "move a mech to group " + group,
                Run = h => WithBio(h, x => x.MechGroup(mech, group))
            };
        }

        static Plan MechGestate(JObject b)
        {
            string kind = Str(b, "kind", 1, 40);
            int gestator = OptId(b, "gestator");
            int count = OptInt(b, "count", 1, 3) ?? 1;
            return new Plan
            {
                What = () => "start gestating a " + kind,
                Run = h => WithBio(h, x => x.MechGestate(kind, gestator, count, BioPure.DefaultHeadroom))
            };
        }

        static Plan Genepack(JObject b)
        {
            int pack = OptId(b, "genepack"), bank = OptId(b, "bank");
            return new Plan
            {
                What = () => "store genepacks",
                Run = h => WithBio(h, x => x.Genepack(pack, bank))
            };
        }

        static Plan Xenogerm(JObject b)
        {
            var a = new XenogermArgs { Op = OneOf(b, "op", XenogermOps) };
            if (a.Op == "assemble")
            {
                var t = b["genepacks"] as JArray;
                if (t == null) throw new ArgError("genepacks must be a list");
                if (t.Count < 1 || t.Count > 12) throw new ArgError("genepacks must have 1..12 entries");
                foreach (var e in t)
                {
                    if (e.Type != JTokenType.String && e.Type != JTokenType.Integer) throw new ArgError("each genepack must be an id or a label");
                    string s = e.Type == JTokenType.Integer ? e.Value<long>().ToString() : e.Value<string>().Trim();
                    if (s.Length < 1 || s.Length > 60) throw new ArgError("each genepack must be 1..60 chars");
                    a.Genepacks.Add(s);
                }
                a.Name = Str(b, "name", 1, 40);
            }
            else
            {
                a.Xenogerm = Pawn(b, "xenogerm");
                a.Pawn = Pawn(b, "pawn");
            }
            a.Plan = Str(b, "plan", 10, 200);
            bool implant = a.Op == "implant";
            return new Plan
            {
                What = () => implant ? "implant a xenogerm" : "assemble a xenogerm",
                Gate = v => implant && !v.Settings.MayImplantGenes ? HandResult.No("the player has not let the narrator implant genes") : null,
                Run = h => WithBio(h, x => x.Xenogerm(a))
            };
        }

        static Plan Hemogen(JObject b)
        {
            string op = OneOf(b, "op", HemogenOps);
            int prisoner = Pawn(b, "prisoner");
            return new Plan
            {
                What = () => "set up blood donation",
                Run = h => WithBio(h, x => x.Hemogen(op, prisoner))
            };
        }

        static Plan ChildSchedule(JObject b)
        {
            int pawn = Pawn(b, "pawn");
            string preset = OneOf(b, "preset", BioPure.ChildPresets);
            return new Plan
            {
                What = () => "set a child's schedule",
                Run = h => WithBio(h, x => x.ChildSchedule(pawn, preset, null))
            };
        }

        static Plan Feed(JObject b)
        {
            int baby = Pawn(b, "baby"), feeder = OptId(b, "feeder");
            string mode = OptOneOf(b, "mode", FeedModes, "childcare");
            return new Plan
            {
                What = () => "set who feeds a baby",
                Run = h => WithBio(h, x => x.Feed(baby, feeder, mode))
            };
        }

        static Plan ChildCare(JObject b)
        {
            int pawn = Pawn(b, "pawn");
            int priority = OptInt(b, "priority", 0, 4) ?? 1;
            return new Plan
            {
                What = () => "set child care",
                Run = h => WithBio(h, x => x.ChildCare(pawn, priority))
            };
        }
    }
}
