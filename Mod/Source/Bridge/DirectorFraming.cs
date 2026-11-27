using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>Zoom per shot kind. Sizes are the camera's root size (half the visible height, in cells).</summary>
    public static class DirectorZoom
    {
        public enum Level { Close, Mid, Wide }

        public const double CloseSize = 13, MidSize = 22, WideSize = 36;
        public const double BattleMin = 16, BattleMax = 55;

        public static Level ForKind(string kind)
        {
            switch (kind)
            {
                case SceneKinds.Meal: case SceneKinds.Medical: case SceneKinds.Romance: case SceneKinds.Child:
                case SceneKinds.Prisoner: case SceneKinds.Recreation:
                case WatchKinds.Death: case WatchKinds.Birth: case WatchKinds.Art: case WatchKinds.Masterwork:
                case WatchKinds.Funeral: case WatchKinds.Wedding: case WatchKinds.SocialFight: case WatchKinds.MentalBreak:
                case WatchKinds.Downed:
                    return Level.Close;
                case WatchKinds.Raid: case WatchKinds.ShipPart: case WatchKinds.Battle: case WatchKinds.BattleEnd:
                    return Level.Wide;
                default:
                    return Level.Mid;   // work, night, taming, follow/thread, combat, party
            }
        }

        public static double Size(Level l) { return l == Level.Close ? CloseSize : l == Level.Wide ? WideSize : MidSize; }

        /// <summary>The kind's size, widened to fit a group when one is given (fit &lt;= 0 = none), inside the player's limits.</summary>
        public static double Target(string kind, double fit, double min, double max)
        {
            double s = Size(ForKind(kind));
            if (fit > s) s = fit;
            return Clamp(s, min, max);
        }

        public static double Clamp(double v, double min, double max)
        {
            if (max < min) { var t = min; min = max; max = t; }
            return v < min ? min : v > max ? max : v;
        }

        /// <summary>Root size that shows the box (with margin) around a centre on a screen of this aspect (width/height).</summary>
        public static double Fit(double minX, double minZ, double maxX, double maxZ, double cx, double cz, double aspect, double margin = 1.2, double pad = 3)
        {
            if (aspect <= 0.1) aspect = 16.0 / 9.0;
            double hx = Math.Max(Math.Abs(maxX - cx), Math.Abs(cx - minX));
            double hz = Math.Max(Math.Abs(maxZ - cz), Math.Abs(cz - minZ));
            return Math.Max(hz, hx / aspect) * margin + pad;
        }
    }

    /// <summary>
    /// One axis of a critically damped spring (Unity's SmoothDamp, Verse-free): no overshoot, no jerk at the start.
    /// </summary>
    public static class Spring
    {
        public static double Step(double cur, double target, ref double vel, double smoothTime, double maxSpeed, double dt)
        {
            if (dt <= 0) return cur;
            smoothTime = Math.Max(0.0001, smoothTime);
            double omega = 2.0 / smoothTime;
            double x = omega * dt;
            double exp = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
            double change = cur - target;
            double maxChange = maxSpeed * smoothTime;
            if (change > maxChange) change = maxChange; else if (change < -maxChange) change = -maxChange;
            double t = cur - change;
            double temp = (vel + omega * change) * dt;
            vel = (vel - omega * temp) * exp;
            double output = t + (change + temp) * exp;
            if ((target - cur > 0) == (output > target)) { output = target; vel = (output - target) / dt; }
            return output;
        }
    }

    /// <summary>
    /// Per-frame follow camera on the map plane: a dead zone with hysteresis (starts moving past StartCells, settles
    /// inside StopCells) so small steps of the pawn never shake the view, and a critically damped spring so motion
    /// eases in and out. Far targets (more than CutCells) are a cut, not a pan. Zoom eases the same way.
    /// </summary>
    public sealed class FollowCam
    {
        public double StartCells = 3.0, StopCells = 0.5, CutCells = 70;
        public double SmoothTime = 0.45, MaxSpeed = 80;          // cells, seconds
        public double ZoomSmoothTime = 0.8, ZoomMaxSpeed = 60;
        double _vx, _vz, _vs;
        bool _moving;

        public bool Moving { get { return _moving; } }

        public void Reset() { _vx = _vz = _vs = 0; _moving = false; }

        /// <summary>True when the target is so far the camera should cut there.</summary>
        public bool ShouldCut(double cx, double cz, double tx, double tz) { return Dist(cx, cz, tx, tz) > CutCells; }

        public void Step(ref double cx, ref double cz, double tx, double tz, double dt)
        {
            if (dt <= 0) return;
            dt = Math.Min(dt, 0.1);   // a frame hitch must not throw the camera
            double d = Dist(cx, cz, tx, tz);
            if (!_moving && d > StartCells) _moving = true;
            if (!_moving) return;
            cx = Spring.Step(cx, tx, ref _vx, SmoothTime, MaxSpeed, dt);
            cz = Spring.Step(cz, tz, ref _vz, SmoothTime, MaxSpeed, dt);
            if (Dist(cx, cz, tx, tz) < StopCells && Math.Sqrt(_vx * _vx + _vz * _vz) < 1.0) { _moving = false; _vx = _vz = 0; }
        }

        public double StepZoom(double size, double target, double dt)
        {
            if (dt <= 0) return size;
            if (Math.Abs(size - target) < 0.02) { _vs = 0; return target; }
            return Spring.Step(size, target, ref _vs, ZoomSmoothTime, ZoomMaxSpeed, Math.Min(dt, 0.1));
        }

        static double Dist(double ax, double az, double bx, double bz) { double dx = ax - bx, dz = az - bz; return Math.Sqrt(dx * dx + dz * dz); }
    }

    /// <summary>A point of one side in a fight.</summary>
    public struct FightPoint
    {
        public int Id; public double X, Z;
        public FightPoint(int id, double x, double z) { Id = id; X = x; Z = z; }
    }

    /// <summary>The framed fight: centre on the front line and a size that shows everyone engaged.</summary>
    public sealed class BattleFrameResult
    {
        public double X, Z, Size;
        public int Colonists, Hostiles;   // engaged on each side
    }

    /// <summary>Pure battle framing math.</summary>
    public static class BattleFrame
    {
        public const double EngageCells = 28;

        /// <summary>
        /// Engaged = within EngageCells of someone on the other side. Centre = midpoint of the two sides' centroids
        /// (the front line); size fits the box of every engaged fighter around that centre, clamped to the battle
        /// range and the player's limits. Null when nobody is engaged.
        /// </summary>
        public static BattleFrameResult Compute(IList<FightPoint> colonists, IList<FightPoint> hostiles, double aspect,
            double playerMin, double playerMax, double engageCells = EngageCells)
        {
            if (colonists == null || hostiles == null || colonists.Count == 0 || hostiles.Count == 0) return null;
            double r2 = engageCells * engageCells;
            var ec = new List<FightPoint>(); var eh = new List<FightPoint>();
            foreach (var c in colonists) foreach (var h in hostiles) if (D2(c, h) <= r2) { ec.Add(c); break; }
            foreach (var h in hostiles) foreach (var c in colonists) if (D2(c, h) <= r2) { eh.Add(h); break; }
            if (ec.Count == 0 || eh.Count == 0) return null;
            double ccx = 0, ccz = 0, hcx = 0, hcz = 0;
            foreach (var p in ec) { ccx += p.X; ccz += p.Z; }
            foreach (var p in eh) { hcx += p.X; hcz += p.Z; }
            ccx /= ec.Count; ccz /= ec.Count; hcx /= eh.Count; hcz /= eh.Count;
            double cx = (ccx + hcx) / 2, cz = (ccz + hcz) / 2;
            double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
            foreach (var p in ec) Box(p, ref minX, ref minZ, ref maxX, ref maxZ);
            foreach (var p in eh) Box(p, ref minX, ref minZ, ref maxX, ref maxZ);
            double size = DirectorZoom.Fit(minX, minZ, maxX, maxZ, cx, cz, aspect);
            double lo = Math.Max(DirectorZoom.BattleMin, playerMin), hi = Math.Min(DirectorZoom.BattleMax, playerMax);
            if (hi < lo) hi = lo;
            return new BattleFrameResult { X = cx, Z = cz, Size = DirectorZoom.Clamp(size, lo, hi), Colonists = ec.Count, Hostiles = eh.Count };
        }

        static double D2(FightPoint a, FightPoint b) { double dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz; }
        static void Box(FightPoint p, ref double a, ref double b, ref double c, ref double d)
        {
            if (p.X < a) a = p.X; if (p.Z < b) b = p.Z; if (p.X > c) c = p.X; if (p.Z > d) d = p.Z;
        }
    }

    /// <summary>What the battle director wants on screen this frame.</summary>
    public sealed class BattleShot
    {
        public string Kind;          // WatchKinds.Battle, Downed or BattleEnd
        public int TargetId = -1;    // downed colonist for the close-up
        public double X, Z, Size;
        public string Caption;       // set once, on the frame the shot starts
    }

    /// <summary>
    /// Pure battle sequencing. While anyone is engaged: hold the framed fight (no cutting away) except a short close-up
    /// of a colonist who goes down; after EndGraceSec with nobody engaged, a closing shot ("the raiders flee") for
    /// ClosingSec, then idle. Clock = real seconds.
    /// </summary>
    public sealed class BattleDirector
    {
        public const double EndGraceSec = 8, ClosingSec = 6, DownedSec = 4, DownedGapSec = 8;

        enum Mode { Idle, Fighting, Closing }
        Mode _mode = Mode.Idle;
        double _lastEngaged = -1e9, _closingUntil, _downedUntil = -1e9, _lastDownedAt = -1e9;
        int _downedId = -1; string _downedName;
        BattleFrameResult _last;
        string _pendingCaption;
        bool _downedNew;

        public bool Active { get { return _mode != Mode.Idle; } }
        public bool Fighting { get { return _mode == Mode.Fighting; } }

        public void Clear() { _mode = Mode.Idle; _last = null; _downedId = -1; _pendingCaption = null; _downedUntil = -1e9; _lastDownedAt = -1e9; _lastEngaged = -1e9; }

        /// <summary>
        /// One scan. frame = BattleFrame.Compute result (null = nobody engaged). noun = "the raiders", "the animals",
        /// "the mechanoids"; hostilesLeft = active hostiles still on the map; colonistsStanding = colonists not downed.
        /// </summary>
        public void Update(double now, BattleFrameResult frame, string noun, int hostilesLeft, int colonistsStanding)
        {
            if (frame != null)
            {
                _last = frame; _lastEngaged = now;
                if (_mode != Mode.Fighting) { _mode = Mode.Fighting; _pendingCaption = "Camera: the fight begins"; }
                return;
            }
            if (_mode == Mode.Fighting && now - _lastEngaged >= EndGraceSec)
            {
                _mode = Mode.Closing; _closingUntil = now + ClosingSec; _downedUntil = -1e9;
                _pendingCaption = ClosingCaption(noun, hostilesLeft, colonistsStanding);
            }
            else if (_mode == Mode.Closing && now >= _closingUntil) { _mode = Mode.Idle; _pendingCaption = null; }
        }

        public static string ClosingCaption(string noun, int hostilesLeft, int colonistsStanding)
        {
            string n = string.IsNullOrWhiteSpace(noun) ? "the enemy" : noun.Trim();
            if (colonistsStanding <= 0) return "Camera: no one is left standing";
            if (hostilesLeft <= 0) return "Camera: " + n + " are beaten";
            return "Camera: " + n + " flee";
        }

        /// <summary>A colonist went down during the fight: a close-up for DownedSec (at most one per DownedGapSec).</summary>
        public void Downed(int id, string name, double now)
        {
            if (_mode != Mode.Fighting || now - _lastDownedAt < DownedGapSec) return;
            _downedId = id; _downedName = name; _downedUntil = now + DownedSec; _lastDownedAt = now; _downedNew = true;
        }

        /// <summary>The shot for this frame, or null when idle. Captions are handed out once.</summary>
        public BattleShot Shot(double now)
        {
            if (_mode == Mode.Idle || _last == null) return null;
            BattleShot s;
            if (_mode == Mode.Fighting && now < _downedUntil && _downedId >= 0)
            {
                s = new BattleShot { Kind = WatchKinds.Downed, TargetId = _downedId, X = _last.X, Z = _last.Z, Size = DirectorZoom.CloseSize };
                if (_downedNew) { s.Caption = "Camera: " + (string.IsNullOrWhiteSpace(_downedName) ? "a colonist" : _downedName) + " is down"; _downedNew = false; }
                return s;
            }
            s = new BattleShot { Kind = _mode == Mode.Closing ? WatchKinds.BattleEnd : WatchKinds.Battle, X = _last.X, Z = _last.Z, Size = _last.Size };
            if (_pendingCaption != null) { s.Caption = _pendingCaption; _pendingCaption = null; }
            return s;
        }
    }
}
