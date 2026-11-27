using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ChatFeedTests
    {
        readonly ChatLog L = new ChatLog();
        ChatEntry Last { get { return L.Entries.Last(); } }

        [Fact]
        public void SayKeepsSpeakerListenerAndText()
        {
            ChatFeed.Say(L, 5, 12, "Ana", "Bo", "Hello there");
            Assert.Equal("colonist", Last.Who); Assert.Equal(12, Last.To); Assert.Equal("Ana", Last.Name); Assert.Equal("Bo", Last.Listener); Assert.Equal("Hello there", Last.Text);
            ChatFeed.Say(L, 6, 12, "Ana", "", "Alone");
            Assert.Equal("", Last.Listener);
        }

        [Fact]
        public void ThoughtDreamDiary()
        {
            ChatFeed.Thought(L, 1, "thought", 3, "Ana", "so tired");
            Assert.Equal("thought", Last.Who); Assert.Equal(3, Last.To); Assert.Equal("Ana", Last.Name);
            ChatFeed.Thought(L, 1, "dream", 3, "Ana", "flying");
            Assert.Equal("dream", Last.Who);
            string longText = string.Join(" ", Enumerable.Repeat("wordy", 80));
            ChatFeed.Thought(L, 1, "diary", 3, "Ana", longText);
            Assert.Equal("diary", Last.Who);
            Assert.True(Last.Text.Length <= 160); Assert.EndsWith("…", Last.Text); Assert.DoesNotContain("wordyw", Last.Text);
            ChatFeed.Thought(L, 1, "diary", 3, "Ana", "short entry");
            Assert.Equal("short entry", Last.Text);
        }

        [Fact]
        public void ChronicleNamesTheDay()
        {
            ChatFeed.Chronicle(L, 1, 14, "The first frost came.");
            Assert.Equal("chronicle", Last.Who); Assert.Equal("Day 14", Last.Name); Assert.Equal("The first frost came.", Last.Text);
        }

        [Fact]
        public void RecapJoinsAndCaps()
        {
            ChatFeed.Recap(L, 1, new[] { "Line one.", "  Line   two. ", "" });
            Assert.Equal("recap", Last.Who); Assert.Equal("Line one. Line two.", Last.Text);
            ChatFeed.Recap(L, 1, Enumerable.Repeat("A fairly long recap sentence here.", 40).ToList());
            Assert.True(Last.Text.Length <= 480); Assert.EndsWith("…", Last.Text);
            Assert.Null(ChatFeed.Recap(L, 1, new string[0]));
        }

        [Fact]
        public void ArtOneLiners()
        {
            ChatFeed.Art(L, 1, "Ana", "Blue Hour", "A calm river at dusk.");
            Assert.Equal("art", Last.Who); Assert.Equal("Ana", Last.Name);
            Assert.Equal("Art: \"Blue Hour\" by Ana: A calm river at dusk.", Last.Text);
            ChatFeed.Art(L, 1, "", "Blue Hour", "A calm river.");
            Assert.Equal("Art: \"Blue Hour\": A calm river.", Last.Text);
            ChatFeed.Art(L, 1, "Ana", "", "A calm river.");
            Assert.Equal("Art by Ana: A calm river.", Last.Text);
            ChatFeed.Art(L, 1, "", "", "A calm river.");
            Assert.Equal("Art: A calm river.", Last.Text);
            ChatFeed.Art(L, 1, "Ana", "Blue Hour", "");
            Assert.Equal("Art: \"Blue Hour\" by Ana", Last.Text);
            int n = L.Entries.Count;
            Assert.Null(ChatFeed.Art(L, 1, "Ana", "", ""));
            Assert.Equal(n, L.Entries.Count);
            ChatFeed.Art(L, 1, "Ana", "Blue Hour", string.Join(" ", Enumerable.Repeat("sweeping", 60)));
            Assert.True(Last.Text.Length <= 160); Assert.StartsWith("Art: \"Blue Hour\" by Ana: ", Last.Text);
        }

        [Theory]
        [InlineData("it was getting in the way.", ", since it was getting in the way")]
        [InlineData("It was getting in the way", ", since it was getting in the way")]
        [InlineData("Ana is hungry", ", since Ana is hungry")]
        [InlineData("because winter is close", ", because winter is close")]
        [InlineData("After the raid", ", After the raid")]
        [InlineData("to keep the peace...", ", to keep the peace")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void WhyClause(string why, string expect)
        {
            Assert.Equal(expect, ChatFeed.WhyClause(why));
        }

        [Fact]
        public void WhyIsTruncatedAtAWord()
        {
            string why = string.Join(" ", Enumerable.Repeat("because", 30));
            string w = ChatFeed.CleanWhy("it " + why);
            Assert.True(w.Length <= 80); Assert.False(w.EndsWith(" ")); Assert.DoesNotContain("becaus ", w);
        }

        [Fact]
        public void ActionAndRefused()
        {
            ChatFeed.Action(L, 1, "moved a chair", "it was getting in the way.");
            Assert.Equal("action", Last.Who); Assert.Equal("moved a chair, since it was getting in the way", Last.Text);
            ChatFeed.Action(L, 1, "paused the game", null);
            Assert.Equal("paused the game", Last.Text);
            ChatFeed.Refused(L, 1, "change the game speed", "the player has not let the narrator change speed");
            Assert.Equal("refused", Last.Who); Assert.Equal("couldn't change the game speed: the player has not let the narrator change speed", Last.Text);
            Assert.Null(ChatFeed.Action(L, 1, "", "x"));
        }

        [Fact]
        public void HandNotesPlainEnglish()
        {
            var kv = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Cooking", 1) };
            Assert.Equal("set Ana's Cooking to 1st", VivariumHandsPure.WorkNote("Ana", kv));
            kv.Add(new KeyValuePair<string, int>("Hauling", 3));
            Assert.Equal("set Ana's Cooking to 1st and Hauling to 3rd", VivariumHandsPure.WorkNote("Ana", kv));
            Assert.Equal("turned off Ana's Hauling", VivariumHandsPure.WorkNote("Ana", new[] { new KeyValuePair<string, int>("Hauling", 0) }));
            Assert.Equal("set Chris' Cooking to 2nd and turned off Hauling", VivariumHandsPure.WorkNote("Chris", new[] { new KeyValuePair<string, int>("Cooking", 2), new KeyValuePair<string, int>("Hauling", 0) }));
            Assert.Equal("put Ana on sleep from 22:00 to 6:00", VivariumHandsPure.ScheduleNote("Ana", 22, 6, "Sleep"));
            Assert.Equal("put Ana on work from 0:00 to 0:00", VivariumHandsPure.ScheduleNote("Ana", 0, 24, "work"));
            Assert.Equal("paused the game", VivariumHandsPure.TimeNote(true, null));
            Assert.Equal("unpaused the game", VivariumHandsPure.TimeNote(false, null));
            Assert.Equal("set the game speed to 2", VivariumHandsPure.TimeNote(null, 2));
            Assert.Equal("change the game speed", VivariumHandsPure.TimeWhat(null, 2));
            Assert.Equal("drafted Bram", VivariumHandsPure.DraftNote(new[] { "Bram" }, true));
            Assert.Equal("released Bram and Ana from the draft", VivariumHandsPure.DraftNote(new[] { "Bram", "Ana" }, false));
            Assert.Equal("had Ana wear the Soldier outfit", VivariumHandsPure.PolicyNote("Ana", "outfit", "Soldier"));
            Assert.Equal("sent Ana to (3, 4)", VivariumHandsPure.OrderNote("Ana", "move", 3, 4, null, false));
            Assert.Equal("had Ana attack Raider", VivariumHandsPure.OrderNote("Ana", "attack", 0, 0, "Raider", false));
            Assert.Equal("had Ana hold position", VivariumHandsPure.OrderNote("Ana", "hold", 0, 0, null, true));
            Assert.Equal("send Ana to (3, 4)", VivariumHandsPure.OrderWhat("Ana", "move", 3, 4, null, false));
        }
    }
}
