namespace HermesVoice.Bridge
{
    /// <summary>
    /// The one-click first run: "Set up" in the first-run prompt installs the engine's recommended model at once,
    /// instead of opening the setup window and waiting for a second click on Install (and, for the fallback model,
    /// a third on a confirmation). Pure, so it is tested without the game.
    /// </summary>
    public static class SetupChoice
    {
        /// <summary>The model to install now, or null: nothing recommended, it is already installed or cannot be
        /// downloaded or does not fit, a job is running, or a model is already being served.</summary>
        public static string AutoInstallTarget(EnginePlan plan, EngineStatus st)
        {
            if (plan == null || plan.Models == null || string.IsNullOrEmpty(plan.Recommended)) return null;
            if (st != null && (EnginePhase.IsBusy(st.Phase) || st.Phase == EnginePhase.Ready || st.Phase == EnginePhase.External)) return null;
            var rec = plan.Models.Find(m => m.Id == plan.Recommended);
            if (rec == null || rec.Installed || rec.ImportOnly || rec.Fits == "no") return null;
            return rec.Id;
        }
    }
}
