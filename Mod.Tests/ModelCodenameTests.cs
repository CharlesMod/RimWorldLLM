using Newtonsoft.Json;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    public class ModelCodenameTests
    {
        [Theory]
                [InlineData("gemma4-e4b-rp-q40", "Flashlight")]
        [InlineData("gemma4-rp-maxxed-q3kxl", "Campfire")]
        [InlineData("GEMMA4-E4B-RP-Q40", "Flashlight")]
        public void Table_gives_the_code_name(string id, string want) { Assert.Equal(want, ModelCodename.For(id, null)); }

        [Fact]
        public void Unknown_ids_have_no_code_name_and_keep_their_name()
        {
            Assert.Null(ModelCodename.For("qwen-27b", ""));
            Assert.Null(ModelCodename.For(null, null));
            Assert.Equal("Qwen 27B", ModelCodename.DisplayFor("qwen-27b", null, "Qwen 27B"));
            Assert.Equal("qwen-27b", ModelCodename.DisplayFor("qwen-27b", null, null));
        }

        [Fact]
        public void Engine_code_name_wins_over_the_table()
        {
            Assert.Equal("Bonfire", ModelCodename.For("gemma4-e2b-q4km", " Bonfire "));
            Assert.Equal("Bonfire (Qwen 27B)", ModelCodename.DisplayFor("qwen-27b", "Bonfire", "Qwen 27B"));
        }

        [Fact]
        public void Display_puts_the_code_name_first()
        {
            Assert.Equal("Campfire (Gemma 4 26B RP)", ModelCodename.DisplayFor("gemma4-rp-maxxed-q3kxl", null, "Gemma 4 26B RP"));
            Assert.Equal("Campfire (Gemma 4 26B RP)", ModelCodename.DisplayFor("gemma4-rp-maxxed-q3kxl", null, null));
        }

        [Fact]
        public void Status_reads_the_codename_fields()
        {
            var st = JsonConvert.DeserializeObject<EngineStatus>("{\"model_codename\":\"Flashlight\",\"upgrade\":{\"target\":\"x\",\"target_codename\":\"Campfire\"}}");
            Assert.Equal("Flashlight", st.ModelCodename);
            Assert.Equal("Campfire", st.Upgrade.TargetCodename);
        }

        [Fact]
        public void Bar_label_shows_code_name_from_engine_or_table()
        {
            var b = new DownloadBar();
            var v = b.Update(new EngineStatus { Phase = EnginePhase.Ready, Upgrade = new EngineUpgrade { State = "downloading", Target = "gemma4-rp-maxxed-q3kxl", TargetName = "Gemma 4 26B RP",
                Progress = new EngineProgress { DoneBytes = 1, TotalBytes = 10000000000, Step = "model x" } } }, 0);
            Assert.Equal("Campfire (Gemma 4 26B RP)", v.LabelArg);
            var v2 = b.Update(new EngineStatus { Phase = EnginePhase.Downloading, Progress = new EngineProgress { DoneBytes = 1, TotalBytes = 3000000000, Step = "model gemma4-e2b-q4km" } }, 1000);
            Assert.Equal("RWL_Bar_Model", v2.LabelKey);
            Assert.Equal("Gemma 4 E2B", v2.LabelArg);
            var v3 = b.Update(new EngineStatus { Phase = EnginePhase.Ready, Upgrade = new EngineUpgrade { State = "downloading", Target = "q", TargetName = "Qwen 27B", TargetCodename = "Bonfire",
                Progress = new EngineProgress { DoneBytes = 1, TotalBytes = 10000000000, Step = "model q" } } }, 2000);
            Assert.Equal("Bonfire (Qwen 27B)", v3.LabelArg);
        }
    }
}
