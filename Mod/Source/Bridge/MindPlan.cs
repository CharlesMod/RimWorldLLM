// Pure (Verse-free) decisions for hosting the mind in the mod: what the mind needs to run, the MindConfig built from the mod's settings plus the
// optional config file in the data dir, and the status line. MindHost (Mod/Source/RimWorld) feeds it the game's state; Mod.Tests covers it.
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;

namespace RimWorldLLM.Bridge
{
    /// <summary>A snapshot of everything the mind's lifecycle and config depend on, taken on the game thread.</summary>
    public sealed class MindInputs
    {
        public bool ModEnabled = true;
        public bool GameLoaded;
        /// <summary>Held off by a memory removal in progress: never start while set.</summary>
        public bool Held;
        /// <summary>llmSource == "own": the player's endpoint, no engine.</summary>
        public bool Own;
        public string OwnBaseUrl = "", OwnModel = "", OwnApiKey = "";
        /// <summary>Local mode: the engine's /v1 base and the model name it reports ("" until known).</summary>
        public string EngineBase = "", EngineModel = "";
        /// <summary>Local mode: the engine can take requests (ready; a sleeping one wakes on a request).</summary>
        public bool EngineReady;
        public string DataDir = "";
        /// <summary>Stamp of the config file (path|length|mtime), "" when none.</summary>
        public string FileStamp = "";
        // Mod settings that mirror a mind setting (the mod's value wins over the file).
        public int PauseAmbientAtSpeed = 4;
        public bool Wants = true, Dreams = true, ArtDescriptions = true, Recap = true, TextFill = true;
    }

    public enum MindState { Disabled, WaitingForGame, WaitingForModel, Running, Error }

    public static class MindPlan
    {
        public const double LocalTimeoutS = 900.0;   // the engine holds a request while the model loads (no engine-side timeout)
        public const double OwnTimeoutS = 120.0;     // a remote endpoint: generous for a slow prefill, still bounded
        public const double ErrorShownS = 120.0;

        /// <summary>Whether the mind should be running.</summary>
        public static bool Wants(MindInputs i)
        {
            if (!i.ModEnabled || !i.GameLoaded || i.Held) return false;
            if (i.Own && string.IsNullOrWhiteSpace(i.OwnBaseUrl)) return false;   // nothing to talk to yet
            return true;
        }

        /// <summary>What a running mind was started with: a change restarts it. The local model name is left out (it changes while the
        /// engine loads one and the engine ignores it); the config file's stamp is in.</summary>
        public static string Key(MindInputs i)
        {
            var sb = new StringBuilder();
            sb.Append(i.Own ? "own" : "local").Append('\n');
            if (i.Own) sb.Append((i.OwnBaseUrl ?? "").Trim()).Append('\n').Append((i.OwnModel ?? "").Trim()).Append('\n').Append((i.OwnApiKey ?? "").Trim());
            else sb.Append(i.EngineBase);
            sb.Append('\n').Append(i.DataDir).Append('\n').Append(i.FileStamp).Append('\n');
            sb.Append(i.PauseAmbientAtSpeed).Append(i.Wants).Append(i.Dreams).Append(i.ArtDescriptions).Append(i.Recap).Append(i.TextFill);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the config file

        public static readonly string[] ConfigFileNames = { "config.toml", "config.json" };

        /// <summary>The config file in the data dir (config.toml, else config.json), or null.</summary>
        public static string FindConfigFile(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return null;
            foreach (string n in ConfigFileNames)
            {
                string p = Path.Combine(dataDir, n);
                try { if (File.Exists(p)) return p; } catch (Exception) { }
            }
            return null;
        }

        public static string Stamp(string dataDir)
        {
            string p = FindConfigFile(dataDir);
            if (p == null) return "";
            try { var f = new FileInfo(p); return p + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks; }
            catch (Exception) { return p; }
        }

        /// <summary>The file's table, or null when there is none. A broken file throws ConfigError (the mind does not start; the status says why).</summary>
        public static JObject ReadConfigFile(string dataDir)
        {
            string path = FindConfigFile(dataDir);
            if (path == null) return null;
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                JToken parsed = path.EndsWith(".toml", StringComparison.Ordinal)
                    ? TomlLite.Parse(new UTF8Encoding(false, true).GetString(raw))
                    : PyJson.Loads(raw);
                return parsed as JObject ?? throw new ConfigError(Path.GetFileName(path) + " must hold a table at the top level");
            }
            catch (ConfigError) { throw; }
            catch (Exception e) { throw new ConfigError(Path.GetFileName(path) + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ settings -> MindConfig

        /// <summary>The config table: defaults, then the file, then what the mod owns (endpoint, data dir, mirrored toggles).</summary>
        public static JObject BuildData(MindInputs i, JObject file)
        {
            var data = new JObject { ["llm"] = new JObject { ["timeout_s"] = i.Own ? OwnTimeoutS : LocalTimeoutS } };
            if (file != null)
            {
                foreach (var p in file.Properties())
                {
                    if (p.Name == "llm" && p.Value is JObject fl)
                    {
                        var llm = (JObject)data["llm"];
                        foreach (var q in fl.Properties()) llm[q.Name] = q.Value.DeepClone();
                    }
                    else if (p.Name != "bridge_json" && p.Name != "bridge_url" && p.Name != "bridge_token") data[p.Name] = p.Value.DeepClone();
                }
            }
            var l = data["llm"] as JObject;
            if (l == null) { l = new JObject(); data["llm"] = l; }
            if (i.Own)
            {
                l["base_url"] = (i.OwnBaseUrl ?? "").Trim();
                l["model"] = (i.OwnModel ?? "").Trim();
                l["api_key"] = (i.OwnApiKey ?? "").Trim();
            }
            else
            {
                l["base_url"] = i.EngineBase ?? "";
                l["model"] = i.EngineModel ?? "";
                l["api_key"] = "";
            }
            data["data_dir"] = i.DataDir;
            data["pause_ambient_at_speed"] = i.PauseAmbientAtSpeed;
            data["wants"] = i.Wants;
            data["dreams"] = i.Dreams;
            data["art_enabled"] = i.ArtDescriptions;
            data["recap_enabled"] = i.Recap;
            data["text_fill"] = i.TextFill;
            return data;
        }

        public static MindConfig BuildConfig(MindInputs i, MindEnv env = null)
        {
            return MindConfig.FromDict(BuildData(i, ReadConfigFile(i.DataDir)), env);
        }

        // ------------------------------------------------------------------ status

        /// <summary>The state to show. <paramref name="lastError"/> counts while it is younger than ErrorShownS.</summary>
        public static MindState State(MindInputs i, bool running, string lastError, double errorAgeS)
        {
            if (!i.ModEnabled) return MindState.Disabled;
            if (!i.GameLoaded || i.Held) return MindState.WaitingForGame;
            if (!string.IsNullOrEmpty(lastError) && errorAgeS < ErrorShownS) return MindState.Error;
            if (i.Own ? string.IsNullOrWhiteSpace(i.OwnBaseUrl) : !i.EngineReady) return MindState.WaitingForModel;
            return MindState.Running;
        }

        public static string StateKey(MindState s)
        {
            switch (s)
            {
                case MindState.Disabled: return "RWL_Mind_Disabled";
                case MindState.WaitingForGame: return "RWL_Mind_WaitingGame";
                case MindState.WaitingForModel: return "RWL_Mind_WaitingModel";
                case MindState.Error: return "RWL_Mind_Error";
                default: return "RWL_Mind_Running";
            }
        }
    }
}
