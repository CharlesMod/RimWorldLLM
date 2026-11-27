// StewardBoard, Grounding and Chronicle against goldens generated from the Python (csmind/side/gen_core_golden.py ->
// Data/core_golden.json), plus the chronicle-only tests of test_diary_chronicle_pacing.py (budget trims hard, survives a
// restart and a rollback) and the recap-ranking test of test_creative_ground.py.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind.Fakes;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    internal static class CoreGolden
    {
        public static readonly JObject G = JObject.Parse(StewardTestData.Resource("RimWorldLLM.Tests.core_golden.json"));

        public sealed class Ad : IChronicleAdapter, IGroundingAdapter
        {
            public string ChronicleRequest => (string)G["adapter"]["request"];
            public string ChronicleSystem => (string)G["adapter"]["system"];
            public string ChronicleSummarySystem => (string)G["adapter"]["summary_system"];
            public bool HasCreativeCard => false;
            public JObject CreativeCard(JObject snapshot) => null;
            public IReadOnlyList<KeyValuePair<string, int>> RecapImportance
                => G["adapter"]["importance"].Select(p => new KeyValuePair<string, int>((string)p[0], (int)p[1])).ToList();
        }
    }

    public class CoreSideTests
    {
        private static readonly CoreGolden.Ad AD = new CoreGolden.Ad();
        private const int DAY = 60000;
        private static readonly Dictionary<int, HashSet<string>> Names = new Dictionary<int, HashSet<string>> { [1] = new HashSet<string> { "Bram", "Eli", "bram", "eli" } };
        private static JObject G => CoreGolden.G;

        // ---- StewardBoard -----------------------------------------------------------------
        [Fact]
        public void PlainMatchesPython()
        {
            foreach (JArray row in (JArray)G["plain"])
            {
                object text = row[0].Type == JTokenType.Null ? null : row[0].Type == JTokenType.Integer ? (object)(long)row[0] : (string)row[0];
                Assert.Equal((string)row[2], StewardBoard.Plain(text, (int)row[1]));
            }
        }

        [Fact]
        public void RenderBoardMatchesPython()
        {
            var boards = (JArray)G["boards"]; var want = (JArray)G["render"];
            for (int i = 0; i < boards.Count; i++)
            {
                var b = (JObject)boards[i];
                Assert.Equal((string)want[i][0], StewardBoard.RenderBoard(b));
                Assert.Equal((string)want[i][1], StewardBoard.RenderBoard(b, 2));
                Assert.Equal((string)want[i][2], StewardBoard.RenderBoard(b, 0));
            }
        }

        [Fact]
        public void BoardToolsMatchPython()
        {
            var boards = (JArray)G["boards"]; var want = (JArray)G["tools"];
            for (int i = 0; i < boards.Count; i++)
                for (int adv = 0; adv < 2; adv++)
                {
                    var got = StewardBoard.BoardTools((JObject)boards[i], adv == 1);
                    var w = (JArray)want[i][adv];
                    Assert.Equal(w.Count, got.Count);
                    for (int k = 0; k < got.Count; k++)
                    {
                        Assert.Equal((string)w[k][0], got[k].Name);
                        Assert.Equal((bool)w[k][2], got[k].EndsTurn);
                        var ps = (JArray)w[k][1];
                        Assert.Equal(ps.Count, got[k].Params.Count);
                        for (int p = 0; p < ps.Count; p++)
                        {
                            Assert.Equal((string)ps[p][0], got[k].Params[p].Name);
                            Assert.Equal((string)ps[p][1], got[k].Params[p].Kind);
                            Assert.Equal(((JArray)ps[p][2]).Select(x => (string)x).ToArray(), (got[k].Params[p].Choices ?? new List<string>()).ToArray());
                        }
                    }
                }
        }

        private static EditOp Op(JToken t) { var a = (JArray)t; return new EditOp((string)a[0], a.Count > 1 ? (string)a[1] : null, a.Count > 2 ? (string)a[2] : null); }

        [Fact]
        public void EditsApplyAndTryAddMatchPython()
        {
            var boards = ((JArray)G["boards"]).Select(b => (JObject)b).ToList();
            boards.Add((JObject)G["full"]);
            foreach (var case_ in (JArray)G["edits"])
            {
                var board = boards[(int)case_["board"]];
                var ops = ((JArray)case_["ops"]).Select(Op).ToList();
                var res = (JObject)case_["res"];
                if (res["err"] != null)
                {
                    var ex = Assert.Throws<PyValueError>(() => new Edits(ops).Apply(board));
                    Assert.Equal((string)res["err"], ex.Message);
                }
                else Assert.True(JToken.DeepEquals(res["ok"], new Edits(ops).Apply(board)), case_.ToString());
                var e2 = new Edits();
                var reasons = (JArray)case_["try"];
                for (int i = 0; i < ops.Count; i++)
                {
                    string r = e2.TryAdd(board, ops[i]);
                    Assert.Equal(reasons[i].Type == JTokenType.Null ? null : (string)reasons[i], r);
                }
            }
        }

        [Fact]
        public void EditsDoNotMutateTheBoard()
        {
            var b = (JObject)((JArray)G["boards"])[2].DeepClone();
            var before = b.ToString();
            new Edits(new[] { new EditOp("set_goal", "g1", "done"), new EditOp("add_goal", "z", "long") }).Apply(b);
            Assert.Equal(before, b.ToString());
        }

        // ---- Grounding --------------------------------------------------------------------
        [Fact]
        public void CardLinesMatchPython()
        {
            var cards = (JArray)G["card"]; var want = (JArray)G["card_lines"];
            for (int i = 0; i < cards.Count; i++)
                Assert.Equal(want[i].Select(x => (string)x).ToArray(), Grounding.CardLines((JObject)cards[i]).ToArray());
        }

        [Fact]
        public void TokensMentionsAndPickMatchPython()
        {
            var names = (JArray)G["names"]; var texts = ((JArray)G["texts"]).Select(x => (string)x).ToList();
            for (int i = 0; i < names.Count; i++)
            {
                var toks = Grounding.TokensOf(names[i].Select(x => (string)x));
                Assert.Equal(G["tokens"][i].Select(x => (string)x).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                             toks.OrderBy(x => x, StringComparer.Ordinal).ToArray());
                for (int t = 0; t < texts.Count; t++) Assert.Equal((bool)G["mentions"][i][t], Grounding.Mentions(texts[t], toks));
                int idx = 0;
                foreach (int k in new[] { 1, 2, 9 })
                    foreach (bool fb in new[] { true, false })
                        Assert.Equal(G["pick"][i][idx++].Select(x => (string)x).ToArray(), Grounding.PickMentioning(texts, toks, k, fb).ToArray());
            }
        }

        [Fact]
        public void ImportanceAndRankMatchPython()
        {
            var entries = ((JArray)G["entries"]).Select(e => (JObject)e).ToList();
            for (int i = 0; i < entries.Count; i++)
                Assert.Equal((int)G["importance"][0][i], Grounding.Importance(AD, (string)entries[i]["text"], (string)entries[i]["kind"]));
            Assert.Equal((int)G["importance"][1][0], Grounding.Importance(AD, "", ""));
            Assert.Equal((int)G["importance"][1][1], Grounding.Importance(AD, "DIED", "major"));
            var cases = new[] { (12, 4), (12, 8), (3, 2), (1, 4), (12, 0), (0, 3) };
            for (int c = 0; c < cases.Length; c++)
                Assert.Equal(G["rank"][c].Select(x => (int)x).ToArray(),
                             Grounding.RankEntries(AD, entries, cases[c].Item1, cases[c].Item2).Select(e => (int)e["day"]).ToArray());
        }

        [Fact]
        public void RecapRankingPutsTheBiggestFirst()          // test_recap_ranking_puts_the_biggest_first
        {
            var entries = ((JArray)G["entries"]).Take(6).Select(e => (JObject)e).ToList();
            var top = Grounding.RankEntries(AD, entries, 12, 4);
            Assert.StartsWith("Old Tam died", (string)top[0]["text"]);
            Assert.StartsWith("Raiders", (string)top[1]["text"]);
            Assert.Equal(4, top.Count);
            Assert.DoesNotContain("Quiet chores", string.Join(" ", top.Take(3).Select(e => (string)e["text"])));
        }

        [Fact]
        public void ReferentsMatchPython()
        {
            var texts = G["reftexts"].Select(x => (string)x).ToList(); var names = G["refnames"].Select(x => (string)x).ToList();
            int i = 0;
            foreach (int k in new[] { 8, 2, 0 })
                Assert.Equal(G["referents"][i++].Select(x => (string)x).ToArray(), Grounding.Referents(texts, names, k).ToArray());
        }

        // ---- Chronicle --------------------------------------------------------------------
        private static Chronicle Filled(out int _)
        {
            _ = 0;
            var ch = new Chronicle(null, DAY);
            for (int i = 0; i < 12; i++) ch.Note(i * 700, "Event " + i + " " + string.Concat(Enumerable.Repeat("word ", 40)), i / 100.0);
            return ch;
        }

        [Fact]
        public void NotesAndEstimatesMatchPython()
        {
            var ch = Filled(out _);
            var want = (JArray)G["ch_pending"];
            Assert.Equal((int)G["ch_pending_len"], ch.State.Pending.Count);
            for (int i = 0; i < want.Count; i++)
            {
                var n = ch.State.Pending[i];
                Assert.Equal((int)want[i][0], n.Day); Assert.Equal((long)want[i][1], n.Game);
                Assert.Equal((double)want[i][2], n.Severity, 12); Assert.Equal((bool)want[i][3], n.Major);
            }
            foreach (JArray p in (JArray)G["est"]) Assert.Equal((int)p[1], Chronicle.EstTokens((string)p[0]));
        }

        [Fact]
        public void BuildMessagesMatchesPython()
        {
            var ch = Filled(out _);
            ch.State.Summary = "The colony began small and grew.";
            for (int i = 0; i < 3; i++) ch.State.Recent.Add(new ChronicleEntry { Day = i, Game = i * (long)DAY, Kind = "day", Text = "Entry " + i + " text here." });
            var led = Enumerable.Repeat(string.Concat(Enumerable.Repeat("a ledger line ", 5)), 14).ToList();
            var lore = Enumerable.Range(0, 15).Select(i => "lore line " + i + " " + new string('x', 120)).ToList();
            foreach (var c in (JArray)G["ch_build"])
            {
                var msgs = ch.BuildMessages(AD, (string)c["kind"], (int)c["day"], ch.State.Pending, led, (int)c["budget"], (bool)c["lore"] ? lore : null);
                var want = (JArray)c["msgs"];
                Assert.Equal(want.Count, msgs.Count);
                for (int i = 0; i < msgs.Count; i++) { Assert.Equal((string)want[i][0], msgs[i].Role); Assert.True((string)want[i][1] == msgs[i].Text, "case " + c["budget"] + " lore=" + c["lore"] + "\nwant " + (string)want[i][1] + "\n got " + msgs[i].Text); }
                Assert.Equal((int)c["tokens"], Chronicle.MessagesTokens(msgs));
            }
        }

        [Fact]
        public void DueMatchesPython()
        {
            var c = new Chronicle(null, DAY);
            var got = new List<JToken>();
            Action<int> rec = today =>
            {
                var d = c.Due(today);
                got.Add(d == null ? JValue.CreateNull() : new JArray(d.Kind, d.Day, new JArray(d.Notes.Select(n => new JArray(n.Day, n.Game, n.Text, n.Major)))));
            };
            rec(0);
            c.Note(10, "first"); c.Note(DAY + 5, "second"); c.Note(2 * DAY + 1, "third");
            foreach (int t in new[] { 0, 1, 2, 3 }) rec(t);
            c.Note(2 * DAY + 9, "death!", 1.0, true); rec(0);
            var want = (JArray)G["due"];
            Assert.Equal(want.Count, got.Count);
            for (int i = 0; i < got.Count; i++) Assert.True(JToken.DeepEquals(want[i], got[i]), "due " + i + ": " + got[i] + " vs " + want[i]);
        }

        [Fact]
        public void BudgetTrimsHardWhenTheBudgetIsSmall()          // test_chronicle_budget_trims_hard_when_the_budget_is_small
        {
            var ch = new Chronicle(null, DAY);
            for (int i = 0; i < 80; i++) ch.Note(i, "Event " + i + " " + string.Concat(Enumerable.Repeat("word ", 40)), i / 100.0);
            var msgs = ch.BuildMessages(AD, "day", 0, ch.State.Pending, Enumerable.Repeat(string.Concat(Enumerable.Repeat("a ledger line ", 5)), 12).ToList(), 900);
            Assert.True(Chronicle.MessagesTokens(msgs) <= 900);
            Assert.Contains("Event 79", msgs[1].Text);
        }

        [Fact]
        public void ImpossiblyTinyBudgetStopsInsteadOfLooping()
        {
            var ch = new Chronicle(null, DAY);
            ch.Note(1, "x"); ch.State.Summary = "short";
            var msgs = ch.BuildMessages(AD, "day", 0, ch.State.Pending, new List<string>(), 10);
            Assert.Equal(2, msgs.Count);
        }

        private static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "csmind-side-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [Fact]
        public void SurvivesRestartAndFollowsARolledBackSave()      // test_chronicle_survives_restart_and_follows_a_rolled_back_save
        {
            string dir = TempDir();
            try
            {
                var c = new Chronicle(dir, DAY);
                c.Note(100, "A thing happened.", 0.6);
                c.Note(5 * DAY, "A later thing happened.", 0.6);
                c.Save();
                var c2 = new Chronicle(dir, DAY);
                Assert.Equal(2, c2.State.Pending.Count);
                c2.Rollback(2 * DAY);
                Assert.Equal(new[] { "A thing happened." }, c2.State.Pending.Select(n => n.Text).ToArray());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task WrittenFilesAreAppendOnlyAndMatchPython()
        {
            string dir = TempDir();
            try
            {
                var c = new Chronicle(dir, DAY);
                c.Note(5, "n1", 0.5); c.Note(DAY * 2, "n2", 0.2, true);
                c.Save();
                Assert.Equal((string)G["files"]["json"], File.ReadAllText(Path.Combine(dir, "chronicle.json"), Encoding.UTF8));
                // the append path is private; drive it through WriteAsync with a scripted model and compare the shapes
                var llm = new FakeLlm();
                llm.Script["chronicle"] = new List<string> { "Bram died of his wounds in the evening, and the others buried him without speeches." };
                var e = await c.WriteAsync(llm, AD, 1, 3, new List<string>(), Names);
                Assert.True(e != null, c.LastDrop);
                Assert.Equal("major", e.Kind);
                string md = File.ReadAllText(Path.Combine(dir, "chronicle.md"), Encoding.UTF8);
                Assert.Equal("## Day 3 (major event)\n\n" + e.Text + "\n\n", md);
                string jl = File.ReadAllText(Path.Combine(dir, "chronicle.jsonl"), Encoding.UTF8);
                Assert.EndsWith("\n", jl);
                var row = JObject.Parse(jl.TrimEnd('\n'));
                Assert.Equal(new[] { "day", "game", "kind", "text" }, row.Properties().Select(p => p.Name).ToArray());
                Assert.DoesNotContain("\r", md + jl);
                // a second written entry appends, never rewrites
                c.Note(DAY * 4, "n3", 0.9, true);
                llm.Script["chronicle"] = new List<string> { "Eli stole a pistol from the armory and nobody spoke of it at dinner that night." };
                await c.WriteAsync(llm, AD, 1, 5, new List<string>(), Names);
                string md2 = File.ReadAllText(Path.Combine(dir, "chronicle.md"), Encoding.UTF8);
                Assert.StartsWith(md, md2);
                Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "chronicle.jsonl")).Length);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
