using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>Tests that read or flip the module-global Adult.Enabled run serially in this collection and reset it to the default (off).</summary>
    // One collection for all shared static state: Adult.Enabled (set by every Director on a config refresh) and the
    // RIMWORLDLLM_STEWARD_STRATEGY env var the steward tests set. A class can sit in only one collection.
    [CollectionDefinition("AdultState")]
    public class AdultStateCollection { }
}
