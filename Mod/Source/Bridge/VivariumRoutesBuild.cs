using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Phase 2 routes: the builder's hands (/act/designate, build, room, zone, area, bill), GET /benches and the
    // player's undo (GET/POST /vivarium/undo). Shapes are checked here; game checks are in the hands.
    public static partial class VivariumRoutes
    {
        static readonly string[] DesignateKinds = { "mine", "chop", "harvest", "haul", "deconstruct", "cancel" };
        static readonly string[] ZoneKinds = { "grow", "stockpile", "dump" };
        static readonly string[] AreaKinds = { "home", "allowed" };
        static readonly string[] AreaOps = { "add", "remove" };
        static readonly string[] BillOps = { "add", "remove", "suspend", "resume" };
        public const int MaxCoord = 1000;
        /// <summary>How recent a player chat line must be for "player_asked" to count (one game hour).</summary>
        public const int AskedTicks = 2500;

        static void RegisterBuild(ApiRouter router, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            router.Add("POST", "/act/designate", true, c => Act(c, vivarium, hands, "designate", Designate));
            router.Add("POST", "/act/build", true, c => Act(c, vivarium, hands, "build", BuildOne));
            router.Add("POST", "/act/room", true, c => Act(c, vivarium, hands, "room", Room));
            router.Add("POST", "/act/zone", true, c => Act(c, vivarium, hands, "zone", Zone));
            router.Add("POST", "/act/area", true, c => Act(c, vivarium, hands, "area", Area));
            router.Add("POST", "/act/bill", true, c => Act(c, vivarium, hands, "bill", Bill));
            router.Add("GET", "/benches", true, c =>
            {
                var h = hands();
                if (h == null) return NoGame();
                var d = h.Benches();
                return d == null ? NoGame() : ApiResult.Success(d);
            });
            router.Add("GET", "/vivarium/undo", false, c => ApiResult.Success(vivarium().Ledger.ToJson(10)));
            router.Add("POST", "/vivarium/undo", true, c =>
            {
                int n;
                try { n = OptInt(c.Body, "n", 1, 20) ?? 1; }
                catch (ArgError e) { return Bad(e.Message); }
                var h = hands();
                if (h == null) return NoGame();
                var r = UndoLast(vivarium(), h, n, c.State.Tick, "player");
                if (!r.Ok) return ApiResult.Fail("refused", r.Reason);
                var body = new JObject { ["done"] = r.Note };
                if (r.Extra != null) foreach (var p in r.Extra.Properties()) if (body[p.Name] == null) body[p.Name] = p.Value;
                return ApiResult.Success(body);
            });
        }

        /// <summary>Takes back the narrator's last n world changes (game thread). Used by POST /vivarium/undo, the chat's
        /// "/undo" and the settings button. Refuses when there is nothing to undo; logs the undo as an action.</summary>
        public static HandResult UndoLast(Vivarium v, IVivariumHands h, int n, int tick, string by)
        {
            if (v.Ledger.Count == 0) return HandResult.No("the narrator has placed nothing that can be undone");
            var popped = v.Ledger.PopLast(Math.Max(1, n));
            var marks = new List<NarratorMark>();
            foreach (var e in popped) marks.AddRange(e.Marks);
            HandResult r;
            try { r = h.Undo(marks); }
            catch (Exception ex) { r = HandResult.No("the game refused: " + ex.Message); }
            if (r == null || !r.Ok)
            {
                v.Ledger.Restore(popped);
                return r ?? HandResult.No("the game gave no answer");
            }
            var notes = new List<string>();
            foreach (var e in popped) notes.Add(e.Note);
            string note = "undid " + popped.Count + " narrator action" + (popped.Count == 1 ? "" : "s") + (string.IsNullOrEmpty(r.Note) ? "" : ": " + r.Note);
            v.Actions.Add("undo", note, tick, by);
            var extra = r.Extra ?? new JObject();
            extra["undone"] = new JArray(notes.ToArray());
            extra["undoable"] = v.Ledger.Count;
            return HandResult.Done(note, extra);
        }

        // ---------------------------------------------------------------- shared shapes
        static string OptStr(JObject b, string f, int max)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return null;
            string s = Str(b, f, 1, max);
            return s;
        }

        static string OptOneOf(JObject b, string f, string[] allowed, string dflt)
        {
            var t = b[f];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            return OneOf(b, f, allowed);
        }

        /// <summary>x/z (both or neither), w/h (both or neither), radius, room. The caller says which combinations it takes.</summary>
        static Where ParseWhere(JObject b, bool box, bool radius)
        {
            var w = new Where();
            int? x = OptInt(b, "x", 0, MaxCoord), z = OptInt(b, "z", 0, MaxCoord);
            if ((x == null) != (z == null)) throw new ArgError("give both x and z, or neither");
            w.X = x; w.Z = z;
            if (box)
            {
                int? bw = OptInt(b, "w", 1, 60), bh = OptInt(b, "h", 1, 60);
                if ((bw == null) != (bh == null)) throw new ArgError("give both w and h, or neither");
                if (bw != null)
                {
                    if (x == null) throw new ArgError("a box needs its south-west corner x and z");
                    w.Box = new CellBox(x.Value, z.Value, bw.Value, bh.Value);
                }
            }
            if (radius)
            {
                w.Radius = OptInt(b, "radius", 1, 40) ?? 0;
                if (w.Radius > 0 && x == null) throw new ArgError("radius needs a centre x and z");
            }
            w.Room = OptInt(b, "room", 1, int.MaxValue) ?? 0;
            return w;
        }

        static HandResult NeedLeft(BuildBudget left, string what, int n)
        {
            return n > 0 ? null : HandResult.No("the narrator's cap on " + what + " for this cycle is used up (it refills next cycle)");
        }

        // ---------------------------------------------------------------- /act/designate
        static Plan Designate(JObject b)
        {
            var a = new DesignateArgs { Kind = OneOf(b, "kind", DesignateKinds) };
            a.At = ParseWhere(b, true, true);
            a.Ore = OptStr(b, "ore", 40);
            a.Target = OptInt(b, "target", 1, int.MaxValue) ?? 0;
            a.Max = OptInt(b, "max", 1, 200) ?? 40;
            a.PlayerAsked = OptBool(b, "player_asked") ?? false;
            if (a.Ore != null && a.Kind != "mine") throw new ArgError("ore is only for kind mine");
            if ((a.Kind == "deconstruct" || a.Kind == "cancel") && a.At.Box == null && a.Target == 0 && a.At.Radius == 0)
                throw new ArgError(a.Kind + " needs a box (x, z, w, h), a centre with radius, or a target thing id");
            return new Plan
            {
                GateAt = (v, tick) =>
                {
                    if (a.PlayerAsked && !PlayerAskedRecently(v, tick))
                        return HandResult.No("player_asked is set but the player has said nothing in the chat this past hour");
                    return a.Kind == "cancel" ? null : NeedLeft(v.Budget.Left(), "designations", Peek(v, tick).Designations);
                },
                RunLeft = (h, left) => h.Designate(a, left)
            };
        }

        static BuildBudget Peek(Vivarium v, int tick) { v.Budget.Tick(tick); return v.Budget; }

        /// <summary>True when the player wrote to the narrator (not to a colonist) in the last AskedTicks.</summary>
        public static bool PlayerAskedRecently(Vivarium v, int tick)
        {
            var es = v.Chat.Entries;
            for (int i = es.Count - 1; i >= 0; i--)
            {
                var e = es[i];
                if (tick - e.Tick > AskedTicks) break;
                if (e.Who == "player" && e.To == 0) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- /act/build
        static Plan BuildOne(JObject b)
        {
            var a = new BuildArgs { Thing = Str(b, "thing", 1, 60), Stuff = OptStr(b, "stuff", 60) };
            var rt = b["rot"];
            if (rt != null && rt.Type != JTokenType.Null)
            {
                int r = rt.Type == JTokenType.Integer ? BuildPure.Rot(rt.Value<long>().ToString()) : rt.Type == JTokenType.String ? BuildPure.Rot(rt.Value<string>()) : -2;
                if (r < 0) throw new ArgError("rot must be 0..3 or north, east, south, west");
                a.Rot = r;
            }
            a.At = ParseWhere(b, false, false);
            if (a.At.X != null && a.At.Room != 0) throw new ArgError("give a cell (x, z) or a room, not both");
            return new Plan
            {
                GateAt = (v, tick) => NeedLeft(null, "blueprints", Peek(v, tick).Blueprints),
                RunLeft = (h, left) => h.Build(a, left)
            };
        }

        // ---------------------------------------------------------------- /act/room
        static Plan Room(JObject b)
        {
            var a = new RoomArgs { Purpose = OneOf(b, "purpose", BuildPure.Purposes) };
            a.Size = OptOneOf(b, "size", BuildPure.Sizes, "medium");
            int? w = OptInt(b, "w", BuildPure.MinRoom, BuildPure.MaxRoom), h = OptInt(b, "h", BuildPure.MinRoom, BuildPure.MaxRoom);
            if ((w == null) != (h == null)) throw new ArgError("give both w and h, or neither");
            if (w != null) { a.W = w.Value; a.H = h.Value; }
            else BuildPure.RoomSize(a.Purpose, a.Size, out a.W, out a.H);
            a.Stuff = OptStr(b, "stuff", 60);
            a.Door = OptOneOf(b, "door", BuildPure.DoorSides, "auto");
            a.At = ParseWhere(b, false, false);
            if (a.At.Room != 0) throw new ArgError("a new room takes a corner (x, z) or nothing, not a room id");
            int walls = 2 * (a.W + a.H) - 4;
            return new Plan
            {
                GateAt = (v, tick) =>
                {
                    int left = Peek(v, tick).Blueprints;
                    if (left < walls) return HandResult.No("a " + a.W + "x" + a.H + " room needs up to " + walls + " blueprints but only " + left + " are left this cycle");
                    return null;
                },
                RunLeft = (hh, left) => hh.Room(a, left)
            };
        }

        // ---------------------------------------------------------------- /act/zone
        static Plan Zone(JObject b)
        {
            var a = new ZoneArgs { Kind = OneOf(b, "kind", ZoneKinds) };
            a.Plant = OptStr(b, "plant", 60);
            if (a.Kind == "grow" && a.Plant == null) throw new ArgError("a growing zone needs a plant");
            if (a.Kind != "grow" && a.Plant != null) throw new ArgError("plant is only for kind grow");
            a.Preset = OptOneOf(b, "preset", BuildPure.Presets, "all");
            a.Priority = OptOneOf(b, "priority", BuildPure.Priorities, null);
            a.Size = OptOneOf(b, "size", BuildPure.Sizes, "medium");
            int? w = OptInt(b, "w", 1, BuildPure.MaxZoneSide), h = OptInt(b, "h", 1, BuildPure.MaxZoneSide);
            if ((w == null) != (h == null)) throw new ArgError("give both w and h, or neither");
            if (w != null) { a.W = w.Value; a.H = h.Value; }
            else BuildPure.ZoneSize(a.Kind, a.Size, out a.W, out a.H);
            a.At = ParseWhere(b, false, false);
            if (a.At.X != null && a.At.Room != 0) throw new ArgError("give a corner (x, z) or a room, not both");
            int area = a.W * a.H;
            return new Plan
            {
                GateAt = (v, tick) =>
                {
                    int left = Peek(v, tick).Cells;
                    if (a.At.Room == 0 && left < area) return HandResult.No("a " + a.W + "x" + a.H + " zone is " + area + " cells but only " + left + " are left this cycle");
                    return NeedLeft(null, "zone cells", left);
                },
                RunLeft = (hh, left) => hh.Zone(a, left)
            };
        }

        // ---------------------------------------------------------------- /act/area
        static Plan Area(JObject b)
        {
            var a = new AreaArgs { Kind = OneOf(b, "kind", AreaKinds), Op = OneOf(b, "op", AreaOps) };
            a.Name = OptStr(b, "name", 40);
            if (a.Kind == "allowed" && a.Name == null) throw new ArgError("an allowed area needs its name");
            var at = ParseWhere(b, true, false);
            if (at.Box == null) throw new ArgError("give the box: x, z, w, h");
            a.Box = at.Box;
            if (b["assign"] is JArray arr)
            {
                if (a.Kind != "allowed") throw new ArgError("assign is only for allowed areas");
                if (arr.Count > 30) throw new ArgError("assign takes at most 30 pawns");
                foreach (var t in arr)
                {
                    if (t.Type != JTokenType.Integer || t.Value<long>() < 1 || t.Value<long>() > int.MaxValue) throw new ArgError("assign must be positive integer pawn ids");
                    a.Assign.Add(t.Value<int>());
                }
            }
            else if (b["assign"] != null && b["assign"].Type != JTokenType.Null) throw new ArgError("assign must be an array of pawn ids");
            int area = a.Box.Area;
            return new Plan
            {
                GateAt = (v, tick) =>
                {
                    int left = Peek(v, tick).Cells;
                    if (left < area) return HandResult.No("that box is " + area + " cells but only " + left + " are left this cycle");
                    return null;
                },
                RunLeft = (h, left) => h.Area(a, left)
            };
        }

        // ---------------------------------------------------------------- /act/bill
        static Plan Bill(JObject b)
        {
            var a = new BillArgs { Op = OneOf(b, "op", BillOps), Bench = Int(b, "bench", 1, int.MaxValue) };
            a.Recipe = OptStr(b, "recipe", 80);
            a.Index = OptInt(b, "index", 1, 30) ?? 0;
            a.Count = OptInt(b, "count", 1, 999) ?? 0;
            a.Until = OptInt(b, "until", 1, 9999) ?? 0;
            a.Radius = OptInt(b, "radius", 3, 999) ?? 0;
            if (a.Op == "add" && a.Recipe == null) throw new ArgError("adding a bill needs a recipe");
            if (a.Op != "add" && a.Recipe == null && a.Index == 0) throw new ArgError(a.Op + " needs the bill's index (1-based) or its recipe");
            if (a.Count != 0 && a.Until != 0) throw new ArgError("give count or until, not both");
            if (a.Op != "add" && (a.Count != 0 || a.Until != 0 || a.Radius != 0)) throw new ArgError("count, until and radius are only for add");
            return new Plan
            {
                GateAt = (v, tick) => a.Op == "add" ? NeedLeft(null, "bills", Peek(v, tick).Bills) : null,
                RunLeft = (h, left) => h.Bill(a, left)
            };
        }
    }
}
