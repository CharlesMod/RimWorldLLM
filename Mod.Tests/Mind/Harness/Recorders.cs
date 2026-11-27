// Recorders for the end-to-end goldens and the harness: every LLM request (with the reply) and every bridge write, in order, in the JSON
// shape the Python capture script writes (~/.local/share/rimworld-hermes-engine/csmind/tools/capture_golden.py).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;

namespace RimWorldLLM.Tests.Mind.ReplayTool
{
    /// <summary>Wraps an ILlmClient and logs each call: kind, slot, actor, max_tokens, stop, grammar, temperature, json_schema, messages, reply.</summary>
    public sealed class RecordingLlm : ILlmClient
    {
        public readonly ILlmClient Inner;
        public readonly List<JObject> Log = new List<JObject>();
        /// <summary>Called with each entry as soon as it is logged (the harness streams to a JSONL file).</summary>
        public Action<JObject> OnEntry;
        public RecordingLlm(ILlmClient inner) { Inner = inner; }

        public async Task<Completion> CompleteAsync(IReadOnlyList<LlmMessage> messages, int? slot, string kind = "line", int maxTokens = 48,
                                                    IReadOnlyList<string> stop = null, string grammar = null, double? temperature = null,
                                                    int? actor = null, JObject jsonSchema = null)
        {
            var e = new JObject
            {
                ["kind"] = kind, ["slot"] = slot, ["actor"] = actor, ["max_tokens"] = maxTokens,
                ["stop"] = stop == null ? JValue.CreateNull() : new JArray(stop.Cast<object>().ToArray()),
                ["grammar"] = grammar, ["temperature"] = temperature,
                ["json_schema"] = jsonSchema == null ? JValue.CreateNull() : jsonSchema.DeepClone(),
                ["messages"] = new JArray(messages.Select(m => (JToken)new JArray(m.Role, m.Text))),
            };
            try
            {
                var c = await Inner.CompleteAsync(messages, slot, kind, maxTokens, stop, grammar, temperature, actor, jsonSchema);
                e["reply"] = c.Text;
                return c;
            }
            catch (Exception ex) { e["error"] = ex.GetType().Name + ": " + ex.Message; throw; }
            finally { lock (Log) { Log.Add(e); } OnEntry?.Invoke(e); }
        }
    }

    /// <summary>Wraps an IBridgeTransport and logs every non-GET request as {method, path, query, body, status} (op_id replaced by true).</summary>
    public sealed class RecordingTransport : IBridgeTransport
    {
        public readonly IBridgeTransport Inner;
        public readonly List<JObject> Log = new List<JObject>();
        public RecordingTransport(IBridgeTransport inner) { Inner = inner; }

        public async Task<BridgeReply> SendAsync(BridgeRequest request, CancellationToken ct = default(CancellationToken))
        {
            var r = await Inner.SendAsync(request, ct);
            if (request.Method != "GET")
            {
                JToken body = JValue.CreateNull();
                if (request.JsonBody != null)
                {
                    try
                    {
                        body = BridgeJson.Parse(request.JsonBody);
                        if (body is JObject o && o["op_id"] != null) o["op_id"] = true;
                    }
                    catch (JsonException) { body = request.JsonBody; }
                }
                string q = request.QueryString();
                var e = new JObject { ["method"] = request.Method, ["path"] = request.Path, ["query"] = q.StartsWith("?", StringComparison.Ordinal) ? q.Substring(1) : q, ["body"] = body, ["status"] = r.Status };
                lock (Log) { Log.Add(e); }
            }
            return r;
        }
    }
}
