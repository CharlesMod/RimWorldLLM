using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// The game side of GET /vision (the narrator can look at the screen). Driven once per frame from RWLBridge.FrameUpdate.
    /// Pictures are the map only: the main camera is rendered into a RenderTexture, so no interface is in them.
    /// view: renders at once. close/base/wide: RimWorld draws only the map sections in the camera's view, so frame N moves
    /// the camera (the director and watch mode stand still while <see cref="Holding"/>), frame N+1 renders after the map
    /// drew with the new view and puts the camera back. The player sees about a frame of blink. Encoding is on a worker thread.
    /// </summary>
    public static class VisionCapture
    {
        public static readonly VisionDesk Desk = new VisionDesk();

        /// <summary>True while the camera is moved for a picture; DirectorCamera and WatchCamera do nothing meanwhile.</summary>
        public static bool Holding { get; private set; }

        /// <summary>Row 0 of ReadPixels is the bottom; the encoder is given top-first rows. Flip to false if the pictures come out upside down the other way.</summary>
        public static bool FlipRows = true;

        static VisionJob _job;
        static int _mapId;
        static Vector3 _savedPos;
        static float _savedSize;
        static string _targetName;

        public static void Update()
        {
            try { Step(); }
            catch (Exception e)
            {
                Log.ErrorOnce("[RimWorldLLM] vision capture failed: " + e, 7731017);
                Abort("capture_failed", "internal error");
            }
        }

        static void Abort(string code, string detail)
        {
            try { Restore(); } catch { Holding = false; }
            var j = _job; _job = null;
            if (j != null) Desk.Fail(j, code, detail);
        }

        static void Restore()
        {
            if (!Holding) return;
            Holding = false;
            var cam = Find.CameraDriver;
            if (cam != null) cam.SetRootPosAndSize(_savedPos, _savedSize);
        }

        static void Step()
        {
            if (_job != null)
            {
                // frame N+1: the map has drawn with the moved camera
                var job = _job; _job = null;
                var map = Find.CurrentMap;
                if (Current.ProgramState != ProgramState.Playing || map == null || map.uniqueID != _mapId)
                {
                    _job = job; Abort("capture_failed", "the map changed"); return;
                }
                try { Render(job, map, _targetName); }
                finally { Restore(); }
                return;
            }

            var next = Desk.TryTakeJob();
            if (next == null) return;
            var cur = Find.CurrentMap;
            if (Current.ProgramState != ProgramState.Playing || cur == null || Find.CameraDriver == null || Find.Camera == null)
            {
                Desk.Fail(next, "capture_failed", "no map"); return;
            }
            if (GUIUtility.keyboardControl != 0) { Desk.Fail(next, "refused", "player_typing"); return; }

            var q = next.Query;
            if (q.Kind == VisionKind.View) { Render(next, cur, "what the player sees"); return; }

            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            CellFrame frame; string name;
            string err = Plan(q, cur, aspect, out frame, out name);
            if (err != null) { Desk.Fail(next, "no_target", err); return; }

            var driver = Find.CameraDriver;
            Vector3 camPos = driver.MyCamera.transform.position;
            _savedPos = camPos; _savedSize = driver.RootSize; _mapId = cur.uniqueID; _targetName = name;
            Holding = true;
            _job = next;
            driver.SetRootPosAndSize(new Vector3((float)frame.CenterX, camPos.y, (float)frame.CenterZ), frame.H / 2f);
        }

        // ------------------------------------------------------------------ planning
        static string Plan(VisionQuery q, Map map, float aspect, out CellFrame frame, out string name)
        {
            int mw = map.Size.x, mh = map.Size.z;
            frame = default(CellFrame); name = null;
            switch (q.Kind)
            {
                case VisionKind.Base:
                    frame = VisionGeometry.Base(HomeBox(map), aspect, mw, mh); name = "the base"; return null;
                case VisionKind.Wide:
                    frame = VisionGeometry.Wide(HomeBox(map), aspect, mw, mh); name = "the surroundings"; return null;
            }
            switch (q.TargetKind)
            {
                case VisionTargetKind.Room:
                {
                    Room room = null;
                    foreach (var r in map.regionGrid.AllRooms) if (r.ID == q.Id) { room = r; break; }
                    if (room == null) return "no room " + q.Id;
                    int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
                    foreach (var c in room.Cells)
                    {
                        if (c.x < x0) x0 = c.x; if (c.x > x1) x1 = c.x;
                        if (c.z < z0) z0 = c.z; if (c.z > z1) z1 = c.z;
                    }
                    if (x1 < x0) return "room " + q.Id + " has no cells";
                    frame = VisionGeometry.Close(new CellFrame(x0, z0, x1, z1), true, aspect, mw, mh);
                    name = "room " + q.Id; return null;
                }
                case VisionTargetKind.Thing:
                {
                    Thing t = null;
                    foreach (var x in map.listerThings.AllThings) if (x.thingIDNumber == q.Id) { t = x; break; }
                    if (t == null || !t.Spawned) return "no thing " + q.Id + " on this map";
                    return PlanThing(t, aspect, mw, mh, out frame, out name);
                }
                case VisionTargetKind.Pawn:
                {
                    Pawn p = null;
                    foreach (var x in map.mapPawns.AllPawnsSpawned) if (x.thingIDNumber == q.Id) { p = x; break; }
                    if (p == null) return "no pawn " + q.Id + " on this map";
                    return PlanThing(p, aspect, mw, mh, out frame, out name);
                }
                case VisionTargetKind.Cell:
                    if (q.X >= mw || q.Z >= mh) return "cell " + q.X + "," + q.Z + " is outside the map";
                    frame = VisionGeometry.Close(new CellFrame(q.X, q.Z, q.X, q.Z), false, aspect, mw, mh);
                    name = "cell " + q.X + "," + q.Z; return null;
                case VisionTargetKind.Dir:
                {
                    var box = HomeBox(map);
                    int x, z;
                    VisionGeometry.DirCell((int)box.CenterX, (int)box.CenterZ, q.Dir, q.Cells, mw, mh, out x, out z);
                    frame = VisionGeometry.Close(new CellFrame(x, z, x, z), false, aspect, mw, mh);
                    name = q.Cells + " cells " + q.Dir + " of the base"; return null;
                }
            }
            return "no target";
        }

        static string PlanThing(Thing t, float aspect, int mw, int mh, out CellFrame frame, out string name)
        {
            var r = t.OccupiedRect();
            frame = VisionGeometry.Close(new CellFrame(r.minX, r.minZ, r.maxX, r.maxZ), false, aspect, mw, mh);
            name = t is Pawn ? ((Pawn)t).LabelShortCap : t.LabelCap;
            return null;
        }

        /// <summary>Bounding box of the home area; else the colonists' bed centre or colonist centre +- 15; else the map centre +- 15.</summary>
        static CellFrame HomeBox(Map map)
        {
            var home = map.areaManager.Home;
            if (home != null && home.TrueCount > 0)
            {
                int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
                foreach (var c in home.ActiveCells)
                {
                    if (c.x < x0) x0 = c.x; if (c.x > x1) x1 = c.x;
                    if (c.z < z0) z0 = c.z; if (c.z > z1) z1 = c.z;
                }
                if (x1 >= x0) return new CellFrame(x0, z0, x1, z1);
            }
            var pts = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Select(b => b.Position).ToList();
            if (pts.Count == 0) pts = map.mapPawns.FreeColonistsSpawned.Select(p => p.Position).ToList();
            if (pts.Count == 0) return VisionGeometry.HomeFromCenter(map.Center.x, map.Center.z);
            return VisionGeometry.HomeFromCenter((int)pts.Average(c => c.x), (int)pts.Average(c => c.z));
        }

        // ------------------------------------------------------------------ render
        static void Render(VisionJob job, Map map, string targetName)
        {
            var cam = Find.Camera;
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
            int w, h;
            VisionGeometry.ImageSize(job.Query.Max, aspect, out w, out h);

            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            byte[] raw;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = prevTarget;
                cam.ResetAspect();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                raw = tex.GetRawTextureData();
                UnityEngine.Object.Destroy(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            var pos = cam.transform.position;
            var legend = VisionGeometry.LegendFromCamera(pos.x, pos.z, cam.orthographicSize, w, h, map.Size.x, map.Size.z, targetName);
            bool flip = FlipRows;
            Desk.CompleteEncoded(job, qual => Encode(raw, w, h, qual, flip), w, h, legend, Find.TickManager != null ? Find.TickManager.TicksGame : 0);
        }

        /// <summary>Worker thread. raw is RGB24 as ReadPixels left it (bottom row first).</summary>
        static byte[] Encode(byte[] raw, int w, int h, int quality, bool flip)
        {
            byte[] rows = raw;
            if (flip)
            {
                int stride = w * 3;
                rows = new byte[raw.Length];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * stride, rows, y * stride, stride);
            }
            return ImageConversion.EncodeArrayToJPG(rows, GraphicsFormat.R8G8B8_SRGB, (uint)w, (uint)h, 0, quality);
        }
    }
}
