namespace HermesVoice.Core
{
    public static class SettingsMigration
    {
        public const int OldDefaultPort = 8765;

        /// <summary>8765 (the v1 default, shared with RIMAPI and others) moves to 9765; anything else is kept.</summary>
        public static int Port(int saved)
        {
            if (saved == OldDefaultPort) return Const.DefaultPort;
            if (saved < 1 || saved > 65525) return Const.DefaultPort;
            return saved;
        }
    }
}
