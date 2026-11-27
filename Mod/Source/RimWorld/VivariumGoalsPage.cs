using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>The "Goals" page of the Voices window: the narrator's goals board and action list, editable by the player.</summary>
    public class VivariumGoalsPage
    {
        Vector2 _scroll;
        string _newGoal = "", _newOrder = "", _plan;
        bool _newLong;
        int _planRev = -1;
        JObject _board; int _boardRev = -1;
        JObject _actions; int _actionsSeq = -1;

        public void Draw(Rect inRect)
        {
            var v = VivariumGame.State;
            if (_board == null || _boardRev != v.Goals.Rev) { _board = v.Goals.ToJson(); _boardRev = v.Goals.Rev; }
            if (_actions == null || _actionsSeq != v.Actions.LastSeq) { _actions = v.Actions.Since(System.Math.Max(0, v.Actions.LastSeq - 30)); _actionsSeq = v.Actions.LastSeq; }
            if (_planRev != _boardRev) { _plan = (string)_board["plan"] ?? ""; _planRev = _boardRev; }

            var view = new Rect(0f, 0f, inRect.width - 18f, _contentH);
            Widgets.BeginScrollView(inRect, ref _scroll, view);
            var l = new Listing_Standard();
            l.Begin(new Rect(0f, 0f, view.width, 99999f));

            // Switch + hint
            bool playing = v.Playing;
            l.CheckboxLabeled(UiUtil.T("RWL_Viv_PagePlaying"), ref playing, UiUtil.T("RWL_Viv_SwitchTip"));
            if (playing != v.Playing) VivariumSwitch.SetPlaying(playing, true);
            l.Gap(6f);

            // Plan
            Text.Font = GameFont.Medium; l.Label(UiUtil.T("RWL_Viv_Plan")); Text.Font = GameFont.Small;
            string np = l.TextEntry(_plan, 3);
            _plan = np;
            if (l.ButtonText(UiUtil.T("RWL_Viv_PlanSave"))) v.Goals.SetPlan(_plan);
            l.GapLine();

            // Goals
            Text.Font = GameFont.Medium; l.Label(UiUtil.T("RWL_Viv_Goals")); Text.Font = GameFont.Small;
            var goals = _board["goals"] as JArray ?? new JArray();
            if (goals.Count == 0) l.Label(UiUtil.T("RWL_Viv_GoalsEmpty"));
            foreach (var g in goals)
            {
                string id = (string)g["id"], status = (string)g["status"];
                string head = UiUtil.T("RWL_Viv_GoalHead", UiUtil.T("RWL_Viv_Horizon_" + (string)g["horizon"]), UiUtil.T("RWL_Viv_Status_" + status), UiUtil.T("RWL_Viv_By_" + (string)g["by"]));
                float h = Mathf.Max(Text.CalcHeight((string)g["text"], l.ColumnWidth - 300f), 26f);
                var row = l.GetRect(h);
                GUI.color = status == "open" ? Color.white : Color.gray;
                Widgets.Label(new Rect(row.x, row.y, row.width - 300f, h), (string)g["text"]);
                GUI.color = Color.gray;
                Widgets.Label(new Rect(row.xMax - 296f, row.y, 120f, 26f), head);
                GUI.color = Color.white;
                float bx = row.xMax - 170f;
                if (status == "open")
                {
                    if (Widgets.ButtonText(new Rect(bx, row.y, 54f, 24f), UiUtil.T("RWL_Viv_Done"))) v.Goals.SetGoalStatus(id, "done");
                    if (Widgets.ButtonText(new Rect(bx + 56f, row.y, 54f, 24f), UiUtil.T("RWL_Viv_Drop"))) v.Goals.SetGoalStatus(id, "dropped");
                }
                else if (Widgets.ButtonText(new Rect(bx, row.y, 110f, 24f), UiUtil.T("RWL_Viv_Reopen"))) v.Goals.SetGoalStatus(id, "open");
                if (Widgets.ButtonText(new Rect(bx + 112f, row.y, 56f, 24f), UiUtil.T("RWL_Viv_Remove"))) v.Goals.RemoveGoal(id);
            }
            l.Gap(4f);
            _newGoal = l.TextEntry(_newGoal);
            var addRow = l.GetRect(28f);
            Widgets.CheckboxLabeled(new Rect(addRow.x, addRow.y, 220f, 28f), UiUtil.T("RWL_Viv_GoalLong"), ref _newLong);
            if (Widgets.ButtonText(new Rect(addRow.x + 230f, addRow.y, 140f, 28f), UiUtil.T("RWL_Viv_GoalAdd")) && !string.IsNullOrWhiteSpace(_newGoal))
            {
                v.Goals.AddGoal(_newGoal.Trim(), _newLong ? "long" : "short");
                _newGoal = "";
            }
            l.GapLine();

            // Standing orders
            Text.Font = GameFont.Medium; l.Label(UiUtil.T("RWL_Viv_Orders")); Text.Font = GameFont.Small;
            var orders = _board["orders"] as JArray ?? new JArray();
            if (orders.Count == 0) l.Label(UiUtil.T("RWL_Viv_OrdersEmpty"));
            foreach (var o in orders)
            {
                var row = l.GetRect(26f);
                Widgets.Label(new Rect(row.x, row.y, row.width - 70f, 26f), (string)o["text"] + "  (" + UiUtil.T("RWL_Viv_By_" + (string)o["by"]) + ")");
                if (Widgets.ButtonText(new Rect(row.xMax - 66f, row.y, 66f, 24f), UiUtil.T("RWL_Viv_Remove"))) v.Goals.RemoveOrder((string)o["id"]);
            }
            _newOrder = l.TextEntry(_newOrder);
            if (l.ButtonText(UiUtil.T("RWL_Viv_OrderAdd")) && !string.IsNullOrWhiteSpace(_newOrder))
            {
                v.Goals.AddOrder(_newOrder.Trim());
                _newOrder = "";
            }
            l.GapLine();

            // The narrator's own log, then the action list
            Text.Font = GameFont.Medium; l.Label(UiUtil.T("RWL_Viv_Log")); Text.Font = GameFont.Small;
            var log = _board["log"] as JArray ?? new JArray();
            for (int i = log.Count - 1; i >= 0 && i >= log.Count - 20; i--)
                l.Label(UiUtil.DateOf((int)log[i]["tick"]) + "  " + (string)log[i]["text"]);
            l.GapLine();
            Text.Font = GameFont.Medium; l.Label(UiUtil.T("RWL_Viv_Actions")); Text.Font = GameFont.Small;
            var acts = _actions["actions"] as JArray ?? new JArray();
            if (acts.Count == 0) l.Label(UiUtil.T("RWL_Viv_ActionsEmpty"));
            for (int i = acts.Count - 1; i >= 0; i--)
                l.Label(UiUtil.DateOf((int)acts[i]["tick"]) + "  " + (string)acts[i]["verb"] + ": " + (string)acts[i]["note"]);

            _contentH = l.CurHeight + 12f;
            l.End();
            Widgets.EndScrollView();
        }

        float _contentH = 800f;
    }
}
