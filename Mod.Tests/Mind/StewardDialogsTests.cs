// Port of agent/tests/test_steward_dialogs.py (unattended play: the steward wakes on a `dialog` event even while the game is paused and
// answers with the choose tool). test_dialog_try_runs_one_cycle_on_samples runs the Python dialog_try/steward_try CLI and is not ported here:
// it belongs with the console harness (tools worker); see docs/csmind/parity-delta2.md.
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    [Collection("StewardEnv")]
    public class StewardDialogsTests
    {
        private static List<JObject> Sample(string name)
            => ((JArray)JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests.dialogs_" + name + ".json"))["dialogs"]).Cast<JObject>().ToList();

        private static void OpenDialog(World w, JObject dlg, bool paused = true)
        {
            w.Fb.Dialogs.Add((JObject)dlg.DeepClone());
            w.Fb.Paused = paused;
            WRig.Ev(w, "dialog", "Needs an answer: " + (string)dlg["title"], 0.6, new JObject
            {
                ["dialog"] = dlg["id"], ["kind"] = dlg["kind"], ["title"] = dlg["title"],
                ["options"] = new JArray(((JArray)dlg["options"]).Select(o => o["label"])), ["default"] = dlg["default"], ["timeout_s"] = 90,
            });
        }

        [Fact]
        public async Task DialogWakesWhileTheGameIsPaused()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await WRig.Boot(world, new object[] { WRig.Final("hello") });
            await WRig.Cycle(w);                                   // the opening heartbeat
            fake.Calls.Clear();
            fake.Script.Clear();
            fake.Script.AddRange(new object[] { WRig.Call("choose", new { dialog = "L4021", option = "Accept the refugee", why = "since we have room" }), WRig.Final("Welcome.") });
            w.Real += 30;
            OpenDialog(w, Sample("refugee")[0]);
            await WRig.Cycle(w);
            Assert.Equal(1, st.Stats.ByReason.GetValueOrDefault("dialog"));
            string user = WRig.User(fake.Calls[0]);
            Assert.True(user.Contains("OPEN DIALOGS") && user.Contains("L4021") && user.Contains("Accept the refugee"));
            Assert.Empty(w.Fb.Dialogs);                            // answered through POST /act/choose
            var last = w.Fb.ActLog[w.Fb.ActLog.Count - 1];
            Assert.Equal("choose", last.Key);
            Assert.Equal(0, (int)last.Value["option"]);
            Assert.Equal("since we have room", (string)last.Value["why"]);
        }

        [Fact]
        public async Task APausedGameWithoutADialogStillSleeps()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await WRig.Boot(world);
            await WRig.Cycle(w);
            w.Fb.Paused = true;
            w.Real += 60;
            await WRig.Cycle(w);
            Assert.Equal(1, st.Stats.Cycles);
        }

        [Fact]
        public async Task DialogWakeWaitsForTheRealFloorAndIsNotLost()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await WRig.Boot(world, new object[] { WRig.Final("ok") });
            await WRig.Cycle(w);
            OpenDialog(w, Sample("ransom")[0]);                    // no real time since the last cycle
            await WRig.Cycle(w);
            Assert.True(st.Stats.Cycles == 1 && st.DialogPending);
            w.Real += 30;
            await WRig.Cycle(w);
            Assert.True(st.Stats.ByReason.GetValueOrDefault("dialog") == 1 && !st.DialogPending);
        }

        [Fact]
        public async Task NotPlayingIgnoresDialogs()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await WRig.Boot(world, playing: false);
            OpenDialog(w, Sample("refugee")[0]);
            w.Real += 60;
            await WRig.Cycle(w);
            Assert.True(fake.Calls.Count == 0 && !st.DialogPending);
        }

        [Fact]
        public async Task ChooseToolShapes()
        {
            WRig.NoEnv();
            using var world = new World();
            var (w, st, fake) = await WRig.Boot(world);
            foreach (var f in new[] { "refugee", "naming" }) foreach (var d in Sample(f)) w.Fb.Dialogs.Add(d);
            var digest = await w.Bridge.DigestAsync();
            var s = new StewardSession(digest, w.Bridge, false);
            Assert.True(s.Tools.Any(t => t.Name == "choose") && s.Acting.Contains("choose"));

            async Task<JObject> Run(JObject v)
            {
                w.Fb.ActLog.Clear();
                var o = await s.RunAsync("choose", v);
                return o.Ok ? w.Fb.ActLog.Last().Value : o.Result;
            }
            string Err(JObject r) => r.ContainsKey("error") ? (string)r["error"] : null;

            // each success removes the dialog from the fake; reopen it for the next shape
            void Reopen() { w.Fb.Dialogs.Clear(); foreach (var f in new[] { "refugee", "naming" }) foreach (var d in Sample(f)) w.Fb.Dialogs.Add(d); }
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"dialog\":\"L4021\",\"option\":1}"), await Run(JObject.Parse("{\"dialog\":\"L4021\",\"option\":\"#1\"}"))));
            Reopen();
            Assert.Equal(1, (int)(await Run(JObject.Parse("{\"dialog\":\"L4021\",\"option\":\"reject\"}")))["option"]);
            Reopen();
            Assert.NotNull(Err(await Run(JObject.Parse("{\"dialog\":\"L4021\",\"option\":\"pay\"}"))));
            Assert.NotNull(Err(await Run(JObject.Parse("{\"dialog\":\"L4021\"}"))));
            var named = await Run(JObject.Parse("{\"dialog\":\"W3\",\"text\":\"The Lanterns | Ashford\"}"));
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"faction\":\"The Lanterns\",\"settlement\":\"Ashford\"}"), named["text"]));
            Reopen();
            Assert.NotNull(Err(await Run(JObject.Parse("{\"dialog\":\"W3\",\"text\":\"One name\"}"))));
            Assert.DoesNotContain(new StewardSession(digest, w.Bridge, true).Tools, t => t.Name == "choose");   // advisor mode never answers dialogs
            Assert.DoesNotContain(new StewardSession(new JObject(), w.Bridge, false).Tools, t => t.Name == "choose");
        }

        [Fact]
        public void DigestTextListsDialogs()
        {
            var d = new JObject { ["dialogs"] = new JArray(Sample("ransom").Concat(Sample("naming"))) };
            string text = StewardDigest.RenderDigest(d);
            Assert.True(text.Contains("OPEN DIALOGS") && text.Contains("L4107") && text.Contains("costs silver") && text.Contains("type a name") && text.Contains("max 40 chars"));
            string naming = text.Split('\n').First(l => l.Contains("(naming)"));
            Assert.True(!naming.Contains("default:") && naming.Contains("invent your own"));      // a shown fallback name got copied in the live check
            Assert.Contains("default:", text.Split('\n').First(l => l.Contains("L4107")));
        }
    }
}
