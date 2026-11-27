using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class VivariumDialogsTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeHands Hands = new FakeHands();
        int op;

        public VivariumDialogsTests() { VivariumRoutes.Register(H.Router, () => V, () => Hands); }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body); j["op_id"] = "op" + (++op);
            return H.Call("POST", path, j.ToString());
        }
        static void Bad(ApiResult r, string contains) { Assert.False(r.Ok); Assert.Equal("bad_request", r.Error); Assert.Contains(contains, (string)r.Body["detail"]); }

        static DialogRecord Letter(string id, string kind, params string[] labels)
        {
            var d = new DialogRecord { Id = id, Kind = kind, Title = kind + " title", Text = "t", IsLetter = true };
            var roles = DialogKinds.RolesFor(kind);
            for (int i = 0; i < labels.Length; i++)
                d.Options.Add(new DialogOption { Index = i, Label = labels[i], Role = i < roles.Length ? roles[i] : DialogOption.Other, Risk = i == 0 ? DialogKinds.AcceptRisk(kind) : "" });
            return d;
        }

        static DialogRecord Naming(string id, params string[] fields)
        {
            var d = new DialogRecord { Id = id, Kind = DialogKinds.Naming, Title = "Name your colony" };
            foreach (var f in fields) d.Fields.Add(new DialogField { Name = f, MaxLen = 12 });
            return d;
        }

        void Open(params DialogRecord[] ds) { V.Dialogs.Sync(ds.ToList(), 100.0); }

        // ---- policy
        [Fact]
        public void DefaultsPerKind()
        {
            Assert.Equal(0, DialogPolicy.Default(Letter("L1", DialogKinds.Joiner, "Accept", "Reject")).Option);
            Assert.Equal(0, DialogPolicy.Default(Letter("L1", DialogKinds.Visitors, "Accept", "Reject")).Option);
            Assert.Equal(1, DialogPolicy.Default(Letter("L2", DialogKinds.Ransom, "Pay 500", "Refuse")).Option);
            Assert.Equal(1, DialogPolicy.Default(Letter("L3", DialogKinds.Quest, "Accept", "Decline")).Option);
            Assert.Equal(0, DialogPolicy.Default(Letter("L4", DialogKinds.Growth, "Take")).Option);
            Assert.Equal(2, DialogPolicy.Default(Letter("L5", DialogKinds.CreepJoiner, "Accept", "Capture", "Reject")).Option);
            Assert.Equal(0, DialogPolicy.Default(Letter("L6", DialogKinds.ChoosePawn, "Ana", "Bram")).Option);
        }

        [Fact]
        public void DefaultSkipsDisabledAndGivesUpWhenNoneOpen()
        {
            var d = Letter("L1", DialogKinds.Joiner, "Accept", "Reject");
            d.Options[0].Disabled = true;
            Assert.Equal(1, DialogPolicy.Default(d).Option);
            d.Options[1].Disabled = true;
            Assert.Null(DialogPolicy.Default(d));
        }

        [Fact]
        public void MessageBoxTakesTheHarmlessButton()
        {
            var d = new DialogRecord { Id = "W1", Kind = DialogKinds.MessageBox, Title = "Sure?" };
            d.Options.Add(new DialogOption { Index = 0, Label = "Abandon", Risk = "destructive" });
            d.Options.Add(new DialogOption { Index = 1, Label = "Cancel", Role = DialogOption.Decline });
            Assert.Equal(1, DialogPolicy.Default(d).Option);
            var ok = new DialogRecord { Id = "W2", Kind = DialogKinds.MessageBox, Title = "Note" };
            ok.Options.Add(new DialogOption { Index = 0, Label = "OK" });
            Assert.Equal(0, DialogPolicy.Default(ok).Option);
        }

        [Fact]
        public void NodeTreePrefersTheFreeCloseOption()
        {
            var d = new DialogRecord { Id = "W1", Kind = DialogKinds.NodeTree, Title = "Trader" };
            d.Options.Add(new DialogOption { Index = 0, Label = "Buy", Risk = "" });
            d.Options.Add(new DialogOption { Index = 1, Label = "Goodbye", Role = DialogOption.Close });
            Assert.Equal(1, DialogPolicy.Default(d).Option);
            d.Options[1].Disabled = true;
            Assert.Equal(0, DialogPolicy.Default(d).Option);
        }

        [Fact]
        public void NamingFallbackFitsTheFields()
        {
            var c = DialogPolicy.Default(Naming("W3", "faction", "settlement"));
            Assert.Equal(2, c.Texts.Count);
            Assert.All(c.Texts.Values, t => { Assert.NotEmpty(t); Assert.True(t.Length <= 12); });
            Assert.Null(DialogPolicy.Default(Naming("W4")));
        }

        [Fact]
        public void NoteText()
        {
            var d = Letter("L1", DialogKinds.Joiner, "Accept the refugee", "Reject");
            var c = DialogPolicy.Default(d);
            Assert.Equal("No answer in 90 s: chose \"Accept the refugee\" on \"joiner title\" (default)", DialogPolicy.Note(d, c, 90));
        }

        // ---- book
        [Fact]
        public void SyncReportsNewOnceAndDropsGone()
        {
            Assert.Single(V.Dialogs.Sync(new[] { Letter("L1", DialogKinds.Joiner, "A", "B") }, 10));
            Assert.Empty(V.Dialogs.Sync(new[] { Letter("L1", DialogKinds.Joiner, "A", "B") }, 20));
            Assert.Equal(10, V.Dialogs.Get("L1").OpenedReal);   // the clock does not restart on a refresh
            V.Dialogs.Sync(new DialogRecord[0], 30);
            Assert.Equal(0, V.Dialogs.Count);
        }

        [Fact]
        public void TextCappedAt300()
        {
            var d = Letter("L1", DialogKinds.Joiner, "A"); d.Text = new string('x', 900);
            V.Dialogs.Sync(new[] { d }, 0);
            Assert.Equal(300, V.Dialogs.Get("L1").Text.Length);
        }

        // ---- sweep
        [Fact]
        public void SweepAnswersAfterTimeoutOnlyWhilePlaying()
        {
            Open(Letter("L1", DialogKinds.Ransom, "Pay", "Refuse"));
            var calls = new List<string>();
            System.Func<DialogRecord, DialogChoice, HandResult> apply = (d, c) => { calls.Add(d.Id + ":" + c.Option); return HandResult.Done("ok"); };
            V.Playing = false;
            Assert.Equal(0, DialogSweep.Run(V, 1000, 5, apply));
            V.Playing = true;
            Assert.Equal(0, DialogSweep.Run(V, 150, 5, apply));      // 50 s open, timeout 90
            Assert.Equal(1, DialogSweep.Run(V, 191, 5, apply));
            Assert.Equal(new[] { "L1:1" }, calls);
            Assert.Equal(0, V.Dialogs.Count);
            Assert.Contains(V.Chat.Entries, e => e.Who == "note" && e.Text.StartsWith("No answer in 90 s:") && e.Text.EndsWith("(default)"));
            Assert.Contains(V.Chat.Entries, e => e.Who == "action");
            Assert.Single(V.Actions.Since(0)["actions"]);
        }

        [Fact]
        public void SweepHonoursTimeoutSettingAndToggle()
        {
            Open(Letter("L1", DialogKinds.Joiner, "A", "B"));
            V.Playing = true; V.Settings.DialogTimeoutS = 20;
            System.Func<DialogRecord, DialogChoice, HandResult> ok = (d, c) => HandResult.Done("ok");
            V.Settings.AnswerDialogs = false;
            Assert.Equal(0, DialogSweep.Run(V, 500, 1, ok));
            V.Settings.AnswerDialogs = true;
            Assert.Equal(1, DialogSweep.Run(V, 121, 1, ok));
        }

        [Fact]
        public void SweepGivesUpAfterThreeFailuresWithOneNote()
        {
            Open(Letter("L1", DialogKinds.Joiner, "A", "B"));
            V.Playing = true;
            int tries = 0;
            System.Func<DialogRecord, DialogChoice, HandResult> fail = (d, c) => { tries++; return HandResult.No("gone"); };
            for (int i = 0; i < 6; i++) DialogSweep.Run(V, 1000 + i, 1, fail);
            Assert.Equal(3, tries);
            Assert.Equal(1, V.Chat.Entries.Count(e => e.Text.Contains("Could not answer")));
            Assert.Equal(1, V.Dialogs.Count);
        }

        // ---- routes
        [Fact]
        public void GetDialogsShape()
        {
            Open(Letter("L1", DialogKinds.Joiner, "Accept", "Reject"), Naming("W2", "name"));
            var r = H.Call("GET", "/dialogs");
            Assert.True(r.Ok);
            Assert.Equal(90, (int)r.Body["timeout_s"]);
            var arr = (JArray)r.Body["dialogs"];
            Assert.Equal(2, arr.Count);
            Assert.Equal("L1", (string)arr[0]["id"]); Assert.Equal("joiner", (string)arr[0]["kind"]);
            Assert.Equal("Accept", (string)arr[0]["default"]);
            Assert.Equal(0, (int)arr[0]["options"][0]["index"]);
            Assert.Equal("name", (string)arr[1]["fields"][0]["name"]);
            Assert.False((bool)arr[1]["options"].HasValues);
        }

        [Fact]
        public void ChooseByIndexAndLabelIsAnAction()
        {
            Open(Letter("L1", DialogKinds.Joiner, "Accept", "Reject"), Letter("L2", DialogKinds.Ransom, "Pay", "Refuse"));
            var r = Post("/act/choose", new { dialog = "L1", option = 0, why = "we have room" });
            Assert.True(r.Ok);
            Assert.Equal("choose L1 0 -", Hands.Calls.Last());
            Assert.Equal(1, V.Dialogs.Count);   // answered one is dropped
            Assert.Contains(V.Chat.Entries, e => e.Who == "action" && e.Text.Contains("we have room"));
            Assert.Equal("choose", (string)V.Actions.Since(0)["actions"][0]["verb"]);
            Assert.True(Post("/act/choose", new { dialog = "L2", option = "refuse" }).Ok);
            Assert.Equal("choose L2 1 -", Hands.Calls.Last());
        }

        [Fact]
        public void ChooseNaming()
        {
            Open(Naming("W2", "name"), Naming("W3", "faction", "settlement"));
            Assert.True(Post("/act/choose", new { dialog = "W2", text = "  Ferngrove " }).Ok);
            Assert.Equal("choose W2 -1 name=Ferngrove", Hands.Calls.Last());
            Assert.True(Post("/act/choose", new { dialog = "W3", text = new { faction = "The Lanterns", settlement = "Ashford Hollow Keep" } }).Ok);
            Assert.Equal("choose W3 -1 faction=The Lanterns,settlement=Ashford Holl", Hands.Calls.Last());   // cut to the field's 12
        }

        [Fact]
        public void ChooseRefusals()
        {
            Open(Letter("L1", DialogKinds.Joiner, "Accept", "Reject"), Naming("W2", "name"));
            V.Dialogs.Get("L1").Options[1].Disabled = true;
            var gone = Post("/act/choose", new { dialog = "L9", option = 0 });
            Assert.False(gone.Ok); Assert.Equal("refused", gone.Error);
            Assert.False(Post("/act/choose", new { dialog = "L1", option = 1 }).Ok);        // disabled
            Assert.False(Post("/act/choose", new { dialog = "L1", option = 7 }).Ok);        // no such
            Assert.False(Post("/act/choose", new { dialog = "L1", text = "x" }).Ok);        // not a naming dialog
            Assert.False(Post("/act/choose", new { dialog = "W2", option = 0 }).Ok);        // naming wants text
            Assert.DoesNotContain(Hands.Calls, c => c.StartsWith("choose"));
            Assert.Contains(V.Chat.Entries, e => e.Who == "refused");
        }

        [Fact]
        public void ChooseBadRequests()
        {
            Bad(Post("/act/choose", new { option = 0 }), "dialog");
            Bad(Post("/act/choose", new { dialog = "L1" }), "option");
            Bad(Post("/act/choose", new { dialog = "L1", option = true }), "option");
            Bad(Post("/act/choose", new { dialog = "L1", text = 5 }), "text");
            Bad(Post("/act/choose", new { dialog = "W1", text = "" }), "text");
            Bad(Post("/act/choose", new { dialog = "W1", text = new string('a', 61) }), "60");
            Bad(Post("/act/choose", new { dialog = "L1", option = 0, why = 5 }), "why");
        }
    }
}
