using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    public class MindViewTests
    {
        const string AgentBlob = @"{""schema_v"":1,""actor_id"":12,""name"":""Ann"",
          ""fixed"":{""text"":""Ann, 33."",""facts_hash"":""x""},
          ""voice"":{""register_line"":""plain"",""tics"":[""Enough.""],""examples"":[""a"",""b""],""edited_by_player"":true},
          ""evolving_bio"":""Hardened."",""bio_history"":[{""game"":600,""before"":""old"",""after"":""new""}],
          ""wants"":[""a warm meal""],
          ""memories"":[{""id"":""m1"",""game"":900,""text"":""Saw a raid"",""source"":""saw"",""told_by"":""Bo"",""confidence"":0.6,""visibility"":""public"",""importance"":0.4,""pinned"":true,""zzz"":1}],
          ""relations"":{""7"":{""name"":""Bo"",""feeling"":""wary"",""history"":""argued"",""opinion"":-5}},
          ""notes"":""be kind"",""unknown"":{""a"":1}}";

        [Fact]
        public void ReadsTheAgentsRealKeys()
        {
            var v = MindView.Parse(JToken.Parse(AgentBlob));
            Assert.Equal("Ann, 33.", v.FixedBio);
            Assert.Equal("plain", v.VoiceRegister);
            Assert.True(v.VoiceEditedByPlayer);
            Assert.Equal(new[] { "Enough." }, v.Tics.ToArray());
            Assert.Equal("Hardened.", v.EvolvingBio);
            Assert.Equal("new", v.History[0].Text);
            Assert.Equal(600, v.History[0].Tick);
            Assert.Equal("a warm meal", v.Wants[0]);
            var m = v.Memories[0];
            Assert.Equal(900, m.Tick); Assert.Equal("Bo", m.From); Assert.True(m.Pinned); Assert.Equal(0.6f, m.Confidence);
            Assert.Equal("wary", v.Relations[0].Feeling);
            Assert.Equal("be kind", v.Notes);
        }

        [Fact]
        public void ReadsTheBriefSpellingToo()
        {
            var v = MindView.Parse(JToken.Parse(@"{""fixed_bio"":""F"",""voice_card"":{""register"":""r""},
              ""evolving_bio"":{""text"":""E"",""history"":[{""rev"":2,""text"":""h"",""tick"":5}]},
              ""memories"":[{""text"":""t"",""tick"":3,""from"":""X""}],
              ""relations"":{""1"":{""feeling"":""f"",""history"":[""a"",""b""]}}}"));
            Assert.Equal("F", v.FixedBio); Assert.Equal("r", v.VoiceRegister);
            Assert.Equal("E", v.EvolvingBio); Assert.Equal(2, v.History[0].Rev); Assert.Equal(5, v.History[0].Tick);
            Assert.Equal(3, v.Memories[0].Tick); Assert.Equal("X", v.Memories[0].From);
            Assert.Equal("a b", v.Relations[0].History);
        }

        [Fact]
        public void NullAndGarbageDoNotThrow()
        {
            Assert.Equal("", MindView.Parse(null).FixedBio);
            Assert.Empty(MindView.Parse(JToken.Parse("[1,2]")).Memories);
            Assert.Empty(MindView.Parse(JToken.Parse(@"{""memories"":[1,""x""],""wants"":5}")).Memories);
        }

        [Fact]
        public void NotesWriteBackKeepsTheRestAndMarksPlayerEdit()
        {
            var mind = JObject.Parse(AgentBlob);
            MindNotes.Apply(mind, "new note");
            Assert.Equal("new note", (string)mind["notes"]);
            Assert.True((bool)mind["notes_edited_by_player"]);
            Assert.Equal(1, (int)mind["notes_rev"]);
            MindNotes.Apply(mind, "again");
            Assert.Equal(2, (int)mind["notes_rev"]);
            Assert.Equal("Ann", (string)mind["name"]);
            Assert.Equal(1, (int)mind["unknown"]["a"]);
        }

        [Fact]
        public void NotesThroughTheMindBookBumpTheStoredRev()
        {
            var book = new MindBook();
            int r;
            book.Put(1, 0, JObject.Parse(AgentBlob), out r);
            var mind = JObject.Parse(book.Records[1].MindJson);
            MindNotes.Apply(mind, "n");
            Assert.Equal(MindBook.PutResult.Ok, book.Put(1, book.Records[1].Rev, mind, out r));
            Assert.Equal(2, r);
        }
    }
}
