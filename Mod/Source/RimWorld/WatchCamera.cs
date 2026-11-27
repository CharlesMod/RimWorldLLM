using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimWorldLLM
{
    /// <summary>
    /// Watch mode ("ant farm"): a screen director that cuts between the colony's events on its own. The Verse-free
    /// <see cref="WatchDirector"/> scores and picks (events, then everyday scenes from <see cref="SceneFinder"/>,
    /// story-thread follows and filler); <see cref="BattleDirector"/> frames fights; <see cref="FollowCam"/> smooths
    /// motion and zoom. This class feeds them (events, light snapshots every few seconds, a hostile scan once a second),
    /// moves the camera through <see cref="DirectorCamera"/>, and writes one short caption per shot to the chat log.
    /// Camera only: never drafts or opens windows; it selects the followed pawn only with watchSelectFollowed on.
    /// The player touching the camera pauses it (watchPlayerPauseSec). On/off is the mod setting <c>watchMode</c>
    /// (default off); the player's zoom comes back when it turns off. Game thread only.
    /// </summary>
    public static class WatchCamera
    {
        const double QuietOfferEverySec = 3.0;    // quiet stretches: at most one scene/thread/filler offer this often
        const double BattleScanEverySec = 1.0;
        const double ThreadScanEverySec = 2.0;

        static readonly WatchDirector Dir = new WatchDirector();
        static readonly BattleDirector Battle = new BattleDirector();
        static readonly StoryThreads Threads = new StoryThreads();
        static readonly FollowCam Follow = new FollowCam();
        static readonly Dictionary<int, Pawn> Pawns = new Dictionary<int, Pawn>();
        static readonly HashSet<int> InBreak = new HashSet<int>();
        static readonly HashSet<int> KnownColonists = new HashSet<int>();
        static readonly Dictionary<int, int> ChildAges = new Dictionary<int, int>();
        static readonly HashSet<int> DownedInFight = new HashSet<int>();
        static readonly List<FightPoint> Cols = new List<FightPoint>(), Hostiles = new List<FightPoint>();
        static readonly List<ScenePawn> Snap = new List<ScenePawn>();
        static readonly HashSet<string> Groups = new HashSet<string>();
        static WatchShot _shot;
        static BattleShot _battleShot;
        static string _battleKind;
        static double _nextQuiet, _nextBattleScan, _nextThreadScan, _lastFrame = -1;
        static int _wanderIx;
        static bool _warned, _knownFilled, _wasPaused;
        static float _playerZoom = -1f;
        static Game _zoomGame;
        static long _tick;
        static int _selectedFor = -1;
        static KeyBindingDef _key;

        static double Now { get { return Time.realtimeSinceStartupAsDouble; } }
        static long Tick { get { return Find.TickManager != null ? Find.TickManager.TicksGame : 0; } }

        public static bool Enabled
        {
            get { var s = RimWorldLLMMod.Settings; return s != null && s.watchMode && Current.ProgramState == ProgramState.Playing; }
        }

        public static void SetEnabled(bool on, bool announce)
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null || s.watchMode == on) return;
            if (!on) RestoreZoom();
            s.watchMode = on;
            try { RimWorldLLMMod.Instance.WriteSettings(); } catch (Exception) { }
            Reset();
            if (on) RememberZoom();
            if (announce) Messages.Message(UiUtil.T(on ? "RWL_Watch_MsgOn" : "RWL_Watch_MsgOff"), MessageTypeDefOf.SilentInput, false);
        }

        public static void Toggle() { var s = RimWorldLLMMod.Settings; if (s != null) SetEnabled(!s.watchMode, true); }

        public static void Reset()
        {
            Dir.Clear(); Battle.Clear(); Threads.Clear(); Follow.Reset(); Pawns.Clear(); InBreak.Clear(); KnownColonists.Clear();
            ChildAges.Clear(); DownedInFight.Clear();
            _shot = null; _battleShot = null; _battleKind = null; _nextQuiet = 0; _nextBattleScan = 0; _nextThreadScan = 0;
            _knownFilled = false; _lastFrame = -1; _selectedFor = -1; _wasPaused = false;
            DirectorCamera.ForgetPlaced();
        }

        public static void OnGUIKey()
        {
            if (Event.current == null || Event.current.type != EventType.KeyDown) return;
            if (_key == null) _key = DefDatabase<KeyBindingDef>.GetNamedSilentFail("RWL_ToggleWatchMode");
            if (_key != null && _key.KeyDownEvent) { Toggle(); Event.current.Use(); }
        }

        // ------------------------------------------------------------------ zoom memory
        static void RememberZoom()
        {
            try { var cam = Find.CameraDriver; if (cam != null && Current.ProgramState == ProgramState.Playing) { _playerZoom = cam.RootSize; _zoomGame = Current.Game; } }
            catch (Exception ex) { WarnOnce(ex); }
        }

        static void RestoreZoom()
        {
            try
            {
                var cam = Find.CameraDriver;
                var s = RimWorldLLMMod.Settings;
                if (cam != null && _playerZoom > 0 && _zoomGame == Current.Game && s != null && s.watchZoom && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                {
                    Vector3 p = cam.MyCamera.transform.position;
                    DirectorCamera.Place(cam, p.x, p.z, _playerZoom);
                }
            }
            catch (Exception ex) { WarnOnce(ex); }
            _playerZoom = -1f;
        }

        static void ZoomLimits(CameraDriver cam, out double min, out double max)
        {
            min = 11; max = 60;
            try { var r = cam.config.sizeRange; if (r.max > r.min) { min = r.min; max = r.max; } } catch (Exception) { }
        }

        // ------------------------------------------------------------------ feeds
        static void Offer(WatchEvent w)
        {
            if (Pawns.Count > 256) Pawns.Clear();
            Dir.Offer(w, Now);
        }

        /// <summary>Any published colony event (death, injury, mental_break, gathering, funeral, birth, masterwork, ship_part, art_created, social).</summary>
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
                ThreadsFromEvent(e, phase);
                string kind = WatchKinds.FromEvent(e.Type, state, gk, phase);
                if (kind == null) return;
                if (kind == WatchKinds.Art && !EventIsPlayers(e)) return;
                Pawn p = e.Pawns.Count > 0 ? e.Pawns[0].Key : null;
                IntVec3 at = e.At ?? (p != null ? p.PositionHeld : e.Map.Center);
                var w = new WatchEvent
                {
                    Kind = kind, MapId = e.Map.uniqueID, X = at.x, Z = at.z,
                    Severity = 0.5 + e.Severity,
                    Name = p != null ? p.LabelShort : null,
                    Style = ShotStyle.Follow
                };
                if (p != null && kind != WatchKinds.Wedding && kind != WatchKinds.Party && kind != WatchKinds.Funeral)
                { w.Key = p.thingIDNumber; w.TargetId = p.thingIDNumber; Pawns[p.thingIDNumber] = p; }
                else w.Style = ShotStyle.Place;
                if (kind == WatchKinds.MentalBreak && p != null) InBreak.Add(p.thingIDNumber);
                if (kind == WatchKinds.Funeral && d != null) w.Detail = (string)d["deceased_name"];
                if (kind == WatchKinds.Masterwork && d != null) w.Detail = (string)d["quality"] == "legendary" ? "legendary " + (string)d["label"] : (string)d["label"];
                if (kind == WatchKinds.ShipPart && d != null) w.Detail = (string)d["label"];
                Offer(w);
            }
            catch (Exception ex) { WarnOnce(ex); }
        }

        /// <summary>Story threads from events: a death (for those close to them), a funeral ending, a breakup.</summary>
        static void ThreadsFromEvent(EventSpec e, string phase)
        {
            long tick = Tick;
            if (e.Type == "death" && e.Pawns.Count > 0)
            {
                Pawn victim = e.Pawns[0].Key;
                if (victim == null) return;
                foreach (var c in e.Map.mapPawns.FreeColonistsSpawned)
                {
                    if (c == victim || c.Dead) continue;
                    bool close = false;
                    try { close = c.GetRelations(victim).Any() || c.relations != null && c.relations.OpinionOf(victim) >= 30; } catch (Exception) { }
                    if (close) Threads.Start(c.thingIDNumber, c.LabelShort, ThreadReasons.Loss(victim.LabelShort), tick);
                }
            }
            else if (e.Type == "funeral" && phase == "end" || e.Type == "gathering_end" && e.Data != null && (string)e.Data["kind"] == "funeral")
                Threads.Relabel(ThreadReasons.LossPrefix, ThreadReasons.Funeral, tick);
            else if (e.Type == "social" && e.Data != null && string.Equals((string)e.Data["interaction"], "Breakup", StringComparison.OrdinalIgnoreCase))
                foreach (var kv in e.Pawns)
                {
                    var p = kv.Key;
                    if (p != null && p.ageTracker != null && p.ageTracker.AgeBiologicalYears >= SceneFinder.AdultAge && p.IsColonist)
                        Threads.Start(p.thingIDNumber, p.LabelShort, ThreadReasons.Breakup, tick);
                }
        }

        static bool EventIsPlayers(EventSpec e)
        {
            return e.Pawns.Count > 0 && e.Pawns[0].Key.Faction != null && e.Pawns[0].Key.Faction.IsPlayer;
        }

        /// <summary>A raid arrived: aim at the raiders' centre (the map edge side they come from), wide.</summary>
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
                var w = new WatchEvent { Kind = WatchKinds.Raid, MapId = map.uniqueID, X = c.x, Z = c.z, Style = ShotStyle.Place };
                if (n > 0)
                {
                    w.X = (int)(sx / n); w.Z = (int)(sz / n);
                    string dir = WatchKinds.Direction(sx / n - c.x, sz / n - c.z);
                    if (dir != "the centre") w.Detail = dir;
                    w.TargetId = first.thingIDNumber; Pawns[first.thingIDNumber] = first; w.Style = ShotStyle.Follow;
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

        /// <summary>Called every frame while watch mode is off: when the setting was turned off elsewhere (the settings
        /// window), give the player's zoom back once.</summary>
        public static void UpdateOff()
        {
            if (_playerZoom > 0) { RestoreZoom(); Reset(); }
        }

        // ------------------------------------------------------------------ per frame
        public static void Update()
        {
            if (VisionCapture.Holding) return;
            try { Step(); } catch (Exception ex) { WarnOnce(ex); }
        }

        static void Step()
        {
            var map = Find.CurrentMap;
            var cam = Find.CameraDriver;
            if (map == null || cam == null || Current.ProgramState != ProgramState.Playing) return;
            double now = Now;
            double dt = _lastFrame < 0 ? 0 : Math.Min(0.1, now - _lastFrame);
            _lastFrame = now;
            var s = RimWorldLLMMod.Settings;
            Dir.PlayerPauseSec = Math.Max(0, s.watchPlayerPauseSec);
            Dir.Pace = DirectorPacing.FromInt(s.watchPace);
            long tick = Tick;
            _tick = tick;
            if (Dir.SubjectBoost == null) Dir.SubjectBoost = id => Threads.Boost(id, _tick);
            if (_playerZoom < 0 || _zoomGame != Current.Game) { _playerZoom = cam.RootSize; _zoomGame = Current.Game; }   // watch mode was already on when the game loaded

            if (DirectorCamera.ManualInput() || DirectorCamera.MovedByOthers(cam))
            {
                Dir.PlayerTouched(now);
                _playerZoom = cam.RootSize;   // the player's own zoom is what comes back when watch mode turns off
                Follow.Reset();
            }
            bool paused = Dir.IsPaused(now);
            if (paused) { _wasPaused = true; DirectorCamera.ForgetPlaced(); }
            else if (_wasPaused) { _wasPaused = false; Follow.Reset(); }

            if (now >= _nextThreadScan) { _nextThreadScan = now + ThreadScanEverySec; ScanThreads(map, tick); }
            if (now >= _nextBattleScan) { _nextBattleScan = now + BattleScanEverySec; ScanBattle(map, cam, now); }

            if (Battle.Active)
            {
                var bs = Battle.Shot(now);
                if (bs != null)
                {
                    if (_battleShot == null || bs.Kind != _battleKind) { Dir.EndShot(); _shot = null; }
                    _battleShot = bs; _battleKind = bs.Kind;
                    if (bs.Caption != null && s.watchCaptions && Dir.TryCaption(bs.Caption, true, now)) WriteCaption(bs.Caption);
                    if (!paused) DriveBattle(bs, map, cam, dt);
                    return;
                }
            }
            if (_battleShot != null) { _battleShot = null; _battleKind = null; }

            if (now >= _nextQuiet && Dir.ShotOver(now) && !paused)
            {
                _nextQuiet = now + QuietOfferEverySec;
                OfferQuiet(map, tick);
            }

            var shot = Dir.Next(now);
            if (shot != null) StartShot(shot, cam);
            if (_shot == null || paused) return;
            Drive(_shot, map, cam, dt);
        }

        // ------------------------------------------------------------------ quiet stretches
        /// <summary>The shot is over: offer the current scenes, the strongest story-thread pawn and one colonist as filler.
        /// Pending events still win in the director; among the rest it picks by score, variety and the no-repeat window
        /// (offers merge by scene key, so the pending list stays small).</summary>
        static void OfferQuiet(Map map, long tick)
        {
            foreach (var c in FindScenes(map).Take(4))
            {
                var w = new WatchEvent
                {
                    Kind = c.Kind, MapId = map.uniqueID, X = c.X, Z = c.Z, Severity = c.Severity, Caption = c.Caption,
                    Style = c.Style, Ids = c.Ids, SceneKey = c.Key, Key = -1, TargetId = c.FollowId
                };
                Offer(w);
            }
            Threads.Prune(tick);
            foreach (int id in Threads.Active(tick))
            {
                Pawn p;
                if (!Pawns.TryGetValue(id, out p) || p == null || !p.Spawned || p.Map != map || p.Dead) continue;
                Offer(new WatchEvent { Kind = WatchKinds.Thread, Key = id, TargetId = id, MapId = map.uniqueID, X = p.Position.x, Z = p.Position.z, Name = p.LabelShort, Caption = Threads.Caption(id, tick) });
                break;
            }
            OfferWander(map);   // lowest weight: shown only when no scene or thread is free
        }

        static void OfferWander(Map map)
        {
            var cols = map.mapPawns.FreeColonistsSpawned;
            if (cols.Count == 0) return;
            var p = cols[_wanderIx++ % cols.Count];
            Pawns[p.thingIDNumber] = p;
            Offer(new WatchEvent { Kind = WatchKinds.Wander, Key = p.thingIDNumber, TargetId = p.thingIDNumber, MapId = map.uniqueID, X = p.Position.x, Z = p.Position.z, Name = p.LabelShort });
        }

        static List<SceneCandidate> FindScenes(Map map)
        {
            var s = RimWorldLLMMod.Settings;
            Groups.Clear();
            if (s.watchSceneMeals) Groups.Add(SceneKinds.GroupMeals);
            if (s.watchSceneRecreation) Groups.Add(SceneKinds.GroupRecreation);
            if (s.watchSceneWork) Groups.Add(SceneKinds.GroupWork);
            if (s.watchSceneNight) Groups.Add(SceneKinds.GroupNight);
            if (s.watchSceneMedical) Groups.Add(SceneKinds.GroupMedical);
            if (s.watchSceneChildren) Groups.Add(SceneKinds.GroupChildren);
            if (s.watchSceneRomance) Groups.Add(SceneKinds.GroupRomance);
            if (Groups.Count == 0) return new List<SceneCandidate>();
            Snap.Clear();
            foreach (var p in map.mapPawns.FreeColonistsSpawned) AddSnap(p, false);
            foreach (var p in map.mapPawns.PrisonersOfColonySpawned) AddSnap(p, true);
            int hour = GenLocalDate.HourOfDay(map);
            return SceneFinder.Find(Snap, hour, Groups);
        }

        static void AddSnap(Pawn p, bool prisoner)
        {
            if (p == null || p.Dead || p.Downed || !p.Spawned) return;
            try
            {
                Pawns[p.thingIDNumber] = p;
                var sp = new ScenePawn { Id = p.thingIDNumber, Name = p.LabelShort, X = p.Position.x, Z = p.Position.z, Prisoner = prisoner };
                var room = p.GetRoom();
                if (room != null && !room.PsychologicallyOutdoors)
                {
                    sp.RoomId = room.ID;
                    var role = room.Role;
                    if (role != null && role != RoomRoleDefOf.None) sp.RoomLabel = role.label;
                }
                var job = p.CurJob;
                if (job != null)
                {
                    sp.Job = job.def.defName;
                    sp.JoyKind = job.def.joyKind != null ? job.def.joyKind.defName : null;
                    sp.WorkType = job.workGiverDef != null && job.workGiverDef.workType != null ? job.workGiverDef.workType.defName : null;
                    var tp = job.targetA.Thing as Pawn;
                    if (tp != null) { sp.TargetId = tp.thingIDNumber; sp.TargetName = tp.LabelShort; }
                }
                var walk = VivariumGame.State != null ? VivariumGame.State.TradeWalk : null;
                if (walk != null && walk.NegotiatorId == p.thingIDNumber) sp.TradeWith = walk.Trader;
                sp.Awake = p.Awake();
                sp.InBed = p.InBed();
                var lord = p.GetLord();
                sp.InGathering = lord != null && lord.LordJob is LordJob_VoluntarilyJoinable;
                sp.AgeYears = p.ageTracker != null ? p.ageTracker.AgeBiologicalYears : 30;
                sp.Child = p.DevelopmentalStage == DevelopmentalStage.Child || p.DevelopmentalStage == DevelopmentalStage.Baby;
                if (p.relations != null)
                {
                    List<int> partners = null;
                    foreach (var r in p.relations.DirectRelations)
                        if ((r.def == PawnRelationDefOf.Lover || r.def == PawnRelationDefOf.Spouse || r.def == PawnRelationDefOf.Fiance) && r.otherPawn != null)
                            (partners ?? (partners = new List<int>())).Add(r.otherPawn.thingIDNumber);
                    if (partners != null) sp.Partners = partners.ToArray();
                }
                Snap.Add(sp);
            }
            catch (Exception ex) { WarnOnce(ex); }
        }

        // ------------------------------------------------------------------ threads (polled)
        /// <summary>New arrivals, recovery from a break, and children's growth moments (ages 7, 10, 13).</summary>
        static void ScanThreads(Map map, long tick)
        {
            foreach (var p in map.mapPawns.FreeColonistsSpawned)
            {
                int id = p.thingIDNumber;
                if (KnownColonists.Add(id) && _knownFilled) { Pawns[id] = p; Threads.Start(id, p.LabelShort, ThreadReasons.Arrival, tick); }
                if (InBreak.Contains(id) && !p.InMentalState) { InBreak.Remove(id); Pawns[id] = p; Threads.Start(id, p.LabelShort, ThreadReasons.Recovered, tick); }
                if (ModsConfig.BiotechActive && p.ageTracker != null && p.DevelopmentalStage == DevelopmentalStage.Child)
                {
                    int age = p.ageTracker.AgeBiologicalYears, was;
                    if (ChildAges.TryGetValue(id, out was) && age > was && (age == 7 || age == 10 || age == 13))
                    { Pawns[id] = p; Threads.Start(id, p.LabelShort, ThreadReasons.Growth, tick); }
                    ChildAges[id] = age;
                }
            }
            _knownFilled = true;
            if (KnownColonists.Count > 512) { KnownColonists.Clear(); _knownFilled = false; }
        }

        // ------------------------------------------------------------------ battles
        static void ScanBattle(Map map, CameraDriver cam, double now)
        {
            Cols.Clear(); Hostiles.Clear();
            int animals = 0, mechs = 0, total = 0;
            var targets = map.attackTargetsCache.TargetsHostileToColony;
            if (targets.Count == 0 && !Battle.Active) return;
            foreach (var t in targets)
            {
                var hp = t.Thing as Pawn;
                if (hp == null || !hp.Spawned || hp.Downed || hp.Dead || !GenHostility.IsActiveThreatToPlayer(t)) continue;
                Hostiles.Add(new FightPoint(hp.thingIDNumber, hp.Position.x, hp.Position.z));
                total++;
                if (hp.RaceProps.Animal) animals++; else if (hp.RaceProps.IsMechanoid) mechs++;
            }
            int standing = 0;
            foreach (var c in map.mapPawns.FreeColonistsSpawned)
            {
                if (c.Downed)
                {
                    if (Battle.Fighting && DownedInFight.Add(c.thingIDNumber)) { Pawns[c.thingIDNumber] = c; Battle.Downed(c.thingIDNumber, c.LabelShort, now); }
                    continue;
                }
                standing++;
                Cols.Add(new FightPoint(c.thingIDNumber, c.Position.x, c.Position.z));
            }
            double min, max; ZoomLimits(cam, out min, out max);
            double aspect = Screen.height > 0 ? (double)Screen.width / Screen.height : 16.0 / 9.0;
            var frame = BattleFrame.Compute(Cols, Hostiles, aspect, min, max);
            string noun = total > 0 && animals == total ? "the animals" : total > 0 && mechs == total ? "the mechanoids" : "the raiders";
            bool wasActive = Battle.Active;
            Battle.Update(now, frame, noun, total, standing);
            if (!Battle.Active && wasActive) DownedInFight.Clear();
            if (!wasActive && Battle.Active) DownedInFight.Clear();
        }

        static void DriveBattle(BattleShot bs, Map map, CameraDriver cam, double dt)
        {
            double tx = bs.X, tz = bs.Z, size = bs.Size;
            if (bs.Kind == WatchKinds.Downed)
            {
                Pawn p;
                if (Pawns.TryGetValue(bs.TargetId, out p) && p != null && p.Spawned && p.Map == map) { var v = p.DrawPos; tx = v.x; tz = v.z; }
            }
            Move(cam, tx, tz, size, true, dt);
        }

        // ------------------------------------------------------------------ shots
        static void StartShot(WatchShot shot, CameraDriver cam)
        {
            _shot = shot;   // the follow spring keeps its velocity: a new subject nearby is a glide, not a stop-start
            if (shot.Caption != null && RimWorldLLMMod.Settings.watchCaptions) WriteCaption(shot.Caption);
            // A shot on another map is a cut there (camera only: CameraJumper.TryJump, never TryJumpAndSelect).
            Map target = null;
            foreach (var m in Find.Maps) if (m.uniqueID == shot.MapId) { target = m; break; }
            if (target != null && target != Find.CurrentMap)
            {
                try { CameraJumper.TryJump(new IntVec3(shot.X, 0, shot.Z), target); } catch (Exception ex) { WarnOnce(ex); }
            }
            SelectFollowed(shot);
        }

        static void WriteCaption(string caption)
        {
            try { VivariumGame.State.Chat.Add(ChatKinds.Camera, caption, VivariumGame.NowTick); } catch (Exception ex) { WarnOnce(ex); }
        }

        /// <summary>Setting watchSelectFollowed (default off): select the followed pawn, nothing else (no draft, no sound).</summary>
        static void SelectFollowed(WatchShot shot)
        {
            if (!RimWorldLLMMod.Settings.watchSelectFollowed || shot.Style != ShotStyle.Follow || shot.TargetId < 0 || shot.TargetId == _selectedFor) return;
            Pawn p;
            if (!Pawns.TryGetValue(shot.TargetId, out p) || p == null || !p.Spawned || p.Dead || p.Map != Find.CurrentMap) return;
            try
            {
                var sel = Find.Selector;
                if (sel == null || sel.IsSelected(p)) return;
                sel.ClearSelection();
                sel.Select(p, false, false);
                _selectedFor = shot.TargetId;
            }
            catch (Exception ex) { WarnOnce(ex); }
        }

        static void Drive(WatchShot shot, Map map, CameraDriver cam, double dt)
        {
            if (map.uniqueID != shot.MapId) return;
            double tx, tz, fit = -1;
            if (shot.Style == ShotStyle.Group && shot.Ids != null && shot.Ids.Count > 0)
            {
                if (!ResolveGroup(shot, map, out tx, out tz, out fit) && !ResolveOne(shot, map, out tx, out tz)) return;
            }
            else if (!ResolveOne(shot, map, out tx, out tz)) return;
            double min, max; ZoomLimits(cam, out min, out max);
            double size = DirectorZoom.Target(shot.Kind, fit, min, max);
            Move(cam, tx, tz, size, false, dt);
        }

        /// <summary>One frame of motion: a cut when far, else the follow spring; zoom eases toward size when zoom is on.</summary>
        static void Move(CameraDriver cam, double tx, double tz, double size, bool battle, double dt)
        {
            var s = RimWorldLLMMod.Settings;
            Vector3 camPos = cam.MyCamera.transform.position;
            double cx = camPos.x, cz = camPos.z;
            double min, max; ZoomLimits(cam, out min, out max);
            double cur = cam.RootSize;
            double want = s.watchZoom ? DirectorZoom.Clamp(size, min, max) : cur;
            if (Follow.ShouldCut(cx, cz, tx, tz))
            {
                DirectorCamera.Cut(cam, (float)tx, (float)tz);
                Follow.Reset();
                return;
            }
            Follow.Step(ref cx, ref cz, tx, tz, dt);
            double ns = s.watchZoom ? Follow.StepZoom(cur, want, dt) : cur;
            if (Math.Abs(cx - camPos.x) < 1e-4 && Math.Abs(cz - camPos.z) < 1e-4 && Math.Abs(ns - cur) < 1e-4) return;   // dead zone: hold still
            DirectorCamera.Place(cam, (float)cx, (float)cz, (float)ns);
        }

        /// <summary>Where a one-pawn shot is now: the followed pawn or its corpse, else the cell it was offered at.
        /// A followed pawn who left the map or despawned ends the shot.</summary>
        static bool ResolveOne(WatchShot shot, Map map, out double x, out double z)
        {
            x = z = 0;
            Pawn p;
            if (shot.TargetId >= 0 && Pawns.TryGetValue(shot.TargetId, out p) && p != null)
            {
                if (!p.Dead && p.Spawned && p.Map == map) { var v = p.DrawPos; x = v.x; z = v.z; return true; }
                var corpse = p.Corpse;
                if (p.Dead && corpse != null && corpse.Spawned && corpse.Map == map) { var v = corpse.DrawPos; x = v.x; z = v.z; return true; }
                if (shot.Style == ShotStyle.Follow) { Dir.EndShot(); _shot = null; return false; }   // gone: the shot ends
            }
            var c = new IntVec3(shot.X, 0, shot.Z);
            if (!c.InBounds(map)) return false;
            var cv = c.ToVector3Shifted(); x = cv.x; z = cv.z;
            return true;
        }

        static bool ResolveGroup(WatchShot shot, Map map, out double x, out double z, out double fit)
        {
            x = z = 0; fit = -1;
            double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue; int n = 0;
            foreach (int id in shot.Ids)
            {
                Pawn p;
                if (!Pawns.TryGetValue(id, out p) || p == null || !p.Spawned || p.Map != map) continue;
                var v = p.DrawPos;
                minX = Math.Min(minX, v.x); maxX = Math.Max(maxX, v.x); minZ = Math.Min(minZ, v.z); maxZ = Math.Max(maxZ, v.z); n++;
            }
            if (n == 0) return false;
            x = (minX + maxX) / 2; z = (minZ + maxZ) / 2;
            double aspect = Screen.height > 0 ? (double)Screen.width / Screen.height : 16.0 / 9.0;
            fit = DirectorZoom.Fit(minX, minZ, maxX, maxZ, x, z, aspect);
            return true;
        }
    }
}
