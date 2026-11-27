// Port of core/model_tier.py: which size of model is serving, "small" or "large".
// The engine reports its model in its status (model, model_size_class, model_tier); a plain endpoint is judged by cfg.llm.model.
// The read is cached for REREAD_S and never blocks a line.
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Mind
{
    public sealed class ModelTier
    {
        public const double RereadS = 60.0;
        private static readonly Regex NonAlnum = new Regex("[^a-z0-9]", RegexOptions.CultureInvariant);

        private static string Norm(string s) => NonAlnum.Replace((s ?? "").ToLowerInvariant(), "");

        /// <summary>The engine's model_size_class ("small"/"large") wins over the name patterns; any other value falls back to them. A starter stage is small.</summary>
        public static string Classify(string model, System.Collections.Generic.IEnumerable<string> patterns, string staging = "", string sizeClass = "")
        {
            if (PyFloat.PyStrip(staging ?? "").ToLowerInvariant() == "starter") return "small";
            string sc = PyFloat.PyStrip(sizeClass ?? "").ToLowerInvariant();
            if (sc == "small" || sc == "large") return sc;
            string m = Norm(model);
            return m.Length > 0 && patterns.Any(p => Norm(p).Length > 0 && m.Contains(Norm(p))) ? "small" : "large";
        }

        private readonly MindConfig _cfg;
        private readonly Func<double> _clock;
        public string Model = "";
        public string Staging = "";
        public string SizeClass = "";
        private double? _at;
        private bool _busy;

        public ModelTier(MindConfig cfg, Func<double> clock) { _cfg = cfg; _clock = clock; }

        public string Tier => Classify(!string.IsNullOrEmpty(Model) ? Model : _cfg.Llm.Model, _cfg.SmallModelPatterns, Staging, SizeClass);
        public bool Small => Tier == "small";

        public bool Due() => !_busy && (_at == null || _clock() - _at.Value >= RereadS);

        /// <summary>Re-read the serving model (at most every RereadS). Never throws.</summary>
        public async Task RefreshAsync(ILlmClient llm)
        {
            if (!Due()) return;
            _busy = true;
            try
            {
                _at = _clock();
                if (!(llm is IEngineClient ec) || !await ec.IsEngineAsync().ConfigureAwait(false))
                {
                    Model = ""; Staging = ""; SizeClass = "";      // a plain endpoint: cfg.llm.model
                    return;
                }
                JToken d = await ec.FetchEngineStatusAsync().ConfigureAwait(false);
                if (d is JObject o)
                {
                    Model = Str(o, "model");
                    Staging = Str(o, "model_tier");
                    SizeClass = Str(o, "model_size_class");
                }
                // no answer: keep what we knew
            }
            catch (Exception e)      // a tier read must never hurt a line
            {
                LlmLog.Warning("rimworldllm.core.model_tier", "model tier read failed: " + e.Message);
            }
            finally { _busy = false; }
        }

        private static string Str(JObject o, string key)
        {
            JToken t = o.TryGetValue(key, out JToken v) ? v : null;
            return PyJson.Truthy(t) ? PyJson.Str(t) : "";
        }

        /// <summary>A feature that may be switched off below the large models.</summary>
        public static bool FeatureOn(ModelTier tier, bool master, bool smallOk) => master && (smallOk || !tier.Small);
    }
}
