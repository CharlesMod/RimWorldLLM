using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Bridge
{
    /// <summary>How a shot moves the camera.</summary>
    public enum ShotStyle { Follow = 0, Group = 1, Place = 2, Battle = 3 }

    /// <summary>One pawn in the light snapshot the scene finder reads (game side fills it every few seconds).</summary>
    public sealed class ScenePawn
    {
        public int Id;
        public string Name;
        public int X, Z;
        public int RoomId = -1;        // -1 = outdoors (or no room)
        public string RoomLabel;       // room role label ("barracks", "dining room"), null when none
        public string Job;             // current job def name ("Ingest", "TendPatient", ...)
        public string JoyKind;         // job's joy kind def name, null when not recreation
        public string WorkType;        // job's work type def name, null when not work
        public int TargetId = -1;      // the job's pawn target (patient, prisoner, animal)
        public string TargetName;
        public bool Awake = true;
        public bool InBed;
        public bool InGathering;       // in a party/wedding/funeral lord: the events cover it
        public int AgeYears = 30;
        public bool Child;
        public bool Prisoner;
        public int[] Partners;         // lover/spouse/fiance ids
    }

    /// <summary>A moment with no event behind it, offered to the director in quiet stretches.</summary>
    public sealed class SceneCandidate
    {
        public string Kind;
        public string Key;             // scene identity for no-repeat ("meal:r12", "night:7")
        public List<int> Ids = new List<int>();
        public int FollowId = -1;      // pawn to follow for Follow shots
        public ShotStyle Style;
        public int X, Z;               // centre
        public double Severity = 1;
        public string Caption;
        public double Score { get { return WatchKinds.Weight(Kind) * Math.Min(1.5, Math.Max(0.5, Severity)); } }
    }

    /// <summary>Scene kinds and their settings group.</summary>
    public static class SceneKinds
    {
        public const string Meal = "scene_meal", Recreation = "scene_recreation", Work = "scene_work", Night = "scene_night",
            Medical = "scene_medical", Child = "scene_child", Romance = "scene_romance", Prisoner = "scene_prisoner",
            Taming = "scene_taming";

        public const string GroupMeals = "meals", GroupRecreation = "recreation", GroupWork = "work", GroupNight = "night",
            GroupMedical = "medical", GroupChildren = "children", GroupRomance = "romance";

        public static readonly string[] All = { Meal, Recreation, Work, Night, Medical, Child, Romance, Prisoner, Taming };

        public static bool IsScene(string kind) { return kind != null && kind.StartsWith("scene_", StringComparison.Ordinal); }

        /// <summary>The settings group a scene kind belongs to (prisoner talks and taming count as work).</summary>
        public static string Group(string kind)
        {
            switch (kind)
            {
                case Meal: return GroupMeals;
                case Recreation: return GroupRecreation;
                case Work: case Prisoner: case Taming: return GroupWork;
                case Night: return GroupNight;
                case Medical: return GroupMedical;
                case Child: return GroupChildren;
                case Romance: return GroupRomance;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Pure scene finder: groups and moments worth a look when nothing is happening. Reads a light snapshot, returns
    /// candidates best first. Romance needs both pawns 18 or older (the age gate) and never shows bed or lovin.
    /// </summary>
    public static class SceneFinder
    {
        public const int ClusterCells = 8;      // outdoors, pawns this close count as together
        public const int WorkClusterCells = 10;
        public const int LoneCells = 10;        // nobody else awake this close: alone at night
        public const int RomanceCells = 6;
        public const int AdultAge = 18;

        static readonly HashSet<string> IdleJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Wait", "Wait_Wander", "GotoWander", "Wait_MaintainPosture", "Wander", "Goto", "Wait_SafeTemperature", "IdleWhileDespawned" };
        static readonly HashSet<string> LearningJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Lessontaking", "NatureRunning", "Floordrawing", "Skydreaming", "Workwatching", "Radiotalking", "Reading", "LearningDesk" };

        /// <summary>Night for the "awake at night" scene: 23:00 to 04:59.</summary>
        public static bool IsNight(int hour) { return hour >= 23 || hour < 5; }

        public static string MealWord(int hour)
        {
            if (hour >= 5 && hour <= 10) return "breakfast";
            if (hour >= 11 && hour <= 14) return "lunch";
            if (hour >= 17 && hour <= 22) return "dinner";
            return "a meal";
        }

        /// <summary>Names as "Ana", "Ana and Bram", "Ana, Bram and Cy", "Ana, Bram, Cy and 2 more".</summary>
        public static string Names(IList<string> names)
        {
            var n = names.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (n.Count == 0) return "someone";
            if (n.Count == 1) return n[0];
            if (n.Count == 2) return n[0] + " and " + n[1];
            if (n.Count <= 4) return string.Join(", ", n.Take(n.Count - 1)) + " and " + n[n.Count - 1];
            return string.Join(", ", n.Take(3)) + " and " + (n.Count - 3) + " more";
        }

        public static string JoyWord(string joyKind)
        {
            switch (joyKind)
            {
                case "Television": return "watching TV";
                case "Gaming_Cerebral": return "a board game";
                case "Gaming_Dexterity": return "games";
                case "Social": return "chatting";
                case "Meditative": return "quiet time";
                case "Telescope": return "stargazing";
                case "Reading": return "reading";
                case "Music": return "music";
                default: return "recreation";
            }
        }

        /// <summary>Work type to a work scene ("field", "kitchen", "construction", "workshop", "mining"), null otherwise.</summary>
        public static string WorkPlace(string workType)
        {
            switch (workType)
            {
                case "Growing": case "PlantCutting": return "field";
                case "Cooking": return "kitchen";
                case "Construction": case "Repair": return "construction";
                case "Crafting": case "Smithing": case "Tailoring": case "Art": return "workshop";
                case "Mining": return "mining";
                default: return null;
            }
        }

        static string WorkCaption(string place, string names)
        {
            switch (place)
            {
                case "field": return "Camera: " + names + " work the fields";
                case "kitchen": return "Camera: " + names + " cook side by side";
                case "construction": return "Camera: " + names + " build together";
                case "workshop": return "Camera: " + names + " work side by side in the workshop";
                case "mining": return "Camera: " + names + " dig together";
                default: return "Camera: " + names + " work side by side";
            }
        }

        static string In(string room) { return string.IsNullOrWhiteSpace(room) ? " together" : " in the " + room.Trim(); }

        /// <summary>Group pawns: same room when indoors, else within cells of someone in the cluster.</summary>
        public static List<List<ScenePawn>> Cluster(IEnumerable<ScenePawn> pawns, int cells)
        {
            var groups = new List<List<ScenePawn>>();
            var outdoors = new List<ScenePawn>();
            foreach (var g in pawns.Where(p => p.RoomId >= 0).GroupBy(p => p.RoomId).OrderBy(g => g.Key))
                groups.Add(g.OrderBy(p => p.Id).ToList());
            foreach (var p in pawns.Where(p => p.RoomId < 0).OrderBy(p => p.Id)) outdoors.Add(p);
            var used = new bool[outdoors.Count];
            for (int i = 0; i < outdoors.Count; i++)
            {
                if (used[i]) continue;
                var c = new List<ScenePawn> { outdoors[i] }; used[i] = true;
                for (int k = 0; k < c.Count; k++)
                    for (int j = 0; j < outdoors.Count; j++)
                        if (!used[j] && Math.Abs(outdoors[j].X - c[k].X) <= cells && Math.Abs(outdoors[j].Z - c[k].Z) <= cells)
                        { c.Add(outdoors[j]); used[j] = true; }
                groups.Add(c.OrderBy(p => p.Id).ToList());
            }
            return groups;
        }

        static SceneCandidate GroupScene(string kind, string key, List<ScenePawn> g, string caption)
        {
            var c = new SceneCandidate { Kind = kind, Key = key, Style = ShotStyle.Group, Caption = caption, Severity = 0.5 + 0.25 * g.Count };
            foreach (var p in g) c.Ids.Add(p.Id);
            c.X = (int)Math.Round(g.Average(p => p.X)); c.Z = (int)Math.Round(g.Average(p => p.Z));
            return c;
        }

        static SceneCandidate Solo(string kind, ScenePawn p, string caption)
        {
            var c = new SceneCandidate { Kind = kind, Key = kind + ":" + p.Id, Style = ShotStyle.Follow, FollowId = p.Id, X = p.X, Z = p.Z, Caption = caption, Severity = 1 };
            c.Ids.Add(p.Id);
            return c;
        }

        static string GroupKey(string kind, List<ScenePawn> g)
        {
            return g[0].RoomId >= 0 ? kind + ":r" + g[0].RoomId : kind + ":" + string.Join(",", g.Select(p => p.Id));
        }

        /// <summary>
        /// Every scene in the snapshot whose group is enabled (enabled null = all), best first (score, then key).
        /// Downed pawns should be left out of the snapshot by the caller.
        /// </summary>
        public static List<SceneCandidate> Find(IList<ScenePawn> pawns, int hour, ICollection<string> enabledGroups = null)
        {
            var res = new List<SceneCandidate>();
            if (pawns == null || pawns.Count == 0) return res;
            Func<string, bool> on = k => enabledGroups == null || enabledGroups.Contains(SceneKinds.Group(k));
            var byId = new Dictionary<int, ScenePawn>();
            foreach (var p in pawns) byId[p.Id] = p;
            var free = pawns.Where(p => p.Awake && !p.Prisoner && !p.InGathering).ToList();
            var claimed = new HashSet<int>();   // a pawn shows in one group scene at most (the first, strongest kind)

            if (on(SceneKinds.Medical))
                foreach (var d in free.Where(p => p.Job == "TendPatient" && p.TargetId >= 0).OrderBy(p => p.Id))
                {
                    ScenePawn pat; byId.TryGetValue(d.TargetId, out pat);
                    string pn = pat != null ? pat.Name : d.TargetName;
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    var c = new SceneCandidate { Kind = SceneKinds.Medical, Key = SceneKinds.Medical + ":" + d.TargetId, Style = ShotStyle.Group, Caption = "Camera: " + d.Name + " tends to " + pn };
                    c.Ids.Add(d.Id); if (pat != null) c.Ids.Add(pat.Id);
                    c.X = pat != null ? (d.X + pat.X) / 2 : d.X; c.Z = pat != null ? (d.Z + pat.Z) / 2 : d.Z;
                    res.Add(c); claimed.Add(d.Id); if (pat != null) claimed.Add(pat.Id);
                }

            if (on(SceneKinds.Meal))
            {
                string meal = MealWord(hour);
                foreach (var g in Cluster(free.Where(p => p.Job == "Ingest" && !claimed.Contains(p.Id)), ClusterCells))
                {
                    if (g.Count < 2) continue;
                    res.Add(GroupScene(SceneKinds.Meal, GroupKey(SceneKinds.Meal, g), g,
                        "Camera: " + meal + In(g[0].RoomLabel) + ": " + Names(g.Select(p => p.Name).ToList())));
                    foreach (var p in g) claimed.Add(p.Id);
                }
            }

            if (on(SceneKinds.Romance))
                foreach (var a in free.OrderBy(p => p.Id))
                {
                    if (a.Partners == null || claimed.Contains(a.Id) || !RomanceOk(a)) continue;
                    foreach (int bid in a.Partners)
                    {
                        ScenePawn b;
                        if (bid <= a.Id || !byId.TryGetValue(bid, out b) || claimed.Contains(b.Id)) continue;
                        if (!b.Awake || b.Prisoner || b.InGathering || !RomanceOk(b)) continue;
                        if (a.RoomId != b.RoomId || Math.Abs(a.X - b.X) > RomanceCells || Math.Abs(a.Z - b.Z) > RomanceCells) continue;
                        var g = new List<ScenePawn> { a, b };
                        var c = GroupScene(SceneKinds.Romance, SceneKinds.Romance + ":" + a.Id + "," + b.Id, g, "Camera: " + a.Name + " and " + b.Name + " spend time together");
                        c.Severity = 1;
                        res.Add(c); claimed.Add(a.Id); claimed.Add(b.Id);
                        break;
                    }
                }

            if (on(SceneKinds.Recreation))
                foreach (var g in Cluster(free.Where(p => p.JoyKind != null && p.Job != "Ingest" && !p.Child && !claimed.Contains(p.Id)), ClusterCells))
                {
                    if (g.Count < 2) continue;
                    string top = g.GroupBy(p => p.JoyKind).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
                    res.Add(GroupScene(SceneKinds.Recreation, GroupKey(SceneKinds.Recreation, g), g,
                        "Camera: " + JoyWord(top) + In(g[0].RoomLabel) + ": " + Names(g.Select(p => p.Name).ToList())));
                    foreach (var p in g) claimed.Add(p.Id);
                }

            if (on(SceneKinds.Child))
            {
                var kids = free.Where(p => p.Child && !claimed.Contains(p.Id) && (p.JoyKind != null || (p.Job != null && LearningJobs.Contains(p.Job)))).ToList();
                foreach (var g in Cluster(kids, ClusterCells))
                {
                    if (g.Count >= 2)
                        res.Add(GroupScene(SceneKinds.Child, GroupKey(SceneKinds.Child, g), g, "Camera: " + Names(g.Select(p => p.Name).ToList()) + " play together"));
                    else
                    {
                        var k = g[0];
                        bool learn = k.Job != null && LearningJobs.Contains(k.Job);
                        res.Add(Solo(SceneKinds.Child, k, "Camera: " + k.Name + (k.Job == "Lessontaking" ? " has a lesson" : learn ? " is learning" : " is playing")));
                    }
                    foreach (var p in g) claimed.Add(p.Id);
                }
            }

            if (on(SceneKinds.Prisoner))
                foreach (var w in free.Where(p => p.Job != null && p.Job.StartsWith("Prisoner", StringComparison.Ordinal) && p.TargetId >= 0 && !claimed.Contains(p.Id)).OrderBy(p => p.Id))
                {
                    ScenePawn pr; byId.TryGetValue(w.TargetId, out pr);
                    string pn = pr != null ? pr.Name : w.TargetName;
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    var c = new SceneCandidate { Kind = SceneKinds.Prisoner, Key = SceneKinds.Prisoner + ":" + w.TargetId, Style = ShotStyle.Group, Caption = "Camera: " + w.Name + " talks with the prisoner " + pn };
                    c.Ids.Add(w.Id); if (pr != null) c.Ids.Add(pr.Id);
                    c.X = pr != null ? (w.X + pr.X) / 2 : w.X; c.Z = pr != null ? (w.Z + pr.Z) / 2 : w.Z;
                    res.Add(c); claimed.Add(w.Id);
                }

            if (on(SceneKinds.Taming))
                foreach (var t in free.Where(p => (p.Job == "Tame" || p.Job == "Train") && !claimed.Contains(p.Id)).OrderBy(p => p.Id))
                {
                    string an = string.IsNullOrWhiteSpace(t.TargetName) ? "an animal" : "the " + t.TargetName.Trim();
                    res.Add(Solo(SceneKinds.Taming, t, "Camera: " + t.Name + (t.Job == "Tame" ? " tames " : " trains ") + an));
                    claimed.Add(t.Id);
                }

            if (on(SceneKinds.Work))
                foreach (var grp in free.Where(p => !claimed.Contains(p.Id) && WorkPlace(p.WorkType) != null).GroupBy(p => WorkPlace(p.WorkType)).OrderBy(x => x.Key, StringComparer.Ordinal))
                    foreach (var g in Cluster(grp.Select(p => { var q = Copy(p); q.RoomId = -1; return q; }), WorkClusterCells))
                    {
                        if (g.Count < 2) continue;
                        var c = GroupScene(SceneKinds.Work, SceneKinds.Work + ":" + grp.Key + ":" + string.Join(",", g.Select(p => p.Id)), g,
                            WorkCaption(grp.Key, Names(g.Select(p => p.Name).ToList())));
                        res.Add(c);
                        foreach (var p in g) claimed.Add(p.Id);
                    }

            if (on(SceneKinds.Night) && IsNight(hour))
            {
                var awake = pawns.Where(p => p.Awake && !p.Prisoner).ToList();
                foreach (var p in free.Where(p => !p.InBed && !claimed.Contains(p.Id)).OrderBy(p => p.Id))
                {
                    bool alone = !awake.Any(o => o.Id != p.Id && Math.Abs(o.X - p.X) <= LoneCells && Math.Abs(o.Z - p.Z) <= LoneCells);
                    if (!alone) continue;
                    bool restless = p.JoyKind != null || p.Job == null || IdleJobs.Contains(p.Job);
                    res.Add(Solo(SceneKinds.Night, p, "Camera: " + p.Name + (restless ? " can't sleep" : " works through the night")));
                }
            }

            return res.OrderByDescending(c => c.Score).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        }

        static bool RomanceOk(ScenePawn p) { return !p.Child && p.AgeYears >= AdultAge && !p.InBed && p.Job != "Lovin"; }

        static ScenePawn Copy(ScenePawn p) { return p.MemberwiseCloneSafe(); }
    }

    static class ScenePawnExt
    {
        public static ScenePawn MemberwiseCloneSafe(this ScenePawn p)
        {
            return new ScenePawn
            {
                Id = p.Id, Name = p.Name, X = p.X, Z = p.Z, RoomId = p.RoomId, RoomLabel = p.RoomLabel, Job = p.Job, JoyKind = p.JoyKind,
                WorkType = p.WorkType, TargetId = p.TargetId, TargetName = p.TargetName, Awake = p.Awake, InBed = p.InBed,
                InGathering = p.InGathering, AgeYears = p.AgeYears, Child = p.Child, Prisoner = p.Prisoner, Partners = p.Partners
            };
        }
    }
}
