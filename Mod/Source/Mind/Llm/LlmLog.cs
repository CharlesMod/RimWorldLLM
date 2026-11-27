// A tiny logging seam for the LLM-side classes (Python: logging.getLogger("rimworldllm")).
// Classes that log take an optional Action<string> / LogSink so tests can collect lines; the default goes here.
// TODO(csmind-merge): the lead may replace this with the one shared logger of RimWorldLLM.Mind.
using System;

namespace RimWorldLLM.Mind
{
    public delegate void LogSink(string level, string logger, string message);

    public static class LlmLog
    {
        /// <summary>Where default logging goes; null (the default) drops it.</summary>
        public static LogSink Sink;

        public static void Debug(string logger, string message) => Sink?.Invoke("DEBUG", logger, message);
        public static void Info(string logger, string message) => Sink?.Invoke("INFO", logger, message);
        public static void Warning(string logger, string message) => Sink?.Invoke("WARNING", logger, message);
    }
}
