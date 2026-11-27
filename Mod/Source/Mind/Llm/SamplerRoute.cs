// Port of core/sampler_route.py: route one call kind to a named engine sampler profile (cfg.aim_sampler).
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    /// <summary>Sends the named profile in X-Easy-Sampler instead of the kind's own. Shares the original's config, call log and http client
    /// (and its engine detection); it has its own AwayGate, as in Python.</summary>
    public sealed class NamedSamplerLlm : HttpLlm
    {
        private readonly HttpLlm _base;
        public string SamplerName { get; }

        public NamedSamplerLlm(HttpLlm baseLlm, string name) : base(baseLlm)
        {
            _base = baseLlm;
            SamplerName = name;
            Engine = baseLlm.Engine;
        }

        public override async Task<bool> IsEngineAsync()
        {
            bool eng = await _base.IsEngineAsync().ConfigureAwait(false);   // one detection, shared
            Engine = eng;
            return eng;
        }

        protected override async Task<(JObject fields, Dictionary<string, string> headers)> SamplingAsync(string kind, string grammar, JObject jsonSchema)
        {
            bool eng = await IsEngineAsync().ConfigureAwait(false);
            var fields = LlmWire.SamplerFields(Cfg, eng, kind);
            var h = new Dictionary<string, string>();
            if (eng) h["X-Easy-Sampler"] = SamplerName;
            return (fields, h);
        }
    }

    public static class SamplerRoute
    {
        /// <summary>A client that sends the named sampler, or null when the name is empty or llm is not an HttpLlm.</summary>
        public static ILlmClient Route(ILlmClient llm, string name)
        {
            if (string.IsNullOrEmpty(name) || !(llm is HttpLlm h)) return null;
            if (h is NamedSamplerLlm n && n.SamplerName == name) return n;
            return new NamedSamplerLlm(h, name);
        }
    }
}
