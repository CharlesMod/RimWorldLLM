using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    /// <summary>Tests that read or flip the module-global Adult.Enabled run serially in this collection and reset it to the default (off).</summary>
    [CollectionDefinition("AdultState")]
    public class AdultStateCollection { }
}
