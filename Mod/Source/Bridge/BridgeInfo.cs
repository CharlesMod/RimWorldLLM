using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    /// <summary>bridge.json: how the agent finds the bridge.</summary>
    public static class BridgeInfo
    {
        public static string PathIn(string configDir) { return Path.Combine(configDir, "HermesVoice", "bridge.json"); }

        public static JObject Build(int port, string bind, string token, int pid, string version, int saveEpoch)
        {
            return new JObject
            {
                ["port"] = port,
                ["bind"] = bind,
                ["token"] = token,
                ["pid"] = pid,
                ["version"] = version,
                ["save_epoch"] = saveEpoch
            };
        }

        /// <summary>Atomic write (temp file, then replace).</summary>
        public static void Write(string configDir, JObject info)
        {
            string path = PathIn(configDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, info.ToString(Newtonsoft.Json.Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
