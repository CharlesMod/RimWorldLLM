using System.Collections.Generic;
using HermesVoice.Bridge;
using Xunit;

namespace HermesVoice.Tests
{
    public class QuoteTests
    {
        [Fact]
        public void ShortLineIsKeptAndWhitespaceCollapsed()
        {
            Assert.Equal("You are a waste.", Quotes.Trim("  You are\n a   waste. "));
            Assert.Equal("", Quotes.Trim("   "));
            Assert.Equal("", Quotes.Trim(null));
        }

        [Fact]
        public void LongLineEndsOnAWordWithEllipsis()
        {
            string line = "You are a waste of rations and you know it, every single one of us carries your share and you never once thank anybody for it at all, not even the cook";
            string t = Quotes.Trim(line, 60);
            Assert.True(t.Length <= 60);
            Assert.EndsWith(Quotes.Ellipsis, t);
            string body = t.Substring(0, t.Length - 1);
            Assert.Contains(body.Substring(body.LastIndexOf(' ') + 1), line.Split(' '));
        }

        [Fact]
        public void NoWordBreakMeansHardCut()
        {
            string t = Quotes.Trim(new string('x', 300), 20);
            Assert.Equal(20, t.Length);
            Assert.EndsWith(Quotes.Ellipsis, t);
        }

        [Fact]
        public void DefaultMaxIs140()
        {
            Assert.True(Quotes.Trim(new string('a', 5) + " " + new string('b', 400)).Length <= 140);
        }

        [Fact]
        public void KeepsThreeNewestAndSkipsDuplicates()
        {
            var l = new List<QuoteEntry>();
            Quotes.Push(l, new QuoteEntry("a", "Bram", 1));
            Quotes.Push(l, new QuoteEntry("a", "Bram", 1));
            Quotes.Push(l, new QuoteEntry("b", "Bram", 2));
            Quotes.Push(l, new QuoteEntry("c", "Bram", 3));
            Quotes.Push(l, new QuoteEntry("d", "Bram", 4));
            Assert.Equal(new[] { "d", "c", "b" }, l.ConvertAll(x => x.Text));
        }

        [Fact]
        public void TooltipListsUpToThreeNewestFirst()
        {
            var l = new List<QuoteEntry>();
            for (int i = 1; i <= 5; i++) Quotes.Push(l, new QuoteEntry("q" + i, "Bram", i));
            string t = Quotes.Tooltip(l, q => "Insulted by " + q.Speaker + ": \"" + q.Text + "\"");
            Assert.Equal("Insulted by Bram: \"q5\"\nInsulted by Bram: \"q4\"\nInsulted by Bram: \"q3\"", t);
            Assert.Equal("", Quotes.Tooltip(new List<QuoteEntry>(), q => "x"));
        }

        [Fact]
        public void LeadInKeepsVanillaSentenceThenQuote()
        {
            Assert.Equal("Bram insulted Hana's cooking: \"Awful.\"", Quotes.LeadIn("Bram insulted Hana's cooking.", "Awful.", (l, q) => l + ": \"" + q + "\""));
            Assert.Equal("Bram insulted Hana.", Quotes.LeadIn("Bram insulted Hana.", "", (l, q) => l + ": \"" + q + "\""));
        }

        [Fact]
        public void SayAndEffectMergeIntoOneEntry()
        {
            Assert.Equal("insult", EntryKinds.EffectWording("insult"));
            Assert.Null(EntryKinds.EffectWording(null));       // refused or no intent: the plain say entry
            Assert.Null(EntryKinds.EffectWording("none"));
        }

        [Fact]
        public void LinesFromVanillaInteractionsNeverApplyEffects()
        {
            Assert.False(EffectGuard.MayApply("v1-3"));
            Assert.True(EffectGuard.MayApply(null));
            Assert.True(EffectGuard.MayApply(""));
        }
    }

    public class PendingTableTests
    {
        [Fact]
        public void AttachesOnceOnArrival()
        {
            var t = new PendingTable<string>();
            t.Register("v1", 1, 2, 100, "entry");
            string p;
            Assert.Equal(PendingTable<string>.Result.Attached, t.TryAttach("v1", 1, 2, 150, 417, out p));
            Assert.Equal("entry", p);
            Assert.Equal(PendingTable<string>.Result.AlreadyAttached, t.TryAttach("v1", 1, 2, 151, 417, out p));
        }

        [Fact]
        public void WrongPairAndUnknownAreRejected()
        {
            var t = new PendingTable<string>();
            t.Register("v1", 1, 2, 100, "entry");
            string p;
            Assert.Equal(PendingTable<string>.Result.WrongPair, t.TryAttach("v1", 2, 1, 110, 417, out p));
            Assert.Equal(PendingTable<string>.Result.WrongPair, t.TryAttach("v1", 1, 3, 110, 417, out p));
            Assert.Equal(PendingTable<string>.Result.Unknown, t.TryAttach("zzz", 1, 2, 110, 417, out p));
            Assert.Equal(PendingTable<string>.Result.Attached, t.TryAttach("v1", 1, 2, 110, 417, out p));
        }

        [Fact]
        public void LateLineExpires()
        {
            var t = new PendingTable<string>();
            t.Register("v1", 1, 2, 100, "entry");
            string p;
            Assert.Equal(PendingTable<string>.Result.Expired, t.TryAttach("v1", 1, 2, 100 + 418, 417, out p));
            Assert.Equal(PendingTable<string>.Result.Unknown, t.TryAttach("v1", 1, 2, 101, 417, out p));
        }

        [Fact]
        public void SweepDropsOldSlotsAndCapacityBounds()
        {
            var t = new PendingTable<int>(3);
            for (int i = 0; i < 5; i++) t.Register("k" + i, 1, 2, i * 10, i);
            Assert.Equal(3, t.Count);
            Assert.Equal(1, t.Sweep(45, 20));    // k2 (tick 20) ages out; k3 (30) and k4 (40) stay
            Assert.Equal(2, t.Count);
        }
    }

    public class InteractionPolicyTests
    {
        static readonly ISet<string> High = InteractionPolicy.ParseList(InteractionPolicy.DefaultHigh);
        static readonly ISet<string> Idle = InteractionPolicy.ParseList(InteractionPolicy.DefaultIdle);

        [Fact]
        public void TiersComeFromTheLists()
        {
            Assert.Equal(InteractionPolicy.Tier.High, InteractionPolicy.Classify("Insult", High, Idle));
            Assert.Equal(InteractionPolicy.Tier.High, InteractionPolicy.Classify("slight", High, Idle));
            Assert.Equal(InteractionPolicy.Tier.Idle, InteractionPolicy.Classify("Chitchat", High, Idle));
            Assert.Equal(InteractionPolicy.Tier.Skip, InteractionPolicy.Classify("AnimalChat", High, Idle));
        }

        [Fact]
        public void RomanceNeverVoicedUnder18()
        {
            var tier = InteractionPolicy.Tier.High;
            Assert.False(InteractionPolicy.Voice(true, "RomanceAttempt", tier, true, true, 17.9f, 30f));
            Assert.False(InteractionPolicy.Voice(true, "MarriageProposal", tier, true, true, 30f, 16f));
            Assert.True(InteractionPolicy.Voice(true, "RomanceAttempt", tier, true, true, 18f, 30f));
            Assert.True(InteractionPolicy.Voice(true, "Insult", tier, true, true, 14f, 30f));
        }

        [Fact]
        public void RomanceRuleHoldsEvenIfAListedUnderAnotherName()
        {
            Assert.True(InteractionPolicy.IsRomance("MyMod_Flirt"));
            Assert.False(InteractionPolicy.Voice(true, "MyMod_Flirt", InteractionPolicy.Tier.Idle, true, true, 17f, 40f));
        }

        [Fact]
        public void SettingOffSkipsAndNonVoicedOrAnimalsSkip()
        {
            var tier = InteractionPolicy.Tier.High;
            Assert.False(InteractionPolicy.Voice(false, "Insult", tier, true, true, 30f, 30f));
            Assert.False(InteractionPolicy.Voice(true, "Insult", tier, false, true, 30f, 30f));
            Assert.False(InteractionPolicy.Voice(true, "Insult", tier, true, false, 30f, 30f));
            Assert.False(InteractionPolicy.Voice(true, "Insult", InteractionPolicy.Tier.Skip, true, true, 30f, 30f));
        }
    }
}
