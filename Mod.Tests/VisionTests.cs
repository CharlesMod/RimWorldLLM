using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class VisionTests
    {
        sealed class Rig
        {
            public double Now = 100;
            public bool Allowed = true;
            public readonly VisionDesk Desk;
            public readonly ApiRouter Router = new ApiRouter(new OpIdStore(() => 0));
            public readonly GameState State = new GameState { GameLoaded = true, Tick = 500 };
            public Rig()
            {
                Desk = new VisionDesk(() => Now, a => a());   // encode runs inline
                VisionRoutes.Register(Router, Desk, () => Allowed);
            }
            public ApiResult Get(string query) { return Router.Handle("GET", "/vision", query, null, State); }
            public static VisionLegend Legend() { return new VisionLegend { X0 = 10, Z0 = 20, X1 = 49, Z1 = 49, PxPerCell = 19.2, TargetName = "the base" }; }
            /// <summary>A fake game side: takes the job and completes it with a small image.</summary>
            public VisionJob Take() { return Desk.TryTakeJob(); }
            public void Finish(VisionJob j, int size = 1000)
            {
                Desk.CompleteEncoded(j, q => new byte[q == 80 ? size : 10], 768, 432, Legend(), 500);
            }
        }

        [Fact]
        public void PendingThenReadyWithLegend()
        {
            var r = new Rig();
            var a = r.Get("kind=base");
            Assert.True(a.Ok);
            Assert.Equal("pending", (string)a.Body["status"]);
            Assert.Equal(250, (int)a.Body["retry_ms"]);
            var job = r.Take();
            Assert.NotNull(job);
            Assert.Null(r.Take());   // handed out once
            Assert.Equal("pending", (string)r.Get("kind=base").Body["status"]);
            r.Finish(job);
            r.Now = 100.5;
            var b = r.Get("kind=base");
            Assert.Equal("ready", (string)b.Body["status"]);
            Assert.Equal("base", (string)b.Body["kind"]);
            Assert.Equal("image/jpeg", (string)b.Body["mime"]);
            Assert.Equal(768, (int)b.Body["width"]);
            Assert.Equal(432, (int)b.Body["height"]);
            Assert.Equal(1000, (int)b.Body["bytes"]);
            Assert.Equal(1000, Convert.FromBase64String((string)b.Body["image_b64"]).Length);
            Assert.Equal(500, (int)b.Body["captured_tick"]);
            Assert.Equal(0.5, (double)b.Body["age_s"], 3);
            var l = (JObject)b.Body["legend"];
            Assert.Equal("up", (string)l["north"]);
            Assert.Equal(10, (int)l["x0"]); Assert.Equal(20, (int)l["z0"]); Assert.Equal(49, (int)l["x1"]); Assert.Equal(49, (int)l["z1"]);
            Assert.Equal(40, (int)l["cells_w"]); Assert.Equal(30, (int)l["cells_h"]);
            Assert.Equal(19.2, (double)l["px_per_cell"], 3);
            Assert.Equal("hidden", (string)l["ui"]);
            Assert.Equal("the base", (string)l["target_name"]);
            Assert.False(string.IsNullOrEmpty((string)l["note"]));
        }

        [Theory]
        [InlineData("")]
        [InlineData("kind=nope")]
        [InlineData("kind=close")]
        [InlineData("kind=close&target=room:x")]
        [InlineData("kind=close&target=cell:5")]
        [InlineData("kind=close&target=cell:-1,4")]
        [InlineData("kind=close&target=dir:up:5")]
        [InlineData("kind=close&target=dir:north:0")]
        [InlineData("kind=close&target=dir:north:121")]
        [InlineData("kind=close&target=blob:3")]
        [InlineData("kind=base&max=big")]
        public void BadQueryIsBadRequest(string q)
        {
            var r = new Rig();
            var a = r.Get(q);
            Assert.False(a.Ok);
            Assert.Equal("bad_request", a.Error);
            Assert.Equal(400, a.Status);
            Assert.Null(r.Take());
        }

        [Fact]
        public void VisionOffIsRefused()
        {
            var r = new Rig { Allowed = false };
            var a = r.Get("kind=base");
            Assert.Equal("refused", a.Error);
            Assert.Equal("vision_off", (string)a.Body["detail"]);
            Assert.Null(r.Take());
        }

        [Fact]
        public void NoGameIsRefusedByRouter()
        {
            var r = new Rig();
            var a = r.Router.Handle("GET", "/vision", "kind=base", null, GameState.Menu);
            Assert.Equal("no_game", a.Error);
        }

        [Fact]
        public void GameSideFailuresPassThrough()
        {
            var r = new Rig();
            r.Get("kind=view");
            r.Desk.Fail(r.Take(), "refused", "player_typing");
            var a = r.Get("kind=view");
            Assert.Equal("refused", a.Error);
            Assert.Equal("player_typing", (string)a.Body["detail"]);
            // the failure was delivered once; the next ask starts a new job
            Assert.Equal("pending", (string)r.Get("kind=view").Body["status"]);
            r.Desk.Fail(r.Take(), "no_target", "no room 9");
            Assert.Equal("no_target", r.Get("kind=view").Error);
            Assert.Equal(404, Envelope.StatusFor("no_target"));
        }

        [Fact]
        public void BusyForADifferentQuery()
        {
            var r = new Rig();
            r.Get("kind=base");
            var b = r.Get("kind=wide");
            Assert.Equal("busy", b.Error);
            Assert.Equal(503, b.Status);
            var j = r.Take();
            Assert.Equal("busy", r.Get("kind=wide").Error);
            r.Finish(j);
            Assert.Equal("pending", (string)r.Get("kind=wide").Body["status"]);   // desk is free now
        }

        [Fact]
        public void AnOldFailureDoesNotBlockADifferentQuery()
        {
            var r = new Rig();
            r.Get("kind=base");
            r.Desk.Fail(r.Take(), "capture_failed", "x");
            Assert.Equal("pending", (string)r.Get("kind=wide").Body["status"]);
        }

        [Fact]
        public void StuckJobTimesOut()
        {
            var r = new Rig();
            r.Get("kind=base");
            r.Take();
            r.Now += VisionDesk.JobTimeoutSeconds + 1;
            Assert.Equal("capture_failed", r.Get("kind=base").Error);
            Assert.False(r.Desk.Busy);
        }

        [Fact]
        public void CacheHoldsTwoSecondsAndTwoImages()
        {
            var r = new Rig();
            foreach (var k in new[] { "base", "wide", "view" })
            {
                r.Get("kind=" + k);
                r.Finish(r.Take());
            }
            Assert.Equal("pending", (string)r.Get("kind=base").Body["status"]);   // oldest evicted -> a new job
            r.Desk.Fail(r.Take(), "capture_failed", "drop");
            r.Get("kind=base");   // clears the failure
            Assert.Equal("ready", (string)r.Get("kind=wide").Body["status"]);
            Assert.Equal("ready", (string)r.Get("kind=view").Body["status"]);
            r.Now += 2.5;
            Assert.Equal("pending", (string)r.Get("kind=view").Body["status"]);
        }

        [Fact]
        public void CacheKeyIsKindTargetAndMax()
        {
            var r = new Rig();
            r.Get("kind=close&target=cell:5,6");
            r.Finish(r.Take());
            Assert.Equal("ready", (string)r.Get("kind=close&target=cell:5,6").Body["status"]);
            Assert.Equal("cell:5,6", (string)r.Get("kind=close&target=cell:5,6").Body["target"]);
            Assert.Equal("pending", (string)r.Get("kind=close&target=cell:5,6&max=500").Body["status"]);
        }

        [Fact]
        public void OversizeReEncodesAtLowerQuality()
        {
            var r = new Rig();
            r.Get("kind=base");
            var qualities = new List<int>();
            r.Desk.CompleteEncoded(r.Take(), q => { qualities.Add(q); return new byte[q == 80 ? VisionDesk.MaxJpegBytes + 1 : 2000]; }, 768, 432, Rig.Legend(), 1);
            Assert.Equal(new[] { 80, 60 }, qualities);
            Assert.Equal(2000, (int)r.Get("kind=base").Body["bytes"]);
        }

        [Fact]
        public void StillTooLargeIsCaptureFailed()
        {
            var r = new Rig();
            r.Get("kind=base");
            r.Desk.CompleteEncoded(r.Take(), q => new byte[VisionDesk.MaxJpegBytes + 1], 768, 432, Rig.Legend(), 1);
            Assert.Equal("capture_failed", r.Get("kind=base").Error);
        }

        [Fact]
        public void EncoderThrowingIsCaptureFailed()
        {
            var r = new Rig();
            r.Get("kind=base");
            r.Desk.CompleteEncoded(r.Take(), q => throw new InvalidOperationException("boom"), 768, 432, Rig.Legend(), 1);
            var a = r.Get("kind=base");
            Assert.Equal("capture_failed", a.Error);
            Assert.DoesNotContain("boom", (string)a.Body["detail"] ?? "");
        }

        [Fact]
        public void EncodingRunsOffThreadWhenAsked()
        {
            var jobs = new List<Action>();
            var desk = new VisionDesk(() => 1, a => jobs.Add(a));
            var router = new ApiRouter(new OpIdStore(() => 0));
            VisionRoutes.Register(router, desk, () => true);
            var st = new GameState { GameLoaded = true };
            router.Handle("GET", "/vision", "kind=view", null, st);
            desk.CompleteEncoded(desk.TryTakeJob(), q => new byte[10], 4, 4, Rig.Legend(), 1);
            Assert.Equal("pending", (string)router.Handle("GET", "/vision", "kind=view", null, st).Body["status"]);
            jobs[0]();
            Assert.Equal("ready", (string)router.Handle("GET", "/vision", "kind=view", null, st).Body["status"]);
        }

        // ------------------------------------------------------------------ parsing
        static VisionQuery P(string kind, string target = null, string max = null)
        {
            VisionQuery q;
            Assert.Null(VisionParse.TryParse(kind, target, max, out q));
            return q;
        }

        [Fact]
        public void TargetFormsParse()
        {
            var q = P("close", "room:12");
            Assert.Equal(VisionTargetKind.Room, q.TargetKind); Assert.Equal(12, q.Id);
            q = P("close", "thing:345"); Assert.Equal(VisionTargetKind.Thing, q.TargetKind); Assert.Equal(345, q.Id);
            q = P("close", "pawn:7"); Assert.Equal(VisionTargetKind.Pawn, q.TargetKind);
            q = P("close", "cell:30, 41"); Assert.Equal(VisionTargetKind.Cell, q.TargetKind); Assert.Equal(30, q.X); Assert.Equal(41, q.Z);
            Assert.Equal("cell:30,41", q.Target);
            q = P("close", "dir:NorthEast:25"); Assert.Equal(VisionTargetKind.Dir, q.TargetKind); Assert.Equal("northeast", q.Dir); Assert.Equal(25, q.Cells);
            foreach (var d in VisionParse.Dirs) Assert.Equal(d, P("close", "dir:" + d + ":120").Dir);
            Assert.Equal(1, P("close", "dir:west:1").Cells);
            Assert.Equal(VisionKind.Base, P("BASE").Kind);
            Assert.Null(P("base").Target);
        }

        [Fact]
        public void MaxIsClampedPerKind()
        {
            Assert.Equal(768, P("close", "cell:1,1").Max);
            Assert.Equal(768, P("base").Max);
            Assert.Equal(1024, P("wide").Max);
            Assert.Equal(768, P("view").Max);
            Assert.Equal(768, P("base", null, "5000").Max);
            Assert.Equal(1024, P("wide", null, "5000").Max);
            Assert.Equal(256, P("base", null, "10").Max);
            Assert.Equal(500, P("wide", null, "500").Max);
        }

        // ------------------------------------------------------------------ geometry
        const double A = 16.0 / 9.0;

        static void AssertAspect(CellFrame f)
        {
            Assert.InRange(f.W / (double)f.H, A - 0.12, A + 0.12);
        }

        [Fact]
        public void ImageSizeFollowsAspect()
        {
            int w, h;
            VisionGeometry.ImageSize(768, A, out w, out h);
            Assert.Equal(768, w); Assert.Equal(432, h);
            VisionGeometry.ImageSize(512, 0.5, out w, out h);
            Assert.Equal(512, h); Assert.Equal(256, w);
        }

        [Fact]
        public void CloseRoomIsBoundsPlusMargin()
        {
            var f = VisionGeometry.Close(new CellFrame(50, 50, 69, 54), true, A, 250, 250);   // 20x5 room
            Assert.True(f.W >= 24 && f.H >= 9);
            Assert.True(f.X0 <= 48 && f.X1 >= 71 && f.Z0 <= 48 && f.Z1 >= 56);
            AssertAspect(f);
        }

        [Fact]
        public void CloseRoomIsAtLeastTenWide()
        {
            var f = VisionGeometry.Close(new CellFrame(100, 100, 102, 102), true, A, 250, 250);
            Assert.True(f.W >= 10);
            Assert.True(f.X0 <= 98 && f.X1 >= 104);
            AssertAspect(f);
        }

        [Fact]
        public void CloseCellIsAbout14x10()
        {
            var f = VisionGeometry.Close(new CellFrame(100, 100, 100, 100), false, A, 250, 250);
            Assert.True(f.W >= 14 && f.H >= 10);
            Assert.True(f.W <= 20);
            Assert.True(f.X0 <= 100 && f.X1 >= 100 && f.Z0 <= 100 && f.Z1 >= 100);
            AssertAspect(f);
        }

        [Fact]
        public void CloseBigThingIsHeldWhole()
        {
            var f = VisionGeometry.Close(new CellFrame(100, 100, 129, 104), false, A, 250, 250);
            Assert.True(f.X0 <= 100 && f.X1 >= 129);
        }

        [Fact]
        public void BaseIsHomeBoxPlusThree()
        {
            var f = VisionGeometry.Base(new CellFrame(100, 100, 139, 129), A, 250, 250);   // 40x30
            Assert.True(f.W >= 46 && f.H >= 36);
            Assert.True(f.X0 <= 97 && f.X1 >= 142 && f.Z0 <= 97 && f.Z1 >= 132);
            AssertAspect(f);
        }

        [Fact]
        public void FallbackHomeIsThirtyOneAroundTheCentre()
        {
            var b = VisionGeometry.HomeFromCenter(100, 100);
            Assert.Equal(85, b.X0); Assert.Equal(115, b.X1); Assert.Equal(85, b.Z0); Assert.Equal(115, b.Z1);
        }

        [Fact]
        public void WideIsAtLeast80AndAboutThreeTimesTheBase()
        {
            var small = VisionGeometry.Wide(new CellFrame(100, 100, 109, 109), A, 400, 400);
            Assert.True(small.W >= 80);
            var home = new CellFrame(100, 100, 139, 129);
            var b = VisionGeometry.Base(home, A, 1000, 1000);
            var w = VisionGeometry.Wide(home, A, 1000, 1000);
            Assert.True(w.W >= 3 * b.W - 2 && w.W <= 3 * b.W + 4);
            AssertAspect(w);
            Assert.True(w.CenterX >= home.CenterX - 2 && w.CenterX <= home.CenterX + 2);
        }

        [Fact]
        public void FramesAreClampedToTheMap()
        {
            var f = VisionGeometry.Wide(new CellFrame(100, 100, 139, 129), A, 120, 120);
            Assert.True(f.X0 >= 0 && f.Z0 >= 0 && f.X1 <= 119 && f.Z1 <= 119);
            AssertAspect(f);
            var c = VisionGeometry.Close(new CellFrame(0, 0, 0, 0), false, A, 250, 250);
            Assert.Equal(0, c.X0); Assert.Equal(0, c.Z0);
            var e = VisionGeometry.Close(new CellFrame(249, 249, 249, 249), false, A, 250, 250);
            Assert.Equal(249, e.X1); Assert.Equal(249, e.Z1);
            AssertAspect(c); AssertAspect(e);
        }

        [Fact]
        public void PortraitScreenKeepsItsAspect()
        {
            var f = VisionGeometry.Base(new CellFrame(100, 100, 139, 129), 0.6, 250, 250);
            Assert.InRange(f.W / (double)f.H, 0.5, 0.7);
        }

        [Theory]
        [InlineData("north", 100, 130)]
        [InlineData("south", 100, 70)]
        [InlineData("east", 130, 100)]
        [InlineData("west", 70, 100)]
        [InlineData("northeast", 130, 130)]
        [InlineData("southwest", 70, 70)]
        public void DirCellFromHomeCentre(string side, int ex, int ez)
        {
            int x, z;
            VisionGeometry.DirCell(100, 100, side, 30, 250, 250, out x, out z);
            Assert.Equal(ex, x); Assert.Equal(ez, z);
        }

        [Fact]
        public void DirCellStaysInsideTheMap()
        {
            int x, z;
            VisionGeometry.DirCell(20, 230, "northwest", 120, 250, 250, out x, out z);
            Assert.Equal(0, x); Assert.Equal(249, z);
        }

        [Fact]
        public void LegendFromTheRealCamera()
        {
            // camera centre (50,40), 15 cells tall (half 7.5), 30x15 view at 1.78 -> aspect 2 in px for easy numbers
            var l = VisionGeometry.LegendFromCamera(50, 40, 10, 800, 400, 250, 250, "x");
            Assert.Equal(30, l.X0); Assert.Equal(69, l.X1); Assert.Equal(30, l.Z0); Assert.Equal(49, l.Z1);
            Assert.Equal(40, l.CellsW); Assert.Equal(20, l.CellsH);
            Assert.Equal(20, l.PxPerCell, 3);
            Assert.Equal("hidden", (string)l.ToJson()["ui"]);
        }
    }
}
