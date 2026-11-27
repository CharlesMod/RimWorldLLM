using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;

namespace RimWorldLLM.Mind
{
    /// <summary>The HTTP transport (the httpx one): for a bridge on another port or machine, and the harness.</summary>
    public sealed class HttpBridgeTransport : IBridgeTransport, IDisposable
    {
        readonly HttpClient _http;
        readonly string _baseUrl;

        public HttpBridgeTransport(string baseUrl, TimeSpan? timeout = null, HttpMessageHandler handler = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = handler != null ? new HttpClient(handler) : new HttpClient();
            _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
        }

        public async Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
        {
            var msg = new HttpRequestMessage(new HttpMethod(request.Method), _baseUrl + request.Path + request.QueryString());
            if (request.Authorization != null) msg.Headers.TryAddWithoutValidation("Authorization", request.Authorization);
            if (request.JsonBody != null) msg.Content = new StringContent(request.JsonBody, new UTF8Encoding(false), "application/json");
            try
            {
                using (var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false))
                    return new BridgeReply((int)resp.StatusCode, await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch (HttpRequestException e) { throw new BridgeTransportException(e.Message, e); }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new BridgeTransportException("timeout", e); }
        }

        public void Dispose() { _http.Dispose(); }
    }

    /// <summary>Finds where bridge.json says the bridge is (the Python's BridgeClient.from_config, without the Config object).</summary>
    public static class BridgeLocator
    {
        /// <summary>The bridge's base url and token. An explicit url wins (token = bridgeToken or ""); otherwise bridge.json at
        /// bridgeJsonPath (else defaultBridgeJsonPath): bind 0.0.0.0 / :: becomes 127.0.0.1, and bridgeToken beats the file's token.
        /// Throws IOException / Newtonsoft JsonException / KeyNotFoundException like the Python's open and json.loads and bj["port"].</summary>
        public static KeyValuePair<string, string> Resolve(string bridgeUrl, string bridgeToken, string bridgeJsonPath, string defaultBridgeJsonPath = null)
        {
            if (!string.IsNullOrEmpty(bridgeUrl))
                return new KeyValuePair<string, string>(bridgeUrl, bridgeToken ?? "");
            string path = !string.IsNullOrEmpty(bridgeJsonPath) ? bridgeJsonPath : defaultBridgeJsonPath;
            var bj = ReadBridgeJson(path);
            JToken hostTok = bj["bind"];
            string host = hostTok != null && hostTok.Type != JTokenType.Null ? hostTok.ToString() : "127.0.0.1";
            if (host == "0.0.0.0" || host == "::") host = "127.0.0.1";
            JToken port = bj["port"];
            if (port == null) throw new KeyNotFoundException("port");
            JToken tok = bj["token"];
            string token = tok != null && tok.Type != JTokenType.Null ? tok.ToString() : "";
            if (!string.IsNullOrEmpty(bridgeToken)) token = bridgeToken;
            return new KeyValuePair<string, string>("http://" + host + ":" + port.ToString(), token);
        }

        /// <summary>bridge.json: {"port", "bind", "token", "pid", "version", "epoch"}.</summary>
        public static JObject ReadBridgeJson(string path)
        {
            var o = BridgeJson.Parse(File.ReadAllText(path)) as JObject;
            if (o == null) throw new Newtonsoft.Json.JsonException("bridge.json is not an object");
            return o;
        }

        /// <summary>BridgeClient over HTTP from the resolved url and token.</summary>
        public static BridgeClient Connect(string bridgeUrl, string bridgeToken, string bridgeJsonPath, string defaultBridgeJsonPath,
            ProtocolNames protocol = null, HttpMessageHandler handler = null)
        {
            var r = Resolve(bridgeUrl, bridgeToken, bridgeJsonPath, defaultBridgeJsonPath);
            return new BridgeClient(new HttpBridgeTransport(r.Key, TimeSpan.FromSeconds(10), handler), protocol, r.Value);
        }
    }
}
