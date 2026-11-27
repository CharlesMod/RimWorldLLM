using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimWorldLLM.Bridge
{
    /// <summary>Shot kinds of the watch-mode camera director.</summary>
    public static class WatchKinds
    {
        public const string Raid = "raid_start", Combat = "combat", MentalBreak = "mental_break", SocialFight = "social_fight",
            Death = "death", Funeral = "funeral", Birth = "birth", Wedding = "wedding", Party = "party",
            Masterwork = "masterwork", ShipPart = "ship_part", Art = "art_created", Wander = "wander",
            Thread = "thread", Battle = "battle", BattleEnd = "battle_end", Downed = "downed";

        /// <summary>Events (things that happened) beat scenes, story-thread follows and filler.</summary>
        public static bool IsEvent(string kind)
        {
            return kind != null && kind != Wander && kind != Thread && !SceneKinds.IsScene(kind);
        }

        /// <summary>Interest of a kind when it is fresh. Higher wins.</summary>
        public static double Weight(string kind)
        {
            switch (kind)
            {
                case Raid: return 20;
                case Death: return 16;
                case Birth: return 14;
                case MentalBreak: return 12;
                case Wedding: return 12;
                case Funeral: return 11;
                case ShipPart: return 10;
                case SocialFight: return 9;
                case Combat: return 8;
                case Party: return 7;
                case Masterwork: return 6;
                case Art: return 4;
                case Thread: return 2.5;
                case SceneKinds.Medical: return 3.5;
                case SceneKinds.Meal: return 3.0;
                case SceneKinds.Romance: return 2.6;
                case SceneKinds.Child: return 2.6;
                case SceneKinds.Recreation: return 2.5;
                case SceneKinds.Prisoner: return 2.4;
                case SceneKinds.Taming: return 2.2;
                case SceneKinds.Night: return 2.0;
                case SceneKinds.Work: return 1.8;
                case Wander: return 1;
                default: return 0;
            }
        }

        /// <summary>Urgent kinds may cut in on a shot before its dwell is over.</summary>
        public static bool IsUrgent(string kind) { return kind == Raid || kind == Death || kind == MentalBreak; }

        /// <summary>Dwell (seconds on the shot): longer for the big events.</summary>
        public static double Dwell(string kind)
        {
            switch (kind)
            {
                case Raid: case Death: case Birth: case Wedding: case Funeral: return 12;
                case ShipPart: case MentalBreak: case SocialFight: case Combat: return 8;
                case Downed: return 4;
                case Wander: return 6;
                default: return 7;
            }
        }

        /// <summary>Wander shots are filler: no caption.</summary>
        public static bool HasCaption(string kind) { return kind != Wander && kind != null; }

        /// <summary>Map a published event (type + data fields) to a shot kind, or null when it is not of interest.</summary>
        public static string FromEvent(string type, string state, string gatherKind, string phase)
        {
            switch (type)
            {
                case "raid_start": return Raid;
                case "death": return Death;
                case "birth": return Birth;
                case "masterwork": return Masterwork;
                case "ship_part": return ShipPart;
                case "art_created": return Art;
                case "injury": return Combat;
                case "mental_break":
                    return string.Equals(state, "SocialFighting", StringComparison.OrdinalIgnoreCase) ? SocialFight : MentalBreak;
                case "funeral": return phase == "end" ? null : Funeral;
                case "gathering":
                    if (phase == "end") return null;
                    if (gatherKind == "wedding") return Wedding;
                    if (gatherKind == "party") return Party;
                    if (gatherKind == "funeral") return Funeral;
                    return null;
                default: return null;
            }
        }

        /// <summary>Eight-point compass word for an offset (x grows east, z grows north).</summary>
        public static string Direction(double dx, double dz)
        {
            if (Math.Abs(dx) < 1e-6 && Math.Abs(dz) < 1e-6) return "the centre";
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI; // 0 = north, 90 = east
            if (deg < 0) deg += 360;
            string[] names = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
            return names[(int)((deg + 22.5) / 45.0) % 8];
        }

        /// <summary>Short plain-English caption; name and detail may be null.</summary>
        public static string Caption(string kind, string name, string detail)
        {
            string n = string.IsNullOrWhiteSpace(name) ? "someone" : name.Trim();
            string d = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
            switch (kind)
            {
                case Raid: return d != null ? "Camera: a raid arrives from the " + d : "Camera: a raid arrives";
                case Combat: return "Camera: " + n + " is fighting";
                case MentalBreak: return "Camera: " + n + " is having a mental break";
                case SocialFight: return "Camera: " + n + " is in an argument";
                case Death: return "Camera: " + n + " has died";
                case Funeral: return d != null ? "Camera: " + d + "'s funeral" : "Camera: a funeral";
                case Birth: return "Camera: " + n + " gives birth";
                case Wedding: return "Camera: a wedding";
                case Party: return "Camera: a party";
                case Masterwork: return d != null ? "Camera: " + n + " finishes a " + d : "Camera: " + n + " finishes a masterwork";
                case ShipPart: return d != null ? "Camera: a ship part is finished (" + d + ")" : "Camera: a ship part is finished";
                case Art: return "Camera: " + n + " finishes a work of art";
                default: return null;
            }
        }
    }

    /// <summary>A thing worth looking at, offered to the director.</summary>
    public sealed class WatchEvent
    {
        public string Kind;
        public int Key = -1;        // the subject (pawn id) used for variety; -1 = by place
        public int TargetId = -1;   // pawn or thing to follow, -1 = cell only
        public int MapId;
        public int X, Z;            // cell, used when the target is gone
        public string Name;         // subject label for the caption
        public string Detail;       // extra caption word (direction, label)
        public double Severity = 1; // scales the weight (clamped 0.5..1.5)
        public string Caption;      // ready-made caption (scenes, threads); null = built from kind/name/detail
        public ShotStyle Style = ShotStyle.Follow;
        public List<int> Ids;       // group members for Group shots
        public string SceneKey;     // scene identity for no-repeat
    }

    /// <summary>The next shot, or null from Next for "stay".</summary>
    public sealed class WatchShot
    {
        public string Kind;
        public int Key, TargetId, MapId, X, Z;
        public string Caption;      // null when no caption is to be written (filler, or rate limited)
        public double Dwell;
        public bool Cut;            // true when it cut in on a shot still in its dwell
        public double Score;
        public ShotStyle Style;
        public List<int> Ids;
        public string BlockKey;
    }

    /// <summary>
    /// Verse-free brain of watch mode. Offer events; Next(now) names the next shot or null to stay. Interest = kind
    /// weight x severity, halved every HalfLifeSec, times a variety factor for the same subject/place/kind shown
    /// recently. A shot has a dwell; urgent kinds may cut in after CutInMinSec when they clearly beat the current
    /// one. The player touching the camera pauses everything. Clock = real seconds, passed in. Not thread-safe.
    /// </summary>
    public sealed class WatchDirector
    {
        public const double HalfLifeSec = 20.0;
        public const double ExpirySec = 60.0;
        public const double MinScore = 0.8;
        public const double MergeWithinSec = 15.0;
        public const double CutInMinSec = 3.0;
        public const double CutInMargin = 1.3;
        public const double SubjectWindowSec = 90.0, SubjectFactor = 0.25;
        public const double KindWindowSec = 30.0, KindFactor = 0.6;
        public const double CaptionGapSec = 12.0, UrgentCaptionGapSec = 4.0, SameCaptionSec = 90.0;

        sealed class Pending { public WatchEvent E; public double At; }
        sealed class Shown { public string Kind; public int Key; public int MapId, X, Z; public double At; public string SceneKey; }

        readonly List<Pending> _pending = new List<Pending>();
        readonly List<Shown> _recent = new List<Shown>();
        WatchShot _cur; double _curAt = -1e9;
        double _pausedUntil = -1;
        double _lastCaptionAt = -1e9; string _lastCaption;
        public double PlayerPauseSec = 20.0;
        public WatchPace Pace = WatchPace.Normal;
        /// <summary>Extra interest for a pawn (story threads); null = none. Takes the pawn id.</summary>
        public Func<int, double> SubjectBoost;
        readonly RepeatGuard _guard = new RepeatGuard();

        public WatchShot Current { get { return _cur; } }
        public int PendingCount { get { return _pending.Count; } }
        public bool IsPaused(double now) { return now < _pausedUntil; }

        /// <summary>True when there is no shot or its hold is over (time for the next quiet-stretch offer).</summary>
        public bool ShotOver(double now) { return _cur == null || now - _curAt >= _cur.Dwell; }

        /// <summary>Drop the current shot (a battle took the camera); the next pick starts fresh.</summary>
        public void EndShot() { _cur = null; _curAt = -1e9; }

        /// <summary>The shared caption limit: true (and remembered) when cap may be written now.</summary>
        public bool TryCaption(string cap, bool urgent, double now)
        {
            if (cap == null) return false;
            double gap = urgent ? UrgentCaptionGapSec : CaptionGapSec;
            if (now - _lastCaptionAt < gap || (cap == _lastCaption && now - _lastCaptionAt < SameCaptionSec)) return false;
            _lastCaption = cap; _lastCaptionAt = now;
            return true;
        }

        public void Clear() { _guard.Clear(); _pending.Clear(); _recent.Clear(); _cur = null; _curAt = -1e9; _pausedUntil = -1; _lastCaption = null; _lastCaptionAt = -1e9; }

        /// <summary>The player moved the camera: no shots for PlayerPauseSec.</summary>
        public void PlayerTouched(double now) { _pausedUntil = now + Math.Max(0, PlayerPauseSec); }

        public void Offer(WatchEvent e, double now)
        {
            if (e == null || WatchKinds.Weight(e.Kind) <= 0) return;
            foreach (var p in _pending)
                if (p.E.Kind == e.Kind && p.E.Key == e.Key && p.E.MapId == e.MapId && p.E.SceneKey == e.SceneKey && now - p.At < MergeWithinSec)
                {
                    if (e.Severity >= p.E.Severity) p.E.Severity = e.Severity;   // refresh, keep one
                    p.E.X = e.X; p.E.Z = e.Z; p.At = now;
                    if (e.Caption != null) p.E.Caption = e.Caption;
                    if (e.Ids != null) p.E.Ids = e.Ids;
                    return;
                }
            _pending.Add(new Pending { E = e, At = now });
        }

        double Score(Pending p, double now)
        {
            double age = Math.Max(0, now - p.At);
            double sev = Math.Min(1.5, Math.Max(0.5, p.E.Severity));
            double s = WatchKinds.Weight(p.E.Kind) * sev * Math.Pow(0.5, age / HalfLifeSec);
            if (SubjectBoost != null)
            {
                double b = p.E.Key >= 0 ? SubjectBoost(p.E.Key) : 1;
                if (p.E.Ids != null) foreach (int id in p.E.Ids) b = Math.Max(b, SubjectBoost(id));
                s *= Math.Max(1, b);
            }
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                var r = _recent[i];
                double dt = now - r.At;
                if (r.Kind == p.E.Kind && dt < KindWindowSec) { s *= KindFactor; }
                bool samePlace = r.MapId == p.E.MapId && Math.Abs(r.X - p.E.X) <= 6 && Math.Abs(r.Z - p.E.Z) <= 6;
                bool sameSubject = p.E.SceneKey != null ? r.SceneKey == p.E.SceneKey   // scenes: by scene, not by place
                    : p.E.Key >= 0 ? r.Key == p.E.Key : samePlace && r.Key < 0 && r.SceneKey == null;
                if (sameSubject && dt < SubjectWindowSec) s *= SubjectFactor;
            }
            return s;
        }

        /// <summary>The next shot, or null to stay where the camera is.</summary>
        public WatchShot Next(double now)
        {
            _pending.RemoveAll(p => now - p.At > ExpirySec);
            _recent.RemoveAll(r => now - r.At > Math.Max(SubjectWindowSec, KindWindowSec));
            if (now < _pausedUntil) return null;

            // Events beat scenes, threads and filler: the best event that clears MinScore wins outright.
            double window = DirectorPacing.NoRepeatSec(Pace);
            _guard.Prune(now, window);
            Pending best = null, bestLow = null; double bs = 0, bls = 0;
            foreach (var p in _pending)
            {
                if (_guard.Blocked(DirectorPacing.BlockKey(p.E.Kind, p.E.Key, p.E.SceneKey), now, window, WatchKinds.IsUrgent(p.E.Kind))) continue;
                double s = Score(p, now);
                if (WatchKinds.IsEvent(p.E.Kind)) { if (s > bs) { bs = s; best = p; } }
                else if (s > bls) { bls = s; bestLow = p; }
            }
            if (best == null || bs < MinScore) { best = bestLow; bs = bls; }
            if (best == null || bs < MinScore) return null;

            bool cut = false;
            if (_cur != null)
            {
                double on = now - _curAt;
                if (on < _cur.Dwell)
                {
                    bool urgent = WatchKinds.IsUrgent(best.E.Kind);
                    bool overQuiet = WatchKinds.IsEvent(best.E.Kind) && !WatchKinds.IsEvent(_cur.Kind);   // events cut into scenes/filler
                    if (on < CutInMinSec || !(urgent && bs > _cur.Score * CutInMargin || overQuiet)) return null;
                    cut = true;
                }
            }

            _pending.Remove(best);
            var shot = new WatchShot
            {
                Kind = best.E.Kind, Key = best.E.Key, TargetId = best.E.TargetId, MapId = best.E.MapId, X = best.E.X, Z = best.E.Z,
                Dwell = DirectorPacing.Hold(best.E.Kind, Pace), Cut = cut, Score = bs,
                Style = best.E.Style, Ids = best.E.Ids, BlockKey = DirectorPacing.BlockKey(best.E.Kind, best.E.Key, best.E.SceneKey)
            };
            if (WatchKinds.HasCaption(shot.Kind))
            {
                string cap = best.E.Caption ?? WatchKinds.Caption(shot.Kind, best.E.Name, best.E.Detail);
                if (TryCaption(cap, WatchKinds.IsUrgent(shot.Kind), now)) shot.Caption = cap;
            }
            _guard.Shown(shot.BlockKey, now);
            _cur = shot; _curAt = now;
            _recent.Add(new Shown { Kind = shot.Kind, Key = shot.Key, MapId = shot.MapId, X = shot.X, Z = shot.Z, At = now, SceneKey = best.E.SceneKey });
            return shot;
        }
    }
}
