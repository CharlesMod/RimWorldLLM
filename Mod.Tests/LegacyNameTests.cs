using System;
using System.IO;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>One-time carry-over from the mod's old name, HermesVoice.</summary>
    public class LegacyNameTests
    {
        sealed class Tmp : IDisposable
        {
            public readonly string Dir = Path.Combine(Path.GetTempPath(), "rwl-legacy-" + Guid.NewGuid().ToString("N"));
            public Tmp() { Directory.CreateDirectory(Dir); }
            public string P(params string[] p) { return Path.Combine(Dir, Path.Combine(p)); }
            public string Sub(params string[] p) { var d = P(p); Directory.CreateDirectory(d); return d; }
            public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
        }

        const string OldXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock>\n  <ModSettings Class=\"HermesVoice.HermesVoiceSettings\">\n    <bridgePort>8765</bridgePort>\n  </ModSettings>\n</SettingsBlock>";

        [Fact]
        public void CopiesOldSettingsWithTheClassRenamedAndKeepsTheOld()
        {
            using (var t = new Tmp())
            {
                string old = t.P("Mod_HermesVoice_HermesVoiceMod.xml");
                File.WriteAllText(old, OldXml);
                string nu = t.P("Mod_RimWorldLLM_RimWorldLLMMod.xml");
                Assert.Equal(LegacySettings.Outcome.Copied, LegacySettings.Migrate(nu, out string found));
                Assert.Equal(old, found);
                string text = File.ReadAllText(nu);
                Assert.Contains("Class=\"RimWorldLLM.RimWorldLLMSettings\"", text);
                Assert.Contains("<bridgePort>8765</bridgePort>", text);
                Assert.DoesNotContain("HermesVoice", text);
                Assert.Equal(OldXml, File.ReadAllText(old));
                Assert.False(File.Exists(nu + ".tmp"));
            }
        }

        [Fact]
        public void NeverOverwritesExistingNewSettings()
        {
            using (var t = new Tmp())
            {
                File.WriteAllText(t.P("Mod_HermesVoice_HermesVoiceMod.xml"), OldXml);
                string nu = t.P("Mod_RimWorldLLM_RimWorldLLMMod.xml");
                File.WriteAllText(nu, "mine");
                Assert.Equal(LegacySettings.Outcome.NewExists, LegacySettings.Migrate(nu, out _));
                Assert.Equal("mine", File.ReadAllText(nu));
            }
        }

        [Fact]
        public void NoOldFileDoesNothing()
        {
            using (var t = new Tmp())
            {
                File.WriteAllText(t.P("Mod_Other_OtherMod.xml"), "x");
                string nu = t.P("Mod_RimWorldLLM_RimWorldLLMMod.xml");
                Assert.Equal(LegacySettings.Outcome.NoOldFile, LegacySettings.Migrate(nu, out _));
                Assert.False(File.Exists(nu));
            }
        }

        [Fact]
        public void FindsOldFileUnderAnyFolderNamePreferringHermesVoice()
        {
            using (var t = new Tmp())
            {
                File.WriteAllText(t.P("Mod_3551200000_HermesVoiceMod.xml"), "workshop");
                Assert.Equal(t.P("Mod_3551200000_HermesVoiceMod.xml"), LegacySettings.FindOld(t.Dir));
                File.WriteAllText(t.P("Mod_HermesVoice_HermesVoiceMod.xml"), "local");
                Assert.Equal(t.P("Mod_HermesVoice_HermesVoiceMod.xml"), LegacySettings.FindOld(t.Dir));
                Assert.Null(LegacySettings.FindOld(t.P("missing")));
            }
        }

        [Fact]
        public void LegacyAgentFolderIsTheDefaultsSiblingOnly()
        {
            var res = new AgentDataDir.Resolved { Path = Path.Combine("/h", ".local", "share", "rimworldllm-agent") };
            Assert.Equal(Path.Combine("/h", ".local", "share", "hermesvoice-agent"), AgentDataDir.Legacy(res).Path);
            Assert.Null(AgentDataDir.Legacy(new AgentDataDir.Resolved { Path = "/x/mine", Overridden = true }));
            Assert.Null(AgentDataDir.Legacy(null));
        }

        [Fact]
        public void LegacyAgentFolderMayBeDeletedButNotUnderAnOverride()
        {
            using (var t = new Tmp())
            {
                string home = t.Sub("home");
                string share = t.Sub("home", ".local", "share");
                string nu = t.Sub("home", ".local", "share", "rimworldllm-agent");
                string old = t.Sub("home", ".local", "share", "hermesvoice-agent");
                File.WriteAllText(Path.Combine(old, "a.db"), "12345");
                var legacy = AgentDataDir.Legacy(new AgentDataDir.Resolved { Path = nu });
                var r = AgentDataDir.Delete(legacy, old, new[] { home, share });
                Assert.Equal(AgentDataDir.DeleteOutcome.Deleted, r.Outcome);
                Assert.Equal(5, r.FreedBytes);
                Assert.False(Directory.Exists(old));
                Assert.True(Directory.Exists(nu));
                // an override named with the old leaf is the player's own folder: refused
                string mine = t.Sub("mine", "hermesvoice-agent"); File.WriteAllText(Path.Combine(mine, "k"), "k");
                var over = new AgentDataDir.Resolved { Path = mine, Overridden = true };
                Assert.Equal(AgentDataDir.DeleteOutcome.Refused, AgentDataDir.Delete(over, mine, new[] { home }).Outcome);
                Assert.True(File.Exists(Path.Combine(mine, "k")));
            }
        }
    }
}
