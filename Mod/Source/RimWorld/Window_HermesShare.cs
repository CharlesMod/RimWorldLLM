using System;
using System.Collections;
using System.IO;
using HermesVoice.Bridge;
using RimWorld;
using UnityEngine;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Preview of a share card with Copy text / Save image. The card is drawn with immediate-mode GUI in a fixed rect;
    /// Save image reads that rect back from the screen at the end of the frame (ReadPixels) and writes a PNG.
    /// </summary>
    public class Window_HermesShare : Window
    {
        const float CardWidth = 560f, StripHeight = 56f;
        static readonly Color CardColor = new Color(0.09f, 0.09f, 0.11f, 1f);

        readonly ShareDoc doc;
        readonly bool footer;
        readonly CardLayout lay;
        Texture2D cardTex;
        bool pendingSave;
        bool capturing;
        string status = "";

        public override Vector2 InitialSize { get { return new Vector2(lay.Width, lay.Height + StripHeight); } }
        protected override float Margin { get { return 0f; } }

        public Window_HermesShare(ShareDoc doc, bool footer)
        {
            this.doc = doc;
            this.footer = footer;
            doCloseX = false;
            doWindowBackground = false;
            drawShadow = false;
            draggable = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            forcePause = false;
            Text.Font = GameFont.Small;
            lay = ShareCardLayout.Compute(doc, footer, Mathf.Min(CardWidth, UI.screenWidth - 20f), Text.LineHeight,
                s => Text.CalcSize(s).x, Mathf.Max(200f, UI.screenHeight - StripHeight - 40f));
        }

        public override void PostClose()
        {
            if (cardTex != null) { UnityEngine.Object.Destroy(cardTex); cardTex = null; }
        }

        static Texture2D RoundedTexture(int w, int h, int radius, Color c)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            Color32 fill = c;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int dx = x < radius ? radius - x : (x >= w - radius ? x - (w - radius - 1) : 0);
                    int dy = y < radius ? radius - y : (y >= h - radius ? y - (h - radius - 1) : 0);
                    bool inside = dx * dx + dy * dy <= radius * radius;
                    px[y * w + x] = inside ? fill : new Color32(0, 0, 0, 0);
                }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }

        static void Colors(ShareLine l, out Color bg, out Color border, out Color text)
        {
            if (l.PawnId == 0)
            {
                bg = new Color(0.17f, 0.15f, 0.10f, 1f); border = new Color(0.9f, 0.75f, 0.45f); text = new Color(0.95f, 0.9f, 0.8f);
                return;
            }
            SpeechBubbles.PawnColors(l.PawnId, out bg, out border, out text);
            bg.a = 1f;
        }

        void DrawCard(Rect card)
        {
            if (cardTex == null) cardTex = RoundedTexture((int)card.width, (int)card.height, 18, CardColor);
            GUI.color = Color.white;
            GUI.DrawTexture(card, cardTex);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            bool wrap = Text.WordWrap;
            Text.WordWrap = false;

            float hy = card.y + ShareCardLayout.Pad;
            for (int i = 0; i < lay.HeaderLines.Count; i++)
            {
                GUI.color = i == 0 ? new Color(0.85f, 0.85f, 0.9f) : new Color(0.65f, 0.65f, 0.7f);
                Widgets.Label(new Rect(card.x + ShareCardLayout.Pad, hy, card.width, Text.LineHeight), lay.HeaderLines[i]);
                hy += Text.LineHeight;
            }

            float bx = card.x + ShareCardLayout.Pad + ShareCardLayout.Portrait + ShareCardLayout.Gap;
            foreach (var b in lay.Blocks)
            {
                Color bg, border, txt;
                Colors(b.Line, out bg, out border, out txt);
                float y = card.y + b.Y;
                var portrait = new Rect(card.x + ShareCardLayout.Pad, y, ShareCardLayout.Portrait, ShareCardLayout.Portrait);
                DrawPortrait(portrait, b.Line, border);
                var bubble = new Rect(bx, y, card.xMax - ShareCardLayout.Pad - bx, b.Height);
                GUI.color = bg; GUI.DrawTexture(bubble, BaseContent.WhiteTex);
                GUI.color = border; GUI.DrawTexture(new Rect(bubble.x, bubble.y, 4f, bubble.height), BaseContent.WhiteTex);
                if (b.Line.Thought) { Widgets.DrawBox(bubble, 1); }
                float ty = bubble.y + ShareCardLayout.Inner;
                if (!string.IsNullOrWhiteSpace(b.Line.Speaker))
                {
                    GUI.color = border;
                    Widgets.Label(new Rect(bubble.x + ShareCardLayout.Inner + 4f, ty, bubble.width, Text.LineHeight),
                        b.Line.Thought ? b.Line.Speaker + " (" + UiUtil.T("HV_Share_Thinking") + ")" : b.Line.Speaker);
                    ty += Text.LineHeight;
                }
                GUI.color = b.Line.Thought ? new Color(txt.r, txt.g, txt.b, 0.8f) : txt;
                foreach (string s in b.Wrapped)
                {
                    Widgets.Label(new Rect(bubble.x + ShareCardLayout.Inner + 4f, ty, bubble.width, Text.LineHeight), s);
                    ty += Text.LineHeight;
                }
            }
            if (lay.Dropped > 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(bx, card.y + lay.Blocks[lay.Blocks.Count - 1].Y + lay.Blocks[lay.Blocks.Count - 1].Height + 4f, card.width, Text.LineHeight),
                    UiUtil.T("HV_Share_More", lay.Dropped));
            }
            if (lay.FooterY >= 0f)
            {
                GUI.color = new Color(0.55f, 0.55f, 0.6f);
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(card.x, card.y + lay.FooterY, card.width, Text.LineHeight), ShareFormat.CardFooter);
                Text.Anchor = TextAnchor.UpperLeft;
            }
            Text.WordWrap = wrap;
            GUI.color = Color.white;
        }

        static void DrawPortrait(Rect r, ShareLine l, Color border)
        {
            Pawn p = l.PawnId != 0 ? Routes.FindPawn(l.PawnId) : null;
            GUI.color = new Color(border.r, border.g, border.b, 0.35f);
            GUI.DrawTexture(r, BaseContent.WhiteTex);
            GUI.color = Color.white;
            if (p != null)
            {
                try { GUI.DrawTexture(r, PortraitsCache.Get(p, new Vector2(r.width, r.height), Rot4.South, default(Vector3), 1f)); }
                catch (Exception) { }
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            var card = new Rect(0f, 0f, lay.Width, lay.Height);
            DrawCard(card);

            if (pendingSave && !capturing && Event.current.type == EventType.Repaint)
            {
                pendingSave = false;
                capturing = true;
                float sc = Prefs.UIScale;
                // Window and card are in UI units with the origin at the screen's top-left; ReadPixels wants pixels from the bottom-left.
                var px = new Rect(windowRect.x * sc, Screen.height - (windowRect.y + card.height) * sc, card.width * sc, card.height * sc);
                Current.Root.StartCoroutine(Capture(px));
            }

            var strip = new Rect(0f, lay.Height, lay.Width, StripHeight);
            Widgets.DrawWindowBackground(strip);
            float bw = (strip.width - 40f) / 3f;
            float by = strip.y + 8f, bh = 30f;
            if (Widgets.ButtonText(new Rect(10f, by, bw, bh), UiUtil.T("HV_Share_CopyText")))
            {
                GUIUtility.systemCopyBuffer = ShareFormat.Text(doc, footer);
                status = UiUtil.T("HV_Share_Copied");
                Messages.Message(status, MessageTypeDefOf.TaskCompletion, false);
            }
            if (Widgets.ButtonText(new Rect(20f + bw, by, bw, bh), UiUtil.T("HV_Share_SaveImage")) && !capturing) pendingSave = true;
            if (Widgets.ButtonText(new Rect(30f + 2 * bw, by, bw, bh), UiUtil.T("HV_Share_Close"))) Close();
            if (status.Length > 0)
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(10f, strip.y + 38f, strip.width - 20f, 18f), status);
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
        }

        IEnumerator Capture(Rect px)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                int w = Mathf.Max(1, Mathf.RoundToInt(px.width)), h = Mathf.Max(1, Mathf.RoundToInt(px.height));
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(Mathf.Round(px.x), Mathf.Round(px.y), w, h), 0, 0);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.Destroy(tex);

                string dir = ShareService.SharesFolder();
                Directory.CreateDirectory(dir);
                string name = ShareFormat.NextFileName(ShareService.ColonyName(), doc.Date, f => File.Exists(Path.Combine(dir, f)));
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, png);
                status = UiUtil.T("HV_Share_Saved", name);
                Messages.Message(status, MessageTypeDefOf.TaskCompletion, false);
                Find.WindowStack.Add(new Dialog_MessageBox(UiUtil.T("HV_Share_SavedBody", path), UiUtil.T("HV_Share_OpenFolder"),
                    () => ShareService.OpenFolder(dir), UiUtil.T("HV_Share_Close")));
            }
            catch (Exception ex)
            {
                status = UiUtil.T("HV_Share_Failed", ex.Message);
                Log.Warning("[HermesVoice] share image failed: " + ex);
            }
            capturing = false;
        }
    }
}
