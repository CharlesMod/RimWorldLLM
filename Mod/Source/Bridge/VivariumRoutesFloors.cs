using System;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // POST /act/floor: lay a floor in an existing room (a terrain blueprint per cell), or mark its rough stone floor for smoothing.
    // Same Act pipeline as the rest; shapes are checked here, the room, the kind and the caps are decided by the hands (FloorsPure).
    public static partial class VivariumRoutes
    {
        public static void RegisterFloors(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/floor", true, c => Act(c, vivarium, hands, "floor", Floor));
        }

        static Plan Floor(JObject b)
        {
            var a = new FloorArgs { Room = Int(b, "room", 1, int.MaxValue), Kind = Str(b, "kind", 1, FloorsPure.MaxWord) };
            return new Plan
            {
                What = () => (FloorsPure.IsSmooth(a.Kind) ? "smooth" : "lay " + a.Kind) + " in room " + a.Room,
                RunLeft = (h, left) =>
                {
                    var f = h as IFloorHands;
                    return f != null ? f.Floor(a, left) : HandResult.No("floor hands are not available");
                }
            };
        }
    }
}
