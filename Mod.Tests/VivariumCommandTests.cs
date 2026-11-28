using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using RimWorldLLM.Mind;
using RimWorldLLM.Mind.RimWorld;
using RimWorldLLM.Mind.ToolLoop;
using RimWorldLLM.Tests.Mind;
using Xunit;

namespace RimWorldLLM.Tests
{
    sealed class FakeCmdHands : FakeBioHands, IVivariumCommandHands
    {
        public CmdArgs Last;
        public HandResult Job(CmdArgs a) { Last = a; Calls.Add("job " + a.Op + " " + a.Who + " " + a.Target); return Next; }
        public HandResult Medical(CmdArgs a) { Last = a; Calls.Add("medical " + a.Op + " " + a.Who + " " + a.Value); return Next; }
        public HandResult Prisoner(CmdArgs a) { Last = a; Calls.Add("prisoner " + a.Op + " " + a.Who + " " + a.Value); return Next; }
        public HandResult Animal(CmdArgs a) { Last = a; Calls.Add("animal " + a.Op + " " + a.Who + " " + a.Value); return Next; }
        public HandResult Social(CmdArgs a) { Last = a; Calls.Add("social " + a.Op + " " + a.Who + " " + a.Value); return Next; }
    }

    public class VivariumCommandRouteTests
    {
        readonly Harness H = new Harness();
        readonly Vivarium V = new Vivarium();
        readonly FakeCmdHands Hands = new FakeCmdHands();
        int op;

        public VivariumCommandRouteTests() { VivariumRoutes.Register(H.Router, () => V, () => Hands); }

        ApiResult Post(string path, object body)
        {
            var j = JObject.FromObject(body);
            j["op_id"] = "c" + (++op);
            return H.Call("POST", path, j.ToString());
        }

        [Fact]
        public void JobPassesTheOrderAndGreentextsIt()
        {
            Hands.Next = HandResult.Done("Ana will haul 40 steel to storage");
            var r = Post("/act/job", new { op = "haul", who = "Ana", target = "#4412", why = "since the steel is in the rain" });
            Assert.True(r.Ok);
            Assert.Equal("haul", Hands.Last.Op); Assert.Equal("Ana", Hands.Last.Who); Assert.Equal("#4412", Hands.Last.Target);
            Assert.Contains(V.Chat.Entries.Select(e => e.Text), t => t.StartsWith("Ana will haul 40 steel to storage"));
        }

        [Fact]
        public void JobNeedsATargetExceptStay()
        {
            var r = Post("/act/job", new { op = "haul", who = "Ana" });
            Assert.False(r.Ok); Assert.Equal(400, r.Status);
            Assert.True(Post("/act/job", new { op = "stay", who = "Ana" }).Ok);
        }

        [Fact]
        public void RefusalCarriesTheReason()
        {
            Hands.Next = HandResult.No("Ana cannot reach it");
            var r = Post("/act/job", new { op = "rescue", who = "Ana", target = "Bram" });
            Assert.False(r.Ok); Assert.Equal(409, r.Status); Assert.Equal("refused", r.Error);
            Assert.Contains(V.Chat.Entries.Select(e => e.Text), t => t.StartsWith("couldn't have Ana rescue Bram: Ana cannot reach it"));
        }

        [Fact]
        public void OrdersAreCappedPerCycleAndMarksAreNot()
        {
            for (int i = 0; i < CommandPure.MaxOrdersPerCycle; i++) Assert.True(Post("/act/job", new { op = "stay", who = "Ana" }).Ok);
            var r = Post("/act/job", new { op = "stay", who = "Ana" });
            Assert.False(r.Ok); Assert.Equal(409, r.Status); Assert.Contains("orders this cycle", (string)r.Body["detail"]);
            H.State.Tick += BuildBudget.CycleTicks + 1;
            Assert.True(Post("/act/job", new { op = "stay", who = "Ana" }).Ok);
        }

        [Fact]
        public void MarkedResultsCostDesignationsNotOrders()
        {
            Hands.Next = HandResult.Placed("marked a boar to be tamed", new List<NarratorMark> { new NarratorMark { Kind = "designation", Def = "Tame", Things = new List<int> { 5 }, Cells = new List<int>() } });
            for (int i = 0; i < 25; i++) Assert.True(Post("/act/animal", new { op = "tame", who = "wild boar" }).Ok);
            Assert.Equal(CommandPure.MaxOrdersPerCycle, V.Budget.Orders);
            Assert.Equal(150 - 25, V.Budget.Designations);
        }

        [Fact]
        public void MedicalCareLevelIsChecked()
        {
            Bad(Post("/act/medical", new { op = "care", who = "Ana", what = "lots" }));
            Assert.True(Post("/act/medical", new { op = "care", who = "Ana", what = "no meds" }).Ok);
            Assert.Equal("no_meds", Hands.Last.Value);
            Assert.True(Post("/act/medical", new { op = "operate", who = "Ana" }).Ok);     // empty what: the hand lists the choices
        }

        [Fact]
        public void ExecuteIsRefusedUnlessTheSettingIsOn()
        {
            var body = new { op = "mode", who = "Mira", mode = "execute" };
            var r = Post("/act/prisoner", body);
            Assert.False(r.Ok); Assert.Equal("refused", r.Error);
            Assert.Contains("has not let the narrator execute prisoners", (string)r.Body["detail"]);
            Assert.DoesNotContain(Hands.Calls, c => c.StartsWith("prisoner"));
            V.Settings.MayExecutePrisoners = true;
            Assert.True(Post("/act/prisoner", body).Ok);
            Assert.True(Post("/act/prisoner", new { op = "mode", who = "Mira", mode = "recruit" }).Ok);
            Bad(Post("/act/prisoner", new { op = "mode", who = "Mira", mode = "torture" }));
            Assert.True(Post("/act/prisoner", new { op = "free", who = "Mira" }).Ok);
        }

        [Fact]
        public void SlaughterCarriesTheNamedPetSetting()
        {
            Assert.True(Post("/act/animal", new { op = "slaughter", who = "Rex" }).Ok);
            Assert.False(Hands.Last.AllowNamed);
            V.Settings.MaySlaughterPets = true;
            Assert.True(Post("/act/animal", new { op = "slaughter", who = "Rex" }).Ok);
            Assert.True(Hands.Last.AllowNamed);
            Bad(Post("/act/animal", new { op = "train", who = "Rex" }));
            Bad(Post("/act/animal", new { op = "area", who = "Rex" }));
        }

        [Fact]
        public void SocialAndSettingsJson()
        {
            Assert.True(Post("/act/social", new { op = "party" }).Ok);
            Bad(Post("/act/social", new { op = "other" }));
            Assert.True(Post("/act/social", new { op = "wedding", who = "Ana" }).Ok);
            Assert.False((bool)V.Settings.ToJson()["may_execute_prisoners"]);
            Assert.False((bool)V.Settings.ToJson()["may_slaughter_pets"]);
            V.Settings.MaySlaughterPets = true;
            Assert.True((bool)V.Settings.ToJson()["may_slaughter_pets"]);
        }

        [Fact]
        public void HandsWithoutTheCommandInterfaceRefuse()
        {
            var h2 = new Harness(); var v2 = new Vivarium();
            VivariumRoutes.Register(h2.Router, () => v2, () => new FakeBioHands());
            var r = h2.Call("POST", "/act/job", "{\"op\":\"stay\",\"who\":\"Ana\",\"op_id\":\"z1\"}");
            Assert.False(r.Ok); Assert.Contains("command hands are not available", (string)r.Body["detail"]);
        }

        static void Bad(ApiResult r) { Assert.False(r.Ok); Assert.Equal("bad_request", r.Error); Assert.Equal(400, r.Status); }
    }

    public class StewardCommandTests
    {
        static JObject Digest(string cmd)
        {
            var d = StewardTestData.Clone(StewardTestData.Sample);
            if (cmd != null) d["cmd"] = JObject.Parse(cmd);
            return d;
        }
        static StewardSession Sess(JObject d) => new StewardKit().Session(d, null, false);
        static string[] Names(StewardSession s) => s.Cmd.Tools.Select(t => t.Name).ToArray();
        static (string verb, JObject body) Body(StewardSession s, string tool, object a)
        {
            var (v, err) = s.Tools.First(t => t.Name == tool).CheckArgs(JObject.FromObject(a), false);
            Assert.Null(err);
            return s.Cmd.Body(tool, v);
        }

        [Fact]
        public void NoCmdBlockNoTools()
        {
            Assert.Empty(Sess(Digest(null)).Cmd.Tools);
            Assert.DoesNotContain(Sess(Digest(null)).Tools, t => CommandKit.ToolNames.Contains(t.Name));
        }

        [Fact]
        public void EachToolOnlyWithItsData()
        {
            Assert.Equal(new[] { "job" }, Names(Sess(Digest("{}"))));
            Assert.Equal(new[] { "job", "medical" }, Names(Sess(Digest("{\"patients\":[\"Bram\"]}"))));
            Assert.Equal(new[] { "job", "medical" }, Names(Sess(Digest("{\"surgery\":true}"))));
            Assert.Equal(new[] { "job", "prisoner", "animal", "social" },
                Names(Sess(Digest("{\"prisoners\":[\"Mira\"],\"wild\":[\"boar\"],\"social\":true}"))));
        }

        [Fact]
        public void PrisonerModesFollowIdeologyAndSetting()
        {
            List<string> Modes(string cmd) => Sess(Digest(cmd)).Cmd.Tools.First(t => t.Name == "prisoner").Params.First(p => p.Name == "mode").Choices;
            var plain = Modes("{\"prisoners\":[\"Mira\"]}");
            Assert.DoesNotContain("execute", plain); Assert.DoesNotContain("convert", plain); Assert.Contains("recruit", plain);
            var full = Modes("{\"prisoners\":[\"Mira\"],\"ideo\":true,\"may_execute\":true}");
            Assert.Contains("execute", full); Assert.Contains("convert", full); Assert.Contains("enslave", full);
        }

        [Fact]
        public void BodiesMapToVerbs()
        {
            var s = Sess(Digest("{\"patients\":[\"Bram\"],\"prisoners\":[\"Mira\"],\"animals\":[\"Rex\"],\"social\":true}"));
            var j = Body(s, "job", new { op = "rescue", who = "ana", target = "Bram", why = "since he is down" });
            Assert.Equal("job", j.verb);
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"rescue\",\"who\":\"Ana\",\"target\":\"Bram\",\"why\":\"since he is down\"}"), j.body), j.body.ToString());
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"care\",\"who\":\"Bram\",\"what\":\"best\"}"), Body(s, "medical", new { op = "care", who = "Bram", what = "best" }).body));
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"mode\",\"who\":\"Mira\",\"mode\":\"recruit\"}"), Body(s, "prisoner", new { op = "mode", who = "Mira", mode = "recruit" }).body));
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"train\",\"who\":\"Rex\",\"what\":\"rescue\"}"), Body(s, "animal", new { op = "train", who = "Rex", what = "rescue" }).body));
            Assert.True(JToken.DeepEquals(JObject.Parse("{\"op\":\"party\"}"), Body(s, "social", new { op = "party" }).body));
        }

        [Fact]
        public void UnknownColonistListsTheColonists()
        {
            var s = Sess(Digest("{}"));
            var (v, _) = s.Tools.First(t => t.Name == "job").CheckArgs(JObject.FromObject(new { op = "stay", who = "Zed" }), false);
            var e = Assert.ThrowsAny<Exception>(() => s.Cmd.Body("job", v));
            Assert.Contains("no colonist named Zed", e.Message); Assert.Contains("Ana", e.Message);
        }

        [Fact]
        public void ActingAndRenderCost()
        {
            var s = Sess(Digest("{\"patients\":[\"Bram\"],\"prisoners\":[\"Mira\"],\"animals\":[\"Rex\"],\"social\":true,\"ideo\":true,\"may_execute\":true}"));
            foreach (var n in CommandKit.ToolNames) Assert.Contains(n, s.Acting);
            string all = Prompt.RenderTools(s.Cmd.Tools);
            Console.WriteLine("COMMAND RenderTools chars=" + all.Length);
            Assert.True(all.Length < 3000, all.Length.ToString());
            Console.WriteLine("COMMAND job-only chars=" + Prompt.RenderTools(Sess(Digest("{}")).Cmd.Tools).Length); Assert.True(Prompt.RenderTools(Sess(Digest("{}")).Cmd.Tools).Length < 1000);
            Assert.True(Prompt.RenderTools(s.Tools).Length < 13500, Prompt.RenderTools(s.Tools).Length.ToString());   // the whole tool section of a full colony (build + every cmd tool + reads): 12.9k chars, ~3.2k tokens
        }

        [Fact]
        public void DigestLineNamesPeopleAndAnimals()
        {
            string line = CommandKit.Line(Digest("{\"patients\":[\"Bram\"],\"prisoners\":[\"Mira\"],\"wild\":[\"boar\"]}"));
            Assert.Equal("PEOPLE: hurt/sick: Bram; prisoners: Mira; tameable wild: boar", line);
            Assert.Equal("", CommandKit.Line(Digest("{}")));
            Assert.Contains("PEOPLE:", StewardDigest.RenderDigest(Digest("{\"prisoners\":[\"Mira\"]}")));
        }
    }
}
