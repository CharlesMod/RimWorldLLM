using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>One request the fake pairing engine saw.</summary>
    sealed class PReq
    {
        public string Method, Path, Auth, Csrf, Body;
        public string Key { get { return Method + " " + Path; } }
    }

    /// <summary>What the fake answers: a status and body, or a dropped connection.</summary>
    sealed class PReply
    {
        public int Status = 200;
        public string Body = "";
        public bool Abort;
        public static PReply Json(int status, string body) { return new PReply { Status = status, Body = body }; }
        public static PReply Drop() { return new PReply { Abort = true }; }
    }

    /// <summary>
    /// A fake easyEngine for the keyless paths of §7f: HttpListener on a free loopback port. The handler is async so a
    /// request can be held (await a TaskCompletionSource the test completes) or answered by dropping the connection.
    /// </summary>
    sealed class FakePairEngine : IDisposable
    {
        public readonly HttpListener Listener = new HttpListener();
        public readonly int Port;
        public readonly List<PReq> Requests = new List<PReq>();
        public Func<PReq, Task<PReply>> Handler;
        readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public FakePairEngine(Func<PReq, Task<PReply>> handler)
        {
            Handler = handler;
            Port = FreePort();
            Listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            Listener.Start();
            Task.Run(Loop);
        }

        public string Root { get { return "http://127.0.0.1:" + Port; } }

        public static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await Listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        async Task Serve(HttpListenerContext c)
        {
            try
            {
                string body;
                using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                var req = new PReq
                {
                    Method = c.Request.HttpMethod,
                    Path = c.Request.Url.AbsolutePath,
                    Auth = c.Request.Headers["Authorization"] ?? "",
                    Csrf = c.Request.Headers["X-Easy-Engine"] ?? "",
                    Body = body,
                };
                lock (Requests) Requests.Add(req);
                var rep = await Handler(req);
                if (rep.Abort)
                {
                    // HttpListener's bare Abort() can surface as an empty 200; promise a long body and cut it, so the client sees a real drop.
                    try { c.Response.ContentLength64 = 1000; c.Response.OutputStream.Write(new byte[] { 1, 2, 3 }, 0, 3); c.Response.OutputStream.Flush(); } catch { }
                    c.Response.Abort();
                    return;
                }
                c.Response.StatusCode = rep.Status;
                c.Response.ContentType = "application/json";
                if (!string.IsNullOrEmpty(rep.Body) && rep.Status != 204)
                {
                    byte[] b = Encoding.UTF8.GetBytes(rep.Body);
                    c.Response.OutputStream.Write(b, 0, b.Length);
                }
                c.Response.Close();
            }
            catch { }
        }

        public int Count(string method, string path)
        {
            lock (Requests) return Requests.Count(r => r.Method == method && r.Path == path);
        }

        public List<PReq> Seen(string method, string path)
        {
            lock (Requests) return Requests.Where(r => r.Method == method && r.Path == path).ToList();
        }

        public void Dispose() { _stop.Cancel(); try { Listener.Close(); } catch { } }
    }

    public class EnginePairingTests
    {
        // ------------------------------------------------------------------------------------ recorded mDNS packets
        const string QueryB64 = "AAAAAAABAAAAAAAADF9lYXN5LWVuZ2luZQRfdGNwBWxvY2FsAAAMAAE=";
        const string AnnounceB64 = "AACEAAAAAAUAAAAADF9lYXN5LWVuZ2luZQRfdGNwBWxvY2FsAAAMAAEAAAB4ABIPZGVuLXBjIFs5ZjJjNGVdwAzALwAhgAEAAAB4ACIAAAAAuXgUZWFzeS1lbmdpbmUtOWYyYzRlMWEFbG9jYWwAwC8AEIABAAAAeAA2I2lkPTlmMmM0ZTFhMGIzZDVmNmU3YThiOWMwZDFlMmYzYTRiBWFwaT0xC25hbWU9ZGVuLXBjFGVhc3ktZW5naW5lLTlmMmM0ZTFhwB4AAYABAAAAeAAEwKgBFMCxAByAAQAAAHgAEP6AAAAAAAAAAAAAAAAAAAE=";
        const string LegacyB64 = "AACEAAABAAEAAAAEDF9lYXN5LWVuZ2luZQRfdGNwBWxvY2FsAAAMAAHADAAMAAEAAAAKABIPZGVuLXBjIFs5ZjJjNGVdwAzANQAhAAEAAAAKACIAAAAAuXgUZWFzeS1lbmdpbmUtOWYyYzRlMWEFbG9jYWwAwDUAEAABAAAACgA2I2lkPTlmMmM0ZTFhMGIzZDVmNmU3YThiOWMwZDFlMmYzYTRiBWFwaT0xC25hbWU9ZGVuLXBjFGVhc3ktZW5naW5lLTlmMmM0ZTFhwB4AAQABAAAACgAEwKgBFMC3ABwAAQAAAAoAEP6AAAAAAAAAAAAAAAAAAAE=";
        const string GoodbyeB64 = "AACEAAAAAAUAAAAADF9lYXN5LWVuZ2luZQRfdGNwBWxvY2FsAAAMAAEAAAAAABIPZGVuLXBjIFs5ZjJjNGVdwAzALwAhgAEAAAAAACIAAAAAuXgUZWFzeS1lbmdpbmUtOWYyYzRlMWEFbG9jYWwAwC8AEIABAAAAAAA2I2lkPTlmMmM0ZTFhMGIzZDVmNmU3YThiOWMwZDFlMmYzYTRiBWFwaT0xC25hbWU9ZGVuLXBjFGVhc3ktZW5naW5lLTlmMmM0ZTFhwB4AAYABAAAAAAAEwKgBFMCxAByAAQAAAAAAEP6AAAAAAAAAAAAAAAAAAAE=";
        const string BueroB64 = "AACEAAABAAEAAAADDF9lYXN5LWVuZ2luZQRfdGNwBWxvY2FsAAAMAAHADAAMAAEAAAAKABQRQsO8cm8gUEMgWzAxMjM0NV3ADMA1ACEAAQAAAAoAIgAAAAC5ixRlYXN5LWVuZ2luZS0wMTIzNDU2NwVsb2NhbADANQAQAAEAAAAKADgjaWQ9MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWYFYXBpPTENbmFtZT1Cw7xybyBQQxRlYXN5LWVuZ2luZS0wMTIzNDU2N8AeAAEAAQAAAAoABAoAAAc=";

        const string Id1 = "9f2c4e1a0b3d5f6e7a8b9c0d1e2f3a4b";
        const string Id2 = "0123456789abcdef0123456789abcdef";
        const string Token = "tok-SECRET-0123456789abcdefghijklmnopqrstuvwxyz";
        const string ReqId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

        static byte[] B(string b64) { return Convert.FromBase64String(b64); }

        // ====================================================================================== 1. mDNS

        [Fact]
        public void BuildQueryMatchesTheGoBytes()
        {
            Assert.Equal(B(QueryB64), Mdns.BuildQuery());
        }

        [Theory]
        [InlineData(AnnounceB64)]
        [InlineData(LegacyB64)]
        public void ParseReadsTheEngineFromAnAnnouncementAndALegacyAnswer(string b64)
        {
            var list = Mdns.Parse(B(b64));
            var e = Assert.Single(list);
            Assert.Equal(Id1, e.Id);
            Assert.Equal("den-pc", e.Name);
            Assert.Equal(1, e.Api);
            Assert.Equal(47480, e.Port);
            Assert.Equal("easy-engine-9f2c4e1a.local.", e.Host);
            Assert.Equal("den-pc [9f2c4e]._easy-engine._tcp.local.", e.Instance);
            Assert.Contains(IPAddress.Parse("192.168.1.20"), e.Addrs);
            Assert.Contains(IPAddress.Parse("fe80::1"), e.Addrs);
            Assert.Equal(2, e.Addrs.Count);
            Assert.Equal("192.168.1.20:47480", e.Address);
        }

        [Fact]
        public void GoodbyeQueryAndNullYieldNothing()
        {
            Assert.Empty(Mdns.Parse(B(GoodbyeB64)));
            Assert.Empty(Mdns.Parse(B(QueryB64)));
            Assert.Empty(Mdns.Parse(null));
            Assert.Empty(Mdns.Parse(new byte[0]));
            Assert.Empty(Mdns.Parse(new byte[11]));
        }

        [Fact]
        public void NonAsciiNameParsesAsUtf8()
        {
            var e = Assert.Single(Mdns.Parse(B(BueroB64)));
            Assert.Equal("Büro PC", e.Name);
            Assert.Equal(Id2, e.Id);
            Assert.Equal(47499, e.Port);
            Assert.Equal("10.0.0.7:47499", e.Address);
        }

        [Fact]
        public void EveryTruncatedPrefixParsesWithoutThrowing()
        {
            foreach (var b64 in new[] { AnnounceB64, LegacyB64, GoodbyeB64, BueroB64 })
            {
                var full = B(b64);
                for (int n = 0; n < full.Length; n++)
                {
                    var prefix = new byte[n];
                    Array.Copy(full, prefix, n);
                    var ex = Record.Exception(() => Mdns.Parse(prefix));
                    Assert.Null(ex);
                }
            }
        }

        [Fact]
        public async Task CompressionPointerLoopNeitherThrowsNorHangs()
        {
            // A response with one answer whose name is a pointer to itself (offset 12).
            var pkt = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 };
            pkt.AddRange(new byte[] { 0xC0, 0x0C, 0, 12, 0, 1, 0, 0, 0, 10, 0, 0 });
            // Also a pointer loop between two labels in a question.
            var pkt2 = new List<byte> { 0, 0, 0x84, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            pkt2.AddRange(new byte[] { 1, (byte)'a', 0xC0, 0x0C, 0, 12, 0, 1 });
            var t = Task.Run(() =>
            {
                Assert.Empty(Mdns.Parse(pkt.ToArray()));
                Assert.Empty(Mdns.Parse(pkt2.ToArray()));
            });
            Assert.Same(t, await Task.WhenAny(t, Task.Delay(5000)));
            await t;
        }

        [Fact]
        public void MergeJoinsByIdAndKeepsFirstSeenFields()
        {
            var a = new MdnsEngine { Id = "x", Name = "first", Port = 0 };
            a.Addrs.Add(IPAddress.Parse("10.0.0.1"));
            var b = new MdnsEngine { Id = "x", Name = "second", Port = 9 };
            b.Addrs.Add(IPAddress.Parse("10.0.0.1"));
            b.Addrs.Add(IPAddress.Parse("10.0.0.2"));
            var c = new MdnsEngine { Id = "y", Name = "other", Port = 1 };
            var into = new List<MdnsEngine>();
            Mdns.Merge(into, new[] { a });
            Mdns.Merge(into, new[] { b, c });
            Assert.Equal(2, into.Count);
            var x = into.First(e => e.Id == "x");
            Assert.Equal("first", x.Name);
            Assert.Equal(9, x.Port);              // a port filled in when the first sighting had none
            Assert.Equal(2, x.Addrs.Count);
        }

        [Fact]
        public void MergeOfTheSameAnnouncementTwiceIsOneEngine()
        {
            var into = new List<MdnsEngine>();
            Mdns.Merge(into, Mdns.Parse(B(AnnounceB64)));
            Mdns.Merge(into, Mdns.Parse(B(LegacyB64)));
            var e = Assert.Single(into);
            Assert.Equal(2, e.Addrs.Count);
        }

        [Fact]
        public void AddressFallsBackToIpv6ThenToTheSourceAddress()
        {
            var v6 = new MdnsEngine { Port = 1234 };
            v6.Addrs.Add(IPAddress.Parse("fe80::1"));
            Assert.Equal("[fe80::1]:1234", v6.Address);

            var v6g = new MdnsEngine { Port = 1234 };
            v6g.Addrs.Add(IPAddress.Parse("fe80::1"));
            v6g.Addrs.Add(IPAddress.Parse("2001:db8::5"));
            Assert.Equal("[2001:db8::5]:1234", v6g.Address);   // a global address beats a link-local one

            var from4 = new MdnsEngine { Port = 77, From = IPAddress.Parse("10.1.1.1") };
            Assert.Equal("10.1.1.1:77", from4.Address);

            var from6 = new MdnsEngine { Port = 77, From = IPAddress.Parse("2001:db8::9") };
            Assert.Equal("[2001:db8::9]:77", from6.Address);

            Assert.Equal("", new MdnsEngine { Port = 77 }.Address);
            Assert.Equal("", new MdnsEngine { Port = 0, From = IPAddress.Parse("10.1.1.1") }.Address);

            var both = new MdnsEngine { Port = 5, From = IPAddress.Parse("10.9.9.9") };
            both.Addrs.Add(IPAddress.Parse("fe80::1"));
            both.Addrs.Add(IPAddress.Parse("192.168.0.2"));
            Assert.Equal("192.168.0.2:5", both.Address);       // IPv4 wins over IPv6 and over the source
        }

        [Fact]
        public void ParseKeepsTheSourceAddress()
        {
            var e = Assert.Single(Mdns.Parse(B(AnnounceB64), IPAddress.Parse("192.168.1.99")));
            Assert.Equal(IPAddress.Parse("192.168.1.99"), e.From);
        }

        // ====================================================================================== 2. EngineAddress

        [Theory]
        [InlineData("host", "http://host:47480")]
        [InlineData("host:1234", "http://host:1234")]
        [InlineData("http://host:1234/v1", "http://host:1234")]
        [InlineData("http://host:1234/v1/", "http://host:1234")]
        [InlineData("[fe80::1]:47480", "http://[fe80::1]:47480")]
        [InlineData("192.168.1.20", "http://192.168.1.20:47480")]
        [InlineData("  192.168.1.20:5 ", "http://192.168.1.20:5")]
        [InlineData("https://den.example:8443", "https://den.example:8443")]
        public void ToRootNormalises(string input, string expected)
        {
            Assert.Equal(expected, EngineAddress.ToRoot(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://")]
        [InlineData("ftp://host")]
        public void ToRootRejectsJunk(string input)
        {
            Assert.Null(EngineAddress.ToRoot(input));
        }

        [Theory]
        [InlineData("http://h:1/v1", "http://h:1")]
        [InlineData("http://h:1/v1/", "http://h:1")]
        [InlineData("http://h:1", "http://h:1")]
        [InlineData("h:9/V1", "http://h:9")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void RootOfBaseCutsTheV1(string input, string expected)
        {
            Assert.Equal(expected, EngineAddress.RootOfBase(input));
        }

        [Theory]
        [InlineData("http://127.0.0.1:47480", true)]
        [InlineData("http://127.5.5.5:1", true)]
        [InlineData("http://localhost:47480", true)]
        [InlineData("http://LOCALHOST:1", true)]
        [InlineData("http://[::1]:47480", true)]
        [InlineData("http://192.168.1.2:47480", false)]
        [InlineData("http://den-pc:47480", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsLoopback(string root, bool expected)
        {
            Assert.Equal(expected, EngineAddress.IsLoopback(root));
        }

        [Fact]
        public void OpenAiBaseAndDisplay()
        {
            Assert.Equal("http://h:1/v1", EngineAddress.OpenAiBase("http://h:1"));
            Assert.Equal("http://h:1/v1", EngineAddress.OpenAiBase("http://h:1/"));
            Assert.Equal("h:1", EngineAddress.Display("http://h:1"));
        }

        // ====================================================================================== 3. EngineHello

        const string HelloExample = "{\"name\":\"den-pc\",\"engine\":\"easy-engine\",\"api\":1,\"version\":\"0.4.0\",\"engine_id\":\"9F2C4E1A0B3D5F6E7A8B9C0D1E2F3A4B\"," +
            "\"model_name\":\"Gemma 4 26B\",\"phase\":\"ready\",\"card\":\"NVIDIA GeForce RTX 4080\",\"pairing\":true,\"token_required\":true}";

        [Fact]
        public void HelloParsesTheExampleBody()
        {
            var h = EngineHello.Parse(HelloExample);
            Assert.NotNull(h);
            Assert.True(h.IsEasyEngine);
            Assert.Equal("den-pc", h.Name);
            Assert.Equal("easy-engine", h.Engine);
            Assert.Equal(1, h.Api);
            Assert.Equal("0.4.0", h.Version);
            Assert.Equal(Id1, h.EngineId);                  // lowercased
            Assert.Equal("Gemma 4 26B", h.ModelName);
            Assert.Equal("ready", h.Phase);
            Assert.Equal("NVIDIA GeForce RTX 4080", h.Card);
            Assert.True(h.Pairing);
            Assert.True(h.TokenRequired);
            Assert.Null(h.Paired);
            Assert.False(h.Revoked);
            Assert.Equal("", h.ClientId);
        }

        [Fact]
        public void HelloParsesPairedAndRevoked()
        {
            var p = EngineHello.Parse("{\"engine\":\"easy-engine\",\"api\":1,\"engine_id\":\"" + Id1 + "\",\"token_required\":true,\"paired\":true,\"client_id\":\"c7\"}");
            Assert.True(p.Paired);
            Assert.Equal("c7", p.ClientId);
            var r = EngineHello.Parse("{\"engine\":\"easy-engine\",\"api\":1,\"engine_id\":\"" + Id1 + "\",\"token_required\":true,\"paired\":false,\"revoked\":true}");
            Assert.False(r.Paired.Value);
            Assert.True(r.Revoked);
        }

        [Theory]
        [InlineData("{\"engine\":\"other\",\"api\":1,\"engine_id\":\"abc\"}")]
        [InlineData("{\"engine\":\"easy-engine\",\"api\":0,\"engine_id\":\"abc\"}")]
        [InlineData("{\"engine\":\"easy-engine\",\"api\":1}")]
        [InlineData("{\"engine\":\"easy-engine\",\"api\":1,\"engine_id\":\"\"}")]
        [InlineData("{}")]
        public void HelloThatIsNotAnEasyEngine(string body)
        {
            var h = EngineHello.Parse(body);
            Assert.NotNull(h);
            Assert.False(h.IsEasyEngine);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("[1,2]")]
        [InlineData("null")]
        public void HelloGarbageIsNull(string body)
        {
            Assert.Null(EngineHello.Parse(body));
        }

        // ====================================================================================== 4. auth codes

        [Theory]
        [InlineData("token_required", EngineAuth.TokenRequired)]
        [InlineData("token_invalid", EngineAuth.TokenInvalid)]
        [InlineData("token_revoked", EngineAuth.TokenRevoked)]
        public void FromReplyMapsA401ByItsCode(string code, EngineAuth expected)
        {
            string body = "{\"error\":\"bearer token required (easy-engine token prints it)\",\"code\":\"" + code + "\"}";
            Assert.Equal(expected, EngineAuthCodes.FromReply(401, body));
        }

        [Fact]
        public void FromReplyTakesANestedErrorCodeToo()
        {
            Assert.Equal(EngineAuth.TokenRevoked, EngineAuthCodes.FromReply(401, "{\"error\":{\"code\":\"token_revoked\"}}"));
        }

        [Theory]
        [InlineData("{\"error\":\"nope\"}")]
        [InlineData("{\"error\":\"nope\",\"code\":\"something_else\"}")]
        [InlineData("{\"code\":7}")]
        [InlineData("not json")]
        [InlineData("")]
        [InlineData(null)]
        public void FromReplyA401WithoutOurCodeIsTokenInvalid(string body)
        {
            Assert.Equal(EngineAuth.TokenInvalid, EngineAuthCodes.FromReply(401, body));
        }

        [Theory]
        [InlineData(200)]
        [InlineData(403)]
        [InlineData(500)]
        public void FromReplyOtherStatusesAreUnknown(int status)
        {
            Assert.Equal(EngineAuth.Unknown, EngineAuthCodes.FromReply(status, "{\"code\":\"token_revoked\"}"));
        }

        static EngineHello H(bool required, bool? paired = null, bool revoked = false)
        {
            return new EngineHello { TokenRequired = required, Paired = paired, Revoked = revoked };
        }

        [Fact]
        public void FromHelloJudgesTheToken()
        {
            Assert.Equal(EngineAuth.Unknown, EngineAuthCodes.FromHello(null, true));
            Assert.Equal(EngineAuth.Open, EngineAuthCodes.FromHello(H(false), false));
            Assert.Equal(EngineAuth.Open, EngineAuthCodes.FromHello(H(false), true));
            Assert.Equal(EngineAuth.TokenRequired, EngineAuthCodes.FromHello(H(true), false));
            Assert.Equal(EngineAuth.Paired, EngineAuthCodes.FromHello(H(true, true), true));
            Assert.Equal(EngineAuth.TokenRevoked, EngineAuthCodes.FromHello(H(true, false, true), true));
            Assert.Equal(EngineAuth.TokenInvalid, EngineAuthCodes.FromHello(H(true, false), true));
            Assert.Equal(EngineAuth.TokenInvalid, EngineAuthCodes.FromHello(H(true), true));
        }

        [Fact]
        public void PairAgainOnlyForInvalidAndRevoked()
        {
            Assert.True(EngineAuthCodes.PairAgain(EngineAuth.TokenInvalid));
            Assert.True(EngineAuthCodes.PairAgain(EngineAuth.TokenRevoked));
            Assert.False(EngineAuthCodes.PairAgain(EngineAuth.Paired));
            Assert.False(EngineAuthCodes.PairAgain(EngineAuth.TokenRequired));
            Assert.False(EngineAuthCodes.PairAgain(EngineAuth.Open));
            Assert.False(EngineAuthCodes.PairAgain(EngineAuth.Unknown));
        }

        [Fact]
        public async Task EngineClientSendsTheBearerTokenAndOmitsItWhenUnset()
        {
            using (var f = new FakePairEngine(r => Task.FromResult(PReply.Json(200, "{\"phase\":\"ready\"}"))))
            {
                var c = new EngineClient(f.Port) { Token = "client-token-1" };
                var ok = await c.GetStatusAsync();
                Assert.True(ok.Ok);
                var plain = new EngineClient(f.Port);
                await plain.GetStatusAsync();
                var seen = f.Seen("GET", "/engine/v1/status");
                Assert.Equal(2, seen.Count);
                Assert.Equal("Bearer client-token-1", seen[0].Auth);
                Assert.Equal("", seen[1].Auth);
            }
        }

        [Fact]
        public async Task EngineClientMapsA401CodeToAuthAndRecordsIt()
        {
            using (var f = new FakePairEngine(r => Task.FromResult(PReply.Json(401, "{\"error\":\"bearer token required\",\"code\":\"token_revoked\"}"))))
            {
                var res = await new EngineClient(f.Port) { Token = "t" }.GetStatusAsync();
                var current = EngineAuthStatus.Current;     // read at once: other tests also set this process-wide state
                Assert.False(res.Ok);
                Assert.Equal(401, res.HttpStatus);
                Assert.Equal(EngineAuth.TokenRevoked, res.Auth);
                Assert.True(current == EngineAuth.TokenRevoked || current != EngineAuth.Unknown);
            }
        }

        [Fact]
        public async Task EngineClientAuthStatusIsSetByA401()
        {
            EngineAuthStatus.Set(EngineAuth.Unknown);
            EngineAuthStatus.Note(401, "{\"code\":\"token_revoked\"}");
            Assert.Equal(EngineAuth.TokenRevoked, EngineAuthStatus.Current);
            // a non-401 leaves the state alone
            Assert.Equal(EngineAuth.Unknown, EngineAuthStatus.Note(200, "{}"));
            await Task.CompletedTask;
        }

        [Fact]
        public async Task EngineClientPlain401IsTokenInvalid()
        {
            using (var f = new FakePairEngine(r => Task.FromResult(PReply.Json(401, "{\"error\":\"no\"}"))))
            {
                var res = await new EngineClient(f.Port).GetStatusAsync();
                Assert.Equal(EngineAuth.TokenInvalid, res.Auth);
            }
        }

        [Fact]
        public async Task EngineClientOtherFailuresLeaveAuthUnknown()
        {
            using (var f = new FakePairEngine(r => Task.FromResult(PReply.Json(500, "{\"error\":\"x\",\"code\":\"token_revoked\"}"))))
            {
                var res = await new EngineClient(f.Port).GetStatusAsync();
                Assert.Equal(EngineAuth.Unknown, res.Auth);
            }
        }

        static HttpOutcome Out(int status, string body = "") { return new HttpOutcome { Status = status, Body = body }; }

        [Theory]
        [InlineData("token_required", ProbeCode.TokenRequired)]
        [InlineData("token_invalid", ProbeCode.TokenInvalid)]
        [InlineData("token_revoked", ProbeCode.TokenRevoked)]
        public void ProbeMapsA401CodeForModelsAndChat(string code, ProbeCode expected)
        {
            var o = Out(401, "{\"error\":\"bearer token required\",\"code\":\"" + code + "\"}");
            var m = LlmProbe.ClassifyModels(o, "m");
            Assert.Equal(expected, m.Code);
            Assert.Equal(ProbeLight.Red, m.Light);
            Assert.Equal(expected, LlmProbe.ClassifyChat(o, "m").Code);
            Assert.Equal(expected, LlmProbe.Rejected(o).Code);
        }

        [Theory]
        [InlineData(401, "")]
        [InlineData(401, "{\"error\":\"no\"}")]
        [InlineData(401, "{\"code\":\"other\"}")]
        [InlineData(401, "not json")]
        [InlineData(403, "{\"code\":\"token_revoked\"}")]
        [InlineData(403, "")]
        public void ProbePlainRejectionIsKeyRejected(int status, string body)
        {
            var o = Out(status, body);
            Assert.Equal(ProbeCode.KeyRejected, LlmProbe.ClassifyModels(o, "m").Code);
            Assert.Equal(ProbeCode.KeyRejected, LlmProbe.ClassifyChat(o, "m").Code);
        }

        // ====================================================================================== 5. PairStore

        [Fact]
        public void StorePutGetIsCaseInsensitiveAndReplaces()
        {
            var s = new PairStore();
            s.Put(new PairedEngine { EngineId = "ABCDEF0123", Token = "t1", Name = "n1" });
            Assert.NotNull(s.Get("abcdef0123"));
            Assert.NotNull(s.Get("  AbCdEf0123 "));
            Assert.Equal("abcdef0123", s.Get("ABCDEF0123").EngineId);
            Assert.True(s.HasToken("ABCDEF0123"));
            s.Put(new PairedEngine { EngineId = "abcdef0123", Token = "t2" });
            Assert.Single(s.Items);
            Assert.Equal("t2", s.Get("ABCDEF0123").Token);
            Assert.Null(s.Get("other"));
            Assert.Null(s.Get(""));
            Assert.Null(s.Get(null));
            Assert.False(s.HasToken("other"));
            s.Put(null);
            s.Put(new PairedEngine { EngineId = "" });
            Assert.Single(s.Items);
        }

        [Fact]
        public void ClearTokenKeepsNameAndAddress()
        {
            var s = new PairStore();
            s.Put(new PairedEngine { EngineId = Id1, Token = Token, ClientId = "c1", Name = "den-pc", Address = "http://1.2.3.4:47480" });
            s.ClearToken(Id1.ToUpperInvariant());
            var p = s.Get(Id1);
            Assert.False(p.HasToken);
            Assert.Equal("", p.Token);
            Assert.Equal("", p.ClientId);
            Assert.Equal("den-pc", p.Name);
            Assert.Equal("http://1.2.3.4:47480", p.Address);
            s.ClearToken("unknown");      // no throw
            s.Remember(Id1, "http://5.6.7.8:47480");
            Assert.Equal("http://5.6.7.8:47480", p.Address);
            s.Remember(Id1, "");
            Assert.Equal("http://5.6.7.8:47480", p.Address);
        }

        [Fact]
        public void StoreJsonRoundTrips()
        {
            var s = new PairStore();
            s.Put(new PairedEngine { EngineId = Id1, Token = Token, ClientId = "c1", Name = "den-pc", Address = "http://1.2.3.4:47480" });
            s.Put(new PairedEngine { EngineId = Id2, Token = "", Name = "Büro PC", Address = "http://10.0.0.7:47499" });
            var back = PairStore.FromJson(s.ToJson());
            Assert.Equal(2, back.Items.Count);
            var a = back.Get(Id1);
            Assert.Equal(Token, a.Token);
            Assert.Equal("c1", a.ClientId);
            Assert.Equal("den-pc", a.Name);
            Assert.Equal("http://1.2.3.4:47480", a.Address);
            Assert.Equal("Büro PC", back.Get(Id2).Name);
            Assert.False(back.HasToken(Id2));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{}")]
        [InlineData("{\"engine_id\":\"x\"}")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("null")]
        [InlineData("[null,{\"engine_id\":\"\"},{\"token\":\"x\"}]")]
        public void StoreFromJsonGarbageIsEmpty(string json)
        {
            var s = PairStore.FromJson(json);
            Assert.NotNull(s);
            Assert.Empty(s.Items);
        }

        [Fact]
        public void StoreFromJsonFillsMissingFields()
        {
            var s = PairStore.FromJson("[{\"engine_id\":\"AB12\"}]");
            var p = s.Get("ab12");
            Assert.Equal("", p.Token);
            Assert.Equal("", p.Name);
            Assert.Equal("", p.Address);
            Assert.Equal("", p.ClientId);
        }

        [Fact]
        public void ToStringsNeverHoldTheToken()
        {
            var pe = new PairedEngine { EngineId = Id1, Token = Token, ClientId = "c1", Name = "den-pc", Address = "http://h:1" };
            Assert.DoesNotContain(Token, pe.ToString());
            Assert.Contains("set", pe.ToString());
            var o = new PairOutcome { Engine = pe, Root = "http://h:1", ModelId = "m", EngineName = "den-pc" };
            Assert.DoesNotContain(Token, o.ToString());
            Assert.Contains("den-pc", o.ToString());
            Assert.Contains("none", new PairOutcome().ToString());
            Assert.Contains("none", new PairedEngine().ToString());
        }

        // ====================================================================================== 6. PairingSession

        static string HelloJson(string id, bool pairing, bool tokenRequired, string extra = "")
        {
            return "{\"name\":\"den-pc\",\"engine\":\"easy-engine\",\"api\":1,\"version\":\"0.4.0\",\"engine_id\":\"" + id + "\",\"model_name\":\"Gemma 4 26B\"," +
                "\"phase\":\"ready\",\"card\":\"RTX\",\"pairing\":" + (pairing ? "true" : "false") + ",\"token_required\":" + (tokenRequired ? "true" : "false") + extra + "}";
        }

        const string PairPath = "/engine/v1/pair";
        static readonly string HeldPath = "/engine/v1/pair/" + ReqId;
        const string ModelsBody = "{\"data\":[{\"id\":\"gemma4-e4b\"}]}";

        /// <summary>Routes the keyless paths; <paramref name="held"/> answers the held GET.</summary>
        static Func<PReq, Task<PReply>> Route(Func<PReq, string> hello, Func<PReq, Task<PReply>> held, Func<PReq, PReply> ask = null)
        {
            return async r =>
            {
                if (r.Method == "GET" && r.Path == "/engine/v1/hello") return PReply.Json(200, hello(r));
                if (r.Method == "POST" && r.Path == PairPath) return ask != null ? ask(r) : PReply.Json(200, "{\"id\":\"" + ReqId + "\",\"code\":\"493201\"}");
                if (r.Method == "GET" && r.Path == HeldPath) return await held(r);
                if (r.Method == "DELETE" && r.Path == HeldPath) return PReply.Json(204, "");
                if (r.Method == "GET" && r.Path == "/v1/models") return PReply.Json(200, ModelsBody);
                return PReply.Json(404, "{\"error\":\"nope\"}");
            };
        }

        static TaskCompletionSource<PReply> Hold() { return new TaskCompletionSource<PReply>(TaskCreationOptions.RunContinuationsAsynchronously); }

        static PairingSession Make(FakePairEngine f, List<string> log, string existingToken = null, string root = null)
        {
            return new PairingSession(new PairingApi { HelloTimeoutMs = 3000, ShortTimeoutMs = 3000 }, root ?? f.Root, "RimWorld on test-box", existingToken,
                s => { lock (log) log.Add(s); })
            { RetryMinMs = 20, RetryMaxMs = 60, ModelsTimeoutMs = 2000 };
        }

        static async Task<bool> Until(Func<bool> cond, int ms = 8000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                await Task.Delay(10);
            }
            return cond();
        }

        static async Task PhaseIs(PairingSession s, PairPhase p)
        {
            Assert.True(await Until(() => s.Phase == p), "expected phase " + p + " but it is " + s.Phase + " detail=" + s.Detail);
        }

        static async Task RunEnds(PairingSession s)
        {
            var t = s.Running;
            Assert.Same(t, await Task.WhenAny(t, Task.Delay(8000)));
            await t;
        }

        static void NoSecrets(PairingSession s, List<string> log, params string[] secrets)
        {
            var texts = new List<string>();
            lock (log) texts.AddRange(log);
            texts.Add(s.Detail ?? "");
            texts.Add(s.EngineName ?? "");
            texts.Add(s.Code ?? "");
            var o = s.Outcome;
            if (o != null) { texts.Add(o.ToString()); if (o.Engine != null) texts.Add(o.Engine.ToString()); }
            foreach (var sec in secrets)
                foreach (var t in texts)
                    Assert.DoesNotContain(sec, t);
        }

        [Fact]
        public async Task PairingSucceedsThroughTheHeldGet()
        {
            var gate = Hold();
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => gate.Task)))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.ShowingCode);
                Assert.True(s.Busy);
                Assert.Equal("493201", s.Code);
                Assert.Equal("493 201", PairingSession.FormatCode(s.Code));
                Assert.Equal("den-pc", s.EngineName);

                Assert.True(await Until(() => f.Count("GET", HeldPath) == 1), "the held GET was not sent");
                var post = Assert.Single(f.Seen("POST", PairPath));
                Assert.Equal("1", post.Csrf);
                Assert.Equal("", post.Auth);
                Assert.Equal("RimWorld on test-box", (string)JObject.Parse(post.Body)["name"]);
                Assert.Equal(PairPhase.ShowingCode, s.Phase);   // still waiting while the GET is held

                gate.SetResult(PReply.Json(200, "{\"token\":\"" + Token + "\",\"client_id\":\"c-42\",\"engine_id\":\"" + Id1.ToUpperInvariant() + "\"}"));
                await PhaseIs(s, PairPhase.Paired);
                await RunEnds(s);
                var o = s.Outcome;
                Assert.NotNull(o);
                Assert.Equal(Token, o.Engine.Token);
                Assert.Equal("c-42", o.Engine.ClientId);
                Assert.Equal(Id1, o.Engine.EngineId.ToLowerInvariant());
                Assert.Equal(f.Root, o.Root);
                Assert.Equal(f.Root, o.Engine.Address);
                Assert.Equal("gemma4-e4b", o.ModelId);
                Assert.Equal("Gemma 4 26B", o.ModelName);
                Assert.Equal("den-pc", o.EngineName);
                var models = Assert.Single(f.Seen("GET", "/v1/models"));
                Assert.Equal("Bearer " + Token, models.Auth);
                Assert.False(s.Busy);
                NoSecrets(s, log, Token, ReqId);
                Assert.Contains(log, l => l.Contains("code shown"));
            }
        }

        [Fact]
        public async Task CancelWhileShowingTheCodeWithdrawsTheRequest()
        {
            var gate = Hold();
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => gate.Task)))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.ShowingCode);
                Assert.True(await Until(() => f.Count("GET", HeldPath) == 1));
                await s.CancelAsync();
                Assert.Equal(PairPhase.Cancelled, s.Phase);
                var del = Assert.Single(f.Seen("DELETE", HeldPath));
                Assert.Equal("1", del.Csrf);
                await RunEnds(s);
                Assert.Equal(PairPhase.Cancelled, s.Phase);   // the ended run does not overwrite it
                Assert.Null(s.Outcome);
                Assert.False(s.Busy);
                gate.TrySetResult(PReply.Json(410, "{}"));
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task CancelBeforeAnyRequestSendsNoDelete()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task)))
            {
                var log = new List<string>();
                var s = Make(f, log);
                await s.CancelAsync();    // never started
                Assert.Equal(0, f.Count("DELETE", HeldPath));
            }
        }

        [Fact]
        public async Task DenyEndsInDenied()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Task.FromResult(PReply.Json(403, "{\"error\":\"denied\",\"code\":\"pair_denied\"}")))))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Denied);
                await RunEnds(s);
                Assert.Null(s.Outcome);
                Assert.False(s.CanTryAgain);
                Assert.False(s.TryAgain());
                Assert.Equal(1, f.Count("POST", PairPath));
                NoSecrets(s, log, Token, ReqId);
                // after a Deny there is nothing to withdraw
                await s.CancelAsync();
                Assert.Equal(0, f.Count("DELETE", HeldPath));
            }
        }

        [Fact]
        public async Task GoneOffersOneTryAgain()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Task.FromResult(PReply.Json(410, "{\"error\":\"gone\",\"code\":\"pair_gone\"}")))))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Gone);
                await RunEnds(s);
                Assert.True(s.CanTryAgain);
                Assert.True(s.TryAgain());
                await RunEnds(s);
                Assert.True(await Until(() => f.Count("GET", HeldPath) == 2));
                Assert.Equal(2, f.Count("POST", PairPath));
                Assert.Equal(1, f.Count("GET", "/engine/v1/hello"));    // the second round does not repeat hello
                Assert.Equal(PairPhase.Gone, s.Phase);
                Assert.False(s.CanTryAgain);
                Assert.False(s.TryAgain());
                Assert.Equal(2, f.Count("POST", PairPath));
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task A410OnThePostIsGoneToo()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task, r => PReply.Json(410, "{\"code\":\"pair_gone\"}"))))
            {
                var s = Make(f, new List<string>());
                s.Start();
                await PhaseIs(s, PairPhase.Gone);
                Assert.True(s.CanTryAgain);
            }
        }

        [Fact]
        public async Task IssueFailedIsAskedAgainAndThenPairs()
        {
            int n = 0;
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r =>
            {
                if (Interlocked.Increment(ref n) == 1) return Task.FromResult(PReply.Json(500, "{\"error\":\"could not issue\",\"code\":\"issue_failed\"}"));
                return Task.FromResult(PReply.Json(200, "{\"token\":\"" + Token + "\",\"client_id\":\"c1\",\"engine_id\":\"" + Id1 + "\"}"));
            })))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Paired);
                Assert.Equal(2, f.Count("GET", HeldPath));
                Assert.Equal(1, f.Count("POST", PairPath));
                Assert.Equal(Token, s.Outcome.Engine.Token);
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task AnEmpty200OnTheHeldGetIsAskedAgain()
        {
            int n = 0;
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r =>
            {
                if (Interlocked.Increment(ref n) == 1) return Task.FromResult(PReply.Json(200, ""));
                return Task.FromResult(PReply.Json(200, "{\"token\":\"" + Token + "\",\"client_id\":\"c1\",\"engine_id\":\"" + Id1 + "\"}"));
            })))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Paired);
                Assert.Equal(2, f.Count("GET", HeldPath));
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task AnotherHeld500FailsWithItsCode()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Task.FromResult(PReply.Json(500, "{\"error\":\"x\",\"code\":\"weird\"}")))))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Failed);
                Assert.Equal("weird", s.Detail);
                Assert.Equal(1, f.Count("GET", HeldPath));
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task PostFailureIsFailedWithItsCode()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task, r => PReply.Json(500, "{\"error\":\"x\",\"code\":\"pair_failed\"}"))))
            {
                var s = Make(f, new List<string>());
                s.Start();
                await PhaseIs(s, PairPhase.Failed);
                Assert.Equal("pair_failed", s.Detail);
            }
        }

        [Fact]
        public async Task Post404IsNoPairing()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task, r => PReply.Json(404, "{\"code\":\"pairing_off\"}"))))
            {
                var s = Make(f, new List<string>());
                s.Start();
                await PhaseIs(s, PairPhase.NoPairing);
            }
        }

        [Fact]
        public async Task ADroppedHeldGetIsRetriedAndThenSucceeds()
        {
            int n = 0;
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r =>
            {
                if (Interlocked.Increment(ref n) == 1) return Task.FromResult(PReply.Drop());
                return Task.FromResult(PReply.Json(200, "{\"token\":\"" + Token + "\",\"client_id\":\"c1\",\"engine_id\":\"" + Id1 + "\"}"));
            })))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Paired);
                Assert.Equal(2, f.Count("GET", HeldPath));
                Assert.Equal(1, f.Count("POST", PairPath));     // asked once; only the GET is repeated
                Assert.False(s.Reconnecting);
                Assert.Equal(Token, s.Outcome.Engine.Token);
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task HelloWithoutTokenRequiredIsOpenAndNeverAsksToPair()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, false, false), r => Hold().Task)))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.Open);
                await RunEnds(s);
                Assert.Equal(0, f.Count("POST", PairPath));
                Assert.Equal("", s.Outcome.Engine.Token);
                Assert.Equal("gemma4-e4b", s.Outcome.ModelId);
                Assert.Equal(Id1, s.Outcome.Engine.EngineId);
                Assert.Equal("", Assert.Single(f.Seen("GET", "/v1/models")).Auth);
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task ExistingTokenThatIsPairedIsKeptWithoutAskingAgain()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true, ",\"paired\":true,\"client_id\":\"c-9\""), r => Hold().Task)))
            {
                var log = new List<string>();
                var s = Make(f, log, Token);
                s.Start();
                await PhaseIs(s, PairPhase.Paired);
                await RunEnds(s);
                Assert.Equal(0, f.Count("POST", PairPath));
                Assert.Equal("Bearer " + Token, Assert.Single(f.Seen("GET", "/engine/v1/hello")).Auth);
                Assert.Equal(Token, s.Outcome.Engine.Token);
                Assert.Equal("c-9", s.Outcome.Engine.ClientId);
                Assert.Equal("Bearer " + Token, Assert.Single(f.Seen("GET", "/v1/models")).Auth);
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task ExistingTokenThatWasRevokedGoesOnToPair()
        {
            var gate = Hold();
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true, ",\"paired\":false,\"revoked\":true"), r => gate.Task)))
            {
                var log = new List<string>();
                var s = Make(f, log, Token);
                s.Start();
                await PhaseIs(s, PairPhase.ShowingCode);
                Assert.Equal(1, f.Count("POST", PairPath));
                Assert.Equal("", f.Seen("POST", PairPath)[0].Auth);     // the old token is not sent to the pair call
                await s.CancelAsync();
                gate.TrySetResult(PReply.Json(410, "{}"));
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task TokenRequiredWithPairingOffIsNoPairing()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, false, true), r => Hold().Task)))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.NoPairing);
                Assert.Equal(0, f.Count("POST", PairPath));
                Assert.Equal("den-pc", s.EngineName);
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task NothingListeningIsUnreachable()
        {
            int port = FakePairEngine.FreePort();
            var log = new List<string>();
            var s = new PairingSession(new PairingApi { HelloTimeoutMs = 3000 }, "http://127.0.0.1:" + port, "RimWorld on test-box", null, x => { lock (log) log.Add(x); });
            s.Start();
            await PhaseIs(s, PairPhase.Unreachable);
            Assert.False(s.Busy);
            NoSecrets(s, log, Token, ReqId, "127.0.0.1");
        }

        [Fact]
        public async Task ANonEasyEngineIsNotAnEngine()
        {
            using (var f = new FakePairEngine(Route(r => "{\"engine\":\"other\"}", r => Hold().Task)))
            {
                var log = new List<string>();
                var s = Make(f, log);
                s.Start();
                await PhaseIs(s, PairPhase.NotAnEngine);
                Assert.Equal(0, f.Count("POST", PairPath));
                NoSecrets(s, log, Token, ReqId);
            }
        }

        [Fact]
        public async Task ANonJsonHelloIsNotAnEngineAndAnHttpErrorSaysItsStatus()
        {
            using (var f = new FakePairEngine(r => Task.FromResult(PReply.Json(200, "<html>hi</html>"))))
            {
                var s = Make(f, new List<string>());
                s.Start();
                await PhaseIs(s, PairPhase.NotAnEngine);
            }
            using (var f = new FakePairEngine(r => Task.FromResult(PReply.Json(404, "{}"))))
            {
                var s = Make(f, new List<string>());
                s.Start();
                await PhaseIs(s, PairPhase.NotAnEngine);
                Assert.Equal("HTTP 404", s.Detail);
            }
        }

        [Fact]
        public async Task ABadPostReplyFails()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task, r => PReply.Json(200, "{\"id\":\"\"}"))))
            {
                var s = Make(f, new List<string>());
                s.Start();
                await PhaseIs(s, PairPhase.Failed);
                Assert.Equal("bad reply", s.Detail);
            }
        }

        [Fact]
        public async Task Start_IsOnceOnly()
        {
            var gate = Hold();
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => gate.Task)))
            {
                var s = Make(f, new List<string>());
                s.Start();
                s.Start();
                await PhaseIs(s, PairPhase.ShowingCode);
                await Task.Delay(50);
                Assert.Equal(1, f.Count("GET", "/engine/v1/hello"));
                Assert.Equal(1, f.Count("POST", PairPath));
                await s.CancelAsync();
                gate.TrySetResult(PReply.Json(410, "{}"));
            }
        }

        [Fact]
        public void FormatCodeAndClientName()
        {
            Assert.Equal("493 201", PairingSession.FormatCode("493201"));
            Assert.Equal("493 201", PairingSession.FormatCode("493 201"));
            Assert.Equal("12345", PairingSession.FormatCode("12345"));
            Assert.Equal("", PairingSession.FormatCode(null));
            Assert.Equal("RimWorld on den", PairingSession.ClientNameFor(" den "));
            Assert.Equal("RimWorld on this computer", PairingSession.ClientNameFor(""));
            Assert.Equal(40, PairingSession.ClientNameFor(new string('x', 100)).Length);
        }

        // ====================================================================================== 7. EngineReconnect

        static Func<PReq, Task<PReply>> ReconnectRoute(string id, bool tokenRequired, Func<PReq, string> extraFor)
        {
            return r =>
            {
                if (r.Method == "GET" && r.Path == "/engine/v1/hello") return Task.FromResult(PReply.Json(200, HelloJson(id, true, tokenRequired, extraFor(r))));
                if (r.Method == "GET" && r.Path == "/v1/models") return Task.FromResult(PReply.Json(200, ModelsBody));
                return Task.FromResult(PReply.Json(404, "{}"));
            };
        }

        static PairStore StoreWith(string address, string token = "tok-reconnect")
        {
            var s = new PairStore();
            s.Put(new PairedEngine { EngineId = Id1, Token = token, ClientId = "c1", Name = "den-pc", Address = address });
            return s;
        }

        static PairingApi Api() { return new PairingApi { HelloTimeoutMs = 3000, ShortTimeoutMs = 3000 }; }

        [Fact]
        public async Task ReconnectPairedAtTheLastAddress()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id1, true, r => r.Auth == "Bearer tok-reconnect" ? ",\"paired\":true,\"client_id\":\"c1\"" : "")))
            {
                var store = StoreWith(f.Root);
                var res = await EngineReconnect.CheckAsync(store, Id1, f.Root, Api(), null);
                Assert.True(res.Found);
                Assert.Equal(EngineAuth.Paired, res.Auth);
                Assert.False(res.Moved);
                Assert.False(res.TokenCleared);
                Assert.Equal(f.Root, res.Root);
                Assert.Equal("gemma4-e4b", res.ModelId);
                Assert.True(store.HasToken(Id1));
                Assert.Equal("Bearer tok-reconnect", Assert.Single(f.Seen("GET", "/v1/models")).Auth);
            }
        }

        [Fact]
        public async Task ReconnectFindsAnEngineThatMoved()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id1, true, r => r.Auth == "Bearer tok-reconnect" ? ",\"paired\":true,\"client_id\":\"c1\"" : "")))
            {
                string dead = "http://127.0.0.1:" + FakePairEngine.FreePort();
                var store = StoreWith(dead);
                var m = new MdnsEngine { Id = Id1, Name = "den-pc", Port = f.Port };
                m.Addrs.Add(IPAddress.Loopback);
                int asked = 0;
                EngineReconnect.Browse browse = (ms, ct) => { asked++; return Task.FromResult(new List<MdnsEngine> { m }); };
                var res = await EngineReconnect.CheckAsync(store, Id1, dead, Api(), browse);
                Assert.Equal(1, asked);
                Assert.True(res.Found);
                Assert.True(res.Moved);
                Assert.Equal(f.Root, res.Root);
                Assert.Equal(EngineAuth.Paired, res.Auth);
                Assert.Equal(f.Root, store.Get(Id1).Address);
                Assert.True(store.HasToken(Id1));
            }
        }

        [Fact]
        public async Task ReconnectIgnoresAnEngineWithAnotherIdAtTheAddress()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id2, true, r => "")))
            {
                var store = StoreWith(f.Root);
                var res = await EngineReconnect.CheckAsync(store, Id1, f.Root, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine>()));
                Assert.False(res.Found);
                Assert.True(store.HasToken(Id1));
            }
        }

        [Fact]
        public async Task ReconnectRevokedClearsTheToken()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id1, true, r => ",\"paired\":false,\"revoked\":true")))
            {
                var store = StoreWith(f.Root);
                var res = await EngineReconnect.CheckAsync(store, Id1, f.Root, Api(), null);
                Assert.True(res.Found);
                Assert.Equal(EngineAuth.TokenRevoked, res.Auth);
                Assert.True(res.TokenCleared);
                Assert.False(store.HasToken(Id1));
                Assert.Equal("", store.Get(Id1).Token);
                Assert.Equal("den-pc", store.Get(Id1).Name);
                Assert.Null(res.ModelId);
            }
        }

        [Fact]
        public async Task ReconnectUnknownTokenClearsTheToken()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id1, true, r => ",\"paired\":false")))
            {
                var store = StoreWith(f.Root);
                var res = await EngineReconnect.CheckAsync(store, Id1, f.Root, Api(), null);
                Assert.Equal(EngineAuth.TokenInvalid, res.Auth);
                Assert.True(res.TokenCleared);
                Assert.False(store.HasToken(Id1));
            }
        }

        [Fact]
        public async Task ReconnectWithNoTokenAsksToPair()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id1, true, r => "")))
            {
                var store = new PairStore();
                var res = await EngineReconnect.CheckAsync(store, Id1, f.Root, Api(), null);
                Assert.True(res.Found);
                Assert.Equal(EngineAuth.TokenRequired, res.Auth);
                Assert.False(res.TokenCleared);
                Assert.Null(res.ModelId);
            }
        }

        [Fact]
        public async Task ReconnectWithNobodyAnsweringKeepsTheToken()
        {
            string dead = "http://127.0.0.1:" + FakePairEngine.FreePort();
            var store = StoreWith(dead);
            var res = await EngineReconnect.CheckAsync(store, Id1, dead, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine>()));
            Assert.False(res.Found);
            Assert.False(res.TokenCleared);
            Assert.Equal(EngineAuth.Unknown, res.Auth);
            Assert.True(store.HasToken(Id1));
            Assert.Equal("tok-reconnect", store.Get(Id1).Token);
            Assert.Equal(dead, store.Get(Id1).Address);
        }

        [Fact]
        public async Task ReconnectSurvivesABrowseThatThrows()
        {
            string dead = "http://127.0.0.1:" + FakePairEngine.FreePort();
            var store = StoreWith(dead);
            var res = await EngineReconnect.CheckAsync(store, Id1, dead, Api(), (ms, ct) => throw new InvalidOperationException("boom"));
            Assert.False(res.Found);
            Assert.True(store.HasToken(Id1));
        }

        [Fact]
        public async Task ReconnectUsesTheStoredAddressWhenNoneIsGiven()
        {
            using (var f = new FakePairEngine(ReconnectRoute(Id1, false, r => "")))
            {
                var store = StoreWith(f.Root);
                var res = await EngineReconnect.CheckAsync(store, Id1, null, Api(), null);
                Assert.True(res.Found);
                Assert.Equal(EngineAuth.Open, res.Auth);
                Assert.Equal(f.Root, res.Root);
                Assert.Equal("gemma4-e4b", res.ModelId);
            }
        }

        // ====================================================================================== 8. EngineFinder

        const string FindJson = "[{\"name\":\"den-pc\",\"engine_id\":\"9F2C4E1A0B3D5F6E7A8B9C0D1E2F3A4B\",\"api\":1,\"address\":\"192.168.1.20:47480\",\"host\":\"easy-engine-9f2c4e1a.local.\"}]";

        [Fact]
        public void ParseFindJsonReadsTheListing()
        {
            var e = Assert.Single(EngineFinder.ParseFindJson(FindJson));
            Assert.Equal(Id1, e.Id);
            Assert.Equal("den-pc", e.Name);
            Assert.Equal(1, e.Api);
            Assert.Equal(47480, e.Port);
            Assert.Equal("192.168.1.20:47480", e.Address);
            Assert.Equal("easy-engine-9f2c4e1a.local.", e.Host);
            Assert.Equal("http://192.168.1.20:47480", EngineFinder.RootOf(e));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("{}")]
        [InlineData("[1,2,3]")]
        [InlineData("[{\"name\":\"x\"}]")]
        [InlineData("[{\"engine_id\":\"ab\"}]")]
        [InlineData("[{\"engine_id\":\"ab\",\"address\":\"\"}]")]
        public void ParseFindJsonGarbageIsEmpty(string json)
        {
            Assert.Empty(EngineFinder.ParseFindJson(json));
        }

        [Fact]
        public void RootOfFallsBackToTheHostName()
        {
            var e = new MdnsEngine { Host = "easy-engine-aa.local.", Port = 47480 };
            Assert.Equal("http://easy-engine-aa.local:47480", EngineFinder.RootOf(e));
            Assert.Null(EngineFinder.RootOf(new MdnsEngine()));
        }

        static MdnsEngine OnFake(FakePairEngine f, string id = Id1)
        {
            var m = new MdnsEngine { Id = id, Name = "den-pc", Api = 1, Port = f.Port, Host = "easy-engine-9f2c4e1a.local." };
            m.Addrs.Add(IPAddress.Loopback);
            return m;
        }

        [Fact]
        public async Task FindAsyncAnswersWithTheHelloFields()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task)))
            {
                var store = new PairStore();
                store.Put(new PairedEngine { EngineId = Id1, Token = Token });
                var found = await EngineFinder.FindAsync(store, null, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine> { OnFake(f) }));
                var e = Assert.Single(found);
                Assert.True(e.Answered);
                Assert.Equal(Id1, e.EngineId);
                Assert.Equal("den-pc", e.Name);
                Assert.Equal("Gemma 4 26B", e.ModelName);
                Assert.Equal("ready", e.Phase);
                Assert.True(e.HaveToken);
                Assert.True(e.NeedsToken);
                Assert.Equal(f.Root, e.Root);
                Assert.Equal("127.0.0.1:" + f.Port, e.Address);
                Assert.Equal("", Assert.Single(f.Seen("GET", "/engine/v1/hello")).Auth);    // the find sends no token
            }
        }

        [Fact]
        public async Task FindAsyncWithoutATokenInTheStore()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task)))
            {
                var found = await EngineFinder.FindAsync(new PairStore(), null, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine> { OnFake(f) }));
                Assert.False(Assert.Single(found).HaveToken);
                var found2 = await EngineFinder.FindAsync(null, null, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine> { OnFake(f) }));
                Assert.False(Assert.Single(found2).HaveToken);
            }
        }

        [Fact]
        public async Task FindAsyncListsAnEngineThatDoesNotAnswerHelloAsUnanswered()
        {
            var m = new MdnsEngine { Id = Id1, Name = "den-pc", Port = FakePairEngine.FreePort() };
            m.Addrs.Add(IPAddress.Loopback);
            var found = await EngineFinder.FindAsync(new PairStore(), null, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine> { m }));
            var e = Assert.Single(found);
            Assert.False(e.Answered);
            Assert.Equal("den-pc", e.Name);
        }

        [Fact]
        public async Task FindAsyncDropsAnEngineWhoseHelloHasAnotherId()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id2, true, true), r => Hold().Task)))
            {
                var found = await EngineFinder.FindAsync(new PairStore(), null, Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine> { OnFake(f) }));
                Assert.False(Assert.Single(found).Answered);
            }
        }

        [Fact]
        public async Task FindAsyncFallsBackToTheBinaryWhenBrowseFindsNothing()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task)))
            {
                string json = "[{\"name\":\"den-pc\",\"engine_id\":\"" + Id1.ToUpperInvariant() + "\",\"api\":1,\"address\":\"127.0.0.1:" + f.Port + "\",\"host\":\"easy-engine-9f2c4e1a.local.\"}]";
                string gotPath = null, gotArgs = null;
                EngineFinder.RunProgram run = (path, args, ms) => { gotPath = path; gotArgs = args; return Task.FromResult(json); };
                var found = await EngineFinder.FindAsync(new PairStore(), "/fake/easy-engine", Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine>()), run);
                Assert.Equal("/fake/easy-engine", gotPath);
                Assert.StartsWith("find --json", gotArgs);
                var e = Assert.Single(found);
                Assert.True(e.Answered);
                Assert.Equal(Id1, e.EngineId);
                Assert.Equal(f.Root, e.Root);
            }
        }

        [Fact]
        public async Task FindAsyncDoesNotRunTheBinaryWhenBrowseFound()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task)))
            {
                bool ran = false;
                EngineFinder.RunProgram run = (path, args, ms) => { ran = true; return Task.FromResult("[]"); };
                var found = await EngineFinder.FindAsync(new PairStore(), "/fake/easy-engine", Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine> { OnFake(f) }), run);
                Assert.False(ran);
                Assert.Single(found);
            }
        }

        [Fact]
        public async Task FindAsyncWithNothingAnywhereIsEmptyAndNeverThrows()
        {
            EngineFinder.RunProgram nullRun = (path, args, ms) => Task.FromResult<string>(null);
            var a = await EngineFinder.FindAsync(new PairStore(), "/fake/easy-engine", Api(), (ms, ct) => Task.FromResult(new List<MdnsEngine>()), nullRun);
            Assert.Empty(a);
            EngineFinder.RunProgram throwingRun = (path, args, ms) => throw new IOException("x");
            var b = await EngineFinder.FindAsync(new PairStore(), "/fake/easy-engine", Api(), (ms, ct) => throw new IOException("y"), throwingRun);
            Assert.Empty(b);
        }

        [Fact]
        public async Task HelloAtAsyncReadsATypedAddress()
        {
            using (var f = new FakePairEngine(Route(r => HelloJson(Id1, true, true), r => Hold().Task)))
            {
                var store = new PairStore();
                store.Put(new PairedEngine { EngineId = Id1, Token = Token });
                var e = await EngineFinder.HelloAtAsync("127.0.0.1:" + f.Port, store, Api());
                Assert.True(e.Answered);
                Assert.Equal(Id1, e.EngineId);
                Assert.Equal("den-pc", e.Name);
                Assert.Equal("Gemma 4 26B", e.ModelName);
                Assert.Equal("ready", e.Phase);
                Assert.True(e.NeedsToken);
                Assert.True(e.HaveToken);
                Assert.Equal(f.Root, e.Root);

                var none = await EngineFinder.HelloAtAsync("http://127.0.0.1:" + f.Port + "/v1", new PairStore(), Api());
                Assert.True(none.Answered);
                Assert.False(none.HaveToken);
            }
        }

        [Fact]
        public async Task HelloAtAsyncWhenNothingAnswersOrTheTextIsJunk()
        {
            string typed = "127.0.0.1:" + FakePairEngine.FreePort();
            var dead = await EngineFinder.HelloAtAsync(typed, new PairStore(), Api());
            Assert.False(dead.Answered);
            Assert.StartsWith("http://127.0.0.1:", dead.Root);
            Assert.Equal(typed, dead.Name);

            var junk = await EngineFinder.HelloAtAsync("ftp://nope", new PairStore(), Api());
            Assert.False(junk.Answered);
            Assert.Equal("", junk.Root);
        }
    }
}
