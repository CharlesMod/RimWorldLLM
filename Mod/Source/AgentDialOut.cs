namespace HermesVoice
{
    public static class AgentDialOut
    {
        // Call on the game thread. No-op when disabled / no agentUrl. Wraps as {"type", "tick", "payload"}.
        public static void Send(string eventType, object payload)
        {
            // STUB: replaced by improve/harden
        }
    }
}
