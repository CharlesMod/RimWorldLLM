using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using Xunit;

namespace RimWorldLLM.Tests.Mind
{
    public class SalienceFactsTests
    {
        private static JObject J(string s) { return JObject.Parse(s); }

        private static readonly JObject Snap = J(@"{ 'id': 1, 'name': {'first':'Ana'}, 'job': {'def':'Mine','label':'mining granite'},
            'health': {'pain': 0.7, 'downed': false, 'conditions': [ {'label':'Gunshot','part':'left arm','severity':0.5,'tended':false}, {'label':'Scratch','part':'leg','severity':0.1,'tended':true} ]},
            'needs': {'food': 0.1, 'rest': 0.25, 'comfort': 0.9, 'joy': 0.8},
            'mood': {'value': 0.15, 'thoughts': [ {'label':'Had a nasty chat','offset':-6}, {'label':'Ate raw food','offset':-12}, {'label':'Slept outside','offset':-1} ]},
            'relations': [ {'pawn': 2, 'name':'Bram', 'kind':'friend', 'opinion': 65} ], 'history': {'arrival':'rescued from a crash'} }");
        private static readonly JObject Lis = J(@"{ 'id': 2, 'name': {'first':'Bram'}, 'job': 'hauling steel', 'asleep': false,
            'health': {'pain': 0.5, 'downed': false, 'conditions': [ {'label':'Cut','severity':0.6,'tended':false} ]}, 'needs': {'rest': 0.1},
            'mood': {'value': 0.2}, 'relations': [ {'pawn': 1, 'kind':'friend', 'opinion': -30} ] }");
        private static readonly JObject Scene = J(@"{ 'room': {'role':'kitchen','clean':-4.0,'impressiveness':60,'temp':-3}, 'weather':'Rain', 'season':'Winter',
            'time_of_day': {'hour': 23, 'label':'night'}, 'present': [ {'pawn':2,'name':'Bram','job':'hauling','distance':3}, {'pawn':3,'name':'Cai','job':'cooking meals','distance':5}, {'pawn':4,'name':'Dov','job':null,'distance':9} ] }");

        private static System.Collections.Generic.List<SalientFact> All(JObject lis = null, JObject[] ev = null)
        { return SalienceFacts.Build(Snap, Scene, lis, lis == null ? -1 : 2, ev, 100000); }

        [Fact]
        public void SceneFacts()
        {
            var f = All();
            Assert.Contains(f, x => x.Key == "room:clean" && x.Text == "The room is filthy.");
            Assert.Contains(f, x => x.Key == "room:impressive");
            Assert.Contains(f, x => x.Key == "room:temp" && x.Text.Contains("cold"));
            Assert.Contains(f, x => x.Key == "weather" && x.Text.Contains("rain"));
            Assert.Contains(f, x => x.Key == "time" && x.Text == "It is night.");
            Assert.Contains(f, x => x.Key == "season");
            Assert.Contains(f, x => x.Key == "job" && x.Text == "You are mining granite.");
            Assert.Contains(f, x => x.Key == "nearby:Cai" && x.Text.Contains("cooking meals"));
        }

        [Fact]
        public void ListenerIsNotAlsoNearby()
        {
            Assert.DoesNotContain(All(Lis), x => x.Key == "nearby:Bram");
            Assert.Contains(All(), x => x.Key == "nearby:Bram");
        }

        [Fact]
        public void BodyNeedsAndMood()
        {
            var f = All();
            Assert.Contains(f, x => x.Key == "injury:Gunshot:left arm" && x.Text.Contains("not tended"));
            Assert.Contains(f, x => x.Key == "injury:pain" && x.Text == "You are in a lot of pain.");
            Assert.Contains(f, x => x.Key == "need:food" && x.Text == "You are starving.");
            Assert.Contains(f, x => x.Key == "need:rest" && x.Text == "You are tired.");
            Assert.DoesNotContain(f, x => x.Key == "need:comfort" || x.Key == "need:joy");
            Assert.Contains(f, x => x.Key == "mood:overall" && x.Text == "Your mood is very low.");
            Assert.Contains(f, x => x.Key == "mood:Ate raw food" && x.Text.StartsWith("Weighing on you"));
            Assert.Contains(f, x => x.Key == "mood:Had a nasty chat" && x.Social);
            Assert.DoesNotContain(f, x => x.Key == "mood:Slept outside");        // too small an offset
        }

        [Fact]
        public void ListenerSectionInWordsWithNoNumbers()
        {
            var f = All(Lis).Where(x => x.AboutListener).ToList();
            Assert.Contains(f, x => x.Key == "listener:state" && x.Text.Contains("exhausted") && x.Text.Contains("hurt"));
            Assert.Contains(f, x => x.Key == "listener:job" && x.Text == "Bram is hauling steel.");
            Assert.Contains(f, x => x.Key == "listener:relation" && x.Text == "Bram is your friend; you are close to Bram.");
            Assert.Contains(f, x => x.Key == "listener:their_opinion" && x.Text == "Bram dislikes you.");
            Assert.All(f, x => Assert.DoesNotContain(x.Text, c => char.IsDigit(c)));
        }

        [Fact]
        public void EventsAgeAndSocialFlag()
        {
            var ev = new[]
            {
                J("{'type':'death','summary':'Old Tom died of his wounds.','game':95000}"),
                J("{'type':'social','summary':'Cai insulted Ana.','game':99000,'data':{'interaction':'Insult'}}"),
                J("{'type':'social','summary':'Ana and Cai chatted.','game':99500,'data':{'interaction':'Chitchat'}}"),
                J("{'type':'letter','summary':'A letter came.','game':99900}"),
                J("{'type':'raid_start','summary':'Raiders approach.','game':100000}"),
            };
            var f = All(null, ev);
            var d = f.Single(x => x.Key == "event:death:95000");
            Assert.Equal(2.0, d.AgeHours, 3);
            Assert.Equal(1.0, d.Weight);
            Assert.True(f.Single(x => x.Key == "event:social:99000").Weight >= 0.7);
            Assert.True(f.Single(x => x.Key == "event:social:99500").Weight < 0.7);
            Assert.All(f.Where(x => x.Key.StartsWith("event:social")), x => Assert.True(x.Social));
            Assert.DoesNotContain(f, x => x.Key.StartsWith("event:letter"));
            Assert.Contains(f, x => x.Key == "event:raid_start:100000");
        }

        [Fact]
        public void UnremarkableStatesStayQuietAndNothingIsInvented()
        {
            var calm = SalienceFacts.Build(J(@"{'id':1,'needs':{'food':0.9,'rest':0.8},'mood':{'value':0.6,'thoughts':[]},'health':{'pain':0.0,'downed':false,'conditions':[]}}"),
                J(@"{'room':{'role':'bedroom','clean':0.2,'impressiveness':15,'temp':21},'weather':'Clear','present':[]}"), null, -1, null, 0);
            Assert.True(calm.All(x => x.Weight <= 0.25), string.Join("|", calm.Select(x => x.Key + x.Weight)));
            Assert.Empty(SalienceFacts.Build(J("{}"), J("{}"), null, -1, null, 0));
            Assert.Empty(SalienceFacts.Build(null, null, null, -1, null, 0));
        }

        [Fact]
        public void PickedFromABusySceneIsGroundedAndCapped()
        {
            var ev = new[] { J("{'type':'death','summary':'Old Tom died.','game':99000}") };
            var picked = Salience.Pick(All(Lis, ev), new SalienceAsk { Purpose = SaliencePurpose.Line, ListenerName = "Bram" });
            Assert.InRange(picked.Count, 3, 6);
            Assert.Contains(picked, x => x.AboutListener);
            Assert.Contains(picked, x => x.Key.StartsWith("event:death"));
        }

        [Theory]
        [InlineData(-80, "can't stand")] [InlineData(-30, "dislike")] [InlineData(0, "feel nothing much for")] [InlineData(30, "like")] [InlineData(80, "are close to")]
        public void OpinionWords(int o, string words) { Assert.Equal(words, SalienceFacts.OpinionWords(o)); }
    }
}
