using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace HermesVoice.Bridge
{
    /// <summary>bridge.json: how the agent finds the bridge.</summary>
    public static class BridgeInfo
    {
        public static string PathIn(string configDir) { return Path.Combine(configDir, "HermesVoice", "bridge.json"); }

        public static JObject Build(int port, string bind, string token, int pid, string version, int saveEpoch, JObject llm = null)
        {
            var o = new JObject
            {
                ["port"] = port,
                ["bind"] = bind,
                ["token"] = token,
                ["pid"] = pid,
                ["version"] = version,
                ["save_epoch"] = saveEpoch
            };
            if (llm != null) o["llm"] = llm;   // {base_url, model, api_key}: the agent's LLM endpoint; the key never goes through /config
            return o;
        }

        /// <summary>chmod 600 where the runtime supports it; Windows profiles are already per-user.</summary>
        static void RestrictToOwner(string path)
        {
            try { Chmod600(path); } catch { }
        }

        static void Chmod600(string path)
        {
            if (Path.DirectorySeparatorChar == '\\') return;
            chmod(path, 384);   // 0600; P/Invoke because the game's Mono has no File.SetUnixFileMode
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, uint mode);

        /// <summary>Atomic write (temp file, then replace).</summary>
        public static void Write(string configDir, JObject info)
        {
            string path = PathIn(configDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, "");
            RestrictToOwner(tmp);                     // the file holds the token and the LLM key: owner-only before any content lands
            File.WriteAllText(tmp, info.ToString(Newtonsoft.Json.Formatting.Indented));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
