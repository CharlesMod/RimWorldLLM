// Turncost: prefix-stable prompts and sticky slots. The engine's cache keeps a prefix only up to the start of the last user message, so
// everything that is stable for an actor must sit in the system message, byte for byte, and everything volatile in the last user message.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;
using MindM = RimWorldLLM.Mind.Mind;

namespace RimWorldLLM.Tests.Mind
{
    public class TurncostSpeechTests
    {
        static readonly RimWorldAdapter AD = new RimWorldAdapter();

        static List<(string, string)> Lease(MindM m, string when, string weather, string mem, string thought, IList<(string, string)> turns, int turnNo)
        {
            var sc = new Scene { Where = "the kitchen", When = when + ", " + weather, Trigger = "Bram argued with Ana about " + mem, SpeechNow = "Right now: " + weather };
            var mems = new List<Memory> { new Memory { Id = "m1", Game = 5, Text = "I saw it: " + mem } };
            string prefix = PromptBlocks.BuildPrefix(AD.BlockA, m, mems, new List<string> { "Raid: " + mem }, sc, new List<string> { thought }, null);
            return PromptBlocks.LineMessages(prefix, turns, m, "Ana", turnNo);
        }

        static int Common(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        [Fact]
        public void LineSystemMessageIsByteIdenticalAcrossLeasesAndTheSharedPrefixReachesTheLastUserMessage()
        {
            var m = new MindM(1, "Ana");
            var a = Lease(m, "morning", "clear", "the stew", "I should rest", new List<(string, string)>(), 0);
            var b = Lease(m, "night", "a thunderstorm", "the wolves", "I hate the cold", new List<(string, string)> { ("Bram", "hello there") }, 1);
            Assert.Equal(a[0].Item2, b[0].Item2);                                          // the system message did not move
            string ra = a[0].Item1 + "\n" + a[0].Item2 + "\n" + a[1].Item1 + "\n", rb = b[0].Item1 + "\n" + b[0].Item2 + "\n" + b[1].Item1 + "\n";
            Assert.True(Common(ra + a[1].Item2, rb + b[1].Item2) >= ra.Length);          // shared prefix ends no earlier than the last user message
            Assert.NotEqual(a[1].Item2, b[1].Item2);
            // nothing volatile in the system message
            foreach (string vol in new[] { "the stew", "the wolves", "morning", "thunderstorm", "I should rest", "WHAT YOU REMEMBER", "THE SCENE" })
                Assert.DoesNotContain(vol, a[0].Item2);
            // the volatile parts lead the user message, then the turns, then the request last
            string u = b[1].Item2;
            Assert.StartsWith("=== WHAT YOU REMEMBER ===", u);
            Assert.True(u.IndexOf("=== THE SCENE ===", StringComparison.Ordinal) < u.IndexOf("=== TURNS SO FAR ===", StringComparison.Ordinal));
            Assert.True(u.IndexOf("=== TURNS SO FAR ===", StringComparison.Ordinal) < u.IndexOf("=== REQUEST ===", StringComparison.Ordinal));
            Assert.EndsWith("One line only.", u);
        }

        [Fact]
        public void IntentAndAimFollowUpsKeepTheLinePrefix()
        {
            var m = new MindM(1, "Ana");
            var line = Lease(m, "morning", "clear", "the stew", "x", new List<(string, string)>(), 0);
            var intent = PromptBlocks.IntentMessages(line, "Well done.", "Bram");
            Assert.Equal(line[0], intent[0]); Assert.Equal(line[1], intent[1]);
            var aim = PromptBlocks.AimMessages(line, "Well done.");
            Assert.Equal(line[1], aim[1]);
        }

        [Fact]
        public void PrefixWithoutAMarkIsAllSystem()
        {
            Assert.Equal("abc", PromptBlocks.SystemOf("abc")); Assert.Equal("", PromptBlocks.VolatileOf("abc"));
            var m = new MindM(1, "Ana");
            var msgs = PromptBlocks.LineMessages("abc", new List<(string, string)>(), m, "Ana");
            Assert.Equal("abc", msgs[0].Item2); Assert.StartsWith("=== TURNS SO FAR ===", msgs[1].Item2);
        }

        // ---- SlotMap ---------------------------------------------------------------------------------------------------------------------

        [Fact]
        public void SlotMapPrefersTheLastSlotWhenFreeElseTheFirstFree()
        {
            var s = new SlotMap(new[] { 1, 2, 3 }, 0);
            Assert.Equal(1, s.Pick(7, new List<int> { 1, 2, 3 }));                         // never held one: the first free
            s.Remember(7, 3);
            Assert.Equal(3, s.Pick(7, new List<int> { 1, 2, 3 }));                         // its last slot, free
            Assert.Equal(1, s.Pick(7, new List<int> { 1, 2 }));                            // taken: the first free
            Assert.Equal(-1, s.Pick(7, new List<int>()));
            Assert.Equal(0, s.ForNarrator());
        }

        [Fact]
        public void LeaseManagerGivesAnActorItsLastSlotBack()
        {
            var lm = new LeaseManager(new[] { 1, 2, 3 }, 0);
            Now t = new Now(100, 0.0);
            WorkItem Item(int actor) => lm.MakeItem(actor, "social", t);
            var a = lm.Acquire(Item(10), t); var b = lm.Acquire(Item(11), t); var c = lm.Acquire(Item(12), t);
            Assert.Equal(new[] { 1, 2, 3 }, new[] { a.Slot, b.Slot, c.Slot });
            lm.Release(b); lm.Release(a);                                                 // 2 then 1 freed
            var b2 = lm.Acquire(Item(11), t);
            Assert.Equal(2, b2.Slot);                                                     // 11 comes back to 2 although 1 is first free
            var d = lm.Acquire(Item(13), t);
            Assert.Equal(1, d.Slot);                                                      // a newcomer takes the first free
        }
    }
}
