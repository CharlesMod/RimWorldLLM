using System.Threading.Tasks;
using HermesVoice.Bridge;
using Newtonsoft.Json;
using Xunit;

namespace HermesVoice.Tests
{
    /// <summary>The engine's fallback flag on plan and status, and the once-per-session notice logic.</summary>
    public class FallbackWarningTests
    {
        const string PlanJson = "{\"recommended\":\"e4b\",\"models\":[" +
            "{\"id\":\"e4b\",\"name\":\"E4B\",\"fits\":\"yes\",\"fallback\":false}," +
            "{\"id\":\"e2b\",\"name\":\"E2B\",\"fits\":\"yes\",\"fallback\":true,\"fallback_note\":\"Smallest model: expect repeated lines.\"}," +
            "{\"id\":\"old\",\"name\":\"Old engine reply\",\"fits\":\"no\"}]}";

        [Fact]
        public void PlanModelsReadFallbackAndNote()
        {
            var p = JsonConvert.DeserializeObject<EnginePlan>(PlanJson);
            Assert.False(p.Models[0].Fallback);
            Assert.True(p.Models[1].Fallback);
            Assert.Equal("Smallest model: expect repeated lines.", p.Models[1].FallbackNote);
            Assert.False(p.Models[2].Fallback);     // an engine that does not send the field means "not a fallback"
            Assert.Null(p.Models[2].FallbackNote);
        }

        [Fact]
        public void StatusReadsModelFallbackAndNote()
        {
            var s = JsonConvert.DeserializeObject<EngineStatus>("{\"phase\":\"ready\",\"model\":\"e2b\",\"model_fallback\":true,\"model_note\":\"Smallest model.\"}");
            Assert.True(s.ModelFallback);
            Assert.Equal("Smallest model.", s.ModelNote);
            var n = JsonConvert.DeserializeObject<EngineStatus>("{\"phase\":\"ready\",\"model\":\"e4b\",\"model_fallback\":false}");
            Assert.False(n.ModelFallback);
            Assert.Null(n.ModelNote);
            Assert.False(JsonConvert.DeserializeObject<EngineStatus>("{\"phase\":\"ready\"}").ModelFallback);
        }

        [Fact]
        public async Task ClientDeliversTheFlagsOverHttp()
        {
            using (var f = new FakeEngine((m, pq) =>
                pq == "/engine/v1/plan" ? (200, PlanJson) :
                pq == "/engine/v1/status" ? (200, "{\"phase\":\"ready\",\"model_fallback\":true,\"model_note\":\"n\"}") : (404, "{}")))
            {
                var plan = await f.Client.GetPlanAsync();
                Assert.True(plan.Ok);
                Assert.True(plan.Value.Models.Find(x => x.Id == "e2b").Fallback);
                var st = await f.Client.GetStatusAsync();
                Assert.True(st.Value.ModelFallback);
                Assert.Equal("n", st.Value.ModelNote);
            }
        }

        [Fact]
        public void WarnsOnceWhenFallbackIsReported()
        {
            var n = new FallbackNotice();
            Assert.False(n.Check(0, true, true, false));                 // not armed: no load yet
            n.Arm(1000);
            Assert.False(n.Check(1500, false, false, false));            // engine not answering yet
            Assert.False(n.Check(2000, true, false, false));             // a normal model
            Assert.True(n.Check(3000, true, true, false));               // fallback reported: warn
            Assert.False(n.Check(4000, true, true, false));              // never twice
            Assert.True(n.Warned);
        }

        [Fact]
        public void LoadingAnotherSaveDoesNotRepeatIt()
        {
            var n = new FallbackNotice();
            n.Arm(0);
            Assert.True(n.Check(10, true, true, false));
            n.Arm(100000);
            Assert.False(n.Armed);
            Assert.False(n.Check(100010, true, true, false));
        }

        [Fact]
        public void NoWarningWhenTheSettingIsOffOrTheGameEnds()
        {
            var n = new FallbackNotice();
            n.Arm(0);
            Assert.False(n.Check(10, true, true, true));                 // "don't warn again"
            Assert.False(n.Waiting(11, false));                          // and it stays off for this session
            Assert.False(n.Warned);

            var m = new FallbackNotice();
            m.Arm(0);
            m.Disarm();
            Assert.False(m.Check(10, true, true, false));
        }

        [Fact]
        public void GivesUpAfterTheTimeout()
        {
            var n = new FallbackNotice();
            n.Arm(0);
            Assert.True(n.Waiting(FallbackNotice.TimeoutMs, false));
            Assert.False(n.Check(FallbackNotice.TimeoutMs + 1, true, true, false));
            Assert.False(n.Waiting(FallbackNotice.TimeoutMs + 2, false));
        }
    }
}
