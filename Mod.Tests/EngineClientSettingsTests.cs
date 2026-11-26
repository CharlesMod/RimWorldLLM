using System.Threading.Tasks;
using HermesVoice.Bridge;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HermesVoice.Tests
{
    public class EngineClientSettingsTests
    {
        const string Check = "{\"ok\":true,\"error\":\"\",\"path\":\"/m/x.gguf\",\"name\":\"x\",\"size\":4000000000,\"arch\":\"gemma4\",\"params\":8000000000,\"ctx_train\":131072,\"quant\":\"Q4_K_M\",\"moe\":true,\"known\":false,\"estimated\":true,\"fits\":\"tight\",\"why\":\"close\",\"shape\":{\"slots\":2,\"ctx_per_slot\":16384,\"n_cpu_moe\":3,\"kv_type\":\"q4_0\",\"vram_mib\":9000},\"est_vram_mib\":9000,\"est_ram_mib\":2000,\"est_decode_tps\":33.5,\"chat_template\":true}";
        const string Storage = "{\"data_dir\":\"/d\",\"total_bytes\":5000,\"free_bytes\":-1,\"parts\":[{\"name\":\"models\",\"path\":\"/d/models\",\"bytes\":4000},{\"name\":\"logs\",\"path\":\"/d/logs\",\"bytes\":10}],\"models\":[{\"file\":\"a.gguf\",\"id\":\"a\",\"bytes\":4000,\"in_use\":true,\"partial\":false},{\"file\":\"b.gguf.part\",\"id\":\"\",\"bytes\":5,\"in_use\":false,\"partial\":true}]}";

        [Fact]
        public async Task CheckCustomParsesAndPosts()
        {
            using (var f = new FakeEngine((m, pq) => (200, Check)))
            {
                var r = await f.Client.CheckCustomAsync("/m/x.gguf");
                Assert.True(r.Ok);
                var c = r.Value;
                Assert.True(c.Ok); Assert.Equal("x", c.Name); Assert.Equal(4000000000L, c.Size);
                Assert.Equal("gemma4", c.Arch); Assert.Equal(8000000000L, c.Params); Assert.Equal(131072, c.CtxTrain);
                Assert.Equal("Q4_K_M", c.Quant); Assert.True(c.Moe); Assert.False(c.Known); Assert.True(c.Estimated);
                Assert.Equal("tight", c.Fits); Assert.Equal(2, c.Shape.Slots); Assert.Equal(9000, c.EstVramMib);
                Assert.Equal(2000, c.EstRamMib); Assert.Equal(33.5, c.EstDecodeTps); Assert.True(c.ChatTemplate);
                Assert.Equal("POST /engine/v1/custom/check", f.Log[0]);
                Assert.Equal("1", f.Csrf[0]);
                Assert.Equal("/m/x.gguf", (string)JObject.Parse(f.Bodies[0])["path"]);
            }
        }

        [Fact]
        public async Task CheckCustomFailureAndMissingFields()
        {
            using (var f = new FakeEngine((m, pq) => (200, "{\"ok\":false,\"error\":\"not a GGUF file\"}")))
            {
                var r = await f.Client.CheckCustomAsync("/x");
                Assert.True(r.Ok);
                Assert.False(r.Value.Ok);
                Assert.Equal("not a GGUF file", r.Value.Error);
                Assert.Null(r.Value.Shape);
                Assert.Null(r.Value.Fits);
                Assert.Equal(0, r.Value.Params);
            }
        }

        [Fact]
        public async Task CheckTimeoutUsesCheckTimeoutMs()
        {
            using (var f = new FakeEngine((m, pq) => (200, Check)) { DelayMs = 600 })
            {
                Assert.Equal(10000, f.Client.CheckTimeoutMs);
                var c = f.Client; c.CheckTimeoutMs = 100;
                var r = await c.CheckCustomAsync("/x");
                Assert.False(r.Ok); Assert.True(r.TimedOut);
            }
        }

        [Fact]
        public async Task UseAndClearCustom()
        {
            using (var f = new FakeEngine((m, pq) => pq.EndsWith("/use") ? (202, "{\"started\":true}") : (200, "{}")))
            {
                var u = await f.Client.UseCustomAsync("/m/x.gguf");
                Assert.True(u.Ok); Assert.Equal(202, u.HttpStatus); Assert.True((bool)u.Value["started"]);
                var c = await f.Client.ClearCustomAsync();
                Assert.True(c.Ok);
                Assert.Equal("POST /engine/v1/custom/use", f.Log[0]);
                Assert.Equal("POST /engine/v1/custom/clear", f.Log[1]);
                Assert.Equal("1", f.Csrf[0]); Assert.Equal("1", f.Csrf[1]);
                Assert.Equal("/m/x.gguf", (string)JObject.Parse(f.Bodies[0])["path"]);
                Assert.Equal("{}", f.Bodies[1]);
            }
        }

        [Fact]
        public async Task ErrorTextMapping()
        {
            int status = 400;
            using (var f = new FakeEngine((m, pq) => (status, status == 404 ? "{\"error\":\"no such model\"}" : status == 409 ? "{\"error\":\"busy\"}" : "{\"error\":\"file not found\"}")))
            {
                var r = await f.Client.UseCustomAsync("/x");
                Assert.False(r.Ok); Assert.Equal(400, r.HttpStatus); Assert.Equal("file not found", r.Error);
                status = 409;
                r = await f.Client.UseCustomAsync("/x");
                Assert.Equal(409, r.HttpStatus); Assert.Equal("busy", r.Error);
                status = 404;
                var d = await f.Client.DeleteModelAsync("zz");
                Assert.False(d.Ok); Assert.Equal(404, d.HttpStatus); Assert.Equal("no such model", d.Error);
                Assert.Null(d.Value);
            }
        }

        [Fact]
        public async Task StorageParses()
        {
            using (var f = new FakeEngine((m, pq) => (200, Storage)))
            {
                var r = await f.Client.GetStorageAsync();
                Assert.True(r.Ok);
                Assert.Equal("GET /engine/v1/storage", f.Log[0]);
                Assert.Equal("/d", r.Value.DataDir); Assert.Equal(5000, r.Value.TotalBytes); Assert.Equal(-1, r.Value.FreeBytes);
                Assert.Equal(2, r.Value.Parts.Count);
                Assert.Equal("models", r.Value.Parts[0].Name); Assert.Equal("/d/models", r.Value.Parts[0].Path); Assert.Equal(4000, r.Value.Parts[0].Bytes);
                Assert.Equal(2, r.Value.Models.Count);
                Assert.True(r.Value.Models[0].InUse); Assert.Equal("a", r.Value.Models[0].Id);
                Assert.True(r.Value.Models[1].Partial); Assert.Equal("", r.Value.Models[1].Id);
            }
        }

        [Fact]
        public async Task StorageRefreshPathAndEmpty()
        {
            using (var f = new FakeEngine((m, pq) => (200, "{\"data_dir\":\"/d\"}")))
            {
                Assert.Equal(5000, f.Client.StorageTimeoutMs);
                var r = await f.Client.GetStorageAsync(true);
                Assert.True(r.Ok);
                Assert.Equal("GET /engine/v1/storage?refresh=1", f.Log[0]);
                Assert.Null(r.Value.Parts); Assert.Null(r.Value.Models);
            }
        }

        [Fact]
        public async Task RemoveDownloadsSendsConfirm()
        {
            using (var f = new FakeEngine((m, pq) => (202, "{\"started\":true}")))
            {
                var r = await f.Client.RemoveDownloadsAsync();
                Assert.True(r.Ok);
                Assert.Equal("POST /engine/v1/remove-downloads", f.Log[0]);
                Assert.Equal("1", f.Csrf[0]);
                Assert.True((bool)JObject.Parse(f.Bodies[0])["confirm"]);
            }
        }

        [Fact]
        public async Task DeleteModelBodies()
        {
            using (var f = new FakeEngine((m, pq) => (200, "{\"freed_bytes\":1234}")))
            {
                var a = await f.Client.DeleteModelAsync("gemma", null);
                Assert.True(a.Ok); Assert.Equal(1234, a.Value.FreedBytes);
                var b = await f.Client.DeleteModelAsync("", "x.gguf");
                Assert.True(b.Ok);
                var c = await f.Client.DeleteModelAsync(null, "y.gguf");
                var j0 = JObject.Parse(f.Bodies[0]); var j1 = JObject.Parse(f.Bodies[1]); var j2 = JObject.Parse(f.Bodies[2]);
                Assert.Equal("gemma", (string)j0["id"]); Assert.Null(j0["file"]);
                Assert.Equal("x.gguf", (string)j1["file"]); Assert.Null(j1["id"]);
                Assert.Equal("y.gguf", (string)j2["file"]);
                Assert.Equal("POST /engine/v1/delete-model", f.Log[0]);
                Assert.Equal("1", f.Csrf[0]);
            }
        }

        [Fact]
        public async Task StatusGainsCustomModelNamePrevious()
        {
            string st = "{\"phase\":\"ready\",\"model\":\"custom:x\",\"model_name\":\"My X\",\"custom\":{\"path\":\"/m/x.gguf\",\"name\":\"x\",\"size\":42,\"missing\":true},\"previous\":{\"id\":\"p\",\"name\":\"Prev\",\"file\":\"p.gguf\",\"bytes\":7}}";
            using (var f = new FakeEngine((m, pq) => (200, st)))
            {
                var r = await f.Client.GetStatusAsync();
                Assert.Equal("My X", r.Value.ModelName);
                Assert.Equal("/m/x.gguf", r.Value.Custom.Path); Assert.Equal("x", r.Value.Custom.Name);
                Assert.Equal(42, r.Value.Custom.Size); Assert.True(r.Value.Custom.Missing);
                Assert.Equal("p", r.Value.Previous.Id); Assert.Equal("Prev", r.Value.Previous.Name);
                Assert.Equal("p.gguf", r.Value.Previous.File); Assert.Equal(7, r.Value.Previous.Bytes);
            }
            using (var f = new FakeEngine((m, pq) => (200, "{\"phase\":\"ready\",\"custom\":null}")))
            {
                var r = await f.Client.GetStatusAsync();
                Assert.Null(r.Value.Custom); Assert.Null(r.Value.Previous); Assert.Null(r.Value.ModelName);
            }
        }

        [Fact]
        public async Task PlanGainsCustomAndDisk()
        {
            string p = "{\"models\":[{\"id\":\"a\",\"disk_need_bytes\":999},{\"id\":\"b\"}],\"disk_free_bytes\":123456,\"custom\":" + Check + "}";
            using (var f = new FakeEngine((m, pq) => (200, p)))
            {
                var r = await f.Client.GetPlanAsync();
                Assert.Equal(123456, r.Value.DiskFreeBytes);
                Assert.Equal("x", r.Value.Custom.Name);
                Assert.Equal(999, r.Value.Models[0].DiskNeedBytes);
                Assert.Equal(0, r.Value.Models[1].DiskNeedBytes);
            }
            using (var f = new FakeEngine((m, pq) => (200, "{\"models\":[],\"custom\":null}")))
            {
                var r = await f.Client.GetPlanAsync();
                Assert.Null(r.Value.Custom); Assert.Equal(0, r.Value.DiskFreeBytes);
            }
        }
    }
}
