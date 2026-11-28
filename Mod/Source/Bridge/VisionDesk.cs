using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    public enum VisionKind { Close, Base, Wide, View }
    public enum VisionTargetKind { None, Room, Thing, Pawn, Cell, Dir }

    /// <summary>A parsed GET /vision query. Key is what the 2 s cache and the in-flight check compare.</summary>
    public sealed class VisionQuery
    {
        public VisionKind Kind;
        public VisionTargetKind TargetKind;
        public string Target;         // as the client wrote it (echoed), null when none
        public int Id;                // room / thing / pawn
        public int X, Z;              // cell
        public string Dir;            // dir
        public int Cells;             // dir
        public int Max;               // long side in px, 256..FixedMax(kind)
        public string Key { get { return Kind + "|" + (Target ?? "") + "|" + Max; } }
        public string KindName { get { return Kind.ToString().ToLowerInvariant(); } }
    }

    /// <summary>Where a picture is: the cell rectangle it shows and its scale. x/z are cell coords, z grows north.</summary>
    public sealed class VisionLegend
    {
        public int X0, Z0, X1, Z1;
        public double PxPerCell;
        public string TargetName;
        public string Note = "north is up; z grows north; the picture shows the map only, no interface";

        public int CellsW { get { return X1 - X0 + 1; } }
        public int CellsH { get { return Z1 - Z0 + 1; } }

        public JObject ToJson()
        {
            return new JObject
            {
                ["north"] = "up", ["x0"] = X0, ["z0"] = Z0, ["x1"] = X1, ["z1"] = Z1,
                ["cells_w"] = CellsW, ["cells_h"] = CellsH,
                ["px_per_cell"] = Math.Round(PxPerCell, 2),
                ["target_name"] = TargetName,
                ["ui"] = "hidden",
                ["note"] = Note
            };
        }
    }

    /// <summary>One capture the game side works on. Handed out once by TryTakeJob.</summary>
    public sealed class VisionJob
    {
        public VisionQuery Query;
        internal double Started;
        internal int State;          // 0 queued, 1 taken, 2 encoding, 3 failed
        internal string FailCode, FailDetail;
    }

    sealed class VisionImage
    {
        public string Key;
        public VisionQuery Query;
        public byte[] Jpeg;
        public int W, H;
        public VisionLegend Legend;
        public long Tick;
        public double At;
    }

    /// <summary>
    /// The picture desk: the route asks, the game side captures. At most one job in flight, a 2 s cache of at most
    /// two finished images. Encoding finishes on a worker thread, so every method is thread-safe. No Verse, no Unity.
    /// </summary>
    public sealed class VisionDesk
    {
        public const double CacheSeconds = 2.0;
        public const int CacheMax = 2;
        public const int MaxJpegBytes = 900 * 1024;   // a 1536 px game frame at q80 is a few hundred KiB
        public const int QualityFirst = 80, QualityRetry = 60;
        public const double JobTimeoutSeconds = 20.0;
        public const int RetryMs = 250;

        readonly object _lock = new object();
        readonly Func<double> _now;
        readonly Action<Action> _runAsync;
        readonly List<VisionImage> _cache = new List<VisionImage>();
        VisionJob _job;

        public VisionDesk() : this(null, null) { }

        /// <summary>now: real-time seconds; runAsync: runs the encode (tests pass a synchronous one).</summary>
        public VisionDesk(Func<double> now, Action<Action> runAsync)
        {
            _now = now ?? (() => Environment.TickCount64 / 1000.0);
            _runAsync = runAsync ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        }

        // ------------------------------------------------------------------ route side
        public ApiResult Request(VisionQuery q)
        {
            lock (_lock)
            {
                double now = _now();
                Prune(now);
                var img = _cache.Find(i => i.Key == q.Key);
                if (img != null) return ApiResult.Success(ReadyJson(img, now));
                if (_job != null)
                {
                    if (now - _job.Started > JobTimeoutSeconds) { _job.State = 3; _job.FailCode = "capture_failed"; _job.FailDetail = "timed out"; }
                    if (_job.Query.Key == q.Key)
                    {
                        if (_job.State == 3)
                        {
                            var f = ApiResult.Fail(_job.FailCode, _job.FailDetail);
                            _job = null;
                            return f;
                        }
                        return Pending();
                    }
                    if (_job.State != 3) return ApiResult.Fail("busy", "another picture is being taken");
                    _job = null;   // an old failure nobody asked about
                }
                _job = new VisionJob { Query = q, Started = now };
                return Pending();
            }
        }

        static ApiResult Pending()
        {
            return ApiResult.Success(new JObject { ["status"] = "pending", ["retry_ms"] = RetryMs });
        }

        JObject ReadyJson(VisionImage i, double now)
        {
            return new JObject
            {
                ["status"] = "ready",
                ["kind"] = i.Query.KindName,
                ["target"] = i.Query.Target == null ? JValue.CreateNull() : (JToken)i.Query.Target,
                ["mime"] = "image/jpeg",
                ["width"] = i.W,
                ["height"] = i.H,
                ["bytes"] = i.Jpeg.Length,
                ["image_b64"] = Convert.ToBase64String(i.Jpeg),
                ["legend"] = i.Legend.ToJson(),
                ["captured_tick"] = i.Tick,
                ["age_s"] = Math.Round(Math.Max(0, now - i.At), 2)
            };
        }

        void Prune(double now)
        {
            _cache.RemoveAll(i => now - i.At > CacheSeconds);
        }

        // ------------------------------------------------------------------ game side
        /// <summary>The queued job, once; null when none waits.</summary>
        public VisionJob TryTakeJob()
        {
            lock (_lock)
            {
                if (_job == null || _job.State != 0) return null;
                if (_now() - _job.Started > JobTimeoutSeconds) { _job = null; return null; }
                _job.State = 1;
                return _job;
            }
        }

        public bool Busy { get { lock (_lock) return _job != null && _job.State != 3; } }

        public void Fail(VisionJob job, string code, string detail)
        {
            lock (_lock)
            {
                if (job == null || _job != job) return;
                job.State = 3; job.FailCode = code; job.FailDetail = detail;
            }
        }

        /// <summary>Finished with encoded bytes already at hand (tests, or a main-thread encode).</summary>
        public void Complete(VisionJob job, byte[] jpeg, int w, int h, VisionLegend legend, long tick)
        {
            if (jpeg == null || jpeg.Length == 0) { Fail(job, "capture_failed", "empty image"); return; }
            if (jpeg.Length > MaxJpegBytes) { Fail(job, "capture_failed", "image too large"); return; }
            Store(job, jpeg, w, h, legend, tick);
        }

        /// <summary>Encodes on a worker thread: quality 80, again at 60 when over the cap, else capture_failed.
        /// encode(quality) must be thread-safe and own its data.</summary>
        public void CompleteEncoded(VisionJob job, Func<int, byte[]> encode, int w, int h, VisionLegend legend, long tick)
        {
            lock (_lock) { if (job != null && _job == job && job.State == 1) job.State = 2; }
            _runAsync(() =>
            {
                try
                {
                    byte[] b = encode(QualityFirst);
                    if (b != null && b.Length > MaxJpegBytes) b = encode(QualityRetry);
                    if (b == null || b.Length == 0) { Fail(job, "capture_failed", "encoder gave nothing"); return; }
                    if (b.Length > MaxJpegBytes) { Fail(job, "capture_failed", "image too large"); return; }
                    Store(job, b, w, h, legend, tick);
                }
                catch (Exception e) { Fail(job, "capture_failed", "encode failed: " + e.GetType().Name); }
            });
        }

        void Store(VisionJob job, byte[] jpeg, int w, int h, VisionLegend legend, long tick)
        {
            lock (_lock)
            {
                if (job == null || _job != job || job.State == 3) return;
                double now = _now();
                Prune(now);
                _cache.RemoveAll(i => i.Key == job.Query.Key);
                _cache.Add(new VisionImage { Key = job.Query.Key, Query = job.Query, Jpeg = jpeg, W = w, H = h, Legend = legend, Tick = tick, At = now });
                while (_cache.Count > CacheMax) _cache.RemoveAt(0);
                _job = null;
            }
        }
    }

    /// <summary>Query parsing for GET /vision.</summary>
    public static class VisionParse
    {
        public static readonly string[] Dirs = { "north", "south", "east", "west", "northeast", "northwest", "southeast", "southwest" };
        public const int MinMax = 256;

        public static int FixedMax(VisionKind k)
        {
            // From the poe evaluation (docs/VIVARIUM.md, Vision): close reads best at 1024, base and wide at 1536.
            switch (k) { case VisionKind.Close: return 1024; default: return 1536; }
        }

        /// <summary>Null error when the query is good.</summary>
        public static string TryParse(string kind, string target, string max, out VisionQuery q)
        {
            q = null;
            VisionKind k;
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "close": k = VisionKind.Close; break;
                case "base": k = VisionKind.Base; break;
                case "wide": k = VisionKind.Wide; break;
                case "view": k = VisionKind.View; break;
                default: return "kind must be close, base, wide or view";
            }
            var r = new VisionQuery { Kind = k, Max = FixedMax(k) };
            if (!string.IsNullOrEmpty(max))
            {
                int m;
                if (!int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return "max must be an integer";
                r.Max = Math.Max(MinMax, Math.Min(FixedMax(k), m));
            }
            if (k == VisionKind.Close)
            {
                if (string.IsNullOrWhiteSpace(target)) return "close needs a target";
                string err = ParseTarget(target.Trim(), r);
                if (err != null) return err;
            }
            q = r;
            return null;
        }

        static bool Num(string s, out int v) { return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v); }

        static string ParseTarget(string t, VisionQuery r)
        {
            int colon = t.IndexOf(':');
            if (colon <= 0) return "target must be room:<id>, thing:<id>, pawn:<id>, cell:<x>,<z> or dir:<side>:<cells>";
            string form = t.Substring(0, colon).ToLowerInvariant(), rest = t.Substring(colon + 1);
            int a, b;
            switch (form)
            {
                case "room":
                case "thing":
                case "pawn":
                    if (!Num(rest, out a) || a < 0) return form + " needs a non-negative integer id";
                    r.Id = a;
                    r.TargetKind = form == "room" ? VisionTargetKind.Room : form == "thing" ? VisionTargetKind.Thing : VisionTargetKind.Pawn;
                    r.Target = form + ":" + a;
                    return null;
                case "cell":
                    var xz = rest.Split(',');
                    if (xz.Length != 2 || !Num(xz[0].Trim(), out a) || !Num(xz[1].Trim(), out b) || a < 0 || b < 0) return "cell needs x,z as non-negative integers";
                    r.X = a; r.Z = b; r.TargetKind = VisionTargetKind.Cell; r.Target = "cell:" + a + "," + b;
                    return null;
                case "dir":
                    var parts = rest.Split(':');
                    if (parts.Length != 2) return "dir needs <side>:<cells>";
                    string side = parts[0].Trim().ToLowerInvariant();
                    if (Array.IndexOf(Dirs, side) < 0) return "dir side must be one of " + string.Join(", ", Dirs);
                    if (!Num(parts[1].Trim(), out a) || a < 1 || a > 120) return "dir cells must be 1..120";
                    r.Dir = side; r.Cells = a; r.TargetKind = VisionTargetKind.Dir; r.Target = "dir:" + side + ":" + a;
                    return null;
                default:
                    return "unknown target form " + form;
            }
        }
    }

    /// <summary>Pure frame maths: which cell rectangle a picture should show, and what a render actually showed.</summary>
    public struct CellFrame
    {
        public int X0, Z0, X1, Z1;
        public int W { get { return X1 - X0 + 1; } }
        public int H { get { return Z1 - Z0 + 1; } }
        public double CenterX { get { return X0 + W / 2.0; } }
        public double CenterZ { get { return Z0 + H / 2.0; } }
        public CellFrame(int x0, int z0, int x1, int z1) { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; }
    }

    public static class VisionGeometry
    {
        public const int CloseW = 14, CloseH = 10, RoomMargin = 2, RoomMinW = 10;
        public const int BaseMargin = 3, FallbackHalf = 15;
        public const int WideMinW = 80;
        public const double WideFactor = 3.0;

        /// <summary>Grow w x h to the aspect (w/h), never shrinking either, then fit it into the map by scaling
        /// (keeping the aspect) and shifting. Returns a frame with exactly the aspect up to rounding.</summary>
        public static CellFrame Fit(double centerX, double centerZ, int w, int h, double aspect, int mapW, int mapH)
        {
            if (aspect <= 0.1) aspect = 16.0 / 9.0;
            w = Math.Max(1, w); h = Math.Max(1, h);
            if (w / (double)h < aspect) w = (int)Math.Ceiling(h * aspect);
            else h = (int)Math.Ceiling(w / aspect);
            if (w > mapW || h > mapH)
            {
                double s = Math.Min(mapW / (double)w, mapH / (double)h);
                w = Math.Max(1, (int)Math.Floor(w * s));
                h = Math.Max(1, (int)Math.Floor(h * s));
            }
            int x0 = (int)Math.Round(centerX - w / 2.0), z0 = (int)Math.Round(centerZ - h / 2.0);
            x0 = Math.Max(0, Math.Min(mapW - w, x0));
            z0 = Math.Max(0, Math.Min(mapH - h, z0));
            return new CellFrame(x0, z0, x0 + w - 1, z0 + h - 1);
        }

        /// <summary>close: a room is its bounds + 2 cells margin (min 10 wide); anything else ~14x10 around the target,
        /// grown to hold the target plus a cell.</summary>
        public static CellFrame Close(CellFrame target, bool isRoom, double aspect, int mapW, int mapH)
        {
            int w, h;
            if (isRoom) { w = Math.Max(RoomMinW, target.W + 2 * RoomMargin); h = target.H + 2 * RoomMargin; }
            else { w = Math.Max(CloseW, target.W + 2); h = Math.Max(CloseH, target.H + 2); }
            return Fit(target.CenterX, target.CenterZ, w, h, aspect, mapW, mapH);
        }

        /// <summary>base: the home bounding box + 3 cells on each side.</summary>
        public static CellFrame Base(CellFrame home, double aspect, int mapW, int mapH)
        {
            return Fit(home.CenterX, home.CenterZ, home.W + 2 * BaseMargin, home.H + 2 * BaseMargin, aspect, mapW, mapH);
        }

        /// <summary>Fallback home box: the centre of the beds/colonists +- 15 cells.</summary>
        public static CellFrame HomeFromCenter(int cx, int cz)
        {
            return new CellFrame(cx - FallbackHalf, cz - FallbackHalf, cx + FallbackHalf, cz + FallbackHalf);
        }

        /// <summary>wide: home bbox centre, about 3x the base span, at least 80 cells wide, clamped to the map.</summary>
        public static CellFrame Wide(CellFrame home, double aspect, int mapW, int mapH)
        {
            if (aspect <= 0.1) aspect = 16.0 / 9.0;
            var b = Base(home, aspect, int.MaxValue / 4, int.MaxValue / 4);
            int w = Math.Max(WideMinW, (int)Math.Ceiling(Math.Max(b.W * WideFactor, b.H * WideFactor * aspect)));
            int h = (int)Math.Ceiling(w / aspect);
            return Fit(home.CenterX, home.CenterZ, w, h, aspect, mapW, mapH);
        }

        /// <summary>The cell a dir:<side>:<cells> target means, from the home centre, kept inside the map.
        /// z grows north.</summary>
        public static void DirCell(int cx, int cz, string side, int cells, int mapW, int mapH, out int x, out int z)
        {
            int dx = 0, dz = 0;
            if (side.Contains("north")) dz = 1;
            if (side.Contains("south")) dz = -1;
            if (side.EndsWith("east")) dx = 1;
            if (side.EndsWith("west")) dx = -1;
            x = Math.Max(0, Math.Min(mapW - 1, cx + dx * cells));
            z = Math.Max(0, Math.Min(mapH - 1, cz + dz * cells));
        }

        /// <summary>Pixel size for a long side of max at the aspect.</summary>
        public static void ImageSize(int max, double aspect, out int w, out int h)
        {
            if (aspect <= 0.1) aspect = 16.0 / 9.0;
            if (aspect >= 1) { w = max; h = Math.Max(1, (int)Math.Round(max / aspect)); }
            else { h = max; w = Math.Max(1, (int)Math.Round(max * aspect)); }
        }

        /// <summary>What an orthographic camera at (camX, camZ) with half-height orthoSize shows, as a legend.
        /// Used on the real camera after the move, so a clamped zoom or position is told truthfully.</summary>
        public static VisionLegend LegendFromCamera(double camX, double camZ, double orthoSize, int pxW, int pxH, int mapW, int mapH, string targetName)
        {
            double aspect = pxW / (double)Math.Max(1, pxH);
            double halfH = orthoSize, halfW = orthoSize * aspect;
            int x0 = (int)Math.Floor(camX - halfW + 0.5), x1 = (int)Math.Floor(camX + halfW + 0.5) - 1;
            int z0 = (int)Math.Floor(camZ - halfH + 0.5), z1 = (int)Math.Floor(camZ + halfH + 0.5) - 1;
            if (x1 < x0) x1 = x0;
            if (z1 < z0) z1 = z0;
            var l = new VisionLegend { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, TargetName = targetName };
            l.PxPerCell = pxW / (2.0 * halfW);
            return l;
        }
    }

    /// <summary>GET /vision.</summary>
    public static class VisionRoutes
    {
        public static void Register(ApiRouter r, VisionDesk desk, Func<bool> allowed)
        {
            r.Add("GET", "/vision", true, c =>
            {
                if (!allowed()) return ApiResult.Fail("refused", "vision_off");
                VisionQuery q;
                string err = VisionParse.TryParse(c.Q("kind"), c.Q("target"), c.Q("max"), out q);
                if (err != null) return ApiResult.Fail("bad_request", err);
                return desk.Request(q);
            });
        }
    }
}
