using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RimWorldLLM.Bridge
{
    public enum RestartMode { Ask = 0, Auto = 1, Off = 2 }
    public enum RestartChoice { None = 0, SameWorld = 1, NewWorld = 2, Stop = 3 }

    /// <summary>What the game side must do now. One act per <see cref="ColonyRestart.Step"/> call.</summary>
    public enum RestartAct { None, EmitColonyLost, ShowChoice, NoteAutoDelay, SaveLostWorld, StartSameWorld, StartNewWorld, NoteStopped, NoteSaveFailed }

    /// <summary>
    /// The colony-lost state machine (Verse-free): lost → (ask | auto | off) → same world / new world / stop. It never deletes
    /// anything: leaving a lost world always goes through SaveLostWorld first, and a failed save cancels the leave.
    /// Time is real seconds. The game side feeds <see cref="Step"/> once per poll and performs the returned act.
    /// </summary>
    public sealed class ColonyRestart
    {
        public enum State { Idle, Announced, Asking, Delay, Decided, Saving, SameWait, Launched, Done }

        public const double AutoDelaySec = 20.0;
        public const double SameWorldWaitSec = 180.0;   // auto: the vanilla wanderer dialog needs answering; after this the new world is taken

        public State Current { get; private set; }
        public RestartChoice Choice { get; private set; }
        public bool SaveDone { get; private set; }
        double _deadline;

        public struct Input
        {
            public bool Lost;               // all colonists gone on every player map
            public RestartMode Mode;
            public bool NarratorPlays;
            public bool SameWorldPossible;  // the game can spawn new wanderers
            public double NowSec;
            public bool Resumed;            // colonists are back (the same-world path worked)
        }

        public RestartAct Step(Input i)
        {
            switch (Current)
            {
                case State.Idle:
                    if (!i.Lost) return RestartAct.None;
                    Current = State.Announced;
                    return RestartAct.EmitColonyLost;
                case State.Announced:
                    if (i.Mode == RestartMode.Off || !i.NarratorPlays) { Current = State.Done; return RestartAct.None; }
                    if (i.Mode == RestartMode.Ask) { Current = State.Asking; return RestartAct.ShowChoice; }
                    Current = State.Delay; _deadline = i.NowSec + AutoDelaySec;
                    return RestartAct.NoteAutoDelay;
                case State.Delay:
                    if (!i.Lost) { Current = State.Idle; return RestartAct.None; }   // colonists came back by themselves
                    if (i.NowSec < _deadline) return RestartAct.None;
                    return Decide(i.SameWorldPossible ? RestartChoice.SameWorld : RestartChoice.NewWorld, i);
                case State.Decided:
                    return Launch(i);
                case State.SameWait:
                    if (i.Resumed || !i.Lost) { Current = State.Launched; return RestartAct.None; }
                    if (i.NowSec >= _deadline && i.Mode == RestartMode.Auto) return Decide(RestartChoice.NewWorld, i);
                    return RestartAct.None;
                default:
                    return RestartAct.None;
            }
        }

        /// <summary>The player answered the choice dialog. Returns the next act at once (the same as the next Step would).</summary>
        public RestartAct Choose(RestartChoice c, Input i)
        {
            if (Current != State.Asking) return RestartAct.None;
            if (c == RestartChoice.SameWorld && !i.SameWorldPossible) c = RestartChoice.NewWorld;
            return Decide(c, i);
        }

        RestartAct Decide(RestartChoice c, Input i)
        {
            Choice = c;
            if (c == RestartChoice.Stop || c == RestartChoice.None) { Current = State.Done; return RestartAct.NoteStopped; }
            if (c == RestartChoice.SameWorld)
            {
                // staying in the world needs no leave and so no extra save: the world is kept as it is
                Current = State.SameWait; _deadline = i.NowSec + SameWorldWaitSec;
                return RestartAct.StartSameWorld;
            }
            // leaving the world: save it first, once, under a new name
            Current = SaveDone ? State.Decided : State.Saving;
            return SaveDone ? Launch(i) : RestartAct.SaveLostWorld;
        }

        RestartAct Launch(Input i)
        {
            Current = State.Launched;
            return RestartAct.StartNewWorld;
        }

        /// <summary>The game side reports the lost-world save. A failed save cancels the leave: nothing is lost.</summary>
        public RestartAct SaveResult(bool ok, Input i)
        {
            if (Current != State.Saving) return RestartAct.None;
            if (!ok) { Current = State.Done; return RestartAct.NoteSaveFailed; }
            SaveDone = true;
            return Launch(i);
        }

        /// <summary>The dialog was closed without an answer: nothing happens (the player can reopen via chat/menu).</summary>
        public void Dismissed() { if (Current == State.Asking) Current = State.Done; }

        public void Reset() { Current = State.Idle; Choice = RestartChoice.None; SaveDone = false; }

        // ---------------------------------------------------------------- naming and handoff

        static readonly Regex BadName = new Regex("[^A-Za-z0-9 _.\\-]", RegexOptions.CultureInvariant);

        /// <summary>"&lt;colony&gt;-lost-&lt;n&gt;" with the first n whose name is not in existing (case-insensitive). Never an existing name.</summary>
        public static string LostSaveName(string colony, ICollection<string> existing)
        {
            string baseName = BadName.Replace(colony ?? "", "").Trim();
            if (baseName.Length == 0) baseName = "Colony";
            if (baseName.Length > 40) baseName = baseName.Substring(0, 40).TrimEnd();
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null) foreach (var e in existing) have.Add(e);
            for (int n = 1; ; n++)
            {
                string name = baseName + "-lost-" + n;
                if (!have.Contains(name)) return name;
            }
        }
    }

    /// <summary>What a lost colony hands to the next one (static, survives the scene change): narrator on or off and a short summary.</summary>
    public static class RestartHandoff
    {
        public static bool Pending;
        public static bool NewWorldRequested;    // the main menu should start a new game
        public static bool NarratorPlays;
        public static string Summary = "";

        public static void Set(bool narratorPlays, string summary, bool newWorld)
        {
            Pending = true; NewWorldRequested = newWorld; NarratorPlays = narratorPlays; Summary = summary ?? "";
        }

        /// <summary>One-shot read of the start-of-next-game handoff; false when none.</summary>
        public static bool Take(out bool narratorPlays, out string summary)
        {
            narratorPlays = NarratorPlays; summary = Summary;
            bool had = Pending && !NewWorldRequested;
            if (had) { Pending = false; NarratorPlays = false; Summary = ""; }
            return had;
        }

        /// <summary>Marks that the new world is being started now (the main menu action is done).</summary>
        public static bool TakeNewWorldRequest()
        {
            if (!Pending || !NewWorldRequested) return false;
            NewWorldRequested = false;
            return true;
        }

        public static void Clear() { Pending = false; NewWorldRequested = false; NarratorPlays = false; Summary = ""; }

        /// <summary>The chronicle line for the new colony, from the lost one's summary.</summary>
        public static string ContinueText(string summary)
        {
            summary = VivJson.Clean(summary, 600);
            return summary.Length == 0 ? "A new colony begins; the story goes on." : "The story continues. " + summary;
        }

        /// <summary>A short, plain summary for the handoff.</summary>
        public static string BuildSummary(string colony, int days, IList<string> lastChronicle)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrWhiteSpace(colony) ? "The last colony" : colony.Trim());
            sb.Append(" fell after ").Append(days).Append(days == 1 ? " day." : " days.");
            if (lastChronicle != null)
                foreach (var l in lastChronicle)
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    if (sb.Length + l.Length > 520) break;
                    sb.Append(' ').Append(l.Trim());
                }
            return sb.ToString();
        }
    }
}
