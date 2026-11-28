using System;
using System.Collections.Generic;
using RimWorldLLM.Bridge;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>
    /// Settings (Model, local engine): "Share this PC's engine on my network", what a guest sees (the engine's name,
    /// its LAN and Tailscale addresses, a QR of the hello URL) and the Paired PCs list with Remove.
    /// </summary>
    public static class EngineShareUi
    {
        static readonly Color Green = new Color(0.5f, 0.85f, 0.5f), Red = new Color(1f, 0.45f, 0.4f), Amber = new Color(1f, 0.8f, 0.4f);
        static string _qrUrl;
        static Texture2D _qrTex;
        static string _picked;
        static float _clientsAt = -999f;

        public static void Draw(Listing_Standard l, RimWorldLLMSettings s, EngineStatus st)
        {
            EngineShareManager.NoteStatus(st);
            l.Gap(6f);
            bool before = s.shareEngine;
            l.CheckboxLabeled(UiUtil.T("RWL_Share_Toggle"), ref s.shareEngine, UiUtil.T("RWL_Share_ToggleTip"));
            if (before != s.shareEngine)
            {
                try { RimWorldLLMMod.Instance.WriteSettings(); } catch { }
                EngineShareManager.SettingsChanged();
                _clientsAt = -999f;
            }
            GUI.color = Color.gray;
            l.Label(UiUtil.T("RWL_Share_Exposure"));
            GUI.color = Color.white;

            var host = EngineShareManager.Host;
            var view = host.View(s.shareEngine, EngineManager.LocalSource);
            string name = EngineShareManager.EngineName;
            GUI.color = view == ShareView.On ? Green : view == ShareView.Failed || view == ShareView.Unsupported ? Red : view == ShareView.OnNoNetwork ? Amber : Color.gray;
            l.Label(UiUtil.T(ShareStatus.LineKey(view), name, host.Error ?? ""));
            GUI.color = Color.white;

            if (view == ShareView.On) Addresses(l, host.Addresses);
            if (s.shareEngine) WaitingRequests(l, host);
            if (s.shareEngine && (view == ShareView.On || view == ShareView.OnNoNetwork)) PairedPcs(l, host);
        }

        static void Addresses(Listing_Standard l, List<string> hostPorts)
        {
            var rows = ShareStatus.Addresses(hostPorts);
            if (rows.Count == 0) return;
            if (_picked == null || !rows.Exists(r => r.HelloUrl == _picked)) _picked = rows[0].HelloUrl;
            const float qrSize = 150f;
            float listH = Math.Max(qrSize, rows.Count * 28f + 26f);
            var area = l.GetRect(listH);
            var left = new Rect(area.x, area.y, area.width - qrSize - 16f, area.height);
            Widgets.Label(new Rect(left.x, left.y, left.width, 24f), UiUtil.T("RWL_Share_SeenAt"));
            float y = left.y + 26f;
            foreach (var r in rows)
            {
                var row = new Rect(left.x, y, left.width, 26f);
                bool on = r.HelloUrl == _picked;
                if (on) Widgets.DrawHighlight(row);
                string badge = UiUtil.T(r.Kind == NetKind.Tailscale ? "RWL_Share_BadgeTailscale" : "RWL_Share_BadgeLan");
                Widgets.Label(new Rect(row.x + 6f, row.y + 3f, 90f, 24f), badge);
                Widgets.Label(new Rect(row.x + 100f, row.y + 3f, row.width - 100f, 24f), r.HostPort);
                if (Widgets.ButtonInvisible(row)) _picked = r.HelloUrl;
                TooltipHandler.TipRegion(row, UiUtil.T("RWL_Share_RowTip"));
                y += 28f;
            }
            var qrRect = new Rect(area.xMax - qrSize, area.y, qrSize, qrSize);
            var tex = Qr(_picked);
            if (tex != null) GUI.DrawTexture(qrRect, tex, ScaleMode.ScaleToFit);
            TooltipHandler.TipRegion(qrRect, UiUtil.T("RWL_Share_QrTip", _picked));
        }

        static Texture2D Qr(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (url == _qrUrl && _qrTex != null) return _qrTex;
            try
            {
                var q = QrCode.Encode(url, QrEcc.Medium);
                const int scale = 4;
                byte[] px = QrCode.ToRgba(q, scale);
                int side = (q.Size + 8) * scale;
                var tex = new Texture2D(side, side, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                tex.LoadRawTextureData(px);
                tex.Apply(false);
                if (_qrTex != null) UnityEngine.Object.Destroy(_qrTex);
                _qrTex = tex;
                _qrUrl = url;
                return tex;
            }
            catch { return null; }
        }

        // Requests waiting for an answer, quiet ones too (a source denied once raises no window for the rest of the
        // engine's life; a mistaken Deny is undone here).
        static void WaitingRequests(Listing_Standard l, ShareHost host)
        {
            var list = host.Book.Waiting;
            if (list.Count == 0) return;
            l.Gap(4f);
            l.Label(UiUtil.T("RWL_Share_WaitingTitle"));
            foreach (var it in list)
            {
                var row = l.GetRect(30f);
                Widgets.Label(new Rect(row.x + 6f, row.y + 4f, row.width - 260f, 26f), UiUtil.T("RWL_Share_WaitingRow", it.Name, PairingSession.FormatCode(it.Code), it.From));
                bool can = it.State == PairAskState.Pending;
                if (Widgets.ButtonText(new Rect(row.xMax - 250f, row.y, 120f, 28f), UiUtil.T("RWL_PairReq_Allow"), true, true, can)) EngineShareManager.Answer(it.Key, true);
                if (Widgets.ButtonText(new Rect(row.xMax - 120f, row.y, 120f, 28f), UiUtil.T("RWL_PairReq_Deny"), true, true, can)) EngineShareManager.Answer(it.Key, false);
            }
            GUI.color = Color.gray;
            l.Label(UiUtil.T("RWL_Share_WaitingNote"));
            GUI.color = Color.white;
        }

        static void PairedPcs(Listing_Standard l, ShareHost host)
        {
            if (Time.realtimeSinceStartup - _clientsAt > 10f) { _clientsAt = Time.realtimeSinceStartup; EngineShareManager.RefreshClients(); }
            l.Gap(4f);
            l.Label(UiUtil.T("RWL_Share_PairedTitle"));
            var list = host.Clients;
            if (list == null)
            {
                GUI.color = Color.gray;
                l.Label(UiUtil.T(host.ClientsError == "unsupported" ? "RWL_Share_PairedUnsupported" : "RWL_Share_PairedLoading"));
                GUI.color = Color.white;
                return;
            }
            if (list.Count == 0) { GUI.color = Color.gray; l.Label(UiUtil.T("RWL_Share_PairedNone")); GUI.color = Color.white; return; }
            foreach (var c in list)
            {
                var row = l.GetRect(30f);
                string since = c.Created ?? "";
                DateTime t;
                if (DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) since = t.ToLocalTime().ToString("yyyy-MM-dd");
                Widgets.Label(new Rect(row.x + 6f, row.y + 4f, row.width - 140f, 26f), UiUtil.T("RWL_Share_PairedRow", c.Name, since));
                var b = new Rect(row.xMax - 120f, row.y, 120f, 28f);
                TooltipHandler.TipRegion(b, UiUtil.T("RWL_Share_RemoveTip"));
                if (Widgets.ButtonText(b, UiUtil.T("RWL_Share_Remove"))) EngineShareManager.Remove(c.Id);
            }
            if (!string.IsNullOrEmpty(host.ClientsError)) { GUI.color = Red; l.Label(UiUtil.T("RWL_Share_PairedError", host.ClientsError)); GUI.color = Color.white; }
        }
    }
}
