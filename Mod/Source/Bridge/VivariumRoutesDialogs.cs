using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    public static partial class VivariumRoutes
    {
        // POST /act/choose {dialog, option (index or label) | text (a name, or {field: name}), why}
        static ApiResult Choose(RouteContext c, Func<Vivarium> vivarium, Func<IVivariumHands> hands)
        {
            return Act(c, vivarium, hands, "choose", b => ChoosePlan(b, vivarium));
        }

        static Plan ChoosePlan(JObject b, Func<Vivarium> vivarium)
        {
            string id = Str(b, "dialog", 1, 64);
            JToken opt = b["option"], text = b["text"];
            bool hasOpt = opt != null && opt.Type != JTokenType.Null, hasText = text != null && text.Type != JTokenType.Null;
            if (!hasOpt && !hasText) throw new ArgError("give option (an index or a label) or text (a name)");
            if (hasOpt && opt.Type != JTokenType.Integer && opt.Type != JTokenType.String) throw new ArgError("option must be an index or a label");
            Dictionary<string, string> texts = null;
            if (hasText)
            {
                texts = new Dictionary<string, string>();
                if (text.Type == JTokenType.String) texts["name"] = text.Value<string>().Trim();
                else if (text.Type == JTokenType.Object)
                    foreach (var p in ((JObject)text).Properties())
                    {
                        if (p.Value.Type != JTokenType.String) throw new ArgError("text." + p.Name + " must be a string");
                        texts[p.Name] = p.Value.Value<string>().Trim();
                    }
                else throw new ArgError("text must be a string or an object of fields");
                foreach (var t in texts.Values) if (t.Length < 1 || t.Length > 60) throw new ArgError("text must be 1..60 chars");
            }
            DialogRecord rec = null; int index = -1; string label = null;
            return new Plan
            {
                What = () => "answer the dialog " + id,
                Gate = v =>
                {
                    rec = v.Dialogs.Get(id);
                    if (rec == null) return HandResult.No("no open dialog " + id + " (maybe already answered)", "answer the dialog " + id);
                    if (rec.Kind == DialogKinds.Naming)
                    {
                        if (!hasText) return HandResult.No("\"" + rec.Title + "\" asks for a name: send text", "name " + rec.Title);
                        if (rec.Fields.Count == 0) return HandResult.No("\"" + rec.Title + "\" has no name field", "name " + rec.Title);
                        if (texts.Count == 1 && rec.Fields.All(f => f.Name != texts.Keys.First()))
                        { var one = texts.Values.First(); texts.Clear(); texts[rec.Fields[0].Name] = one; }   // a bare name goes to the first field
                        if (texts.Keys.Any(k => rec.Fields.All(f => f.Name != k)))
                            return HandResult.No("unknown name field; fields: " + string.Join(", ", rec.Fields.Select(f => f.Name)), "name " + rec.Title);
                        foreach (var f in rec.Fields)
                            if (texts.ContainsKey(f.Name)) texts[f.Name] = DialogPolicy.Fit(texts[f.Name], f.MaxLen);
                        return null;
                    }
                    if (!hasOpt) return HandResult.No("\"" + rec.Title + "\" is a choice: send option", "answer " + rec.Title);
                    string err;
                    var o = DialogBook.Resolve(rec, opt, out err);
                    if (o == null) return HandResult.No(err, "answer " + rec.Title);
                    index = o.Index; label = o.Label;
                    return null;
                },
                Run = h =>
                {
                    var r = h.Choose(rec, index, texts);
                    if (r != null && r.Ok) vivarium().Dialogs.Remove(id);
                    return r;
                }
            };
        }
    }

    /// <summary>The unattended sweep: dialogs open past the timeout get the safe default while Narrator plays is on.
    /// Verse-free; the game side passes the hand that answers.</summary>
    public static class DialogSweep
    {
        /// <summary>Returns the number answered. apply answers the dialog on the game thread.</summary>
        public static int Run(Vivarium v, double nowReal, int tick, Func<DialogRecord, DialogChoice, HandResult> apply)
        {
            if (!v.Playing || !v.Settings.AnswerDialogs) return 0;
            int timeout = Math.Max(5, v.Settings.DialogTimeoutS);
            int n = 0;
            foreach (var d in v.Dialogs.Due(nowReal, timeout))
            {
                var choice = DialogPolicy.Default(d);
                if (choice == null) { d.Attempts = DialogBook.MaxAttempts; continue; }
                HandResult r = null;
                try { r = apply(d, choice); } catch (Exception) { }
                if (r == null || !r.Ok)
                {
                    d.Attempts++;
                    if (d.Attempts >= DialogBook.MaxAttempts)
                        v.Chat.Add(ChatKinds.Note, "Could not answer \"" + d.Title + "\" by default" + (r != null && !string.IsNullOrEmpty(r.Reason) ? ": " + r.Reason : "") + ". It stays open.", tick);
                    continue;
                }
                string act = DialogPolicy.Done(d, choice);
                v.Dialogs.Remove(d.Id);
                v.Actions.Add("choose", act + " (default)", tick, "default", choice.Why);
                ChatFeed.Action(v.Chat, tick, act, choice.Why);
                v.Chat.Add(ChatKinds.Note, DialogPolicy.Note(d, choice, timeout), tick);
                n++;
            }
            return n;
        }
    }
}
