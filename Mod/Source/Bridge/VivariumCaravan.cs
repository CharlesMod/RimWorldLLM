using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

// Vivarium phase 4: caravan planning, who may go, quest kind and feasibility, and the narrator caravans' purposes.
// Verse-free; the game bodies fill the inputs.
namespace RimWorldLLM.Bridge
{
    public sealed class CaravanMember
    {
        public string Name;
        public double CapacityKg;
        public double NutritionPerDay;
        public bool IsAnimal;
        public bool EatsKibble;   // animals only: kibble can feed it
    }

    public sealed class CaravanGood { public string Item; public double MassEach; public int Count; }

    public sealed class CaravanFood
    {
        public string Item;
        public string Kind;           // pemmican | survival | kibble | meal | raw
        public double NutritionEach;
        public double MassEach;
        public double SpoilDays = double.PositiveInfinity;
        public int Available;
    }

    public sealed class PlanInput
    {
        public double TravelDays;
        public bool RoundTrip = true;
        public double MarginDays = 3;
        public List<CaravanMember> Members = new List<CaravanMember>();
        public List<CaravanGood> Goods = new List<CaravanGood>();
        public List<CaravanFood> Food = new List<CaravanFood>();
    }

    public sealed class FoodPick { public string Item; public int Count; }

    public sealed class CaravanPlanResult
    {
        public double Days, FoodNeedDays, FoodDays, Mass, Capacity;
        public List<FoodPick> FoodPick = new List<FoodPick>();
        public bool Ok;
        public List<string> Problems = new List<string>();

        public JObject ToJson()
        {
            var food = new JArray();
            foreach (var f in FoodPick) food.Add(new JObject { ["item"] = f.Item, ["count"] = f.Count });
            var pr = new JArray(); foreach (var p in Problems) pr.Add(p);
            return new JObject
            {
                ["days"] = Math.Round(Days, 1), ["food_days"] = Math.Round(FoodDays, 1), ["food_need"] = Math.Round(FoodNeedDays, 1),
                ["mass"] = Math.Round(Mass, 1), ["capacity"] = Math.Round(Capacity, 1), ["food"] = food, ["ok"] = Ok, ["problems"] = pr
            };
        }
    }

    /// <summary>Travel days, food days with margin, carry capacity and which food to pack. Pure.</summary>
    public static class CaravanPlanner
    {
        static readonly string[] Order = { "pemmican", "survival", "kibble", "meal", "raw" };
        static string F1(double d) { return d.ToString("F1", CultureInfo.InvariantCulture); }
        static string F0(double d) { return d.ToString("F0", CultureInfo.InvariantCulture); }

        public static double FoodNeed(double days, bool roundTrip, double margin) { return days * (roundTrip ? 2 : 1) + margin; }

        public static CaravanPlanResult Plan(PlanInput p)
        {
            var r = new CaravanPlanResult { Days = p.TravelDays };
            r.FoodNeedDays = FoodNeed(p.TravelDays, p.RoundTrip, p.MarginDays);
            r.Capacity = p.Members.Sum(m => Math.Max(0, m.CapacityKg));
            if (!p.Members.Any(m => !m.IsAnimal)) { r.Problems.Add("no pawns"); }
            double need = r.FoodNeedDays;
            double humanPerDay = p.Members.Where(m => !m.IsAnimal).Sum(m => m.NutritionPerDay);
            double animalPerDay = p.Members.Where(m => m.IsAnimal && m.EatsKibble).Sum(m => m.NutritionPerDay);
            double humanLeft = humanPerDay * need, animalLeft = animalPerDay * need;
            double humanGot = 0, animalGot = 0, foodMass = 0;
            foreach (var kind in Order)
            {
                foreach (var f in p.Food.Where(x => string.Equals(x.Kind, kind, StringComparison.OrdinalIgnoreCase) && x.NutritionEach > 0 && x.Available > 0))
                {
                    if ((kind == "meal" || kind == "raw") && !(f.SpoilDays > need)) continue;   // would rot on the way
                    bool forAnimals = kind == "kibble";
                    double left = forAnimals ? animalLeft : humanLeft;
                    if (left <= 1e-9) continue;
                    int n = Math.Min(f.Available, (int)Math.Ceiling(left / f.NutritionEach - 1e-9));
                    if (n <= 0) continue;
                    double nut = n * f.NutritionEach;
                    if (forAnimals) { animalLeft -= nut; animalGot += nut; } else { humanLeft -= nut; humanGot += nut; }
                    foodMass += n * f.MassEach;
                    r.FoodPick.Add(new FoodPick { Item = f.Item, Count = n });
                }
            }
            double days = double.PositiveInfinity;
            if (humanPerDay > 0) days = Math.Min(days, humanGot / humanPerDay);
            if (animalPerDay > 0) days = Math.Min(days, animalGot / animalPerDay);
            r.FoodDays = double.IsInfinity(days) ? need : days;
            r.Mass = p.Goods.Sum(g => g.MassEach * g.Count) + foodMass;
            if (r.Mass > r.Capacity + 1e-9) r.Problems.Add("too heavy: " + F0(r.Mass) + "/" + F0(r.Capacity) + " kg");
            if (r.FoodDays + 1e-6 < need) r.Problems.Add("food for " + F1(r.FoodDays) + " days, needs " + F1(need));
            r.Ok = r.Problems.Count == 0;
            return r;
        }
    }

    public sealed class CaravanCandidate
    {
        public int Id;
        public string Name;
        public bool IsAnimal;
        public double CapacityKg;     // animals: what it can carry
        public bool IsDoctorBest;     // the colony's best Medicine
        public bool IsOnlyCook;       // the only one who can cook
        public bool CanFight;
        public bool Downed, Drafted, InBed;
        public string MentalState;    // null or empty = none
        public int Age;
    }

    public sealed class RulesInput
    {
        public List<CaravanCandidate> Candidates = new List<CaravanCandidate>();
        public int FreeColonists;     // all free colonists on the home map, going or not
        public int FreeFighters;      // of them, how many can fight
        public int MaxPawns = 3;
        public bool PlayerAsked;
    }

    /// <summary>Who may go (docs/VIVARIUM.md "Who may go"). Pure.</summary>
    public static class CaravanRules
    {
        public const int MinAge = 13;

        /// <summary>null when the party may go, else the reason.</summary>
        public static string Check(RulesInput r)
        {
            var humans = r.Candidates.Where(c => !c.IsAnimal).ToList();
            if (humans.Count == 0) return "no pawns: send at least one colonist";
            foreach (var c in r.Candidates)
            {
                if (c.IsAnimal)
                {
                    if (c.Downed) return c.Name + " is downed";
                    if (c.CapacityKg <= 0) return c.Name + " cannot carry anything";
                    continue;
                }
                if (c.Age < MinAge) return c.Name + " is too young to travel";
                if (c.Downed) return c.Name + " is downed";
                if (c.Drafted) return c.Name + " is drafted";
                if (c.InBed) return c.Name + " is sick in bed";
                if (!string.IsNullOrEmpty(c.MentalState)) return c.Name + " is in a mental state (" + c.MentalState + ")";
            }
            if (humans.Count >= r.FreeColonists) return "at least one colonist must stay home";
            if (r.FreeFighters > 0 && r.FreeFighters - humans.Count(c => c.CanFight) < 1) return "at least one fighter must stay home";
            if (!r.PlayerAsked)
            {
                var d = humans.Find(c => c.IsDoctorBest);
                if (d != null) return d.Name + " is the colony's best doctor and must stay";
                var k = humans.Find(c => c.IsOnlyCook);
                if (k != null) return k.Name + " is the colony's only cook and must stay";
                if (humans.Count > r.MaxPawns) return "at most " + r.MaxPawns + " pawns go on a narrator caravan unless the player asks";
            }
            return null;
        }
    }

    /// <summary>Quest kind from its def and parts, and whether the narrator can take it on. Pure.</summary>
    public static class QuestPure
    {
        public static readonly string[] Kinds = { "site", "delivery", "hosting", "combat", "other" };
        // Lower-case fragments of the quest root defName or any part type name.
        public static readonly string[] DeliveryWords = { "traderequest", "tradeoffer" };
        public static readonly string[] CombatWords = { "raid", "defend", "bandit", "siege", "assault", "ambush", "skirmish", "hunt", "pirate", "mech", "threat", "infestation", "manhunter", "hostile" };
        public static readonly string[] HostingWords = { "lodger", "guest", "hospitality", "refugee", "visitor", "ambassador", "diplomat", "wanderer" };

        static bool Has(IEnumerable<string> names, string[] words)
        {
            foreach (var n in names) { string l = (n ?? "").ToLowerInvariant(); foreach (var w in words) if (l.Contains(w)) return true; }
            return false;
        }

        public static string Kind(string rootDefName, IEnumerable<string> partTypeNames, bool hasSite, bool hasTradeRequest)
        {
            var names = new List<string> { rootDefName ?? "" };
            if (partTypeNames != null) names.AddRange(partTypeNames);
            if (hasTradeRequest || Has(names, DeliveryWords)) return "delivery";
            if (Has(names, CombatWords)) return "combat";
            if (Has(names, HostingWords)) return "hosting";
            if (hasSite) return "site";
            return "other";
        }

        /// <summary>Can the narrator work it now? Dangers are in points; expiresDays PositiveInfinity or NaN = no deadline.</summary>
        public static (bool can, string whyNot) Feasible(string kind, double threatPoints, double colonyPoints, double travelDays, double expiresDays, double foodDays, double healthyFraction, bool combatHandsReady = false, bool caravansOff = false)
        {
            // caravans are off for the narrator: a site or a delivery needs one, so the caravan reason comes before anything else
            if (caravansOff && (kind == "site" || kind == "delivery")) return (false, "needs a caravan (caravans are off)");
            if (kind == "combat" && !combatHandsReady) return (false, "needs combat (not yet)");
            if (threatPoints > 0 && threatPoints > 0.6 * colonyPoints)
                return (false, "too dangerous (threat " + TradeRules.Num(threatPoints, 0) + " against our " + TradeRules.Num(colonyPoints, 0) + ")");
            if (!double.IsNaN(expiresDays) && !double.IsInfinity(expiresDays))
            {
                double need = kind == "site" ? travelDays : travelDays * 2;   // a site is one way; the way home is planned on arrival
                if (expiresDays <= 0 || need > expiresDays) return (false, "no time: needs " + TradeRules.Num(need) + " days, it ends in " + TradeRules.Num(Math.Max(0, expiresDays)));
            }
            if (foodDays < 10) return (false, "food too low (" + TradeRules.Num(foodDays) + " days)");
            if (healthyFraction < 0.7) return (false, "too many colonists are hurt or sick");
            return (true, null);
        }
    }

    /// <summary>What each narrator caravan is for. Saved as `caravan_plans`, at most 20 (the oldest go first).</summary>
    public sealed class CaravanPlans
    {
        public const int Max = 20;
        public sealed class Entry { public string Key, Purpose, To, Quest, Note; public int Formed; }
        readonly List<Entry> _items = new List<Entry>();

        public int Count { get { return _items.Count; } }
        public IReadOnlyList<Entry> Items { get { return _items; } }

        public Entry Get(string key) { return _items.Find(e => e.Key == key); }

        public void Set(string key, string purpose, string to, string quest, int formed, string note)
        {
            if (string.IsNullOrEmpty(key)) return;
            _items.RemoveAll(e => e.Key == key);
            _items.Add(new Entry { Key = key, Purpose = purpose ?? "", To = to ?? "", Quest = quest, Formed = formed, Note = VivJson.Clean(note, 200) });
            while (_items.Count > Max) _items.RemoveAt(0);
        }

        public bool Remove(string key) { return _items.RemoveAll(e => e.Key == key) > 0; }

        /// <summary>Drops the plans whose caravan is gone.</summary>
        public void Prune(Func<string, bool> alive) { _items.RemoveAll(e => !alive(e.Key)); }

        public JArray Export()
        {
            var a = new JArray();
            foreach (var e in _items)
                a.Add(new JObject { ["key"] = e.Key, ["purpose"] = e.Purpose, ["to"] = e.To, ["quest"] = e.Quest, ["formed"] = e.Formed, ["note"] = e.Note });
            return a;
        }

        public void Import(JArray saved)
        {
            Clear();
            if (saved == null) return;
            foreach (var t in saved)
            {
                var o = t as JObject;
                if (o == null) continue;
                string key = VivJson.Str(o["key"]);
                if (key.Length == 0) continue;
                string q = VivJson.Str(o["quest"], null);
                Set(key, VivJson.Str(o["purpose"]), VivJson.Str(o["to"]), q, VivJson.Int(o["formed"]), VivJson.Str(o["note"]));
            }
        }

        public void Clear() { _items.Clear(); }
    }
}
