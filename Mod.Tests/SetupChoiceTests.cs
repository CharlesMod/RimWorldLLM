using System.Collections.Generic;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class SetupChoiceTests
    {
        static EnginePlan Plan(string rec, params EnginePlanModel[] ms) { return new EnginePlan { Recommended = rec, Models = new List<EnginePlanModel>(ms) }; }
        static EnginePlanModel M(string id, string fits = "yes", bool installed = false, bool fallback = false, bool importOnly = false)
        { return new EnginePlanModel { Id = id, Fits = fits, Installed = installed, Fallback = fallback, ImportOnly = importOnly }; }
        static EngineStatus St(string phase) { return new EngineStatus { Phase = phase }; }

        [Fact]
        public void InstallsTheRecommendedModel()
        {
            Assert.Equal("e4b", SetupChoice.AutoInstallTarget(Plan("e4b", M("26b", "no"), M("e4b")), St(EnginePhase.NotInstalled)));
        }

        [Fact]
        public void FallbackRecommendationNeedsNoSecondClick()
        {
            // a 4 GB card: the engine recommends the fallback; "Set up" already accepted it
            Assert.Equal("e2b", SetupChoice.AutoInstallTarget(Plan("e2b", M("e4b", "no"), M("e2b", fallback: true)), St(EnginePhase.NotInstalled)));
        }

        [Fact]
        public void TightFitStillInstalls()
        {
            Assert.Equal("e4b", SetupChoice.AutoInstallTarget(Plan("e4b", M("e4b", "tight")), St(EnginePhase.Error)));
        }

        [Fact]
        public void NothingWhenThereIsNothingToDo()
        {
            Assert.Null(SetupChoice.AutoInstallTarget(null, St(EnginePhase.NotInstalled)));
            Assert.Null(SetupChoice.AutoInstallTarget(Plan(null, M("e4b")), St(EnginePhase.NotInstalled)));
            Assert.Null(SetupChoice.AutoInstallTarget(Plan("x", M("e4b")), St(EnginePhase.NotInstalled)));
            Assert.Null(SetupChoice.AutoInstallTarget(Plan("e4b", M("e4b", installed: true)), St(EnginePhase.NotInstalled)));
            Assert.Null(SetupChoice.AutoInstallTarget(Plan("e4b", M("e4b", importOnly: true)), St(EnginePhase.NotInstalled)));
            Assert.Null(SetupChoice.AutoInstallTarget(Plan("e4b", M("e4b", "no")), St(EnginePhase.NotInstalled)));
        }

        [Fact]
        public void NothingWhileBusyOrServing()
        {
            var p = Plan("e4b", M("e4b"));
            foreach (var ph in new[] { EnginePhase.Downloading, EnginePhase.Installing, EnginePhase.Starting, EnginePhase.Loading, EnginePhase.Ready, EnginePhase.External })
                Assert.Null(SetupChoice.AutoInstallTarget(p, St(ph)));
            Assert.Equal("e4b", SetupChoice.AutoInstallTarget(p, null));
        }
    }
}
