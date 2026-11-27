using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using RimWorldLLM;

// Offline preview helper: reads a scene request (sample lines, pawn head positions, character advance tables
// measured by the preview script), runs the REAL BubbleLayout code and writes where every balloon, tail and
// thought dot goes. Used by Mod/Textures/src/preview.py. Usage: PreviewEmit <request.json> <layout.json>
static class Program
{
    static int Main(string[] args)
    {
        var req = JObject.Parse(File.ReadAllText(args[0]));
        float sw = (float)req["screen"][0], sh = (float)req["screen"][1];
        var small = Measurer(req["fonts"]["small"]); var tiny = Measurer(req["fonts"]["tiny"]);
        float lineSmall = (float)req["fonts"]["small"]["lineHeight"], lineTiny = (float)req["fonts"]["tiny"]["lineHeight"];
        var kinds = new List<BubbleKind>(); var texts = new List<string>(); var targets = new List<float[]>();
        var sizes = new List<float[]>(); var lineSets = new List<List<string>>(); var rects = new List<BRect>();
        foreach (JObject b in req["bubbles"])
        {
            string clean;
            var kind = BubbleLayout.ChooseKind((string)b["kind"], (string)b["intent"], (string)b["text"], b.Value<bool?>("combat") ?? false, out clean);
            string text = (b.Value<bool?>("caps") ?? false) ? clean.ToUpperInvariant() : clean;
            bool sm = BubbleLayout.SmallText(kind);
            var measure = sm ? tiny : small;
            var lines = BubbleLayout.Wrap(text, measure, BubbleMetrics.MaxTextWidth, BubbleMetrics.MaxLines);
            float widest = 0; foreach (var l in lines) widest = Math.Max(widest, measure(l));
            float tw = widest + 2f, th = lines.Count * (sm ? lineTiny : lineSmall);
            float w, h; BubbleLayout.BalloonSize(kind, tw, th, out w, out h);
            float tx = (float)b["tx"], ty = (float)b["ty"] - 4f;
            kinds.Add(kind); lineSets.Add(lines); targets.Add(new[] { tx, ty }); sizes.Add(new[] { w, h });
            rects.Add(BubbleLayout.DefaultRect(w, h, tx, ty, new BRect(4, 4, sw - 8, sh - 8 - 40)));
        }
        var tailArr = new TailInfo[rects.Count];
        BubbleLayout.Arrange(rects, targets.ConvertAll(t => t[0]), targets.ConvertAll(t => t[1]), kinds,
                             new BRect(4, 4, sw - 8, sh - 8 - 40), 3f, tailArr);
        var outArr = new JArray();
        for (int i = 0; i < rects.Count; i++)
        {
            var kind = kinds[i]; var r = rects[i];
            var tail = tailArr[i];
            var o = new JObject
            {
                ["kind"] = kind.ToString(),
                ["rect"] = new JArray(r.X, r.Y, r.W, r.H),
                ["lines"] = new JArray(lineSets[i]),
                ["small"] = BubbleLayout.SmallText(kind),
                ["scale"] = BubbleLayout.CornerScale(kind, r.W, r.H),
                ["target"] = new JArray(targets[i][0], targets[i][1]),
                ["tail"] = new JObject { ["side"] = tail.Side.ToString(), ["ax"] = tail.AnchorX, ["ay"] = tail.AnchorY, ["angle"] = tail.Angle, ["length"] = tail.Length }
            };
            if (kind == BubbleKind.Thought && tail.Has)
            {
                var xs = new float[3]; var ys = new float[3]; var d = new float[3];
                BubbleLayout.ThoughtDots(tail, xs, ys, d);
                var dots = new JArray();
                for (int k = 0; k < 3; k++) dots.Add(new JArray(xs[k], ys[k], d[k]));
                o["dots"] = dots;
            }
            outArr.Add(o);
        }
        // The numbers the drawing code needs, straight from BubbleMetrics.
        var kindInfo = new JObject();
        foreach (BubbleKind k in Enum.GetValues(typeof(BubbleKind)))
            kindInfo[k.ToString()] = new JObject { ["mid"] = BubbleMetrics.MidSrc(k), ["tiled"] = BubbleMetrics.Tiled(k), ["margin"] = BubbleMetrics.Margin(k), ["padX"] = BubbleMetrics.PadX(k), ["padY"] = BubbleMetrics.PadY(k) };
        File.WriteAllText(args[1], new JObject { ["bubbles"] = outArr, ["kinds"] = kindInfo,
            ["metrics"] = new JObject { ["unit"] = BubbleMetrics.Unit, ["border"] = BubbleMetrics.BorderSrc, ["tailW"] = BubbleMetrics.TailW, ["tailH"] = BubbleMetrics.TailH, ["tailTipY"] = BubbleMetrics.TailTipYSrc, ["stub"] = BubbleMetrics.TailStub, ["dot"] = BubbleMetrics.DotSize, ["ink"] = BubbleMetrics.InkSrc } }.ToString());
        return 0;
    }

    static Func<string, float> Measurer(JToken font)
    {
        var adv = new Dictionary<char, float>();
        foreach (JProperty p in font["advance"]) adv[p.Name[0]] = (float)p.Value;
        float def = (float)font["default"];
        return s => { float w = 0; foreach (char c in s) w += adv.TryGetValue(c, out var a) ? a : def; return w; };
    }
}
