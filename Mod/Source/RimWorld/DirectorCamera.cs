using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Optional "director camera": when on, the map camera follows the most interesting pawn on the current map
    /// (scored by <see cref="DirectorFocus"/>). It only moves the camera: it never changes map, selects or drafts.
    /// Any manual camera input pauses following for <see cref="ManualPauseSec"/> real seconds (it resumes by itself,
    /// so a player who looks around is not fighting the camera, and does not have to turn it on again).
    /// Game thread only.
    /// </summary>
    public static class DirectorCamera
    {
        public const double ManualPauseSec = 10.0;
        public const double DeathGraceSec = 8.0;       // how long the camera may stay on a corpse
        public const double ConversationSec = 12.0;
        const double SweepEverySec = 0.5;
        const float DeadZoneCells = 2.5f;              // no movement while the pawn is this close to the view centre
        const float JumpCells = 70f;                   // farther than this: cut instead of pan
        const float ManualJumpCells = 12f;             // the camera is this far from where we left it: someone moved it

        static readonly DirectorFocus Focus = new DirectorFocus();
        static readonly Dictionary<int, Pawn> Pawns = new Dictionary<int, Pawn>();
        static readonly Dictionary<int, double> DeathUntil = new Dictionary<int, double>();
        static readonly List<int> Ids = new List<int>();

        public static bool Enabled { get; private set; }
        static int _mapId = -1;
        static double _pausedUntil, _nextSweep;
        static bool _haveSet;
        static Vector2 _lastSet;
        static float _lastSize;
        static KeyBindingDef _key;

        static double Now { get { return Time.realtimeSinceStartupAsDouble; } }

        public static bool Allowed
        {
            get { var s = RimWorldLLMMod.Settings; return s != null && s.directorCameraAllowed; }
        }

        /// <summary>True while it follows: on, allowed, and not paused by the player's own camera input.</summary>
        public static bool Active { get { return Enabled && Allowed; } }

        public static void SetEnabled(bool on, bool announce)
        {
            if (on && !Allowed) on = false;
            if (Enabled == on) return;
            Enabled = on;
            Reset();
            if (announce) Messages.Message(UiUtil.T(on ? "RWL_Camera_MsgOn" : "RWL_Camera_MsgOff"), MessageTypeDefOf.SilentInput, false);
        }

        public static void Toggle() { SetEnabled(!Enabled, true); }

        /// <summary>A game starts or loads: the start setting decides, nothing is followed yet.</summary>
        public static void OnGameStart()
        {
            var s = RimWorldLLMMod.Settings;
            Enabled = s != null && s.directorCameraAllowed && s.directorCameraOnAtStart;
            Reset();
        }

        public static void Reset()
        {
            Focus.Clear(); Pawns.Clear(); DeathUntil.Clear();
            _mapId = -1; _pausedUntil = 0; _haveSet = false;
            WatchCamera.Reset();
        }

        // ------------------------------------------------------------------ signals (one-line hooks elsewhere)
        static bool Track(Pawn p, FocusSignal sig, double severity = 1.0)
        {
            // MapHeld: a pawn that just died is not spawned (Map is null) but its corpse is on the map.
            if (!Active || p == null || p.MapHeld == null || p.MapHeld != Find.CurrentMap) return false;
            if (!p.RaceProps.Humanlike && p.Faction != Faction.OfPlayer && sig != FocusSignal.RaidStart) return false;
            Pawns[p.thingIDNumber] = p;
            Focus.Signal(p.thingIDNumber, sig, Now, severity);
            return true;
        }

        /// <summary>A speech balloon was shown over <paramref name="p"/>.</summary>
        public static void OnSpeech(Pawn p, BubbleKind look, string intent)
        {
            string i = (intent ?? "").Trim().ToLowerInvariant();
            var sig = FocusSignal.SpeechPlain;
            if (look == BubbleKind.Shout || i == "insult") sig = FocusSignal.SpeechHigh;
            else if (look == BubbleKind.Whisper || i == "confide" || i == "gossip") sig = FocusSignal.SpeechMedium;
            Track(p, sig);
        }

        /// <summary>Two pawns are talking to each other: both get a bonus for a while.</summary>
        public static void OnConversation(Pawn a, Pawn b)
        {
            if (!Active || a == null || b == null || a.Map == null || a.Map != Find.CurrentMap || b.Map != a.Map) return;
            Pawns[a.thingIDNumber] = a; Pawns[b.thingIDNumber] = b;
            Focus.Conversation(a.thingIDNumber, b.thingIDNumber, Now, ConversationSec);
        }

        public static void OnThought(Pawn p) { Track(p, FocusSignal.Thought); }

        /// <summary>A colony event was published (death, injury, mental break).</summary>
        public static void OnEvent(EventSpec e)
        {
            WatchCamera.OnEvent(e);
            if (!Active || e == null || e.Map == null || e.Map != Find.CurrentMap || e.Pawns.Count == 0) return;
            var p = e.Pawns[0].Key;
            switch (e.Type)
            {
                case "death":
                    if (Track(p, FocusSignal.Death)) DeathUntil[p.thingIDNumber] = Now + DeathGraceSec;
                    break;
                case "mental_break": Track(p, FocusSignal.MentalBreak); break;
                case "injury": Track(p, FocusSignal.Injury, Math.Max(0.5f, e.Severity * 2f)); break;
            }
        }

        /// <summary>A raid arrived: look at the first raider on the map (or at a colonist when none is on the map yet).</summary>
        public static void OnRaid(Map map, List<Pawn> raiders)
        {
            WatchCamera.OnRaid(map, raiders);
            if (!Active || map == null || map != Find.CurrentMap) return;
            if (raiders != null)
                foreach (var r in raiders)
                    if (r != null && r.Spawned && r.Map == map) { Track(r, FocusSignal.RaidStart); return; }
            var cols = map.mapPawns.FreeColonistsSpawned;
            if (cols.Count > 0) Track(cols[0], FocusSignal.RaidStart);
        }

        // ------------------------------------------------------------------ per frame
        /// <summary>Called every frame (also paused) by the game component.</summary>
        public static void Update()
        {
            if (WatchCamera.Enabled) { WatchCamera.Update(); return; }   // watch mode drives the camera instead
            if (!Active) return;
            var map = Find.CurrentMap;
            if (map == null || Current.ProgramState != ProgramState.Playing) return;
            if (map.uniqueID != _mapId)
            {
                Focus.Clear(); Pawns.Clear(); DeathUntil.Clear();
                _mapId = map.uniqueID; _haveSet = false;
                return;
            }
            if (Focus.Count == 0 && Focus.Current < 0) { _haveSet = false; return; }   // idle: nothing to do

            double now = Now;
            var cam = Find.CameraDriver;
            if (cam == null) return;
            if (ManualInput()) { _pausedUntil = now + ManualPauseSec; _haveSet = false; }
            if (now < _pausedUntil) return;

            if (now >= _nextSweep) { _nextSweep = now + SweepEverySec; Sweep(map, now); }

            int id = Focus.Pick(now);
            if (id < 0) { _haveSet = false; return; }
            Vector3 target;
            if (!Resolve(id, map, now, out target)) { Focus.Remove(id); Pawns.Remove(id); DeathUntil.Remove(id); return; }

            if (MovedByOthers(cam))
            {
                _pausedUntil = now + ManualPauseSec;      // a jump from a letter or the log, or a zoom: the player moved it
                _haveSet = false;
                return;
            }
            Pan(cam, target);
        }

        /// <summary>True when the camera is not where we left it (a jump from a letter or the log, or a zoom): someone else moved it.</summary>
        internal static bool MovedByOthers(CameraDriver cam)
        {
            if (!_haveSet) return false;
            Vector3 camPos = cam.MyCamera.transform.position;
            var c = new Vector2(camPos.x, camPos.z);
            if ((c - _lastSet).magnitude > ManualJumpCells || Mathf.Abs(cam.RootSize - _lastSize) > 0.05f) { _haveSet = false; return true; }
            return false;
        }

        /// <summary>One frame of camera motion toward a world position: a cut when far, a smooth pan when near, nothing in the dead zone.</summary>
        internal static void Pan(CameraDriver cam, Vector3 target)
        {
            Vector3 camPos = cam.MyCamera.transform.position;
            var c = new Vector2(camPos.x, camPos.z);
            float size = cam.RootSize;
            var t = new Vector2(target.x, target.z);
            float dist = (t - c).magnitude;
            if (dist > JumpCells)
            {
                cam.JumpToCurrentMapLoc(new Vector3(target.x, camPos.y, target.z));
                _lastSet = t; _lastSize = cam.RootSize; _haveSet = true;
            }
            else if (dist > DeadZoneCells)
            {
                float speed = Mathf.Clamp(RimWorldLLMMod.Settings.directorPanSpeed, 0.2f, 8f);
                float k = 1f - Mathf.Exp(-speed * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
                var n = c + (t - c) * k;
                cam.SetRootPosAndSize(new Vector3(n.x, camPos.y, n.y), size);
                _lastSet = n; _lastSize = size; _haveSet = true;
            }
            else { _lastSet = c; _lastSize = size; _haveSet = true; }
        }

        static void Sweep(Map map, double now)
        {
            Focus.CopyIds(Ids);
            Vector3 pos;
            for (int i = 0; i < Ids.Count; i++)
                if (!Resolve(Ids[i], map, now, out pos)) { Focus.Remove(Ids[i]); Pawns.Remove(Ids[i]); DeathUntil.Remove(Ids[i]); }
        }

        /// <summary>Where the pawn is now on this map; false when it died (past the corpse grace), left the map or is gone.</summary>
        static bool Resolve(int id, Map map, double now, out Vector3 pos)
        {
            pos = Vector3.zero;
            Pawn p;
            if (!Pawns.TryGetValue(id, out p) || p == null || p.Destroyed && !p.Dead) return false;
            if (p.Dead)
            {
                double until;
                var corpse = p.Corpse;
                if (!DeathUntil.TryGetValue(id, out until) || now > until || corpse == null || !corpse.Spawned || corpse.Map != map) return false;
                pos = corpse.DrawPos;
                return true;
            }
            if (!p.Spawned || p.Map != map) return false;
            pos = p.DrawPos;
            return true;
        }

        // Keys, wheel, middle-button drag, or the mouse parked on a screen edge (edge scrolling).
        internal static bool ManualInput()
        {
            if (KeyBindingDefOf.MapDolly_Up.IsDown || KeyBindingDefOf.MapDolly_Down.IsDown
                || KeyBindingDefOf.MapDolly_Left.IsDown || KeyBindingDefOf.MapDolly_Right.IsDown
                || KeyBindingDefOf.MapZoom_In.IsDown || KeyBindingDefOf.MapZoom_Out.IsDown) return true;
            if (Input.GetMouseButton(2)) return true;
            if (Mathf.Abs(Input.mouseScrollDelta.y) > 0.001f && !Find.WindowStack.MouseObscuredNow) return true;
            if (Prefs.EdgeScreenScroll && Application.isFocused && !Find.WindowStack.MouseObscuredNow)
            {
                Vector3 m = Input.mousePosition;
                if (m.x <= 2f || m.y <= 2f || m.x >= Screen.width - 3f || m.y >= Screen.height - 3f) return true;
            }
            return false;
        }

        /// <summary>The toggle key (unbound by default), checked from the game component's OnGUI.</summary>
        public static void OnGUIKey()
        {
            if (Event.current == null || Event.current.type != EventType.KeyDown || !Allowed) return;
            if (_key == null) _key = DefDatabase<KeyBindingDef>.GetNamedSilentFail("RWL_ToggleDirectorCamera");
            if (_key != null && _key.KeyDownEvent) { Toggle(); Event.current.Use(); }
        }
    }
}
