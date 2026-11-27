using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorld.Planet;
using RimWorldLLM.Bridge;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Unattended play, game side: the rotating mod autosave (only when the game's own autosave is off), and the colony-lost
    /// handling (event, ask/auto/off, same world or new world). The decisions are in AutosaveRotation and ColonyRestart
    /// (Verse-free, tested); this file only reads the game and performs the acts. Runs from GameComponentUpdate, about once a second.
    /// </summary>
    public static class VivariumStability
    {
        static long _nextMs;
        static int _lastSaveTick = -1;
        static readonly ColonyRestart _restart = new ColonyRestart();
        static int _lostPolls;
        static Window _choice;
        static bool _choiceOpened;
        static string _pendingSaveName;
        static bool? _saveResult;
        static bool _warned;

        public static void OnGameStart()
        {
            _lastSaveTick = -1; _restart.Reset(); _lostPolls = 0; _choice = null; _choiceOpened = false;
            _pendingSaveName = null; _saveResult = null; _nextMs = 0;
        }

        public static void Update()
        {
            long ms = Environment.TickCount64;
            if (ms < _nextMs) return;
            _nextMs = ms + 1000;
            if (Current.Game == null || Find.TickManager == null) return;
            try { Autosave(); } catch (Exception e) { Warn("autosave", e); }
            try { Restart(ms / 1000.0); } catch (Exception e) { Warn("restart", e); }
        }

        static void Warn(string what, Exception e)
        {
            if (_warned) return;
            _warned = true;
            Log.Warning("[RimWorldLLM] unattended " + what + " failed: " + e.Message);
        }

        // ------------------------------------------------------------------ rotating autosave
        static string SavePath(string name) { return GenFilePaths.FilePathForSavedGame(name); }

        static void Autosave()
        {
            var s = RimWorldLLMMod.Settings;
            if (s == null) return;
            int now = Find.TickManager.TicksGame;
            if (_lastSaveTick < 0 || _lastSaveTick > now) _lastSaveTick = now;   // a fresh load starts the clock, never saves at once
            var input = new AutosaveRotation.Input
            {
                Enabled = s.narratorAutosave, NarratorPlays = VivariumGame.State.Playing,
                GameAutosaveOn = Prefs.AutosaveIntervalDays > 0.001f,
                NowTick = now, LastSaveTick = _lastSaveTick, EveryDays = s.narratorAutosaveDays,
                HardHold = LongEventHandler.AnyEventNowOrWaiting || GameDataSaveLoader.SavingIsTemporarilyDisabled
                    || (Find.WindowStack != null && Find.WindowStack.WindowsPreventSave),
                SoftHold = EventBus.Ledger.Open.Any(e => e.Kind == "raid")
            };
            if (AutosaveRotation.Decide(input) != AutosaveRotation.Decision.Save) return;
            int slot = AutosaveRotation.NextSlot(s.narratorAutosaveKeep, i =>
            {
                string p = SavePath(AutosaveRotation.SlotName(i));
                return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : (DateTime?)null;
            });
            string name = AutosaveRotation.SlotName(slot);
            if (!AutosaveRotation.IsOurs(name)) return;   // never write a name that is not ours
            _lastSaveTick = now;
            LongEventHandler.QueueLongEvent(() =>
            {
                try { GameDataSaveLoader.SaveGame(name); }
                catch (Exception e) { Log.Warning("[RimWorldLLM] narrator autosave failed: " + e.Message); }
            }, "Autosaving", false, null, false);
        }

        // ------------------------------------------------------------------ colony lost
        /// <summary>Every player map has no free colonist and no player caravan carries one; or the game itself says it is ending.</summary>
        static bool ColonyGone()
        {
            if (Find.TickManager.TicksGame < 2500) return false;
            var ender = Find.GameEnder;
            if (ender != null && ender.gameEnding) return true;
            bool anyHome = false;
            foreach (var m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                anyHome = true;
                if (m.mapPawns.FreeColonistsCount > 0) return false;
            }
            if (!anyHome) return false;
            foreach (var c in Find.WorldObjects.Caravans)
                if (c.IsPlayerControlled && c.PawnsListForReading.Any(p => p.IsFreeColonist)) return false;
            return true;
        }

        static string ColonyName()
        {
            try { var f = Faction.OfPlayer; return f != null && f.HasName ? f.Name : "Colony"; } catch (Exception) { return "Colony"; }
        }

        static ColonyRestart.Input Inp(double nowSec, bool lost)
        {
            var s = RimWorldLLMMod.Settings;
            bool same = false;
            try { same = Find.GameEnder != null && Find.GameEnder.CanSpawnNewWanderers(); } catch (Exception) { }
            return new ColonyRestart.Input
            {
                Lost = lost, Mode = s != null ? (RestartMode)Math.Max(0, Math.Min(2, s.narratorRestartMode)) : RestartMode.Ask,
                NarratorPlays = VivariumGame.State.Playing, SameWorldPossible = same, NowSec = nowSec,
                Resumed = !lost
            };
        }

        static void Restart(double nowSec)
        {
            bool gone = ColonyGone();
            _lostPolls = gone ? _lostPolls + 1 : 0;
            bool lost = _lostPolls >= 3;   // three polls in a row: not a map change or a pod landing
            var inp = Inp(nowSec, lost);
            if (_restart.Current == ColonyRestart.State.Asking && _choiceOpened && Find.WindowStack != null && !Find.WindowStack.IsOpen(_choice))
            {
                _restart.Dismissed();
                Say("RWL_Viv_RestartDismissed");
            }
            if (_saveResult.HasValue && _restart.Current == ColonyRestart.State.Saving)
            {
                bool ok = _saveResult.Value; _saveResult = null;
                Perform(_restart.SaveResult(ok, inp), inp);
                return;
            }
            Perform(_restart.Step(inp), inp);
            if (_restart.Current == ColonyRestart.State.Launched && !lost) ApplyHandoff();   // same world: colonists are back
        }

        static void Say(string key, params object[] args)
        {
            VivariumGame.State.Chat.Add("note", UiUtil.T(key, args), VivariumGame.NowTick);
        }

        static void Perform(RestartAct act, ColonyRestart.Input inp)
        {
            switch (act)
            {
                case RestartAct.None: return;
                case RestartAct.EmitColonyLost:
                    EventBus.Publish(new EventSpec { Type = "colony_lost", Severity = 1.0f, Summary = "The colony is lost", AllOnMap = false, Data = new JObject() });
                    Say("RWL_Viv_ColonyLost");
                    return;
                case RestartAct.NoteAutoDelay: Say("RWL_Viv_RestartAuto", (int)ColonyRestart.AutoDelaySec); return;
                case RestartAct.NoteStopped: Say("RWL_Viv_RestartStopped"); return;
                case RestartAct.NoteSaveFailed: Say("RWL_Viv_RestartSaveFailed"); return;
                case RestartAct.ShowChoice: ShowChoice(); return;
                case RestartAct.SaveLostWorld: SaveLost(); return;
                case RestartAct.StartSameWorld:
                    RestartHandoff.Set(VivariumGame.State.Playing, Summary(), false);
                    Say("RWL_Viv_RestartSame");
                    Find.WindowStack.Add(new Dialog_ChooseNewWanderers());
                    return;
                case RestartAct.StartNewWorld:
                    RestartHandoff.Set(VivariumGame.State.Playing, Summary(), true);
                    Say("RWL_Viv_RestartNew");
                    GenScene.GoToMainMenu();
                    return;
            }
        }

        static string Summary()
        {
            var last = new List<string>();
            try
            {
                var h = RWLBridge.Chronicle.History();
                foreach (var e in h.Skip(Math.Max(0, h.Count - 2))) last.Add(VivJson.Clean(e.Text, 200));
            }
            catch (Exception) { }
            return RestartHandoff.BuildSummary(ColonyName(), GenDate.DaysPassed, last);
        }

        static void SaveLost()
        {
            var existing = new List<string>();
            try { foreach (var f in GenFilePaths.AllSavedGameFiles) existing.Add(Path.GetFileNameWithoutExtension(f.Name)); }
            catch (Exception) { }
            string name = ColonyRestart.LostSaveName(ColonyName(), existing);
            if (File.Exists(SavePath(name))) { _saveResult = false; return; }   // never overwrite anything
            _pendingSaveName = name;
            LongEventHandler.QueueLongEvent(() =>
            {
                try { GameDataSaveLoader.SaveGame(name); }
                catch (Exception e) { Log.Warning("[RimWorldLLM] saving the lost world failed: " + e.Message); }
                _saveResult = File.Exists(SavePath(name));
            }, "Autosaving", false, null, false);
        }

        static void ShowChoice()
        {
            var node = new DiaNode(UiUtil.T("RWL_Viv_RestartText", ColonyName()));
            node.options.Add(Opt("RWL_Viv_RestartSameBtn", RestartChoice.SameWorld, Inp(Environment.TickCount64 / 1000.0, true).SameWorldPossible));
            node.options.Add(Opt("RWL_Viv_RestartNewBtn", RestartChoice.NewWorld, true));
            node.options.Add(Opt("RWL_Viv_RestartStopBtn", RestartChoice.Stop, true));
            var dlg = new Dialog_NodeTree(node, false, false, UiUtil.T("RWL_Viv_RestartTitle"));
            _choice = dlg; _choiceOpened = true;
            Find.WindowStack.Add(dlg);
        }

        static DiaOption Opt(string key, RestartChoice c, bool enabled)
        {
            var o = new DiaOption(UiUtil.T(key)) { resolveTree = true };
            if (!enabled) o.Disable(UiUtil.T("RWL_Viv_RestartSameNo"));
            o.action = () =>
            {
                _choiceOpened = false;
                try { Perform(_restart.Choose(c, Inp(Environment.TickCount64 / 1000.0, true)), Inp(Environment.TickCount64 / 1000.0, true)); }
                catch (Exception e) { Warn("restart choice", e); }
            };
            return o;
        }

        // ------------------------------------------------------------------ the next colony
        /// <summary>A new colony has started (new game, or the same world got new colonists): carry the story over.</summary>
        public static void ApplyHandoff()
        {
            bool playing; string summary;
            if (!RestartHandoff.Take(out playing, out summary)) return;
            try
            {
                if (playing) VivariumSwitch.SetPlaying(true, false);
                string text = RestartHandoff.ContinueText(summary);
                int day = GenDate.DaysPassed, tick = VivariumGame.NowTick;
                RWLBridge.Chronicle.Add(day, ChronicleBook.KindMajor, text, tick);
                ChatFeed.Chronicle(VivariumGame.State.Chat, tick, day, text);
            }
            catch (Exception e) { Warn("handoff", e); }
        }
    }

    /// <summary>The new world is started from the main menu: the game's own quick start (what -quicktest uses).</summary>
    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.Init))]
    public static class Patch_RestartMainMenu
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (!RestartHandoff.TakeNewWorldRequest()) return;
                LongEventHandler.QueueLongEvent(() => { Root_Play.SetupForQuickTestPlay(); PageUtility.InitGameStart(); }, "GeneratingMap", true, null);
            }
            catch (Exception e) { Log.ErrorOnce("[RimWorldLLM] new world after a lost colony failed: " + e.Message, 7340301); }
        }
    }
}
