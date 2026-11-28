using System;

namespace RimWorldLLM.Bridge
{
    /// <summary>What the main-menu download panel shows. Pure data: the UI turns <see cref="LabelKey"/> into text.</summary>
    public sealed class DownloadBarView
    {
        public enum Kind { Hidden, Bar, Plain, Error, Ready }

        public Kind Mode;
        /// <summary>Keyed string for the label line (RWL_Bar_*), or null when hidden.</summary>
        public string LabelKey;
        /// <summary>Argument {0} of the label: a model name, or the megabytes done for the unknown-total line.</summary>
        public string LabelArg;
        /// <summary>Whole percent 0..100 for a Bar, else -1. Never 100 until the download is done.</summary>
        public int Percent = -1;
        /// <summary>Plain text for an Error line (the engine's message, short), else null.</summary>
        public string Text;

        public bool Visible { get { return Mode != Kind.Hidden; } }
        public static readonly DownloadBarView Hidden = new DownloadBarView { Mode = Kind.Hidden };
    }

    /// <summary>Decides what the main-menu panel shows from engine status samples, and how often to sample. Game-agnostic.</summary>
    public sealed class DownloadBar
    {
        public const long LargeBytes = 50L * 1000 * 1000;
        public const long ActivePollMs = 1000, IdlePollMs = 2000, ReadyShowMs = 4000, ErrorShowMs = 8000;
        const int MaxTextLen = 90;

        bool _wasActive;
        long _readyUntil = long.MinValue, _errorUntil = long.MinValue;
        string _errorText;
        long _lastSampleMs = long.MinValue / 2;
        DownloadBarView _view = DownloadBarView.Hidden;

        public DownloadBarView View { get { return _view; } }
        /// <summary>True while a download is on screen (the sampler runs at 1 Hz).</summary>
        public bool Active { get { return _view.Mode == DownloadBarView.Kind.Bar || _view.Mode == DownloadBarView.Kind.Plain; } }

        /// <summary>The 1 Hz sampler: true at most once per second while a download is active (or once per 2 s idle).
        /// Call every frame; when it returns true, take one status sample off the main thread.</summary>
        public bool ShouldSample(long nowMs)
        {
            long every = Active ? ActivePollMs : IdlePollMs;
            if (nowMs - _lastSampleMs < every) return false;
            _lastSampleMs = nowMs;
            return true;
        }

        /// <summary>Whole percent of a byte count, floored, 0..99 (100 is shown only by the Ready line); -1 when total is unknown.</summary>
        public static int Percent(long done, long total)
        {
            if (total <= 0) return -1;
            if (done <= 0) return 0;
            long p = (long)((decimal)done * 100m / total);
            return (int)Math.Max(0, Math.Min(99, p));
        }

        /// <summary>Feed one status sample (null = no engine / unreachable). Returns the view to draw until the next sample.</summary>
        public DownloadBarView Update(EngineStatus st, long nowMs)
        {
            _view = Compute(st, nowMs);
            return _view;
        }

        DownloadBarView Compute(EngineStatus st, long nowMs)
        {
            if (st == null)
            {
                _wasActive = false;
                return Ready(nowMs) ? ReadyView() : DownloadBarView.Hidden;
            }

            var up = st.Upgrade;
            string upState = up != null ? up.State : null;
            // an upgrade that is failing or out of room: one short line, no bar
            if (up != null && (upState == "retrying" || upState == "failed" || upState == "no_space"))
            {
                _wasActive = false;
                return new DownloadBarView
                {
                    Mode = DownloadBarView.Kind.Error, LabelKey = "RWL_Bar_Paused",
                    Text = !string.IsNullOrWhiteSpace(up.Error) && upState != "retrying" ? Short(up.Error) : null
                };
            }

            // the engine's own install/download (backend, starter model), else the chosen model downloading in the background
            EngineProgress p = null;
            string name = null;
            bool engineBusy = st.Phase == EnginePhase.Downloading || st.Phase == EnginePhase.Installing;
            if (engineBusy && st.Progress != null)
            {
                p = st.Progress;
                string id = StepModelId(p.Step) ?? st.Model;
                if (IsModelStep(p.Step) && ModelCodename.PlainName(id) != null) name = ModelCodename.DisplayFor(id, st.ModelCodename, st.ModelName);
            }
            else if (up != null && upState == "downloading" && up.Progress != null) { p = up.Progress; name = ModelCodename.DisplayFor(up.Target, up.TargetCodename, up.TargetName); }

            if (p != null && IsLarge(p))
            {
                _wasActive = true;
                return Build(p, name);
            }

            if (st.Phase == EnginePhase.Error && _wasActive)
            {
                _wasActive = false;
                _errorUntil = nowMs + ErrorShowMs;   // the engine stops downloading: keep the line up a while, not for one sample
                _errorText = Short(st.Message);
            }
            if (nowMs < _errorUntil && st.Phase == EnginePhase.Error)
                return new DownloadBarView { Mode = DownloadBarView.Kind.Error, LabelKey = "RWL_Bar_Paused", Text = _errorText };

            // nothing big now: if one just finished, say Ready for a few seconds
            if (_wasActive)
            {
                _wasActive = false;
                _readyUntil = nowMs + ReadyShowMs;
            }
            return Ready(nowMs) ? ReadyView() : DownloadBarView.Hidden;
        }

        /// <summary>The catalog id in a "model <id>" or "verifying model <id>" step, or null.</summary>
        public static string StepModelId(string step)
        {
            if (step == null) return null;
            int i = step.IndexOf("model ", StringComparison.Ordinal);
            if (i < 0) return null;
            string id = step.Substring(i + 6).Trim();
            return id.Length == 0 ? null : id;
        }

        bool Ready(long nowMs) { return nowMs < _readyUntil; }
        static DownloadBarView ReadyView() { return new DownloadBarView { Mode = DownloadBarView.Kind.Ready, LabelKey = "RWL_Bar_Ready" }; }

        /// <summary>Large: at least 50 MB known, or a model file of unknown size.</summary>
        public static bool IsLarge(EngineProgress p)
        {
            if (p == null) return false;
            if (p.TotalBytes > 0) return p.TotalBytes >= LargeBytes;
            return IsModelStep(p.Step) && p.DoneBytes >= 0;
        }

        static bool IsModelStep(string step)
        {
            if (step == null) return false;
            return step.StartsWith("model", StringComparison.Ordinal) || step.StartsWith("verifying model", StringComparison.Ordinal);
        }

        DownloadBarView Build(EngineProgress p, string modelName)
        {
            if (p.Verifying)
                return new DownloadBarView { Mode = DownloadBarView.Kind.Bar, LabelKey = "RWL_Bar_Checking", Percent = Percent(p.DoneBytes, p.TotalBytes) < 0 ? 0 : Percent(p.DoneBytes, p.TotalBytes) };

            string key = Label(p.Step, modelName, out string arg);
            if (p.TotalBytes <= 0)
                return new DownloadBarView { Mode = DownloadBarView.Kind.Plain, LabelKey = "RWL_Bar_Unknown", LabelArg = (Math.Max(0, p.DoneBytes) / 1000000).ToString() };
            return new DownloadBarView { Mode = DownloadBarView.Kind.Bar, LabelKey = key, LabelArg = arg, Percent = Percent(p.DoneBytes, p.TotalBytes) };
        }

        /// <summary>The label line: the model's name when known, "the AI model" for a model without a name, "the AI engine" for the rest.</summary>
        public static string Label(string step, string modelName, out string arg)
        {
            arg = null;
            if (IsModelStep(step) || !string.IsNullOrWhiteSpace(modelName))
            {
                if (!string.IsNullOrWhiteSpace(modelName)) { arg = modelName.Trim(); return "RWL_Bar_Model"; }
                return "RWL_Bar_ModelGeneric";
            }
            return "RWL_Bar_Engine";
        }

        static string Short(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= MaxTextLen ? s : s.Substring(0, MaxTextLen - 3) + "...";
        }
    }
}
