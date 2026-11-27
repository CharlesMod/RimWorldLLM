using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Bridge;
using Newtonsoft.Json.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>"Mind" tab on colonists: the agent's view of the pawn, plus a Director's notes box that writes back into the mind blob.</summary>
    public class ITab_Pawn_RWLMind : ITab
    {
        const float TabWidth = 520f, TabHeight = 560f;
        Vector2 scroll;
        float lastHeight = 800f;
        int notesPawn = -1;
        int notesRev = -1;
        string notesBuf = "";
        string status = "";

        public ITab_Pawn_RWLMind()
        {
            size = new Vector2(TabWidth, TabHeight);
            labelKey = "RWL_MindTab";
        }

        public override bool IsVisible
        {
            get
            {
                var p = SelPawn;
                return p != null && p.RaceProps != null && p.RaceProps.Humanlike && p.Faction != null && p.Faction.IsPlayer && !p.Dead;
            }
        }

        protected override void FillTab()
        {
            var pawn = SelPawn;
            if (pawn == null) return;
            var rect = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
            rect.yMin += 14f;
            int id = pawn.thingIDNumber;
            MindRecord rec;
            RWLBridge.Minds.Records.TryGetValue(id, out rec);

            // Sync the notes box with the stored blob when the pawn changes or the agent changed the blob.
            var view = MindView.Parse(rec != null && rec.MindJson != null ? SafeParse(rec.MindJson) : null);
            if (notesPawn != id || notesRev != (rec != null ? rec.Rev : -1))
            {
                if (notesPawn != id || string.IsNullOrEmpty(notesBuf) || notesBuf == view.Notes) notesBuf = view.Notes;
                if (notesPawn != id) status = "";
                notesPawn = id;
                notesRev = rec != null ? rec.Rev : -1;
            }

            var inner = new Rect(0f, 0f, rect.width - 18f, lastHeight);
            Widgets.BeginScrollView(rect, ref scroll, inner);
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(inner);
            Draw(list, pawn, rec, view);
            lastHeight = list.CurHeight + 10f;
            list.End();
            Widgets.EndScrollView();
        }

        static JToken SafeParse(string json) { try { return JToken.Parse(json); } catch { return null; } }

        static void Header(Listing_Standard l, string key)
        {
            l.Gap(6f);
            Text.Font = GameFont.Medium;
            l.Label(UiUtil.T(key));
            Text.Font = GameFont.Small;
        }

        static void Dim(Listing_Standard l, string text)
        {
            GUI.color = Color.gray;
            l.Label(text);
            GUI.color = Color.white;
        }

        void Draw(Listing_Standard l, Pawn pawn, MindRecord rec, MindView v)
        {
            // Latest inner thought.
            Header(l, "RWL_MindLatestThought");
            if (rec != null && rec.Thoughts.Count > 0)
            {
                var t = rec.Thoughts[rec.Thoughts.Count - 1];
                l.Label(t.Value);
                Dim(l, UiUtil.T("RWL_MindThoughtAt", UiUtil.DateOf(t.Key)));
            }
            else Dim(l, UiUtil.T("RWL_MindNoThought"));

            Header(l, "RWL_MindDiary");
            if (rec != null && rec.Diaries.Count > 0)
            {
                for (int i = rec.Diaries.Count - 1; i >= 0; i--)
                {
                    var d = rec.Diaries[i];
                    var dr = l.GetRect(Text.LineHeight);
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(dr.x, dr.y, dr.width - 74f, dr.height), UiUtil.DateOf(d.Key));
                    GUI.color = Color.white;
                    if (Widgets.ButtonText(new Rect(dr.xMax - 70f, dr.y, 70f, dr.height), UiUtil.T("RWL_Share_Button"))) ShareService.ShareDiary(pawn, d);
                    l.Label(d.Value);
                    l.Gap(4f);
                }
            }
            else Dim(l, UiUtil.T("RWL_MindNoDiary"));

            if (rec == null || rec.MindJson == null)
            {
                l.Gap(8f);
                Dim(l, UiUtil.T("RWL_MindNoMind", pawn.LabelShortCap));
                return;
            }

            Header(l, "RWL_MindFixedBio");
            l.Label(v.FixedBio);

            Header(l, "RWL_MindVoice");
            if (v.VoiceRegister.Length > 0) l.Label(UiUtil.T("RWL_MindVoiceRegister", v.VoiceRegister) + (v.VoiceEditedByPlayer ? " " + UiUtil.T("RWL_MindVoicePlayerEdited") : ""));
            if (v.Tics.Count > 0) l.Label(UiUtil.T("RWL_MindVoiceTics", string.Join("; ", v.Tics.ToArray())));
            foreach (var e in v.Examples) Dim(l, UiUtil.T("RWL_MindVoiceExample", e));

            Header(l, "RWL_MindEvolvingBio");
            if (v.EvolvingBio.Length > 0) l.Label(v.EvolvingBio); else Dim(l, UiUtil.T("RWL_MindNoEvolvingBio"));
            if (v.History.Count > 0)
            {
                l.Gap(4f);
                l.Label(UiUtil.T("RWL_MindBioHistory") + ":");
                for (int i = v.History.Count - 1; i >= 0; i--)
                {
                    var h = v.History[i];
                    Dim(l, UiUtil.T("RWL_MindBioRevision", h.Rev, UiUtil.DateOf(h.Tick)));
                    l.Label(h.Text);
                }
            }

            Header(l, "RWL_MindWants");
            if (v.Wants.Count == 0) Dim(l, UiUtil.T("RWL_MindNone"));
            foreach (var w in v.Wants) l.Label("- " + w);

            Header(l, "RWL_MindRelations");
            if (v.Relations.Count == 0) Dim(l, UiUtil.T("RWL_MindNone"));
            foreach (var r in v.Relations)
            {
                string who = r.Name.Length > 0 ? r.Name : "#" + r.Id;
                l.Label(UiUtil.T("RWL_MindRelationLine", who, r.Feeling));
                if (r.History.Length > 0) Dim(l, r.History);
            }

            Header(l, "RWL_MindMemories");
            if (v.Memories.Count == 0) Dim(l, UiUtil.T("RWL_MindNone"));
            for (int i = v.Memories.Count - 1; i >= 0; i--)
            {
                var m = v.Memories[i];
                l.Label(m.Text);
                Dim(l, UiUtil.T("RWL_MindMemoryMeta", UiUtil.DateOf(m.Tick), m.Source, m.From.Length > 0 ? UiUtil.T("RWL_MindMemoryFrom", m.From) : "", m.Visibility, m.Confidence.ToString("0.0"))
                    + (m.Pinned ? "  |  " + UiUtil.T("RWL_MindPinned") : ""));
            }

            Header(l, "RWL_MindNotes");
            Dim(l, UiUtil.T("RWL_MindNotesHint"));
            notesBuf = Widgets.TextArea(l.GetRect(110f), notesBuf ?? "");
            var row = l.GetRect(28f);
            if (Widgets.ButtonText(new Rect(row.x, row.y, 140f, 28f), UiUtil.T("RWL_MindNotesSave"))) SaveNotes(pawn);
            if (Widgets.ButtonText(new Rect(row.x + 150f, row.y, 100f, 28f), UiUtil.T("RWL_MindNotesRevert"))) { notesBuf = v.Notes; status = ""; }
            if (status.Length > 0) Dim(l, status);
        }

        void SaveNotes(Pawn pawn)
        {
            var minds = RWLBridge.Minds;
            MindRecord rec;
            if (!minds.Records.TryGetValue(pawn.thingIDNumber, out rec) || rec.MindJson == null) return;
            var mind = SafeParse(rec.MindJson) as JObject;
            if (mind == null) { status = UiUtil.T("RWL_MindNotesConflict", "unreadable mind"); return; }
            MindNotes.Apply(mind, notesBuf);
            int newRev;
            switch (minds.Put(pawn.thingIDNumber, rec.Rev, mind, out newRev))
            {
                case MindBook.PutResult.Ok: status = UiUtil.T("RWL_MindNotesSaved"); notesRev = newRev; break;
                case MindBook.PutResult.TooLarge: status = UiUtil.T("RWL_MindNotesConflict", "mind is full (" + MindBook.MaxMindBytes + " bytes)"); break;
                default: status = UiUtil.T("RWL_MindNotesConflict", "the agent changed the mind; try again"); break;
            }
        }
    }

    /// <summary>Adds the Mind tab to every humanlike pawn def at startup.</summary>
    [StaticConstructorOnStartup]
    public static class MindTabInjector
    {
        static MindTabInjector()
        {
            try
            {
                var tab = typeof(ITab_Pawn_RWLMind);
                foreach (var def in DefDatabase<ThingDef>.AllDefs.Where(d => d.race != null && d.race.Humanlike).ToList())
                {
                    if (def.inspectorTabs == null) def.inspectorTabs = new List<System.Type>();
                    if (!def.inspectorTabs.Contains(tab)) def.inspectorTabs.Add(tab);
                    if (def.inspectorTabsResolved == null) def.inspectorTabsResolved = new List<InspectTabBase>();
                    if (!def.inspectorTabsResolved.Any(t => t is ITab_Pawn_RWLMind))
                        def.inspectorTabsResolved.Add(InspectTabManager.GetSharedInstance(tab));
                }
            }
            catch (System.Exception e) { Log.Error("[RimWorldLLM] could not add the Mind tab: " + e); }
        }
    }
}
