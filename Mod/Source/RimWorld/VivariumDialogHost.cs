using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using RimWorld;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Unattended play: sees every window that wants a player choice (choice letters, node trees, message boxes,
    /// naming dialogs, growth moments) and answers it on request or, past the timeout, with the safe default.
    /// Game thread, driven from GameComponentUpdate so it also runs while a modal window has the game paused.
    /// The model, policy and sweep are Verse-free (Mod/Source/Bridge/VivariumDialogs.cs); this is the thin game side.</summary>
    public static class VivariumDialogHost
    {
        const float ScanEvery = 0.35f;   // real seconds
        static float _last;
        static bool _warned;
        static int _nextWindow;
        static readonly Dictionary<Window, string> WindowIds = new Dictionary<Window, string>();
        static Dictionary<string, object> _handles = new Dictionary<string, object>();   // id -> Window or Letter

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static T Fld<T>(object o, string name, T dflt = default(T))
        {
            try
            {
                for (var t = o.GetType(); t != null; t = t.BaseType)
                {
                    var f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                    if (f != null) { var v = f.GetValue(o); return v is T ? (T)v : dflt; }
                    var p = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                    if (p != null) { var v = p.GetValue(o, null); return v is T ? (T)v : dflt; }
                }
            }
            catch (Exception) { }
            return dflt;
        }

        static T Call<T>(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethod(name, Any);
            if (m == null) throw new MissingMethodException(o.GetType().Name, name);
            var r = m.Invoke(o, args);
            return r is T ? (T)r : default(T);
        }

        static void Call(object o, string name, params object[] args) { Call<object>(o, name, args); }

        static void Warn(Exception e)
        {
            if (_warned) return;
            _warned = true;
            Log.Warning("[RimWorldLLM] dialog scan failed (logged once): " + e);
        }

        /// <summary>Per frame from GameComponentUpdate.</summary>
        public static void Update()
        {
            if (Current.Game == null || Find.WindowStack == null) return;
            float now = Time.realtimeSinceStartup;
            if (now - _last < ScanEvery) return;
            _last = now;
            try
            {
                var v = VivariumGame.State;
                var seen = new List<DialogRecord>();
                var handles = new Dictionary<string, object>();
                ScanWindows(seen, handles);
                ScanLetters(seen, handles);
                _handles = handles;
                foreach (var w in WindowIds.Keys.Where(w => !w.IsOpen).ToList()) WindowIds.Remove(w);
                double real = Vivarium.NowReal();
                var added = v.Dialogs.Sync(seen, real);
                int timeout = Math.Max(5, v.Settings.DialogTimeoutS);
                foreach (var d in added)
                {
                    var rec = d;
                    EventPayload.Safe("dialog", () => EventBus.Publish(new EventSpec
                    {
                        Type = "dialog", Severity = 0.6f, Summary = "Needs an answer: " + rec.Title,
                        Data = DialogBook.EventData(rec, timeout)
                    }));
                }
                DialogSweep.Run(v, real, VivariumGame.NowTick, (d, c) => Answer(d, c.Option, c.Texts));
            }
            catch (Exception e) { Warn(e); }
        }

        // ------------------------------------------------------------------ scanning

        static string WindowId(Window w)
        {
            string id;
            if (!WindowIds.TryGetValue(w, out id)) { id = "W" + (++_nextWindow); WindowIds[w] = id; }
            return id;
        }

        static DialogOption Opt(int i, string label, bool disabled = false, string why = "", string risk = "", string role = DialogOption.Other)
        {
            return new DialogOption { Index = i, Label = label ?? "", Disabled = disabled, DisabledReason = why ?? "", Risk = risk, Role = role };
        }

        static void ScanWindows(List<DialogRecord> seen, Dictionary<string, object> handles)
        {
            foreach (var w in Find.WindowStack.Windows.ToList())
            {
                try
                {
                    if (w == VivariumStability.ChoiceWindow) continue;   // the colony-lost choice is the player's (or auto mode's)
                    var nt = w as Dialog_NodeTree;
                    if (nt != null) { AddNodeTree(nt, seen, handles); continue; }
                    var mb = w as Dialog_MessageBox;
                    if (mb != null) { AddMessageBox(mb, seen, handles); continue; }
                    var cf = w as Dialog_Confirm;
                    if (cf != null)
                    {
                        var d = new DialogRecord { Id = WindowId(w), Kind = DialogKinds.MessageBox, Title = Fld<string>(cf, "title", "Confirm") };
                        d.Options.Add(Opt(0, "Confirm", risk: "confirms an action"));
                        d.Options.Add(Opt(1, "Cancel", role: DialogOption.Close));
                        seen.Add(d); handles[d.Id] = w; continue;
                    }
                    var gn = w as Dialog_GiveName;
                    if (gn != null) { AddNaming(gn, seen, handles); continue; }
                    var gm = w as Dialog_GrowthMomentChoices;
                    if (gm != null)
                    {
                        var letter = Fld<ChoiceLetter_GrowthMoment>(gm, "letter");
                        if (letter != null) AddGrowth(letter, false, seen, handles);
                    }
                }
                catch (Exception e) { Warn(e); }
            }
        }

        static void AddNodeTree(Dialog_NodeTree nt, List<DialogRecord> seen, Dictionary<string, object> handles)
        {
            var node = Fld<DiaNode>(nt, "curNode");
            if (node == null || node.options == null) return;
            string title = Fld<string>(nt, "title", "");
            var d = new DialogRecord { Id = WindowId(nt), Kind = DialogKinds.NodeTree, Title = string.IsNullOrEmpty(title) ? FirstWords(Fld<string>(node, "text", "")) : title, Text = Fld<string>(node, "text", "") };
            for (int i = 0; i < node.options.Count; i++)
            {
                var o = node.options[i];
                string label = VivariumDigest.OptText(o);
                bool closes = o.resolveTree && o.link == null && Fld<object>(o, "linkLateBind") == null && o.action == null;
                d.Options.Add(Opt(i, label, o.disabled, o.disabledReason, "", closes ? DialogOption.Close : DialogOption.Other));
            }
            seen.Add(d); handles[d.Id] = nt;
        }

        static void AddMessageBox(Dialog_MessageBox mb, List<DialogRecord> seen, Dictionary<string, object> handles)
        {
            var d = new DialogRecord { Id = WindowId(mb), Kind = DialogKinds.MessageBox, Title = Fld<string>(mb, "title", ""), Text = Fld<string>(mb, "text", "") };
            string a = Fld<string>(mb, "buttonAText", ""), b = Fld<string>(mb, "buttonBText", ""), c = Fld<string>(mb, "buttonCText", "");
            if (!string.IsNullOrEmpty(a)) d.Options.Add(Opt(0, a, risk: Fld<bool>(mb, "buttonADestructive") ? "destructive" : ""));
            if (!string.IsNullOrEmpty(b)) d.Options.Add(Opt(1, b, role: DialogOption.Decline));
            if (!string.IsNullOrEmpty(c)) d.Options.Add(Opt(2, c, role: DialogOption.Decline));
            if (d.Options.Count == 0) return;
            if (string.IsNullOrEmpty(d.Title)) d.Title = FirstWords(d.Text);
            seen.Add(d); handles[d.Id] = mb;
        }

        static void AddNaming(Dialog_GiveName gn, List<DialogRecord> seen, Dictionary<string, object> handles)
        {
            int max = Fld<int>(gn, "FirstCharLimit", 40);
            if (max <= 0) max = 40;
            string key = Fld<string>(gn, "nameMessageKey", "");
            string title = gn is Dialog_NamePlayerFactionAndSettlement ? "Name your faction and settlement"
                : gn is Dialog_NamePlayerSettlement ? "Name your settlement"
                : gn is Dialog_NamePlayerFaction ? "Name your faction"
                : FirstWords(gn.GetType().Name.Replace("Dialog_", ""));
            var d = new DialogRecord { Id = WindowId(gn), Kind = DialogKinds.Naming, Title = title, Text = string.IsNullOrEmpty(key) ? "" : SafeTranslate(key) };
            if (gn is Dialog_NamePlayerFactionAndSettlement)
            {
                d.Fields.Add(new DialogField { Name = "faction", Label = "Faction name", MaxLen = max });
                d.Fields.Add(new DialogField { Name = "settlement", Label = "Settlement name", MaxLen = Math.Max(1, Fld<int>(gn, "SecondCharLimit", 40)) });
            }
            else d.Fields.Add(new DialogField { Name = "name", Label = "Name", MaxLen = max });
            seen.Add(d); handles[d.Id] = gn;
        }

        static string SafeTranslate(string key) { try { return key.Translate().ToString(); } catch (Exception) { return ""; } }

        static string FirstWords(string s) { s = (s ?? "").Trim(); return s.Length > 50 ? s.Substring(0, 50) + "…" : s; }

        static void AddGrowth(ChoiceLetter_GrowthMoment gl, bool isLetter, List<DialogRecord> seen, Dictionary<string, object> handles)
        {
            if (Fld<bool>(gl, "choiceMade")) return;
            string id = "L" + gl.ID;
            if (handles.ContainsKey(id)) return;
            var pawn = Fld<Pawn>(gl, "pawn");
            string who = pawn != null ? pawn.LabelShort : "a child";
            var d = new DialogRecord { Id = id, Kind = DialogKinds.Growth, IsLetter = isLetter, Title = "Growth moment: " + who, Text = Fld<string>(gl, "text", "") };
            d.Options.Add(Opt(0, "Take the first passion and trait choices for " + who));
            try { d.Growth = GrowthOf(gl, pawn); } catch (Exception e) { Warn(e); }
            seen.Add(d); handles[id] = gl;
        }

        static string Trim(string s, int n) { s = (s ?? "").Replace("\n", " ").Trim(); return s.Length > n ? s.Substring(0, n) + "…" : s; }

        static GrowthInfo GrowthOf(ChoiceLetter_GrowthMoment gl, Pawn pawn)
        {
            var g = new GrowthInfo();
            if (pawn != null)
            {
                g.Child = pawn.LabelShort;
                g.Age = pawn.ageTracker != null ? pawn.ageTracker.AgeBiologicalYears.ToString() : "";
                if (pawn.skills != null)
                    g.Skills = string.Join(", ", pawn.skills.skills.Where(sk => sk.passion != Passion.None || sk.Level > 0)
                        .Select(sk => sk.def.skillLabel + " " + sk.Level + (sk.passion == Passion.Major ? "**" : sk.passion == Passion.Minor ? "*" : "")).Take(12));
                if (pawn.story != null)
                    g.Traits = string.Join(", ", pawn.story.traits.allTraits.Select(t => t.LabelCap.ToString()).Take(8));
            }
            foreach (var sd in Fld<List<SkillDef>>(gl, "passionChoices") ?? new List<SkillDef>()) g.Passions.Add(sd.skillLabel);
            g.MaxPassions = Math.Max(1, Fld<int>(gl, "passionGainsCount", 1));
            foreach (var t in Fld<List<Trait>>(gl, "traitChoices") ?? new List<Trait>())
                g.TraitChoices.Add(new KeyValuePair<string, string>(t.Label, Trim(t.TipString(pawn), 160)));
            return g;
        }

        static string LetterKind(ChoiceLetter cl)
        {
            if (cl is ChoiceLetter_AcceptJoiner) return DialogKinds.Joiner;
            if (cl is ChoiceLetter_AcceptCreepJoiner) return DialogKinds.CreepJoiner;
            if (cl is ChoiceLetter_AcceptVisitors) return DialogKinds.Visitors;
            if (cl.GetType().Name == "ChoiceLetter_BetrayVisitors") return DialogKinds.BetrayVisitors;   // obsolete in 1.7; matched by name
            if (cl is ChoiceLetter_RansomDemand) return DialogKinds.Ransom;
            if (cl is ChoiceLetter_ChoosePawn) return DialogKinds.ChoosePawn;
            if (cl is ChoiceLetter_BabyToChild) return DialogKinds.BabyToChild;
            if (cl is ChoiceLetter_GameEnded || cl is ChoiceLetter_EntityDiscovered || cl is ChoiceLetter_BabyBirth) return null;
            if (cl.GetType() == typeof(ChoiceLetter)) return cl.quest != null && cl.quest.State == QuestState.NotYetAccepted ? DialogKinds.Quest : null;
            return DialogKinds.Letter;
        }

        static void ScanLetters(List<DialogRecord> seen, Dictionary<string, object> handles)
        {
            var stack = Find.LetterStack;
            if (stack == null) return;
            foreach (var l in stack.LettersListForReading.ToList())
            {
                try
                {
                    var gl = l as ChoiceLetter_GrowthMoment;
                    if (gl != null) { AddGrowth(gl, true, seen, handles); continue; }
                    var cl = l as ChoiceLetter;
                    if (cl == null) continue;
                    string kind = LetterKind(cl);
                    if (kind == null) continue;
                    var d = new DialogRecord { Id = "L" + cl.ID, Kind = kind, IsLetter = true, Title = cl.Label.ToString(), Text = cl.Text.ToString() };
                    if (kind == DialogKinds.Quest)
                    {
                        d.Options.Add(Opt(0, "Accept the quest", risk: DialogKinds.AcceptRisk(kind), role: DialogOption.Accept));
                        d.Options.Add(Opt(1, "Decline the quest", role: DialogOption.Decline));
                    }
                    else
                    {
                        var roles = DialogKinds.RolesFor(kind);
                        var opts = cl.Choices.ToList();
                        for (int i = 0; i < opts.Count; i++)
                        {
                            var o = opts[i];
                            string role = i < roles.Length ? roles[i] : DialogOption.Other;
                            bool inert = o.action == null && o.link == null;
                            d.Options.Add(Opt(i, VivariumDigest.OptText(o), o.disabled || inert,
                                inert ? "only opens a window" : o.disabledReason, role == DialogOption.Accept ? DialogKinds.AcceptRisk(kind) : "", role));
                        }
                    }
                    seen.Add(d); handles[d.Id] = cl;
                }
                catch (Exception e) { Warn(e); }
            }
        }

        // ------------------------------------------------------------------ answering

        /// <summary>The hand: answers a scanned dialog the way the player's click would. Game thread.</summary>
        public static HandResult Answer(DialogRecord rec, int option, IDictionary<string, string> texts)
        {
            try
            {
                object h;
                if (rec == null || !_handles.TryGetValue(rec.Id, out h)) return HandResult.No("the dialog is gone");
                var done = HandResult.Done(DialogPolicy.Done(rec, new DialogChoice { Option = option, Texts = texts == null ? new Dictionary<string, string>() : new Dictionary<string, string>(texts) }));
                var w = h as Window;
                if (w != null && !w.IsOpen) return HandResult.No("the window is already closed");
                var nt = h as Dialog_NodeTree;
                if (nt != null)
                {
                    var node = Fld<DiaNode>(nt, "curNode");
                    if (node == null || option < 0 || option >= node.options.Count) return HandResult.No("the dialog moved on; read /dialogs again");
                    var o = node.options[option];
                    if (o.disabled) return HandResult.No("that option is disabled");
                    Call(o, "Activate");
                    return done;
                }
                var mb = h as Dialog_MessageBox;
                if (mb != null)
                {
                    Action act = option == 0 ? Fld<Action>(mb, "buttonAAction") : option == 1 ? Fld<Action>(mb, "buttonBAction") : Fld<Action>(mb, "buttonCAction");
                    if (act != null) act();
                    if (mb.IsOpen) mb.Close();
                    return done;
                }
                var cf = h as Dialog_Confirm;
                if (cf != null)
                {
                    if (option == 0) { var act = Fld<Action>(cf, "onConfirm"); if (act != null) act(); }
                    if (cf.IsOpen) cf.Close();
                    return done;
                }
                var gn = h as Dialog_GiveName;
                if (gn != null)
                {
                    string first, second = null;
                    if (texts == null || !texts.TryGetValue(rec.Fields[0].Name, out first)) return HandResult.No("send the name text");
                    bool two = gn is Dialog_NamePlayerFactionAndSettlement;
                    if (two && (rec.Fields.Count < 2 || !texts.TryGetValue(rec.Fields[1].Name, out second))) return HandResult.No("this dialog needs both names");
                    if (!Call<bool>(gn, "IsValidName", first)) return HandResult.No("\"" + first + "\" is not a valid name here (too long or already taken)");
                    if (two && !Call<bool>(gn, "IsValidSecondName", second)) return HandResult.No("\"" + second + "\" is not a valid name here");
                    gn.Named(first);
                    if (two) Call(gn, "NamedSecond", second);
                    if (gn.IsOpen) gn.Close();
                    return done;
                }
                var growth = h as ChoiceLetter_GrowthMoment;
                if (growth != null)
                {
                    var passions = Fld<List<SkillDef>>(growth, "passionChoices") ?? new List<SkillDef>();
                    int gains = Math.Max(1, Fld<int>(growth, "passionGainsCount", 1));
                    var traits = Fld<List<Trait>>(growth, "traitChoices") ?? new List<Trait>();
                    var pickP = passions.Take(gains).ToList();
                    var pickT = traits.FirstOrDefault();
                    string pp, pt;
                    if (texts != null && texts.TryGetValue("passions", out pp))
                    {
                        var names = pp.Split('|');
                        pickP = passions.Where(sd => names.Any(n => string.Equals(n, sd.skillLabel, StringComparison.OrdinalIgnoreCase))).Take(gains).ToList();
                        if (pickP.Count != names.Length) return HandResult.No("the offered passions changed; read /dialogs again");
                    }
                    if (texts != null && texts.TryGetValue("trait", out pt))
                    {
                        pickT = traits.FirstOrDefault(t => string.Equals(t.Label, pt, StringComparison.OrdinalIgnoreCase));
                        if (pickT == null) return HandResult.No("the offered traits changed; read /dialogs again");
                    }
                    growth.MakeChoices(pickP, pickT);
                    var open = Find.WindowStack.Windows.FirstOrDefault(x => x is Dialog_GrowthMomentChoices && Fld<ChoiceLetter_GrowthMoment>(x, "letter") == growth);
                    if (open != null) open.Close();
                    if (Find.LetterStack.LettersListForReading.Contains(growth)) Find.LetterStack.RemoveLetter(growth);
                    return done;
                }
                var cl = h as ChoiceLetter;
                if (cl != null)
                {
                    if (!Find.LetterStack.LettersListForReading.Contains(cl)) return HandResult.No("the letter is gone");
                    if (rec.Kind == DialogKinds.Quest) return AnswerQuest(cl, option, done);
                    var opts = cl.Choices.ToList();
                    if (option < 0 || option >= opts.Count) return HandResult.No("no such choice");
                    var o = opts[option];
                    if (o.disabled || o.action == null) return HandResult.No("that choice is not available");
                    o.action();
                    return done;
                }
                return HandResult.No("this kind of dialog cannot be answered");
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Log.Warning("[RimWorldLLM] dialog answer failed: " + e); }
                return HandResult.No("the game refused: " + e.Message);
            }
        }

        static HandResult AnswerQuest(ChoiceLetter cl, int option, HandResult done)
        {
            var q = cl.quest;
            if (q == null) return HandResult.No("the quest is gone");
            if (option == 0)
            {
                if (q.State != QuestState.NotYetAccepted) return HandResult.No("the quest is no longer on offer");
                Pawn by = null;
                if (q.RequiresAccepter)
                {
                    by = PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault();
                    if (by == null) return HandResult.No("no free colonist can accept the quest");
                }
                q.Accept(by);
            }
            else
            {
                var rej = Fld<DiaOption>(cl, "Option_Reject");
                if (rej != null && rej.action != null) rej.action();
            }
            if (Find.LetterStack.LettersListForReading.Contains(cl)) Find.LetterStack.RemoveLetter(cl);
            return done;
        }
    }
}
