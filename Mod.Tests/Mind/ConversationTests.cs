// Ports of agent/tests/test_conversation.py (world-free tests) plus CheckEnd recorded from CPython random.Random.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.Py;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class ConversationTests
    {
        static Conversation Mk() { return new Conversation("c1", 1, 2, new Dictionary<int, string> { { 1, "Ana" }, { 2, "Bram" } }); }

        [Fact]
        public void OpenerObliges()
        {
            Assert.True(Conversation.OpenerObliges("Where is the medicine?"));
            Assert.True(Conversation.OpenerObliges("Bring me a bedroll."));
            Assert.True(Conversation.OpenerObliges("Fine day.", "insult") && Conversation.OpenerObliges("Fine day.", "confide"));
            Assert.False(Conversation.OpenerObliges("Fine day."));
        }

        [Fact]
        public void AppendOnlyTurnsAlternate()
        {
            var c = Mk();
            c.Add(1, "Hi"); c.Add(2, "Hey");
            Assert.True(c.NextSpeaker() == 1 && c.ListenerOf(1) == 2);
            Assert.Equal(new List<(string, string)> { ("Ana", "Hi"), ("Bram", "Hey") }, c.TurnsForPrompt());
        }

        [Fact]
        public void TurnCapSix()
        {
            var c = Mk(); var rng = new PyRandom(0);
            for (int i = 0; i < 6; i++)
            {
                c.Add(i % 2 == 0 ? 1 : 2, "unique line number " + i + " about topic" + i);
                var r = c.CheckEnd(rng);
                if (i < 5) Assert.NotEqual("turn_cap", r);
            }
            Assert.Equal("turn_cap", c.CheckEnd(rng));
        }

        [Fact]
        public void ObligedReplyHappensEvenIfChanceFails()
        {
            Func<double> never = () => 0.999;
            var c = Mk(); c.Add(1, "Where is the medicine?");
            Assert.True(c.Obliged && c.CheckEnd(never) == "");
            var c2 = Mk(); c2.Add(1, "Nice weather.");
            Assert.True(!c2.Obliged && c2.CheckEnd(never) == "chance");
        }

        [Fact]
        public void ContinueChanceDecays()
        {
            var c = Mk(); var seen = new List<double>();
            for (int i = 0; i < 5; i++) { c.Add(1, "x" + i); seen.Add(c.ContinueChance()); }
            Assert.Equal(seen.OrderByDescending(x => x).ToList(), seen);
            Assert.True(seen[0] > seen[seen.Count - 1]);
        }

        [Fact]
        public void RepetitionCheck()
        {
            Assert.True(Conversation.Repetitive("Get the medicine now", new[] { "Get the medicine now" }));
            Assert.True(Conversation.Repetitive("Get the medicine now please", new[] { "get the medicine now" }));
            Assert.False(Conversation.Repetitive("Bram stole my rifle", new[] { "Get the medicine now" }));
            var c = Mk(); c.Add(1, "Get the medicine from storage"); c.Add(2, "Get the medicine from storage");
            Assert.Equal("repetition", c.CheckEnd(new PyRandom(0)));
        }

        [Fact]
        public void ApartJobPreempt()
        {
            var c = Mk(); c.Add(1, "Where is it?");
            var rng = new PyRandom(0);
            Assert.Equal("apart", c.CheckEnd(rng, apart: true));
            Assert.Equal("job_changed", c.CheckEnd(rng, jobChanged: true));
            Assert.Equal("preempted", c.CheckEnd(rng, preempted: true));
        }

        [Fact]
        public void EndTemplates()
        {
            var c = Mk(); c.Add(1, "You are useless.", "insult"); c.Add(2, "Shut it.");
            Assert.Contains("Bram", Conversation.TemplateMemory(c, 1)); Assert.Contains("Ana", Conversation.TemplateMemory(c, 2));
            Assert.Contains("Wary", Conversation.TemplateRelation(c, 2).Item1);
            Assert.Contains("Nothing", Conversation.TemplateRelation(c, 1).Item1);
        }

        [Fact]
        public void TemplateTextsExact()
        {
            var c = Mk(); c.Add(1, "You are useless.", "insult"); c.Add(2, "Shut it.");
            Assert.Equal("I talked with Bram. I started it: \"You are useless.\" It ended on: \"Shut it.\"", Conversation.TemplateMemory(c, 1));
            Assert.Equal("I talked with Ana. Ana started it: \"You are useless.\" It ended on: \"Shut it.\"", Conversation.TemplateMemory(c, 2));
            Assert.Equal(("Wary after the way they spoke to me.", "Last talked about: You are useless."), Conversation.TemplateRelation(c, 2));
        }

        // Recorded from CPython (rec_d.py): the results of check_end over many conversations and the next draw proving the draw count.
        [Theory]
        [InlineData("1")] [InlineData("7")] [InlineData("42")] [InlineData("99")]
        public void CheckEndMatchesPythonRandom(string seed)
        {
            var rec = (Newtonsoft.Json.Linq.JObject)ConversationRec.J[seed];
            string[] lines = { "Nice weather today.", "The fields look dry.", "Cattle wandering near the creek.", "Winter comes early here.",
                               "Rice harvest was thin.", "Somebody fix the roof." };
            var rng = new PyRandom(long.Parse(seed));
            var res = new List<string>();
            for (int rounds = 0; rounds < 40; rounds++)
            {
                var c = Mk();
                for (int i = 0; i < 6; i++)
                {
                    c.Add(i % 2 == 0 ? 1 : 2, lines[(i + rounds) % 6] + " " + new string('x', rounds % 3));
                    res.Add(c.CheckEnd(rng));
                    if (res[res.Count - 1] != "") break;
                }
            }
            Assert.Equal(rec["res"].Select(t => (string)t).ToList(), res);
            Assert.Equal((double)rec["after"], rng.Random());
        }

        // Deferred: test_director_conversation_end_writes_memory_and_relation, test_conversation_ends_when_apart
        // (need the Director + FakeBridge + FakeLLM world fixture).
    }
}
