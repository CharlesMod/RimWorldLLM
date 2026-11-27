using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// In-game .gguf file browser: folders and model files, one pick. The listing is read only when the folder changes.
    /// All filesystem logic lives in <see cref="FileBrowserModel"/>.
    /// </summary>
    public class Window_FileBrowser : Window
    {
        const float RowH = 26f;

        readonly Action<string> _onPick;
        FileBrowserModel.Listing _listing;
        List<FileBrowserModel.Entry> _roots;   // non-null while showing "This computer"
        string _selected;
        Vector2 _scroll;

        public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(UI.screenWidth - 40f, 640f), Mathf.Min(UI.screenHeight - 80f, 560f)); } }

        public Window_FileBrowser(string startPath, Action<string> onPick)
        {
            _onPick = onPick;
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            optionalTitle = UiUtil.T("RWL_FileBrowser_Title");
            string dir = FileBrowserModel.StartDir(startPath);
            if (dir == null) ShowRoots(); else Navigate(dir);
        }

        void Navigate(string dir)
        {
            _roots = null;
            _listing = FileBrowserModel.List(dir);
            _selected = null;
            _scroll = Vector2.zero;
        }

        void ShowRoots()
        {
            _roots = FileBrowserModel.Roots();
            _listing = null;
            _selected = null;
            _scroll = Vector2.zero;
        }

        void Pick(string path)
        {
            Close();
            if (_onPick != null) _onPick(path);
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Top bar: Up, This computer, current path.
            var top = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            string parent = _listing != null ? _listing.Parent : null;
            bool canUp = _roots == null; // from a root folder, Up goes to the roots list
            var up = new Rect(top.x, top.y, 70f, 28f);
            if (Widgets.ButtonText(up, UiUtil.T("RWL_FileBrowser_Up"), true, true, canUp))
            {
                if (parent != null) Navigate(parent); else ShowRoots();
            }
            TooltipHandler.TipRegion(up, UiUtil.T("RWL_FileBrowser_UpTip"));
            var rootsBtn = new Rect(up.xMax + 6f, top.y, 140f, 28f);
            if (Widgets.ButtonText(rootsBtn, UiUtil.T("RWL_FileBrowser_Roots"))) ShowRoots();
            TooltipHandler.TipRegion(rootsBtn, UiUtil.T("RWL_FileBrowser_RootsTip"));
            var pathRect = new Rect(rootsBtn.xMax + 8f, top.y + 3f, top.xMax - rootsBtn.xMax - 8f, 26f);
            string shown = _roots != null ? UiUtil.T("RWL_FileBrowser_Roots") : _listing.Path;
            Widgets.Label(pathRect, shown);
            TooltipHandler.TipRegion(pathRect, shown);

            // Footer: selection, Select, Cancel.
            var foot = new Rect(inRect.x, inRect.yMax - 32f, inRect.width, 30f);
            var cancel = new Rect(foot.xMax - 110f, foot.y, 110f, 30f);
            if (Widgets.ButtonText(cancel, UiUtil.T("RWL_FileBrowser_Cancel"))) { Close(); return; }
            TooltipHandler.TipRegion(cancel, UiUtil.T("RWL_FileBrowser_CancelTip"));
            var select = new Rect(cancel.x - 116f, foot.y, 110f, 30f);
            if (Widgets.ButtonText(select, UiUtil.T("RWL_FileBrowser_Select"), true, true, _selected != null)) { Pick(_selected); return; }
            TooltipHandler.TipRegion(select, UiUtil.T("RWL_FileBrowser_SelectTip"));
            var selRect = new Rect(foot.x, foot.y + 4f, select.x - foot.x - 8f, 24f);
            if (_selected != null)
            {
                string name = System.IO.Path.GetFileName(_selected);
                Widgets.Label(selRect, name);
                TooltipHandler.TipRegion(selRect, _selected);
            }

            // List.
            var listRect = new Rect(inRect.x, top.yMax + 6f, inRect.width, foot.y - top.yMax - 12f);
            Widgets.DrawBoxSolid(listRect, new Color(0f, 0f, 0f, 0.25f));
            if (_roots != null) DrawRoots(listRect.ContractedBy(2f));
            else DrawListing(listRect.ContractedBy(2f));
        }

        void DrawRoots(Rect outRect)
        {
            var view = new Rect(0f, 0f, outRect.width - 18f, _roots.Count * RowH);
            Widgets.BeginScrollView(outRect, ref _scroll, view);
            string go = null;
            for (int i = 0; i < _roots.Count; i++)
            {
                var r = new Rect(0f, i * RowH, view.width, RowH);
                if (i % 2 == 1) Widgets.DrawBoxSolid(r, new Color(1f, 1f, 1f, 0.04f));
                Widgets.DrawHighlightIfMouseover(r);
                Widgets.Label(new Rect(r.x + 6f, r.y + 2f, r.width - 12f, RowH - 2f), "[ ] " + _roots[i].Name);
                if (Widgets.ButtonInvisible(r)) go = _roots[i].Path;
            }
            Widgets.EndScrollView();
            if (go != null) Navigate(go);
        }

        void DrawListing(Rect outRect)
        {
            var entries = _listing.Entries;
            bool hasError = !string.IsNullOrEmpty(_listing.Error);
            float extra = hasError || entries.Count == 0 ? RowH : 0f;
            var view = new Rect(0f, 0f, outRect.width - 18f, entries.Count * RowH + extra);
            Widgets.BeginScrollView(outRect, ref _scroll, view);
            string enter = null, pick = null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var r = new Rect(0f, i * RowH, view.width, RowH);
                if (e.Path == _selected) Widgets.DrawBoxSolid(r, new Color(0.25f, 0.5f, 0.9f, 0.35f));
                else if (i % 2 == 1) Widgets.DrawBoxSolid(r, new Color(1f, 1f, 1f, 0.04f));
                Widgets.DrawHighlightIfMouseover(r);
                if (e.IsDir)
                {
                    Widgets.Label(new Rect(r.x + 6f, r.y + 2f, r.width - 12f, RowH - 2f), "[ ] " + e.Name);
                }
                else
                {
                    const float sizeW = 90f;
                    Widgets.Label(new Rect(r.x + 6f, r.y + 2f, r.width - sizeW - 18f, RowH - 2f), e.Name);
                    var old = Text.Anchor;
                    Text.Anchor = TextAnchor.UpperRight;
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(r.xMax - sizeW - 6f, r.y + 2f, sizeW, RowH - 2f), Window_EngineSetup.Bytes(e.Size));
                    GUI.color = Color.white;
                    Text.Anchor = old;
                    TooltipHandler.TipRegion(r, e.Path);
                }
                if (Widgets.ButtonInvisible(r))
                {
                    if (e.IsDir) enter = e.Path;
                    else if (Event.current.clickCount >= 2 && _selected == e.Path) pick = e.Path;
                    else _selected = e.Path;
                }
            }
            if (hasError || entries.Count == 0)
            {
                GUI.color = hasError ? new Color(0.9f, 0.4f, 0.4f) : Color.gray;
                Widgets.Label(new Rect(6f, entries.Count * RowH + 2f, view.width - 12f, RowH),
                    hasError ? UiUtil.T("RWL_FileBrowser_Error", _listing.Error) : UiUtil.T("RWL_FileBrowser_Empty"));
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
            // Navigation and picking happen after the list is drawn, so the listing is never swapped mid-loop.
            if (enter != null) Navigate(enter);
            else if (pick != null) Pick(pick);
        }
    }
}
