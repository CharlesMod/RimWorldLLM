using System;
using System.IO;
using System.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class FileBrowserModelTests : IDisposable
    {
        readonly string _root;

        public FileBrowserModelTests()
        {
            _root = Directory.CreateTempSubdirectory("rwl-fb-").FullName;
        }

        // Removes only the unique directory this test instance created.
        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        string Dir(string name) { return Directory.CreateDirectory(Path.Combine(_root, name)).FullName; }
        string File_(string rel, int bytes = 0)
        {
            string p = Path.Combine(_root, rel);
            File.WriteAllBytes(p, new byte[bytes]);
            return p;
        }

        [Fact]
        public void IsGgufIsCaseInsensitive()
        {
            Assert.True(FileBrowserModel.IsGguf("a/b/model.gguf"));
            Assert.True(FileBrowserModel.IsGguf("MODEL.GGUF"));
            Assert.False(FileBrowserModel.IsGguf("model.gguf.part"));
            Assert.False(FileBrowserModel.IsGguf("model.bin"));
            Assert.False(FileBrowserModel.IsGguf(null));
            Assert.False(FileBrowserModel.IsGguf(""));
        }

        [Fact]
        public void ListFiltersToFoldersAndGgufOnly()
        {
            Dir("sub");
            File_("a.gguf", 10);
            File_("b.txt");
            File_("c.GGUF", 20);
            File_("d.gguf.part");
            var l = FileBrowserModel.List(_root, OsKind.Linux);
            Assert.Null(l.Error);
            Assert.Equal(new[] { "sub", "a.gguf", "c.GGUF" }, l.Entries.Select(e => e.Name).ToArray());
        }

        [Fact]
        public void ListOrdersFoldersFirstThenFilesCaseInsensitively()
        {
            Dir("zeta"); Dir("Alpha"); Dir("beta");
            File_("Zed.gguf"); File_("apple.gguf"); File_("Banana.gguf");
            var l = FileBrowserModel.List(_root, OsKind.Linux);
            Assert.Equal(new[] { "Alpha", "beta", "zeta", "apple.gguf", "Banana.gguf", "Zed.gguf" }, l.Entries.Select(e => e.Name).ToArray());
            Assert.All(l.Entries.Take(3), e => Assert.True(e.IsDir));
            Assert.All(l.Entries.Skip(3), e => Assert.False(e.IsDir));
        }

        [Fact]
        public void FileEntriesCarrySizeAndFullPath()
        {
            string p = File_("m.gguf", 1234);
            var e = FileBrowserModel.List(_root, OsKind.Linux).Entries.Single();
            Assert.Equal(1234, e.Size);
            Assert.Equal(p, e.Path);
        }

        [Fact]
        public void DotFoldersHiddenOnLinuxAndMacButNotWindows()
        {
            Dir(".hidden"); Dir("shown");
            foreach (var os in new[] { OsKind.Linux, OsKind.Mac })
                Assert.Equal(new[] { "shown" }, FileBrowserModel.List(_root, os).Entries.Select(e => e.Name).ToArray());
            Assert.Equal(2, FileBrowserModel.List(_root, OsKind.Windows).Entries.Count);
        }

        [Fact]
        public void MissingDirDoesNotThrowAndSetsError()
        {
            var l = FileBrowserModel.List(Path.Combine(_root, "nope"), OsKind.Linux);
            Assert.NotNull(l.Error);
            Assert.Empty(l.Entries);
        }

        [Fact]
        public void NullOrEmptyDirDoesNotThrow()
        {
            Assert.NotNull(FileBrowserModel.List(null).Error);
            Assert.NotNull(FileBrowserModel.List("").Error);
        }

        [Fact]
        public void FileInsteadOfDirIsAnError()
        {
            string f = File_("x.gguf");
            var l = FileBrowserModel.List(f, OsKind.Linux);
            Assert.NotNull(l.Error);
            Assert.Empty(l.Entries);
        }

        [Fact]
        public void UnreadableDirGivesErrorNotException()
        {
            if (OperatingSystem.IsWindows()) return;
            string d = Dir("locked");
            File.WriteAllBytes(Path.Combine(d, "a.gguf"), new byte[1]);
            File.SetUnixFileMode(d, UnixFileMode.None);
            try
            {
                var probe = false;
                try { Directory.GetFileSystemEntries(d); probe = true; } catch (Exception) { }
                if (probe) return; // running as root or permissions not enforced
                var l = FileBrowserModel.List(d, OsKind.Linux);
                Assert.NotNull(l.Error);
                Assert.Empty(l.Entries);
            }
            finally
            {
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [Fact]
        public void ParentNavigatesUpAndIsNullAtRoot()
        {
            string sub = Dir("a");
            string deep = Directory.CreateDirectory(Path.Combine(sub, "b")).FullName;
            Assert.Equal(sub, FileBrowserModel.Parent(deep));
            Assert.Equal(_root, FileBrowserModel.Parent(sub));
            Assert.Equal(sub, FileBrowserModel.Parent(deep + Path.DirectorySeparatorChar));
            string root = Path.GetPathRoot(_root);
            Assert.Null(FileBrowserModel.Parent(root));
            Assert.Null(FileBrowserModel.Parent(null));
            Assert.Null(FileBrowserModel.Parent(""));
        }

        [Fact]
        public void ListReportsParent()
        {
            string sub = Dir("a");
            Assert.Equal(_root, FileBrowserModel.List(sub, OsKind.Linux).Parent);
            Assert.Null(FileBrowserModel.List(Path.GetPathRoot(_root), OsKind.Linux).Parent);
        }

        [Fact]
        public void StartDirUsesFolderOfLastPath()
        {
            string sub = Dir("models");
            string f = Path.Combine(sub, "gone.gguf"); // the file itself need not exist
            Assert.Equal(sub, FileBrowserModel.StartDir(f));
        }

        [Fact]
        public void StartDirFallsBackToHomeThenFirstRoot()
        {
            string missing = Path.Combine(_root, "no", "such", "x.gguf");
            string home = FileBrowserModel.Home();
            string expect = home ?? FileBrowserModel.Roots().First().Path;
            Assert.Equal(expect, FileBrowserModel.StartDir(missing));
            Assert.Equal(expect, FileBrowserModel.StartDir(null));
            Assert.Equal(expect, FileBrowserModel.StartDir("  "));
        }

        [Fact]
        public void RootsAreNonEmptyExistingDirs()
        {
            var r = FileBrowserModel.Roots();
            Assert.NotEmpty(r);
            Assert.All(r, e => Assert.True(Directory.Exists(e.Path)));
            Assert.Equal(r.Count, r.Select(e => e.Path).Distinct().Count());
            if (!OperatingSystem.IsWindows()) Assert.Equal("/", r[0].Path);
        }
    }
}
