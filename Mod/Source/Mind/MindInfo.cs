// RimWorldLLM.Mind: the agent (formerly the Python package agent/rimworldllm_agent), ported to C#.
// Everything under Mod/Source/Mind is Verse-free: no UnityEngine, Verse or RimWorld references, so
// Mod.Tests compiles it directly. Game access goes only through IBridge. See docs/CSHARP-MIND.md.
namespace RimWorldLLM.Mind
{
    public static class MindInfo
    {
        public const string Version = "3.0.0-csmind";
    }
}
