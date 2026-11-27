using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Phase 3 routes: the fight (GET /combat), the squad's orders (POST /act/squad) and defense planning
    // (POST /act/defend). Shapes are checked here; game checks are in the hands (VivariumCombatHands.cs).
    public static partial class VivariumRoutes
    {
        static void RegisterCombat(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("GET", "/combat", true, c =>
            {
                var h = hands();
                if (h == null) return NoGame();
                var v = vivarium();
                var d = h.Combat(v.Combat);
                if (d == null) return NoGame();
                d["squad"] = v.Combat.Squad.ToJson();
                d["speed_rule"] = v.Combat.SpeedRuleJson();   // not "speed": every reply's envelope carries the game speed under that name
                return ApiResult.Success(d);
            });
            router.Add("POST", "/act/squad", true, c => Act(c, vivarium, hands, "squad", b => Squad(b, vivarium)));
            router.Add("POST", "/act/defend", true, c => Act(c, vivarium, hands, "defend", b => Defend(b, vivarium)));
        }

        static List<int> OptIds(JObject b, string f)
        {
            var ids = new List<int>();
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return ids;
            var arr = t as JArray;
            if (arr == null) throw new ArgError(f + " must be an array of pawn ids");
            if (arr.Count > 30) throw new ArgError(f + " must have at most 30 ids");
            foreach (var x in arr)
            {
                if (x.Type != JTokenType.Integer || x.Value<long>() < 1 || x.Value<long>() > int.MaxValue) throw new ArgError(f + " must be positive integer ids");
                if (!ids.Contains(x.Value<int>())) ids.Add(x.Value<int>());
            }
            return ids;
        }

        static float? OptFloat(JObject b, string f, float min, float max)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float) throw new ArgError(f + " must be a number");
            double d = t.Value<double>();
            if (double.IsNaN(d) || d < min || d > max) throw new ArgError(f + " must be " + min.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".." + max.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return (float)d;
        }

        // ---------------------------------------------------------------- /act/squad
        static Plan Squad(JObject b, Func<Vivarium> vivarium)
        {
            var a = new SquadArgs { Op = OneOf(b, "op", SquadArgs.Ops) };
            a.Pawns = OptIds(b, "pawns");
            a.At = OptOneOf(b, "at", SquadArgs.Positions, null);
            a.Room = OptInt(b, "room", 1, int.MaxValue) ?? 0;
            int? x = OptInt(b, "x", 0, MaxCoord), z = OptInt(b, "z", 0, MaxCoord);
            if ((x == null) != (z == null)) throw new ArgError("give both x and z, or neither");
            a.X = x; a.Z = z;
            a.Target = OptInt(b, "target", 1, int.MaxValue) ?? 0;
            a.Below = OptFloat(b, "below", 0.1f, 0.9f) ?? 0f;
            if (a.At != null && a.X != null) throw new ArgError("give a position (at) or a cell (x, z), not both");
            if (a.Op == "move" && a.At == null && a.X == null) throw new ArgError("move needs at (cover, chokepoint, door, wall_n/e/s/w, home, inside) or x and z");
            if (a.Op == "focus" && a.Target == 0) throw new ArgError("focus needs target: a threat group id or a hostile pawn id from GET /combat");
            if (a.Op == "retreat" && a.At == null && a.X == null) a.At = "inside";
            if (a.Room != 0 && a.At != "door" && a.At != "inside") throw new ArgError("room goes with at door or at inside");
            return new Plan
            {
                What = () => CombatText.SquadWhat(a.Op, a.At, a.Room),
                Run = h => h.Squad(a, vivarium().Combat)
            };
        }

        // ---------------------------------------------------------------- /act/defend
        static Plan Defend(JObject b, Func<Vivarium> vivarium)
        {
            var a = new DefendArgs { Kind = OneOf(b, "kind", DefendArgs.Kinds) };
            a.Side = OptOneOf(b, "side", DefendArgs.Sides, "threat");
            a.At = OptOneOf(b, "at", DefendArgs.Ats, null);
            int? x = OptInt(b, "x", 0, MaxCoord), z = OptInt(b, "z", 0, MaxCoord);
            if ((x == null) != (z == null)) throw new ArgError("give both x and z, or neither");
            a.X = x; a.Z = z;
            if (a.At != null && a.X != null) throw new ArgError("give at (chokepoint, entrance) or a cell (x, z), not both");
            if (a.Kind == "turret") a.Count = OptInt(b, "count", 1, 4) ?? 1;
            else if (a.Kind == "traps") a.Count = OptInt(b, "count", 1, 8) ?? 4;
            else if (b["count"] != null && b["count"].Type != JTokenType.Null) OptInt(b, "count", 1, 8);   // shape only; ignored
            a.Length = OptInt(b, "length", 1, 20) ?? 7;
            a.Stuff = OptStr(b, "stuff", 60);
            if (a.Kind == "killbox" && a.At == "chokepoint") throw new ArgError("a killbox goes in front of the main entrance (at entrance, a side, or the door's x and z)");
            return new Plan
            {
                What = () => CombatText.DefendWhat(a.Kind),
                GateAt = (v, tick) => NeedLeft(null, "blueprints", Peek(v, tick).Blueprints),
                RunLeft = (h, left) => h.Defend(a, left, vivarium().Combat)
            };
        }
    }
}
