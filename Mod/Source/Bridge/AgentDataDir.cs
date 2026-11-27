using System;
using System.Collections.Generic;
using System.IO;

namespace RimWorldLLM.Bridge
{
    /// <summary>Where the agent keeps its memory and logs, and a careful way to empty it. Mirrors agent/rimworldllm_agent/core/config.py default_data_dir(). Verse-free.</summary>
    public static class AgentDataDir
    {
        public const string LeafName = "rimworldllm-agent";

        /// <summary>True when the removal job is over and the agent is down, so its folder may be emptied. <paramref name="sawRemoving"/>: the engine has reported phase "removing" at least once.</summary>
        public static bool RemovalFinished(string phase, string agentPhase, bool sawRemoving, long elapsedMs)
        {
            if (phase == "removing") return false;
            if (!sawRemoving && elapsedMs < 3000) return false;   // the job may not have started yet
            return AgentDown(agentPhase);
        }

        public static bool AgentDown(string agentPhase)
        {
            return string.IsNullOrEmpty(agentPhase) || agentPhase == "not_installed" || agentPhase == "stopped" || agentPhase == "error";
        }

        public enum DeleteOutcome { Deleted, NothingThere, Refused }

        public sealed class DeleteResult
        {
            public DeleteOutcome Outcome;
            public string Reason;   // why it was refused, "" otherwise
            public long FreedBytes;
        }

        public sealed class Resolved
        {
            public string Path;
            /// <summary>True when RIMWORLDLLM_DATA_DIR chose the folder (the leaf name is then the player's own).</summary>
            public bool Overridden;
        }

        /// <summary><paramref name="os"/> is windows, linux or darwin; <paramref name="env"/> returns null for an unset variable.</summary>
        public static Resolved Resolve(string os, Func<string, string> env, string home)
        {
            string over = env("RIMWORLDLLM_DATA_DIR");
            if (!string.IsNullOrEmpty(over)) return new Resolved { Path = over, Overridden = true };
            if (os == "windows")
            {
                string local = env("LOCALAPPDATA");
                if (string.IsNullOrEmpty(local)) local = System.IO.Path.Combine(home ?? "", "AppData", "Local");
                return new Resolved { Path = System.IO.Path.Combine(local, LeafName) };
            }
            string xdg = env("XDG_DATA_HOME");
            string baseDir = string.IsNullOrEmpty(xdg) ? System.IO.Path.Combine(home ?? "", ".local", "share") : xdg;
            return new Resolved { Path = System.IO.Path.Combine(baseDir, LeafName) };
        }

        public static Resolved ResolveHere(EnginePlatform p)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Resolve(p.Os, n => Environment.GetEnvironmentVariable(n), home);
        }

        static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        static string Full(string p)
        {
            try { return System.IO.Path.GetFullPath(p).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar); }
            catch { return null; }
        }

        /// <summary>Total size of the files under the folder (links are not followed); 0 when absent.</summary>
        public static long SizeOf(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || IsLink(dir)) return 0;
                return Walk(new DirectoryInfo(dir));
            }
            catch { return 0; }
        }

        static long Walk(DirectoryInfo d)
        {
            long n = 0;
            try
            {
                foreach (var f in d.GetFiles()) { try { n += f.Length; } catch { } }
                foreach (var s in d.GetDirectories())
                    if ((s.Attributes & FileAttributes.ReparsePoint) == 0) n += Walk(s);
            }
            catch { }
            return n;
        }

        /// <summary>Why this folder must not be deleted, or null when it is the agent's own. <paramref name="extraProtected"/> are folders that must never be the target (home, LOCALAPPDATA, XDG base).</summary>
        public static string Check(Resolved expected, string dir, IEnumerable<string> extraProtected)
        {
            if (expected == null || string.IsNullOrEmpty(expected.Path) || string.IsNullOrEmpty(dir)) return "no folder";
            string want = Full(expected.Path), got = Full(dir);
            if (want == null || got == null) return "unreadable path";
            if (!System.IO.Path.IsPathRooted(expected.Path)) return "path is not absolute";
            var cmp = StringComparison.Ordinal;
            if (System.IO.Path.DirectorySeparatorChar == '\\') cmp = StringComparison.OrdinalIgnoreCase;
            if (!string.Equals(want, got, cmp)) return "not the agent's folder";
            string root = System.IO.Path.GetPathRoot(got);
            if (string.IsNullOrEmpty(System.IO.Path.GetFileName(got)) || (root != null && string.Equals(root.TrimEnd('/', '\\'), got, cmp))) return "a drive or filesystem root";
            // always the agent's own leaf name, even for an overridden folder: an override pointing at, say, Documents must never be emptied
            if (!string.Equals(System.IO.Path.GetFileName(got), LeafName, cmp)) return "not named " + LeafName;
            if (extraProtected != null)
                foreach (var p in extraProtected)
                {
                    string f = string.IsNullOrEmpty(p) ? null : Full(p);
                    if (f == null) continue;
                    if (string.Equals(f, got, cmp)) return "a system folder";
                    // the folder must not contain a protected one (e.g. an override pointing at a parent of home)
                    if (f.StartsWith(got + System.IO.Path.DirectorySeparatorChar, cmp)) return "contains a system folder";
                }
            if (IsLink(got)) return "a link to somewhere else";
            return null;
        }

        /// <summary>Deletes what is inside the agent's folder (and the folder itself when it is the default one). Refuses anything that is not exactly that folder.</summary>
        public static DeleteResult Delete(Resolved expected, string dir, IEnumerable<string> extraProtected)
        {
            var r = new DeleteResult { Outcome = DeleteOutcome.Refused, Reason = "" };
            string why = Check(expected, dir, extraProtected);
            if (why != null) { r.Reason = why; return r; }
            try
            {
                if (!Directory.Exists(dir)) { r.Outcome = DeleteOutcome.NothingThere; return r; }
                r.FreedBytes = SizeOf(dir);
                var d = new DirectoryInfo(dir);
                foreach (var f in d.GetFileSystemInfos()) Remove(f);
                if (!expected.Overridden) { try { d.Delete(false); } catch { } }
                r.Outcome = DeleteOutcome.Deleted;
            }
            catch (Exception e) { r.Outcome = DeleteOutcome.Refused; r.Reason = e.Message; }
            return r;
        }

        // A link is removed as a link; its target is never entered.
        static void Remove(FileSystemInfo f)
        {
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (f is DirectoryInfo) ((DirectoryInfo)f).Delete(false);
                else f.Delete();
                return;
            }
            var d = f as DirectoryInfo;
            if (d == null) { f.Attributes = FileAttributes.Normal; f.Delete(); return; }
            foreach (var c in d.GetFileSystemInfos()) Remove(c);
            d.Delete(false);
        }
    }
}
