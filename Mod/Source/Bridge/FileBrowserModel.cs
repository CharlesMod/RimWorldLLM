using System;
using System.Collections.Generic;
using System.IO;

namespace HermesVoice.Bridge
{
    public enum OsKind { Windows, Linux, Mac }

    /// <summary>
    /// Pure logic behind the in-game .gguf file browser: roots, parent, a filtered and ordered listing, and the start folder.
    /// No game references. Nothing here throws on unreadable or missing paths; a listing carries an Error instead.
    /// </summary>
    public static class FileBrowserModel
    {
        public class Entry
        {
            public string Name;
            public string Path;
            public bool IsDir;
            /// <summary>File size in bytes; 0 for folders.</summary>
            public long Size;
        }

        public class Listing
        {
            public string Path;
            /// <summary>Parent folder, or null when Path is a root.</summary>
            public string Parent;
            public List<Entry> Entries = new List<Entry>();
            public string Error;
        }

        public static OsKind DetectOs()
        {
            if (Path.DirectorySeparatorChar == '\\') return OsKind.Windows;
            return Directory.Exists("/Volumes") && Directory.Exists("/Applications") ? OsKind.Mac : OsKind.Linux;
        }

        public static bool IsGguf(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
        }

        public static string Home()
        {
            try
            {
                string h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return !string.IsNullOrEmpty(h) && Directory.Exists(h) ? h : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The top-level places: drives on Windows; "/", home and mounted media elsewhere.</summary>
        public static List<Entry> Roots(OsKind? os = null)
        {
            var kind = os ?? DetectOs();
            var list = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Action<string, string> add = (name, p) =>
            {
                if (string.IsNullOrEmpty(p) || !seen.Add(p)) return;
                list.Add(new Entry { Name = name, Path = p, IsDir = true });
            };
            try
            {
                if (kind == OsKind.Windows)
                {
                    foreach (var d in Directory.GetLogicalDrives()) add(d, d);
                    return list;
                }
                add("/", "/");
                add("~ (" + (Home() ?? "") + ")", Home());
                if (kind == OsKind.Mac) AddChildren(add, "/Volumes", 1);
                else
                {
                    AddChildren(add, "/media", 2);
                    AddChildren(add, "/mnt", 1);
                    AddChildren(add, "/run/media", 2);
                }
            }
            catch (Exception) { }
            return list;
        }

        // Adds the folders `depth` levels under `dir` (Linux automounts sit at /media/<user>/<label>).
        static void AddChildren(Action<string, string> add, string dir, int depth)
        {
            if (depth <= 0) return;
            string[] subs;
            try { if (!Directory.Exists(dir)) return; subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs)
            {
                if (depth == 1) add(s, s);
                else
                {
                    string[] inner = null;
                    try { inner = Directory.GetDirectories(s); } catch (Exception) { }
                    if (inner == null || inner.Length == 0) { add(s, s); continue; }
                    Array.Sort(inner, StringComparer.OrdinalIgnoreCase);
                    foreach (var i in inner) { add(i, i); }
                }
            }
        }

        /// <summary>The folder above path, or null when path is a root (or empty).</summary>
        public static string Parent(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (string.Equals(full.TrimEnd('/', '\\'), (root ?? "").TrimEnd('/', '\\'), StringComparison.Ordinal) || full == root) return null;
                return Path.GetDirectoryName(full.TrimEnd('/', '\\'));
            }
            catch (Exception) { return null; }
        }

        /// <summary>Folders first, then .gguf files, each sorted case-insensitively. Never throws.</summary>
        public static Listing List(string dir, OsKind? os = null)
        {
            var kind = os ?? DetectOs();
            var l = new Listing { Path = dir, Parent = Parent(dir) };
            if (string.IsNullOrEmpty(dir)) { l.Error = "No folder."; return l; }
            try
            {
                if (!Directory.Exists(dir)) { l.Error = "Folder not found."; return l; }
                var dirs = new List<Entry>();
                var files = new List<Entry>();
                var info = new DirectoryInfo(dir);
                foreach (var d in info.EnumerateDirectories())
                {
                    if (kind != OsKind.Windows && d.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    dirs.Add(new Entry { Name = d.Name, Path = d.FullName, IsDir = true });
                }
                foreach (var f in info.EnumerateFiles())
                {
                    if (!IsGguf(f.Name)) continue;
                    long size = 0;
                    try { size = f.Length; } catch (Exception) { }
                    files.Add(new Entry { Name = f.Name, Path = f.FullName, Size = size });
                }
                Comparison<Entry> byName = (a, b) =>
                {
                    int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
                };
                dirs.Sort(byName);
                files.Sort(byName);
                l.Entries.AddRange(dirs);
                l.Entries.AddRange(files);
            }
            catch (Exception e)
            {
                l.Entries.Clear();
                l.Error = e.Message;
            }
            return l;
        }

        /// <summary>Where to open: the folder of lastPath if it exists, else home, else the first root.</summary>
        public static string StartDir(string lastPath, OsKind? os = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(lastPath))
                {
                    if (Directory.Exists(lastPath)) return lastPath;
                    string dir = Path.GetDirectoryName(lastPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch (Exception) { }
            string home = Home();
            if (home != null) return home;
            var roots = Roots(os);
            return roots.Count > 0 ? roots[0].Path : null;
        }
    }
}
