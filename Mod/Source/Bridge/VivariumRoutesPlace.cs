using System;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // POST /act/place: buildings in a room, power, climate and ship parts. Shapes are checked here; the game hands
    // (IPlaceHands, set into PlacePure.Hands by the game assembly) do the rest.
    public static partial class VivariumRoutes
    {
        static void RegisterPlace(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/place", true, c => Act(c, vivarium, hands, "place", Place));
        }

        static Plan Place(JObject b)
        {
            var a = new PlaceArgs { Op = OneOf(b, "op", PlacePure.Ops) };
            a.What = OptStr(b, "what", 40);
            a.At = OptStr(b, "at", 60);
            string temp = OptStr(b, "temp", 20);
            if (temp != null)
            {
                a.Temp = temp.ToLowerInvariant();
                a.Degrees = PlacePure.Degrees(a.Temp);
                if (a.Degrees == null) throw new ArgError("temp must be one of " + string.Join(", ", PlacePure.TempWords));
            }
            string w = a.What == null ? null : a.What.ToLowerInvariant();
            if (a.Op == "furnish" && (w == "heater" || w == "cooler" || w == "battery")) { a.Op = w; a.What = null; w = null; }   // seen live: furnish what=cooler; the cooler op puts it in a wall and keeps the temp
            switch (a.Op)
            {
                case "furnish":
                    if (a.What == null || a.At == null) throw new ArgError("furnish needs what (a building like \"research bench\") and at (the room to put it in)");
                    break;
                case "generator":
                    if (w == null || Array.IndexOf(PlacePure.GeneratorKinds, w) < 0) throw new ArgError("generator needs what, one of " + string.Join(", ", PlacePure.GeneratorKinds));
                    a.What = w;
                    break;
                case "connect":
                    if (a.At == null) throw new ArgError("connect needs at (the room, or #id of the building, to run power to)");
                    break;
                case "heater":
                case "cooler":
                    if (a.At == null) throw new ArgError(a.Op + " needs at (the room to put it in)");
                    break;
                case "target":
                    if (a.At == null || a.Temp == null) throw new ArgError("target needs at (the room) and temp, one of " + string.Join(", ", PlacePure.TempWords));
                    break;
                case "ship":
                    if (w == "launch") throw new ArgError("the keeper launches the ship; the narrator cannot launch");
                    if (w == null || Array.IndexOf(PlacePure.ShipParts, w) < 0) throw new ArgError("ship needs what, one of " + string.Join(", ", PlacePure.ShipParts));
                    a.What = w;
                    break;
            }
            return new Plan
            {
                What = () => PlacePure.WhatText(a),
                GateAt = (v, tick) => NeedLeft(null, "blueprints", Peek(v, tick).Blueprints),
                RunLeft = (h, left) =>
                {
                    var ph = PlacePure.Hands == null ? null : PlacePure.Hands();
                    if (ph == null) return HandResult.No("the game is not ready for placement");
                    return ph.Place(a, left);
                }
            };
        }
    }
}
