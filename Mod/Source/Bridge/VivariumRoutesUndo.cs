using System;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // POST /act/undo: the narrator takes back its own last n placements (1..5, default 1) through the same ledger and the same
    // UndoLast as the player's /undo. It goes through the Act pipeline (op_id, why, greentext "undid N narrator action(s): ...") but
    // adds no marks, so the undo itself has no ledger entry and charges no caps. The mind allows one call per turn.
    public static partial class VivariumRoutes
    {
        public const int MaxNarratorUndo = 5;

        public static void RegisterUndo(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/undo", true, c => Act(c, vivarium, hands, "undo", b => UndoPlan(b, vivarium)));
        }

        static Plan UndoPlan(JObject b, Func<Vivarium> vivarium)
        {
            int n = OptInt(b, "n", 1, MaxNarratorUndo) ?? 1;
            int tick = 0;
            return new Plan
            {
                What = () => "take back " + (n == 1 ? "its last placement" : "its last " + n + " placements"),
                GateAt = (v, t) => { tick = t; return null; },
                Run = h => UndoLast(vivarium(), h, n, tick, "narrator", false)
            };
        }
    }
}
