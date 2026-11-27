using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind;
using Xunit;

namespace RimWorldLLM.Tests
{
    sealed class TempDir : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "rwl-mindhost-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Dir); }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }   // only what this test made
    }

    public class MindPlanTests
    {
        static MindInputs Ready(string dir = "/d")
        {
            return new MindInputs { ModEnabled = true, GameLoaded = true, EngineBase = "http://127.0.0.1:47480/v1", EngineModel = "m1", EngineReady = true, DataDir = dir };
        }

        [Fact]
        public void WantsNeedsModGameAndNoHoldAndAnEndpoint()
        {
            Assert.True(MindPlan.Wants(Ready()));
            var i = Ready(); i.ModEnabled = false; Assert.False(MindPlan.Wants(i));
            i = Ready(); i.GameLoaded = false; Assert.False(MindPlan.Wants(i));
            i = Ready(); i.Held = true; Assert.False(MindPlan.Wants(i));
            i = Ready(); i.Own = true; Assert.False(MindPlan.Wants(i));            // own endpoint not filled in yet
            i.OwnBaseUrl = "https://x/v1"; Assert.True(MindPlan.Wants(i));
        }

        [Fact]
        public void KeyChangesWithEndpointAndFileButNotWithTheLocalModelName()
        {
            string k = MindPlan.Key(Ready());
            var i = Ready(); i.EngineModel = "other"; i.EngineReady = false; Assert.Equal(k, MindPlan.Key(i));
            i = Ready(); i.EngineBase = "http://127.0.0.1:47481/v1"; Assert.NotEqual(k, MindPlan.Key(i));
            i = Ready(); i.FileStamp = "x|1|2"; Assert.NotEqual(k, MindPlan.Key(i));
            i = Ready(); i.Wants = false; Assert.NotEqual(k, MindPlan.Key(i));
            var o = Ready(); o.Own = true; o.OwnBaseUrl = "https://x/v1"; o.OwnModel = "a";
            string ko = MindPlan.Key(o);
            o.OwnModel = "b"; Assert.NotEqual(ko, MindPlan.Key(o));
            o.OwnModel = "a"; o.OwnApiKey = "sk"; Assert.NotEqual(ko, MindPlan.Key(o));
        }

        [Fact]
        public void LocalModeUsesTheEnginesV1AndAGenerousTimeout()
        {
            var d = MindPlan.BuildData(Ready(), null);
            Assert.Equal("http://127.0.0.1:47480/v1", (string)d["llm"]["base_url"]);
            Assert.Equal("m1", (string)d["llm"]["model"]);
            Assert.Equal("", (string)d["llm"]["api_key"]);
            Assert.Equal(MindPlan.LocalTimeoutS, (double)d["llm"]["timeout_s"]);
            Assert.Equal("/d", (string)d["data_dir"]);
            Assert.True(MindPlan.LocalTimeoutS >= 600);
        }

        [Fact]
        public void OwnModeUsesThePlayersEndpointTrimmed()
        {
            var i = Ready(); i.Own = true; i.OwnBaseUrl = " https://api.example/v1 "; i.OwnModel = " gpt "; i.OwnApiKey = " sk-1 ";
            var c = MindPlan.BuildConfig(i);
            Assert.Equal("https://api.example/v1", c.Llm.BaseUrl);
            Assert.Equal("gpt", c.Llm.Model);
            Assert.Equal("sk-1", c.Llm.ApiKey);
            Assert.Equal(MindPlan.OwnTimeoutS, c.Llm.TimeoutS);
            Assert.Equal("/d", c.DataDir);
        }

        [Fact]
        public void TheFileFillsTheRestButCannotTakeTheEndpointOrDataDir()
        {
            var file = JObject.Parse("{\"voice_lines_per_min\": 9, \"data_dir\": \"/elsewhere\", \"bridge_url\": \"http://x\", \"wants\": false, \"llm\": {\"base_url\": \"http://evil\", \"temperature\": 0.7, \"timeout_s\": 5}}");
            var c = MindConfig.FromDict(MindPlan.BuildData(Ready(), file));
            Assert.Equal(9.0, c.VoiceLinesPerMin);
            Assert.Equal(0.7, c.Llm.Temperature);
            Assert.Equal(5.0, c.Llm.TimeoutS);                      // the file may change the timeout
            Assert.Equal("http://127.0.0.1:47480/v1", c.Llm.BaseUrl);
            Assert.Equal("/d", c.DataDir);
            Assert.True(c.Wants);                                   // the mod's own setting wins
            Assert.Null(c.BridgeUrl);
        }

        [Fact]
        public void MirroredModSettingsReachTheConfig()
        {
            var i = Ready(); i.PauseAmbientAtSpeed = 2; i.Wants = false; i.Dreams = false; i.ArtDescriptions = false; i.Recap = false; i.TextFill = false;
            var c = MindPlan.BuildConfig(i);
            Assert.Equal(2, c.PauseAmbientAtSpeed);
            Assert.False(c.Wants); Assert.False(c.Dreams); Assert.False(c.ArtEnabled); Assert.False(c.RecapEnabled); Assert.False(c.TextFill);
        }

        [Fact]
        public void ConfigFileTomlBeatsJsonAndIsReadFromTheDataDir()
        {
            using (var t = new TempDir())
            {
                Assert.Null(MindPlan.ReadConfigFile(t.Dir));
                Assert.Equal("", MindPlan.Stamp(t.Dir));
                File.WriteAllText(Path.Combine(t.Dir, "config.json"), "{\"windows\": 3}");
                Assert.Equal(3, (int)MindPlan.ReadConfigFile(t.Dir)["windows"]);
                File.WriteAllText(Path.Combine(t.Dir, "config.toml"), "windows = 5\n[llm]\ntemperature = 0.5\n");
                var i = Ready(t.Dir);
                var c = MindPlan.BuildConfig(i);
                Assert.Equal(5, c.Windows);
                Assert.Equal(0.5, c.Llm.Temperature);
                Assert.Contains("config.toml", MindPlan.Stamp(t.Dir));
            }
        }

        [Fact]
        public void ABrokenFileIsAConfigErrorNotACrash()
        {
            using (var t = new TempDir())
            {
                File.WriteAllText(Path.Combine(t.Dir, "config.json"), "[1,2]");
                Assert.Throws<ConfigError>(() => MindPlan.ReadConfigFile(t.Dir));
                File.WriteAllText(Path.Combine(t.Dir, "config.json"), "{\"windows\": \"many\"}");
                Assert.Throws<ConfigError>(() => MindPlan.BuildConfig(Ready(t.Dir)));
                File.Delete(Path.Combine(t.Dir, "config.json"));
                File.WriteAllText(Path.Combine(t.Dir, "config.toml"), "windows = = 3");
                Assert.Throws<ConfigError>(() => MindPlan.ReadConfigFile(t.Dir));
            }
        }

        [Fact]
        public void StatusStates()
        {
            var i = Ready();
            Assert.Equal(MindState.Running, MindPlan.State(i, true, null, 1e9));
            i.EngineReady = false; Assert.Equal(MindState.WaitingForModel, MindPlan.State(i, true, null, 0));
            i = Ready(); i.GameLoaded = false; Assert.Equal(MindState.WaitingForGame, MindPlan.State(i, false, null, 0));
            i = Ready(); i.ModEnabled = false; Assert.Equal(MindState.Disabled, MindPlan.State(i, false, null, 0));
            i = Ready(); Assert.Equal(MindState.Error, MindPlan.State(i, true, "boom", 10));
            Assert.Equal(MindState.Running, MindPlan.State(i, true, "boom", MindPlan.ErrorShownS + 1));   // an old error is not shown
            i.Own = true; Assert.Equal(MindState.WaitingForModel, MindPlan.State(i, false, null, 0));     // no endpoint yet
            i.OwnBaseUrl = "https://x/v1"; i.EngineReady = false; Assert.Equal(MindState.Running, MindPlan.State(i, true, null, 0));   // own mode needs no engine
        }

        [Theory]
        [InlineData(MindState.Disabled, "RWL_Mind_Disabled")]
        [InlineData(MindState.WaitingForGame, "RWL_Mind_WaitingGame")]
        [InlineData(MindState.WaitingForModel, "RWL_Mind_WaitingModel")]
        [InlineData(MindState.Running, "RWL_Mind_Running")]
        [InlineData(MindState.Error, "RWL_Mind_Error")]
        public void StateKeysAreKeyed(MindState s, string key)
        {
            Assert.Equal(key, MindPlan.StateKey(s));
        }
    }

    public class MindSupervisorTests
    {
        sealed class FakeProc : IMindProc
        {
            public bool Running { get; set; }
            public string LastError { get; set; }
            public int Restarts { get; set; }
            public int Starts, Stops;
            public TaskCompletionSource<bool> Gate;
            public void Start() { Starts++; Running = true; }
            public async Task StopAsync() { if (Gate != null) await Gate.Task; Stops++; Running = false; }
        }

        sealed class Rig
        {
            public double Now;
            public readonly List<FakeProc> Procs = new List<FakeProc>();
            public readonly List<string> Logs = new List<string>();
            public Func<MindInputs, FakeProc> Make = i => new FakeProc();
            public MindSupervisor Sup;
            public Rig() { Sup = new MindSupervisor(i => { var p = Make(i); Procs.Add(p); return p; }, () => Now, Logs.Add); }
            public static MindInputs In() { return new MindInputs { GameLoaded = true, EngineBase = "http://e/v1", EngineReady = true, DataDir = "/d" }; }
            public void Settle() { for (int n = 0; n < 200; n++) { System.Threading.Thread.Sleep(5); Sup.Tick(Last); if (Sup.Starts >= 0 && !PendingStop()) break; } }
            public MindInputs Last = In();
            bool PendingStop() { foreach (var p in Procs) if (p.Gate != null && !p.Gate.Task.IsCompleted) return false; return false; }
        }

        static void WaitFor(Func<bool> c) { for (int n = 0; n < 400 && !c(); n++) System.Threading.Thread.Sleep(5); Assert.True(c()); }

        [Fact]
        public void NothingStartsWithoutAGame()
        {
            var r = new Rig(); var i = Rig.In(); i.GameLoaded = false;
            r.Sup.Tick(i);
            Assert.Empty(r.Procs); Assert.False(r.Sup.IsRunning);
        }

        [Fact]
        public void StartsOnceWhenAGameIsLoadedAndStopsWhenItEnds()
        {
            var r = new Rig(); var i = Rig.In();
            r.Sup.Tick(i); r.Sup.Tick(i); r.Sup.Tick(i);
            Assert.Single(r.Procs); Assert.Equal(1, r.Procs[0].Starts); Assert.True(r.Sup.IsRunning);
            i.GameLoaded = false;
            r.Sup.Tick(i);
            Assert.False(r.Sup.IsRunning);
            WaitFor(() => r.Procs[0].Stops == 1);
        }

        [Fact]
        public void StopNowStopsWithoutWaiting()
        {
            var r = new Rig(); r.Sup.Tick(Rig.In());
            r.Sup.StopNow();
            Assert.False(r.Sup.IsRunning);
            WaitFor(() => r.Procs[0].Stops == 1);
        }

        [Fact]
        public void ModDisabledStopsAndReEnablingStartsAgain()
        {
            var r = new Rig(); var i = Rig.In();
            r.Sup.Tick(i);
            i.ModEnabled = false; r.Sup.Tick(i);
            WaitFor(() => r.Procs[0].Stops == 1);
            i.ModEnabled = true;
            WaitFor(() => { r.Sup.Tick(i); return r.Procs.Count == 2; });
        }

        [Fact]
        public void ChangedEndpointRestartsAfterTheOldOneStopped()
        {
            var r = new Rig(); var i = Rig.In();
            var gate = new TaskCompletionSource<bool>();
            r.Make = x => new FakeProc { Gate = r.Procs.Count == 0 ? gate : null };
            r.Sup.Tick(i);
            i.EngineBase = "http://e:2/v1";
            r.Sup.Tick(i);                         // old one is stopping (held up)
            r.Sup.Tick(i);
            Assert.Single(r.Procs);                // no second start before the first has stopped
            gate.SetResult(true);
            WaitFor(() => { r.Sup.Tick(i); return r.Procs.Count == 2; });
            Assert.Equal(1, r.Procs[0].Stops);
        }

        [Fact]
        public void AFailingStartIsLoggedOnceAndRetriedLater()
        {
            var r = new Rig(); var i = Rig.In();
            r.Make = x => throw new ConfigError("bad file");
            r.Sup.Tick(i); r.Sup.Tick(i);
            Assert.Single(r.Logs);
            Assert.Contains("bad file", r.Sup.LastError);
            Assert.False(r.Sup.IsRunning);
            r.Now += MindSupervisor.RetryAfterFailureS + 1;
            r.Make = x => new FakeProc();
            r.Sup.Tick(i);
            Assert.True(r.Sup.IsRunning);
            Assert.Null(r.Sup.LastError);
        }

        [Fact]
        public void ARunnerErrorIsReportedAndLoggedOnce()
        {
            var r = new Rig(); var i = Rig.In();
            r.Sup.Tick(i);
            var p = r.Procs[0];
            p.LastError = "HttpRequestException: down"; p.Restarts = 1;
            r.Sup.Tick(i); r.Sup.Tick(i);
            Assert.Equal("HttpRequestException: down", r.Sup.LastError);
            Assert.Single(r.Logs);
        }

        [Fact]
        public async Task HoldStopsAndKeepsItOffUntilReleased()
        {
            var r = new Rig(); var i = Rig.In();
            r.Sup.Tick(i);
            await r.Sup.HoldAsync();
            Assert.Equal(1, r.Procs[0].Stops);       // stopped before HoldAsync returned: the folder may be deleted now
            for (int n = 0; n < 3; n++) r.Sup.Tick(i);
            Assert.Single(r.Procs);
            r.Sup.Release();
            r.Sup.Tick(i);
            Assert.Equal(2, r.Procs.Count);
        }

        [Fact]
        public void TickNeverThrows()
        {
            var r = new Rig(); r.Make = x => throw new InvalidOperationException("x");
            r.Sup.Tick(Rig.In());
            r.Sup.Tick(null);
        }
    }

    public class InProcessChatLiveTests
    {
        static (InProcessChatLive, ChatLive, List<int>) Make()
        {
            var live = new ChatLive(); var cycles = new List<int>();
            return (new InProcessChatLive(live, () => cycles.Add(1)), live, cycles);
        }

        [Fact]
        public void NarratorLineThinksThenTypesThenTheFinalLineEndsIt()
        {
            var (c, live, cycles) = Make();
            string id = c.Begin("narrator", null, null);
            Assert.Single(cycles);
            Assert.True(live.IsLive(id));
            c.Progress(id, 0.5);
            var s = live.Snapshot(); Assert.Equal("narrator", s[0].Who); Assert.Equal(0.5f, s[0].Progress);
            c.Typing(id); c.Partial(id, "Hello wor");
            Assert.True(live.Snapshot()[0].Typing);
            c.End(id, "Hello world.");            // the line went to /chat with this stream id: nothing more is logged here
            Assert.False(live.HasFinals);
        }

        [Fact]
        public void CancelAndEndWithoutTextDropTheLine()
        {
            var (c, live, _) = Make();
            string a = c.Begin("narrator", null, null); c.Cancel(a);
            Assert.False(live.IsLive(a)); Assert.Equal(0, live.Count);
            string b = c.Begin("colonist", 7, "Ana"); Assert.True(live.IsLive(b));
            Assert.Equal(7, live.Snapshot()[0].To); Assert.Equal("Ana", live.Snapshot()[0].Name);
            c.End(b, null);
            Assert.Equal(0, live.Count); Assert.False(live.HasFinals);
        }

        [Fact]
        public void ColonistCyclesDoNotResetTheBuildCaps()
        {
            var (c, _, cycles) = Make();
            c.Begin("colonist", 3, "Bo");
            Assert.Empty(cycles);
        }

        [Fact]
        public void NegativeProgressRefreshesAndNeverThrows()
        {
            var (c, live, _) = Make();
            string id = c.Begin("narrator", null, null);
            c.Progress(id, -1); c.Progress("nope", 0.3); c.Typing("nope"); c.Partial("nope", "x"); c.End("nope", null); c.Cancel("nope");
            Assert.True(live.IsLive(id));
            string bad = c.Begin("colonist", null, "x");        // a colonist line needs a pawn id: no live line, no throw
            c.Progress(bad, 0.2); c.Typing(bad); c.End(bad, "t");
            Assert.True(live.IsLive(id));
        }

        [Fact]
        public void ARunsAnIdThatIsValidForThePostedChatLine()
        {
            var (c, _, _) = Make();
            Assert.True(ChatLive.ValidId(c.Begin("narrator", null, null)));
        }
    }
}
