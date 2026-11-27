using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorldLLM.Bridge;
using Xunit;

namespace RimWorldLLM.Tests
{
    /// <summary>
    /// A simulated long unattended session (20 game days at 3x, an hour per step) driving every Verse-free log and book the mod
    /// keeps, with worst-case text sizes. Asserts each cap holds, the saved JSON stays under a bound, and the autosave rotation
    /// never writes a name that is not its own. Fast: well under 5 seconds.
    /// </summary>
    public class SoakTests
    {
        const int HoursPerDay = 24, Days = 20, TicksPerHour = 2500;

        [Fact]
        public void TwentyDaysAtTripleSpeedKeepEveryCap()
        {
            var rnd = new Random(12345);
            double clock = 0;
            var viv = new Vivarium();
            viv.Playing = true;
            var live = new ChatLive(() => clock);
            var ledger = new Ledger();
            var events = new EventRing();
            var desk = new TextDesk();
            var minds = new MindBook();
            var chron = new ChronicleBook();
            var art = new ArtBook();
            var avoid = new AvoidTable();
            var seen = new FirstCues.Seen();
            string big = new string('x', 700);   // longer than any cap: must be cut

            // rotating autosave: an in-memory "folder" with a stranger's saves in it
            var files = new Dictionary<string, DateTime> { { "Autosave-1", DateTime.UtcNow }, { "MyColony", DateTime.UtcNow } };
            var written = new HashSet<string>();
            int lastSave = 0, saves = 0;
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var rows = new ChatPanelRows();
            int liveSeen = -1; var liveLines = new List<LiveLine>();
            int maxChat = 0, maxRows = 0, maxMinds = 0, maxLedger = 0, maxActions = 0;
            int pawnId = 1000, art_n = 0;

            for (int step = 0; step < Days * HoursPerDay; step++)
            {
                int tick = step * TicksPerHour;
                clock += 20;   // 3x: an in-game hour is about 20 real seconds

                // chat: a mix of every kind, some at the full length
                string[] kinds = { "player", "narrator", "note", "colonist", "thought", "dream", "diary", "chronicle", "recap", "art", "action", "refused", "camera", "comment" };
                for (int i = 0; i < 14; i++)
                    viv.Chat.Add(kinds[rnd.Next(kinds.Length)], rnd.Next(4) == 0 ? big : "line " + step + "." + i, tick, rnd.Next(3), "Pawn" + rnd.Next(9), rnd.Next(3) == 0 ? "Other" : null);
                // actions, undo marks
                for (int i = 0; i < 4; i++) viv.Actions.Add("work", big, tick, "narrator", big);
                viv.Ledger.Add("build", big, tick, new List<NarratorMark> { new NarratorMark { Kind = "blueprint", Def = "Wall" } });
                // live streams: begun, ended, abandoned (timed out)
                string id = live.Begin("narrator");
                live.Partial(id, big);
                if (step % 3 == 0) live.End(id, "done " + step); else if (step % 3 == 1) live.End(id); // else left to time out
                live.Pump(viv.Chat, tick);
                live.Refresh(ref liveSeen, liveLines);
                // events
                for (int i = 0; i < 40; i++) events.Add(new JObject { ["type"] = "x", ["summary"] = big });
                // ledger: raids and weather that never close, quests of every id
                ledger.OpenEntry("raid", "raid:" + step, tick, null, "raid");
                ledger.OpenEntry("weather", "weather:" + step, tick, null, "weather");
                ledger.PruneOlderThan(tick, 4 * 60000, "raid");
                ledger.PruneOlderThan(tick, 6 * 60000, "weather");
                // text desk: queue and fill many items of each kind
                foreach (var k in TextKinds.All)
                    for (int i = 0; i < 25; i++)
                    {
                        string tid = k.Id + ":" + (step * 100 + i);
                        desk.Enqueue(new TextItem { Id = tid, Kind = k.Id }, tick);
                        string err, det;
                        desk.Fill(tid, new string('a', Math.Max(k.MinLen, 20)) + " " + new string('b', 10), k.WantsTitle ? "T" : null, tick, out err, out det);
                    }
                // minds of pawns that come and go, thoughts and diaries
                for (int i = 0; i < 3; i++) { int p = ++pawnId; minds.AddThought(p, tick, big); minds.AddDiary(p, tick, big); }
                // chronicle, art, avoid, first cues
                chron.Add(step / HoursPerDay, step % 5 == 0 ? "major" : "day", big + big + big, tick);
                for (int i = 0; i < 4; i++) art.Set(++art_n, big, tick);
                for (int i = 0; i < 30; i++) avoid.Add(rnd.Next(100000), rnd.Next(100000), tick, 5000);
                for (int i = 0; i < 400; i++) seen.First(rnd.Next(1 << 20), "cue");
                // the panel's rows, as the draw loop would
                bool changed = rows.SyncChat(viv.Chat.Entries, viv.Chat.LastSeq, 31, 400, 1, w => true, e => e.Text, t => 22f);
                rows.MergeIfDirty();

                // autosave rotation (game autosave off, narrator on)
                var dec = AutosaveRotation.Decide(new AutosaveRotation.Input { Enabled = true, NarratorPlays = true, GameAutosaveOn = false, NowTick = tick, LastSaveTick = lastSave, EveryDays = 1f });
                if (dec == AutosaveRotation.Decision.Save)
                {
                    int slot = AutosaveRotation.NextSlot(5, i => files.ContainsKey(AutosaveRotation.SlotName(i)) ? files[AutosaveRotation.SlotName(i)] : (DateTime?)null);
                    string name = AutosaveRotation.SlotName(slot);
                    Assert.True(AutosaveRotation.IsOurs(name));
                    files[name] = t0.AddHours(step); written.Add(name); lastSave = tick; saves++;
                }

                maxChat = Math.Max(maxChat, viv.Chat.Entries.Count);
                maxRows = Math.Max(maxRows, rows.Rows.Count);
                maxMinds = Math.Max(maxMinds, minds.Records.Count);
                maxLedger = Math.Max(maxLedger, ledger.Open.Count);
                maxActions = Math.Max(maxActions, ((JArray)viv.Actions.Export()).Count);
            }

            // caps
            Assert.True(maxChat <= ChatLog.Max, "chat " + maxChat);
            Assert.True(viv.Chat.Entries.All(e => e.Text.Length <= ChatLog.MaxText));
            Assert.True(maxActions <= ActionLog.Max);
            Assert.True(viv.Ledger.Count <= NarratorLedger.Max);
            Assert.True(maxLedger <= Ledger.MaxOpen, "ledger " + maxLedger);
            Assert.True(events.Count <= Const.EventRingSize);
            Assert.True(maxMinds <= MindBook.MaxRecords, "minds " + maxMinds);
            Assert.True(minds.Records.Values.All(r => r.Thoughts.Count <= MindBook.MaxThoughts && r.Diaries.Count <= MindBook.MaxDiaries));
            Assert.True(chron.Entries.Count <= ChronicleBook.MaxEntries);
            Assert.True(art.Entries.Count <= ArtBook.MaxEntries);
            Assert.True(avoid.Count <= AvoidTable.MaxEntries);
            Assert.True(seen.Count <= FirstCues.Seen.Cap);
            foreach (var k in TextKinds.All) Assert.True(desk.Count(k.Id) <= k.StoreCap && desk.Queued(k.Id) <= TextDesk.QueueCap);
            Assert.True(desk.Queue.Count <= TextDesk.QueueCap);
            Assert.True(live.Count <= 2);
            Assert.True(maxRows <= ChatPanelRows.MaxRows);
            Assert.True(rows.CacheCount <= 2 * ChatLog.Max + 1);

            // saved sizes
            int vivBytes = viv.Export().ToString(Newtonsoft.Json.Formatting.None).Length;
            Assert.True(vivBytes < 1_500_000, "vivarium json " + vivBytes);
            Assert.True(desk.Export().ToString(Newtonsoft.Json.Formatting.None).Length < 3_000_000);
            int mindBytes = minds.Records.Values.Sum(r => r.Thoughts.Sum(t => t.Value.Length) + r.Diaries.Sum(t => t.Value.Length));
            Assert.True(mindBytes < MindBook.MaxRecords * (MindBook.MaxThoughts * MindBook.MaxThoughtChars + MindBook.MaxDiaries * MindBook.MaxDiaryChars));

            // autosave: about one a day, rotating over five slots, never touching strangers
            Assert.InRange(saves, Days - 2, Days + 1);
            Assert.Equal(5, written.Count);
            Assert.True(written.All(AutosaveRotation.IsOurs));
            Assert.True(files.ContainsKey("MyColony") && files.ContainsKey("Autosave-1"));
        }
    }
}
