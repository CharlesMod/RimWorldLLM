using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>A request as the fake server saw it (httpx.MockTransport's Request).</summary>
    public sealed class Seen
    {
        public string Method, Path, Url;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string BodyText => new UTF8Encoding(false).GetString(Body);
        public JObject Json() => (JObject)PyJson.Loads(Body);
    }

    public sealed class MockResponse
    {
        public int Status = 200;
        public byte[] Body = new byte[0];
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        public static MockResponse Json(int status, object body, string retryAfter = null)
        {
            var r = new MockResponse { Status = status, Body = PyJson.HttpxJson(body) };
            if (retryAfter != null) r.Headers["Retry-After"] = retryAfter;
            return r;
        }
        public static MockResponse Empty(int status) => new MockResponse { Status = status };
    }

    /// <summary>httpx.MockTransport: the handler sees each request and returns a response or throws (HttpRequestException = connect error).</summary>
    public sealed class MockHandler : HttpMessageHandler
    {
        private readonly Func<Seen, MockResponse> _h;
        public readonly List<Seen> All = new List<Seen>();
        public MockHandler(Func<Seen, MockResponse> h) { _h = h; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var s = new Seen { Method = request.Method.Method, Url = request.RequestUri.ToString(), Path = request.RequestUri.AbsolutePath };
            foreach (var h in request.Headers) s.Headers[h.Key] = string.Join(", ", h.Value);
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers) s.Headers[h.Key] = string.Join(", ", h.Value);
                s.Body = await request.Content.ReadAsByteArrayAsync(ct);
            }
            lock (All) All.Add(s);
            MockResponse r = _h(s);
            var resp = new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new ByteArrayContent(r.Body) };
            foreach (var kv in r.Headers) resp.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            return resp;
        }
    }

    public static class Mock
    {
        public static HttpClient Client(Func<Seen, MockResponse> h) => new HttpClient(new MockHandler(h)) { Timeout = Timeout.InfiniteTimeSpan };

        public static HttpClient Client(Func<Seen, MockResponse> h, out MockHandler handler)
        {
            handler = new MockHandler(h);
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public static HttpRequestException ConnectError(string m = "down") => new HttpRequestException(m);
    }
}

namespace RimWorldLLM.Tests.Mind.Fakes
{
    /// <summary>An ILlmClient that is not an HttpLlm (for "not an HttpLlm" checks).</summary>
    public sealed class NullClient : RimWorldLLM.Mind.ILlmClient
    {
        public System.Threading.Tasks.Task<RimWorldLLM.Mind.Completion> CompleteAsync(System.Collections.Generic.IReadOnlyList<RimWorldLLM.Mind.LlmMessage> messages, int? slot,
            string kind = "line", int maxTokens = 48, System.Collections.Generic.IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
            int? actor = null, Newtonsoft.Json.Linq.JObject jsonSchema = null)
            => System.Threading.Tasks.Task.FromResult(new RimWorldLLM.Mind.Completion("", new RimWorldLLM.Mind.CallStats()));
    }
}
