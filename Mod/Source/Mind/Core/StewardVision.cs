// NEW (the narrator can look at the screen): what the core steward asks of a kit that has a look tool. Game words stay in the kit.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind.ToolLoop;

namespace RimWorldLLM.Mind
{
    /// <summary>One turn's look tool: its spec, the call, the limits, and the picture waiting to go into the next request.</summary>
    public interface IVisionLook
    {
        ToolSpec Spec { get; }
        string ToolName { get; }
        /// <summary>The kinds looked at this turn, in order (for the turn trace; no pictures).</summary>
        IReadOnlyList<string> Looked { get; }
        /// <summary>The tool's result: a legend line on success, else {"error": ...}. Never the picture bytes.</summary>
        Task<JToken> RunAsync(JObject values);
        /// <summary>The picture of the last successful look, once, as an image content part; null when there is none.</summary>
        JObject TakeAttachment();
    }

    /// <summary>A kit with a look tool (the Python had none). The steward asks it on routine turns only, once the gate is open.</summary>
    public interface IStewardVisionKit
    {
        /// <summary>A fresh look for this turn, or null when this digest has nothing to look at.</summary>
        IVisionLook NewLook(JObject digest, IBridge bridge, Func<int, Task> delay, ILookClient look);
    }
}
