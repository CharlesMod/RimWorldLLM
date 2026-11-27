using System;
using System.IO;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>One-time carry-over of the settings saved under the mod's old name (HermesVoice). Verse-free.
    /// RimWorld names a mod's settings file Mod_&lt;folder&gt;_&lt;Mod class&gt;.xml in its Config folder, so the
    /// old file is Mod_*_HermesVoiceMod.xml. The old file is never changed or removed.</summary>
    public static class LegacySettings
    {
        public const string OldModClass = "HermesVoiceMod";
        public const string OldSettingsClass = "HermesVoice.HermesVoiceSettings";
        public const string NewSettingsClass = "RimWorldLLM.RimWorldLLMSettings";
        public const string OldFolder = "HermesVoice";

        public enum Outcome { NewExists, NoOldFile, Copied, Failed }

        /// <summary>The old settings file in <paramref name="configDir"/>, or null. Prefers the one from a folder named HermesVoice, else the newest.</summary>
        public static string FindOld(string configDir)
        {
            try
            {
                if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir)) return null;
                var found = Directory.GetFiles(configDir, "Mod_*_" + OldModClass + ".xml");
                if (found.Length == 0) return null;
                string preferred = found.FirstOrDefault(f => Path.GetFileName(f) == "Mod_" + OldFolder + "_" + OldModClass + ".xml");
                return preferred ?? found.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
            }
            catch { return null; }
        }

        /// <summary>When <paramref name="newFile"/> is missing and an old settings file exists beside it, writes the old values
        /// to <paramref name="newFile"/> (the settings class renamed so RimWorld loads them into the new settings).</summary>
        public static Outcome Migrate(string newFile, out string oldFile)
        {
            oldFile = null;
            try
            {
                if (string.IsNullOrEmpty(newFile) || File.Exists(newFile)) return Outcome.NewExists;
                oldFile = FindOld(Path.GetDirectoryName(newFile));
                if (oldFile == null) return Outcome.NoOldFile;
                string text = File.ReadAllText(oldFile).Replace(OldSettingsClass, NewSettingsClass);
                string tmp = newFile + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, newFile);
                return Outcome.Copied;
            }
            catch { return Outcome.Failed; }
        }
    }
}
