using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;

namespace RimWorldLLM
{
    /// <summary>Game side of the read tools inspect/find/survey (see Bridge/InspectPure.cs). Game thread only, no side effects.
    /// Each method returns {text, ids?, total?} or {refused: reason}. The bodies live in VivariumInspectRead.cs (Inspect) and
    /// VivariumInspectFind.cs (Find, Survey).</summary>
    public sealed partial class VivariumInspectHands : IInspectHands
    {
        public JObject Inspect(string target) { return InspectImpl(target); }
        public JObject Find(string what, string near, string item) { return FindImpl(what, near, item); }
        public JObject Survey(string what) { return SurveyImpl(what); }

        static JObject Text(string text) { return new JObject { ["text"] = text }; }
        static JObject Refused(string why) { return new JObject { ["refused"] = why }; }
    }
}
