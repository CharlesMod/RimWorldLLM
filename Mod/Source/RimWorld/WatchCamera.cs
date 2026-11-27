using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Watch mode ("ant farm"): a camera director that cuts between the colony's events on its own. The Verse-free
    /// <see cref="WatchDirector"/> scores and picks; this class feeds it (events, a filler shot now and then), moves the
    /// camera through <see cref="DirectorCamera"/>'s pan, and writes one short caption per shot to the chat log.
    /// Camera only: never selects, drafts or opens windows. The player touching the camera pauses it
    /// (setting watchPlayerPauseSec). On/off is the mod setting <c>watchMode</c> (default off). Game thread only.
    /// </summary>
    public static class WatchCamera
    {
        const double WanderEverySec = 25.0;

        static readonly WatchDirector Dir = new WatchDirector();
        static readonly Dictionary<int, Pawn> Pawns = new Dictionary<int, Pawn>();
        static WatchShot _shot;
        static double _nextWander;
        static int _wanderIx;
        static bool _warned;
        static KeyBindingDef _key;

        static double Now { get { return Time.realtimeSinceStartupAsDouble; } }

        public static bool Enabled
        {
            get { var s = RimWorldLLMMod.Settings; return s != null && s.watchMode && Current.ProgramState == ProgramState.Playing; }
        }

        public static void SetEnabled(bool on, bool announce)
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null || s.watchMode == on) return;
            s.watchMode = on;
            try { RimWorldLLMMod.Instance.WriteSettings(); } catch (Exception) { }
            Reset();
            if (announce) Messages.Message(UiUtil.T(on ? "RWL_Watch_MsgOn" : "RWL_Watch_MsgOff"), MessageTypeDefOf.SilentInput, false);
        }

        public static void Toggle() { var s = RimWorldLLMMod.Settings; if (s != null) SetEnabled(!s.watchMode, true); }

        public static void Reset() { Dir.Clear(); Pawns.Clear(); _shot = null; _nextWander = 0; }

        public static void OnGUIKey()
        {
            if (Event.current == null || Event.current.type != EventType.KeyDown) return;
            if (_key == null) _key = DefDatabase<KeyBindingDef>.GetNamedSilentFail("RWL_ToggleWatchMode");
            if (_key != null && _key.KeyDownEvent) { Toggle(); Event.current.Use(); }
        }

        // ------------------------------------------------------------------ feeds
        static void Offer(WatchEvent w)
        {
            if (Pawns.Count > 128) Pawns.Clear();
            Dir.Offer(w, Now);
        }

        /// <summary>Any published colony event (death, injury, mental_break, gathering, funeral, birth, masterwork, ship_part, art_created).</summary>
        public static void OnEvent(EventSpec e)
        {
            if (!Enabled || e == null || e.Map == null) return;
            try
            {
                if (e.Type == "raid_start") return;   // OnRaid knows where they come from
                var d = e.Data;
                string state = d != null ? (string)d["state"] : null;
                string gk = d != null ? (string)d["kind"] : null;
                string phase = d != null ? (string)d["phase"] : null;
                string kind = WatchKinds.FromEvent(e.Type, state, gk, phase);
                if (kind == null) return;
                if (kind == WatchKinds.Art && !EventIsPlayers(e)) return;
                Pawn p = e.Pawns.Count > 0 ? e.Pawns[0].Key : null;
                IntVec3 at = e.At ?? (p != null ? p.PositionHeld : e.Map.Center);
                var w = new WatchEvent
                {
                    Kind = kind, MapId = e.Map.uniqueID, X = at.x, Z = at.z,
                    Severity = 0.5 + e.Severity,
                    Name = p != null ? p.LabelShort : null
                };
                if (p != null && kind != WatchKinds.Wedding && kind != WatchKinds.Party && kind != WatchKinds.Funeral)
                { w.Key = p.thingIDNumber; w.TargetId = p.thingIDNumber; Pawns[p.thingIDNumber] = p; }
                if (kind == WatchKinds.Funeral && d != null) w.Detail = (string)d["deceased_name"];
                if (kind == WatchKinds.Masterwork && d != null) w.Detail = (string)d["quality"] == "legendary" ? "legendary " + (string)d["label"] : (string)d["label"];
                if (kind == WatchKinds.ShipPart && d != null) w.Detail = (string)d["label"];
                Offer(w);
            }
            catch (Exception ex) { WarnOnce(ex); }
        }

        static bool EventIsPlayers(EventSpec e)
        {
            return e.Pawns.Count > 0 && e.Pawns[0].Key.Faction != null && e.Pawns[0].Key.Faction.IsPlayer;
        }

        /// <summary>A raid arrived: aim at the first raider (or the map edge side they come from).</summary>
        public static void OnRaid(Map map, List<Pawn> raiders)
        {
            if (!Enabled || map == null) return;
            try
            {
                double sx = 0, sz = 0; int n = 0; Pawn first = null;
                if (raiders != null)
                    foreach (var r in raiders)
                        if (r != null && r.Spawned && r.Map == map) { if (first == null) first = r; sx += r.Position.x; sz += r.Position.z; n++; }
                var c = map.Center;
                var w = new WatchEvent { Kind = WatchKinds.Raid, MapId = map.uniqueID, X = c.x, Z = c.z };
                if (n > 0)
                {
                    w.X = (int)(sx / n); w.Z = (int)(sz / n);
                    string dir = WatchKinds.Direction(sx / n - c.x, sz / n - c.z);
                    if (dir != "the centre") w.Detail = dir;
                    w.TargetId = first.thingIDNumber; Pawns[first.thingIDNumber] = first;
                }
                Offer(w);
            }
            catch (Exception ex) { WarnOnce(ex); }
        }

        static void WarnOnce(Exception ex)
        {
            if (_warned) return; _warned = true;
            Log.Warning("[RimWorldLLM] watch camera: " + ex.Message);
        }

        // ------------------------------------------------------------------ per frame
        public static void Update()
        {
            try { Step(); } catch (Exception ex) { WarnOnce(ex); }
        }

        static void Step()
        {
            var map = Find.CurrentMap;
            var cam = Find.CameraDriver;
            if (map == null || cam == null || Current.ProgramState != ProgramState.Playing) return;
            double now = Now;
            var s = RimWorldLLMMod.Settings;
            Dir.PlayerPauseSec = Math.Max(0, s.watchPlayerPauseSec);

            if (DirectorCamera.ManualInput() || DirectorCamera.MovedByOthers(cam)) Dir.PlayerTouched(now);

            if (now >= _nextWander)
            {
                _nextWander = now + WanderEverySec;
                if (Dir.PendingCount == 0) OfferWander(map);
            }

            var shot = Dir.Next(now);
            if (shot != null) StartShot(shot, cam);
            if (_shot == null || Dir.IsPaused(now)) return;

            Vector3 target;
            if (Resolve(_shot, map, out target)) DirectorCamera.Pan(cam, target);
        }

        static void OfferWander(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned;
            if (cols.Count == 0) return;
            var p = cols[_wanderIx++ % cols.Count];
            Pawns[p.thingIDNumber] = p;
            Offer(new WatchEvent { Kind = WatchKinds.Wander, Key = p.thingIDNumber, TargetId = p.thingIDNumber, MapId = map.uniqueID, X = p.Position.x, Z = p.Position.z, Name = p.LabelShort });
        }

        static void StartShot(WatchShot shot, CameraDriver cam)
        {
            _shot = shot;
            if (shot.Caption != null && RimWorldLLMMod.Settings.watchCaptions)
            {
                try { VivariumGame.State.Chat.Add(ChatKinds.Camera, shot.Caption, VivariumGame.NowTick); } catch (Exception ex) { WarnOnce(ex); }
            }
            // A shot on another map is a cut there (camera only: CameraJumper.TryJump, never TryJumpAndSelect).
            Map target = null;
            foreach (var m in Find.Maps) if (m.uniqueID == shot.MapId) { target = m; break; }
            if (target != null && target != Find.CurrentMap)
            {
                try { CameraJumper.TryJump(new IntVec3(shot.X, 0, shot.Z), target); } catch (Exception ex) { WarnOnce(ex); }
            }
        }

        /// <summary>Where the shot is now: the followed pawn or its corpse, else the cell it was offered at.</summary>
        static bool Resolve(WatchShot shot, Map map, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (map.uniqueID != shot.MapId) return false;
            Pawn p;
            if (shot.TargetId >= 0 && Pawns.TryGetValue(shot.TargetId, out p) && p != null)
            {
                if (!p.Dead && p.Spawned && p.Map == map) { pos = p.DrawPos; return true; }
                var corpse = p.Corpse;
                if (p.Dead && corpse != null && corpse.Spawned && corpse.Map == map) { pos = corpse.DrawPos; return true; }
            }
            var c = new IntVec3(shot.X, 0, shot.Z);
            if (!c.InBounds(map)) return false;
            pos = c.ToVector3Shifted();
            return true;
        }
    }
}
