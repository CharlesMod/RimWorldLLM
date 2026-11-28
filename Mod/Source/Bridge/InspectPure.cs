using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // Verse-free half of the narrator's read tools inspect / find / survey (contract: tools/inspect/contract.md).
    // The game-side gatherer fills the FACT classes below; the formatters turn them into capped words-only text
    // ("#id" ids, never raw coordinates, "N cells <dir> of home"). Every formatter returns <= InspectText.Soft chars.

    /// <summary>The game hands for the three read tools. Each returns {text, ids?, total?} or {refused: reason}; null = no game.</summary>
    public interface IInspectHands
    {
        JObject Inspect(string target);
        JObject Find(string what, string near, string item);
        JObject Survey(string what);
    }

    /// <summary>Text helpers shared by every formatter.</summary>
    public static class InspectText
    {
        public const int Soft = 600, Hard = 700;
        static readonly string[] DirKeys = { "n", "ne", "e", "se", "s", "sw", "w", "nw" };
        static readonly string[] DirWords = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

        /// <summary>Trim to at most max chars (clamped to Hard) at a word boundary, adding "...".</summary>
        public static string Cap(string s, int max = Soft)
        {
            if (s == null) return "";
            if (max > Hard) max = Hard;
            s = s.Trim();
            if (s.Length <= max) return s;
            int cut = Math.Max(1, max - 3);
            int sp = s.LastIndexOf(' ', cut - 1 < 0 ? 0 : cut - 1);
            if (sp > max / 2) cut = sp;
            return s.Substring(0, cut).TrimEnd(' ', ',', ';', ':', '.', '-') + "...";
        }

        /// <summary>Compass word for a CompassKey ("ne" -> "north-east"); unknown or "here" returns the key itself.</summary>
        public static string DirWord(string key)
        {
            if (key == null) return "";
            int i = Array.IndexOf(DirKeys, key.Trim().ToLowerInvariant());
            return i < 0 ? key.Trim() : DirWords[i];
        }

        /// <summary>"at home" when dist &lt;= 2, else "N cells north of home".</summary>
        public static string Where(int dist, string dirWord)
        {
            if (dist <= 2) return "at home";
            string d = DirWord(dirWord);
            return dist + " cells " + (string.IsNullOrEmpty(d) ? "" : d + " ") + "of home";
        }

        /// <summary>Same, from cells (home and target): uses CombatPure Approach.Compass.</summary>
        public static string Where(int homeX, int homeZ, int x, int z)
        {
            int dist = (int)Math.Round(Math.Sqrt((double)(x - homeX) * (x - homeX) + (double)(z - homeZ) * (z - homeZ)));
            return Where(dist, dist <= 2 ? "" : Approach.Compass(homeX, homeZ, x, z));
        }

        /// <summary>Where plus the room: "14 cells north of home (in the stockpile)".</summary>
        public static string WhereIn(int dist, string dirWord, string room)
        {
            string w = Where(dist, dirWord);
            return string.IsNullOrEmpty(room) ? w : w + " (in " + room + ")";
        }

        internal static string Join(IEnumerable<string> parts, string sep)
        {
            return string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        internal static string Pct(double frac01) { return ((int)Math.Round(frac01 * 100)).ToString(CultureInfo.InvariantCulture) + "%"; }
        internal static string Num(double d) { return d.ToString("0.#", CultureInfo.InvariantCulture); }
        internal static string Sign(int n) { return (n >= 0 ? "+" : "") + n.ToString(CultureInfo.InvariantCulture); }
        internal static string Id(int id) { return id > 0 ? " #" + id.ToString(CultureInfo.InvariantCulture) : ""; }

        /// <summary>Severity fraction (0..1) in words: minor, moderate, serious, severe.</summary>
        public static string SeverityWord(double frac)
        {
            return frac < 0.25 ? "minor" : frac < 0.5 ? "moderate" : frac < 0.75 ? "serious" : "severe";
        }

        static string Plural(string w, int n) { return n == 1 || w.EndsWith("s") ? w : w + "s"; }
        internal static string Count(int n, string one) { return n + " " + Plural(one, n); }
    }

    // ------------------------------------------------------------------------------------------ pawn facts
    public sealed class InjuryFact
    {
        public string Part = "";            // "left leg"
        public string Label = "";           // "gunshot wound", "burn"
        public double Severity;             // 0..1 of the part's health
        public bool Bleeding, Infected, Permanent;
    }

    public sealed class DiseaseFact
    {
        public string Label = "";           // "flu"
        public double Severity;             // 0..1
        public double Immunity = -1;        // 0..1, -1 = none/unknown
        public bool Lethal;                 // shows as life-threatening
    }

    public sealed class NeedFact { public string Label = ""; public int Pct; }            // Pct 0..100 (100 = full)
    public sealed class ThoughtFact { public string Label = ""; public int Mood; }       // signed mood points
    public sealed class SkillFact { public string Label = ""; public int Level; public int Passion; }  // Passion 0 none, 1 minor, 2 major

    public sealed class PawnFacts
    {
        public const int NeedShowBelow = 30;     // needs at or above this percent are left out
        public string Kind = "colonist";         // colonist/animal/prisoner/guest/slave/enemy
        public string Name = ""; public int Id;
        public int Age = -1;                      // years, -1 unknown
        public string Xenotype = "";              // "" for baseliners
        public string Role = "";                  // ideology role or title, "" none
        public string Faction = "";               // for enemy/guest, optional
        public bool Downed;
        public string MentalBreak = "";           // current mental state label, "" none
        public List<InjuryFact> Injuries = new List<InjuryFact>();
        public List<DiseaseFact> Diseases = new List<DiseaseFact>();
        public double Pain = -1;                  // 0..1, -1 unknown
        public List<string> LowCapacities = new List<string>();   // already worded: "moving 60%"
        public List<NeedFact> Needs = new List<NeedFact>();       // all needs; the formatter keeps the low ones
        public int Mood = -1;                     // 0..100, -1 not applicable (animals/enemies)
        public List<ThoughtFact> Thoughts = new List<ThoughtFact>();
        public List<SkillFact> Skills = new List<SkillFact>();
        public string Weapon = ""; public List<string> Apparel = new List<string>();
        public string Job = ""; public List<string> Queue = new List<string>();
        public List<string> Relations = new List<string>();       // "wife Mara"
        public double RecruitResistance = -1;     // prisoners, -1 n/a
        public double Will = -1;                  // prisoners, -1 n/a
        public string InteractionMode = "";       // prisoners
        public int Suppression = -1;              // slaves, 0..100, -1 n/a
        public string Room = "";                  // room label, "" outdoors
        public int Dist; public string DirKey = "";   // from home; DirKey = Approach.CompassKey

        public static string Format(PawnFacts p)
        {
            var head = new List<string>();
            head.Add(p.Name + InspectText.Id(p.Id));
            head.Add(p.Kind);
            if (p.Age >= 0) head.Add("age " + p.Age);
            if (!string.IsNullOrEmpty(p.Xenotype)) head.Add(p.Xenotype);
            if (!string.IsNullOrEmpty(p.Role)) head.Add(p.Role);
            if (!string.IsNullOrEmpty(p.Faction) && (p.Kind == "enemy" || p.Kind == "guest")) head.Add(p.Faction);
            var parts = new List<string> { string.Join(", ", head) };

            if (p.Downed) parts.Add("DOWNED");
            if (!string.IsNullOrEmpty(p.MentalBreak)) parts.Add("MENTAL BREAK: " + p.MentalBreak);
            parts.Add(InjuryText(p.Injuries));
            parts.Add(DiseaseText(p.Diseases));
            string pain = p.Pain >= 0.15 ? "pain " + InspectText.Pct(p.Pain) : "";
            string low = p.LowCapacities.Count > 0 ? "low: " + string.Join(", ", p.LowCapacities.Take(4)) : "";
            parts.Add(InspectText.Join(new[] { pain, low }, "; "));
            var needs = p.Needs.Where(n => n.Pct < NeedShowBelow).OrderBy(n => n.Pct).Take(4).Select(n => n.Label + " " + n.Pct + "%").ToList();
            if (needs.Count > 0) parts.Add("needs: " + string.Join(", ", needs));
            if (p.Mood >= 0)
            {
                string m = "mood " + p.Mood + "%";
                var th = p.Thoughts.Where(t => t.Mood != 0).OrderByDescending(t => Math.Abs(t.Mood)).Take(3).Select(t => InspectText.Sign(t.Mood) + " " + t.Label).ToList();
                if (th.Count > 0) m += " (" + string.Join(", ", th) + ")";
                parts.Add(m);
            }
            if (!string.IsNullOrEmpty(p.Job))
            {
                string j = "doing " + p.Job;
                if (p.Queue.Count > 0) j += ", then " + string.Join(", ", p.Queue.Take(2));
                parts.Add(j);
            }
            string loc = !string.IsNullOrEmpty(p.Room) || p.DirKey != "" || p.Dist > 0 ? InspectText.WhereIn(p.Dist, p.DirKey, p.Room) : "";
            if (loc != "") parts.Add(loc);
            var pr = new List<string>();
            if (p.RecruitResistance >= 0) pr.Add("recruit resistance " + InspectText.Num(p.RecruitResistance));
            if (p.Will >= 0) pr.Add("will " + InspectText.Num(p.Will));
            if (!string.IsNullOrEmpty(p.InteractionMode)) pr.Add("mode " + p.InteractionMode);
            if (p.Suppression >= 0) pr.Add("suppression " + p.Suppression + "%");
            if (pr.Count > 0) parts.Add(string.Join(", ", pr));
            var sk = p.Skills.OrderByDescending(s => s.Level).ThenByDescending(s => s.Passion).Take(4)
                .Select(s => s.Label + " " + s.Level + (s.Passion >= 2 ? " (major passion)" : s.Passion == 1 ? " (passion)" : "")).ToList();
            if (sk.Count > 0) parts.Add("skills: " + string.Join(", ", sk));
            var gear = new List<string>();
            if (!string.IsNullOrEmpty(p.Weapon)) gear.Add("wielding " + p.Weapon);
            if (p.Apparel.Count > 0) gear.Add("wearing " + string.Join(", ", p.Apparel.Take(4)));
            if (gear.Count > 0) parts.Add(string.Join("; ", gear));
            if (p.Relations.Count > 0) parts.Add(string.Join(", ", p.Relations.Take(3)));
            return InspectText.Cap(InspectText.Join(parts, ". "));
        }

        /// <summary>Bleeding and infected first, then by severity; at most 5 listed.</summary>
        public static string InjuryText(IList<InjuryFact> list)
        {
            if (list == null || list.Count == 0) return "";
            var ord = list.OrderByDescending(i => i.Bleeding).ThenByDescending(i => i.Infected).ThenByDescending(i => i.Severity).ToList();
            var rows = ord.Take(5).Select(i =>
            {
                var tags = new List<string> { InspectText.SeverityWord(i.Severity) };
                if (i.Bleeding) tags.Insert(0, "bleeding");
                if (i.Infected) tags.Insert(i.Bleeding ? 1 : 0, "infected");
                if (i.Permanent) tags.Add("permanent");
                return (string.IsNullOrEmpty(i.Part) ? "" : i.Part + ": ") + i.Label + " (" + string.Join(", ", tags) + ")";
            }).ToList();
            string s = "injuries: " + string.Join("; ", rows);
            if (ord.Count > 5) s += "; +" + (ord.Count - 5) + " more";
            return s;
        }

        public static string DiseaseText(IList<DiseaseFact> list)
        {
            if (list == null || list.Count == 0) return "";
            var rows = list.OrderByDescending(d => d.Lethal).ThenByDescending(d => d.Severity).Take(3).Select(d =>
                d.Label + " (" + (d.Lethal ? "life-threatening, " : "") + "severity " + InspectText.Pct(d.Severity) + (d.Immunity >= 0 ? ", immunity " + InspectText.Pct(d.Immunity) : "") + ")");
            return "illness: " + string.Join("; ", rows);
        }
    }

    // ------------------------------------------------------------------------------------------ room / building / stack / zone facts
    public sealed class RoomFacts
    {
        public int Id; public string Role = "";           // "kitchen", "bedroom", "none"
        public string Label = "";                         // "the kitchen"
        public string ImpressiveLabel = ""; public double Impressiveness = -1;   // game's word + stat
        public double Beauty = double.NaN, Cleanliness = double.NaN;             // stat values
        public int Cells; public string SpaceLabel = "";                          // "cramped", "spacious"
        public double Temperature = double.NaN;                                   // degrees C
        public List<string> Contents = new List<string>();                        // "3 beds", "stove #44"
        public List<string> Owners = new List<string>();

        public static string Format(RoomFacts r)
        {
            string name = !string.IsNullOrEmpty(r.Label) ? r.Label : "room";
            var head = name + (r.Id > 0 ? " (room " + r.Id + ")" : "") + (!string.IsNullOrEmpty(r.Role) && r.Role != "none" && name.IndexOf(r.Role, StringComparison.OrdinalIgnoreCase) < 0 ? ", " + r.Role : "");
            var parts = new List<string> { head };
            var q = new List<string>();
            if (!string.IsNullOrEmpty(r.ImpressiveLabel)) q.Add(r.ImpressiveLabel + (r.Impressiveness >= 0 ? " " + InspectText.Num(r.Impressiveness) : ""));
            if (!double.IsNaN(r.Beauty)) q.Add("beauty " + InspectText.Num(r.Beauty));
            if (!double.IsNaN(r.Cleanliness)) q.Add("cleanliness " + InspectText.Num(r.Cleanliness));
            if (q.Count > 0) parts.Add(string.Join(", ", q));
            var s = new List<string>();
            if (r.Cells > 0 || r.SpaceLabel != "") s.Add((r.SpaceLabel != "" ? r.SpaceLabel + ", " : "") + (r.Cells > 0 ? r.Cells + " cells" : "").TrimEnd(',', ' '));
            if (!double.IsNaN(r.Temperature)) s.Add(InspectText.Num(r.Temperature) + " C");
            if (s.Count > 0) parts.Add(string.Join(", ", s.Where(x => x != "")));
            if (r.Contents.Count > 0) parts.Add("holds " + string.Join(", ", r.Contents.Take(8)) + (r.Contents.Count > 8 ? ", +" + (r.Contents.Count - 8) + " more" : ""));
            if (r.Owners.Count > 0) parts.Add("owners: " + string.Join(", ", r.Owners.Take(4)));
            return InspectText.Cap(InspectText.Join(parts, ". "));
        }
    }

    public sealed class BuildingFacts
    {
        public string Label = ""; public int Id;
        public bool HasPower; public bool PowerOn; public int? NetWatts;     // NetWatts: +gen / -use; null = unknown
        public int FuelPct = -1;                                            // 0..100, -1 n/a
        public List<string> Bills = new List<string>();                      // "make steel x10 (running)"
        public List<string> Storage = new List<string>();                    // "steel x200"
        public int HpPct = 100; public bool Forbidden; public bool BrokenDown;
        public string Room = "";
        public string Owner = "";                                            // bed owner etc.
        public string Note = "";                                             // anything else worth a line

        public static string Format(BuildingFacts b)
        {
            var parts = new List<string> { b.Label + InspectText.Id(b.Id) };
            if (b.BrokenDown) parts.Add("BROKEN DOWN");
            if (b.HasPower)
            {
                string p = b.PowerOn ? "power on" : "power OFF";
                if (b.NetWatts.HasValue) p += ", " + InspectText.Sign(b.NetWatts.Value) + " W";
                parts.Add(p);
            }
            if (b.FuelPct >= 0) parts.Add("fuel " + b.FuelPct + "%");
            if (b.HpPct < 100) parts.Add("hit points " + b.HpPct + "%");
            if (b.Forbidden) parts.Add("forbidden");
            if (!string.IsNullOrEmpty(b.Owner)) parts.Add("owner " + b.Owner);
            if (b.Bills.Count > 0) parts.Add("bills: " + string.Join("; ", b.Bills.Take(4)));
            if (b.Storage.Count > 0) parts.Add("holds " + string.Join(", ", b.Storage.Take(6)) + (b.Storage.Count > 6 ? ", +" + (b.Storage.Count - 6) + " more" : ""));
            if (!string.IsNullOrEmpty(b.Room)) parts.Add("in " + b.Room);
            if (!string.IsNullOrEmpty(b.Note)) parts.Add(b.Note);
            return InspectText.Cap(InspectText.Join(parts, ". "));
        }
    }

    public sealed class StackFacts
    {
        public string Label = ""; public int Id; public int Count = 1;
        public string Quality = "";             // "good", "" none
        public int HpPct = 100;
        public double RotDays = -1;             // days until it rots, -1 n/a
        public bool Forbidden;
        public string Holder = "";              // pawn carrying/wearing it
        public string Room = ""; public int Dist; public string DirKey = "";

        public static string Format(StackFacts s)
        {
            string head = s.Label + (s.Count > 1 ? " x" + s.Count : "") + InspectText.Id(s.Id);
            var parts = new List<string> { head };
            if (!string.IsNullOrEmpty(s.Quality)) parts.Add(s.Quality + " quality");
            if (s.HpPct < 100) parts.Add("hit points " + s.HpPct + "%");
            if (s.RotDays >= 0) parts.Add(s.RotDays < 1 ? "rots within a day" : "rots in " + InspectText.Num(Math.Round(s.RotDays, 1)) + " days");
            if (s.Forbidden) parts.Add("forbidden");
            parts.Add(!string.IsNullOrEmpty(s.Holder) ? "held by " + s.Holder : InspectText.WhereIn(s.Dist, s.DirKey, s.Room));
            return InspectText.Cap(InspectText.Join(parts, ". "));
        }
    }

    public sealed class ZoneFacts
    {
        public string Label = ""; public int Id;
        public string Kind = "grow";            // grow / stockpile / area / other
        public string Crop = ""; public int GrowthPct = -1;      // grow zones: average growth 0..100
        public int Cells;
        public List<string> Contents = new List<string>();       // stockpile contents "steel x200"
        public string Priority = "";

        public static string Format(ZoneFacts z)
        {
            var parts = new List<string> { z.Label + (z.Id > 0 ? " (zone " + z.Id + ")" : "") + ", " + z.Kind + (z.Cells > 0 ? ", " + z.Cells + " cells" : "") };
            if (z.Kind == "grow" && !string.IsNullOrEmpty(z.Crop)) parts.Add("growing " + z.Crop + (z.GrowthPct >= 0 ? ", " + z.GrowthPct + "% grown" : ""));
            if (!string.IsNullOrEmpty(z.Priority)) parts.Add("priority " + z.Priority);
            if (z.Contents.Count > 0) parts.Add("holds " + string.Join(", ", z.Contents.Take(8)) + (z.Contents.Count > 8 ? ", +" + (z.Contents.Count - 8) + " more" : ""));
            else if (z.Kind == "stockpile") parts.Add("empty");
            return InspectText.Cap(InspectText.Join(parts, ". "));
        }
    }

    // ------------------------------------------------------------------------------------------ find
    public enum FindKind { Item, Wounded, Idle, Hostiles, Fires, Corpses, Forbidden, BedFree, DoorOut, Tameable, Rotting, Sick, Downed, Prisoners, Animals, Guests, Building }

    public static class FindKinds
    {
        /// <summary>The exact words the model may use, in contract order.</summary>
        public static readonly string[] Words = { "item", "wounded", "idle", "hostiles", "fires", "corpses", "forbidden", "bed_free", "door_out", "tameable", "rotting", "sick", "downed", "prisoners", "animals", "guests", "building" };

        public static string Word(FindKind k) { return Words[(int)k]; }
        public static string List() { return string.Join(", ", Words); }

        public static bool Parse(string s, out FindKind kind)
        {
            kind = FindKind.Item;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string w = s.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            int i = Array.IndexOf(Words, w);
            if (i < 0) return false;
            kind = (FindKind)i; return true;
        }

        /// <summary>Plain label for headings: "free beds", "wounded colonists".</summary>
        public static string Label(FindKind k, string item = null)
        {
            switch (k)
            {
                case FindKind.Item: return string.IsNullOrWhiteSpace(item) ? "items" : item.Trim();
                case FindKind.Building: return string.IsNullOrWhiteSpace(item) ? "buildings" : item.Trim();
                case FindKind.Wounded: return "wounded";
                case FindKind.Idle: return "idle colonists";
                case FindKind.Hostiles: return "hostiles";
                case FindKind.Fires: return "fires";
                case FindKind.Corpses: return "corpses";
                case FindKind.Forbidden: return "forbidden things";
                case FindKind.BedFree: return "free beds";
                case FindKind.DoorOut: return "outside doors";
                case FindKind.Tameable: return "tameable animals";
                case FindKind.Rotting: return "rotting things";
                case FindKind.Sick: return "sick";
                case FindKind.Downed: return "downed";
                case FindKind.Prisoners: return "prisoners";
                case FindKind.Animals: return "animals";
                default: return "guests";
            }
        }
    }

    public sealed class FindRow
    {
        public string Label = ""; public int Count = 1; public int Id;   // Id 0 = no id (e.g. a fire)
        public int Dist; public string DirKey = ""; public string RoomLabel = "";
        public FindRow() { }
        public FindRow(string label, int count, int id, int dist, string dirKey, string room) { Label = label; Count = count; Id = id; Dist = dist; DirKey = dirKey; RoomLabel = room; }
        public override string ToString()
        {
            return Label + (Count > 1 ? " x" + Count : "") + InspectText.Id(Id) + ", " + InspectText.WhereIn(Dist, DirKey, RoomLabel);
        }
    }

    public static class InspectFind
    {
        public const int MaxRows = 8;

        /// <summary>"steel: steel x400 #12, 14 cells north of home (in the stockpile); ... 12 total; showing 8". Rows are kept whole; fewer shown if the cap bites.</summary>
        public static string FormatFind(string kindLabel, IList<FindRow> rows, int total, int cap = MaxRows)
        {
            kindLabel = string.IsNullOrWhiteSpace(kindLabel) ? "matches" : kindLabel.Trim();
            if (rows == null || rows.Count == 0 || total <= 0) return InspectText.Cap("No " + kindLabel + " found.");
            string head = kindLabel + ": ";
            int budget = InspectText.Soft - head.Length - 40;
            var used = new List<string>(); int len = 0;
            foreach (var r in rows.Take(Math.Max(1, cap)))
            {
                string s = r.ToString();
                if (used.Count > 0 && len + s.Length + 2 > budget) break;
                used.Add(s); len += s.Length + 2;
            }
            if (total < used.Count) total = used.Count;
            return InspectText.Cap(head + string.Join("; ", used) + ". " + total + " total; showing " + used.Count);
        }

        /// <summary>'3 match "hay": Hay #12 (kitchen), Hay #15, Hay #19 (the stockpile); say #id'.</summary>
        public static string Ambiguity(string query, IList<(string name, int id, string room)> cands)
        {
            int n = cands.Count;
            var show = cands.Take(6).Select(c => c.name + InspectText.Id(c.id) + (string.IsNullOrEmpty(c.room) ? "" : " (" + c.room + ")")).ToList();
            string more = n > 6 ? ", +" + (n - 6) + " more" : "";
            return InspectText.Cap(n + " match \"" + query + "\": " + string.Join(", ", show) + more + ". say #id");
        }
    }

    public sealed class NameMatch
    {
        public string Tier = "none";     // none, id, exact, prefix, substring
        public List<(string name, int id)> Matches = new List<(string name, int id)>();
        public bool Single { get { return Matches.Count == 1; } }
    }

    public static class InspectNames
    {
        /// <summary>"#12" or "room 12" (also "id 12"): returns the number.</summary>
        public static bool TryId(string q, out int id)
        {
            id = 0;
            if (q == null) return false;
            string s = q.Trim().ToLowerInvariant();
            if (s.StartsWith("#")) s = s.Substring(1);
            else if (s.StartsWith("room ")) s = s.Substring(5);
            else if (s.StartsWith("id ")) s = s.Substring(3);
            else return false;
            return int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        static string Norm(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.StartsWith("the ")) s = s.Substring(4);
            return s;
        }

        /// <summary>#id / room N first, then exact (case-insensitive), then prefix, then substring; the first tier with any hit wins. A leading "the " is ignored on both sides.</summary>
        public static NameMatch MatchName(string query, IList<(string name, int id)> items)
        {
            var r = new NameMatch();
            if (items == null || string.IsNullOrWhiteSpace(query)) return r;
            int id;
            if (TryId(query, out id))
            {
                var hit = items.Where(i => i.id == id).ToList();
                if (hit.Count > 0) { r.Tier = "id"; r.Matches = hit; }
                return r;
            }
            string q = Norm(query);
            if (q == "") return r;
            Func<Func<string, bool>, List<(string name, int id)>> pick = f => items.Where(i => f(Norm(i.name))).ToList();
            var m = pick(n => n == q);
            if (m.Count > 0) { r.Tier = "exact"; r.Matches = m; return r; }
            m = pick(n => n.StartsWith(q, StringComparison.Ordinal));
            if (m.Count > 0) { r.Tier = "prefix"; r.Matches = m; return r; }
            m = pick(n => n.Contains(q));
            if (m.Count > 0) { r.Tier = "substring"; r.Matches = m; }
            return r;
        }
    }

    // ------------------------------------------------------------------------------------------ survey: stock
    public sealed class StockRow { public string Label = ""; public int Stored, Loose; public StockRow() { } public StockRow(string l, int s, int lo) { Label = l; Stored = s; Loose = lo; } }

    public sealed class StockFacts
    {
        public const double DefaultNutritionPerPawnDay = 1.6;   // same as Routes.FoodDays
        public List<StockRow> Rows = new List<StockRow>();        // key resources (steel, wood, silver, ...), medicine and components may be included too
        public int Colonists, Prisoners, Injured;                 // mouths = Colonists + Prisoners
        public double FoodNutritionStored;                        // nutrition units (meals + raw + hay-excluded), not item count
        public double NutritionPerPawnDay = DefaultNutritionPerPawnDay;
        public int Medicine;                                      // medicine item count (all kinds)
        public int Components;                                    // component count

        public int Mouths { get { return Colonists + Prisoners; } }

        /// <summary>Days of food left; -1 when nobody eats.</summary>
        public double FoodDays { get { return InspectStock.DaysLeft(FoodNutritionStored, Mouths * NutritionPerPawnDay); } }
        public double MedicineDays { get { return InspectStock.DaysLeft(Medicine, InspectStock.MedicinePerDay(Colonists, Injured)); } }
        public double ComponentDays { get { return InspectStock.DaysLeft(Components, InspectStock.ComponentsPerDay(Colonists)); } }

        public static string Format(StockFacts s) { return InspectStock.FormatStock(s); }
    }

    public static class InspectStock
    {
        /// <summary>have / perDay; -1 when perDay &lt;= 0 (no use: unlimited).</summary>
        public static double DaysLeft(double have, double perDay)
        {
            if (perDay <= 0) return -1;
            if (have <= 0) return 0;
            return have / perDay;
        }

        /// <summary>1 per 3 colonists per day, raised to half a dose per injured pawn when that is more.</summary>
        public static double MedicinePerDay(int colonists, int injured)
        {
            return Math.Max(colonists / 3.0, injured * 0.5);
        }

        /// <summary>A fifth of a component per colonist per day (repairs, building, crafting).</summary>
        public static double ComponentsPerDay(int colonists) { return colonists * 0.2; }

        public static string DaysWord(double days)
        {
            if (days < 0) return "no use";
            if (days < 0.1) return "none";
            if (days < 1) return "under a day";
            if (days >= 100) return "100+ days";
            return days < 10 ? InspectText.Num(Math.Round(days, 1)) + " days" : Math.Round(days).ToString(CultureInfo.InvariantCulture) + " days";
        }

        static string Warn(double days, double low) { return days >= 0 && days < low ? " LOW" : ""; }

        public static string FormatStock(StockFacts s)
        {
            var parts = new List<string>();
            if (s.Mouths > 0)
                parts.Add("food " + DaysWord(s.FoodDays) + Warn(s.FoodDays, 3) + " (" + InspectText.Num(Math.Round(s.FoodNutritionStored)) + " nutrition, " + s.Mouths + " mouths)");
            parts.Add("medicine " + s.Medicine + (s.Colonists > 0 ? " (" + DaysWord(s.MedicineDays) + ")" + Warn(s.MedicineDays, 3) : ""));
            parts.Add("components " + s.Components + (s.Colonists > 0 ? " (" + DaysWord(s.ComponentDays) + ")" : ""));
            var rows = s.Rows.Where(r => r.Stored + r.Loose > 0).OrderByDescending(r => r.Stored + r.Loose).Take(10)
                .Select(r => r.Label + " " + (r.Stored + r.Loose) + (r.Stored > 0 && r.Loose > 0 ? " (" + r.Stored + " stored, " + r.Loose + " loose)" : r.Loose > 0 ? " (loose)" : ""));
            string head = "Days left: " + string.Join("; ", parts);
            string res = rows.Any() ? ". Stock: " + string.Join(", ", rows) : "";
            return InspectText.Cap(head + res);
        }
    }

    // ------------------------------------------------------------------------------------------ survey: threats
    public sealed class ThreatEntry
    {
        public string Kind = "hostile"; public int Count = 1;
        public int Dist; public string DirKey = "";
        public bool Attacking, Manhunter, Mech, Siege;

        /// <summary>From CombatPure's grouping: Dist and the direction key come from the group's centre against home.</summary>
        public static ThreatEntry From(ThreatGroup g, int homeX, int homeZ, bool attacking, bool manhunter, bool mech, bool siege)
        {
            return new ThreatEntry
            {
                Kind = string.IsNullOrEmpty(g.Kind) ? "hostile" : g.Kind, Count = g.Count, Dist = g.Dist,
                DirKey = g.Dist <= 2 ? "" : Approach.CompassKey(homeX, homeZ, g.X, g.Z),
                Attacking = attacking, Manhunter = manhunter, Mech = mech, Siege = siege
            };
        }
    }

    public sealed class ThreatFacts
    {
        public List<ThreatEntry> Groups = new List<ThreatEntry>();
        public int Fires; public int NearestFireDist; public string NearestFireDirKey = "";
        public List<string> Conditions = new List<string>();   // "toxic fallout", "flashstorm", "heat wave"
        public int Insects; public int InsectDist; public string InsectDirKey = "";   // infestation / insects seen
        public string Unknown = "";                             // e.g. "2 raiders signalled but not seen"

        public static string Format(ThreatFacts t) { return InspectThreats.FormatThreats(t); }
    }

    public static class InspectThreats
    {
        public static string FormatThreats(ThreatFacts t)
        {
            var parts = new List<string>();
            var groups = t.Groups.OrderByDescending(g => g.Attacking).ThenBy(g => g.Dist).ToList();
            foreach (var g in groups.Take(4))
            {
                var tags = new List<string>();
                if (g.Attacking) tags.Add("attacking");
                if (g.Manhunter) tags.Add("manhunters");
                if (g.Mech) tags.Add("mechs");
                if (g.Siege) tags.Add("siege");
                parts.Add(g.Count + " " + g.Kind + (tags.Count > 0 ? " (" + string.Join(", ", tags) + ")" : "") + ", " + InspectText.Where(g.Dist, g.DirKey));
            }
            if (groups.Count > 4) parts.Add("+" + (groups.Count - 4) + " more groups");
            if (t.Fires > 0) parts.Add(InspectText.Count(t.Fires, "fire") + ", nearest " + InspectText.Where(t.NearestFireDist, t.NearestFireDirKey));
            if (t.Insects > 0) parts.Add(t.Insects + " insects, " + InspectText.Where(t.InsectDist, t.InsectDirKey));
            if (t.Conditions.Count > 0) parts.Add("conditions: " + string.Join(", ", t.Conditions.Take(4)));
            if (!string.IsNullOrEmpty(t.Unknown)) parts.Add(t.Unknown);
            if (parts.Count == 0) return "Calm: nothing hostile";
            return InspectText.Cap("Threats: " + string.Join("; ", parts));
        }
    }
}
