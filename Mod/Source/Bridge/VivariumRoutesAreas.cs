using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Bridge
{
    // The word form of POST /act/area (the narrator's area tool): "where" is a digest room id ("room 12"), a digest zone id ("#4") or
    // "base"; "who" names pawns or animals. Same Act pipeline; the numeric route (x, z, w, h, assign ids) is untouched and Area() in
    // VivariumRoutesBuild.cs hands over here when the body has where, who or op "assign". The game resolves the words (AreasPure).
    public static partial class VivariumRoutes
    {
        static Plan AreaWordsPlan(JObject b)
        {
            var a = new AreaWordArgs { Op = OneOf(b, "op", new[] { "add", "remove", "assign" }) };
            string name = Str(b, "name", 1, AreasPure.MaxName);
            a.Home = name.ToLowerInvariant() == AreasPure.HomeWord;
            a.Name = a.Home ? "Home" : name;
            string ws = OptStr(b, "where", 24);
            if (a.Op != "assign")
            {
                if (ws == null) throw new ArgError("where is required: a room id (room 12), a zone id (#4) or base");
                string why;
                if (!AreasPure.ParseWhere(ws, out a.Where, out why)) throw new ArgError(why);
            }
            else if (ws != null) throw new ArgError("assign takes who, not where");
            var who = b["who"];
            if (who is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (t.Type != JTokenType.String) throw new ArgError("who must be names");
                    a.Who.AddRange(AreasPure.SplitNames((string)t));
                }
            }
            else if (who != null && who.Type == JTokenType.String) a.Who = AreasPure.SplitNames((string)who);
            else if (who != null && who.Type != JTokenType.Null) throw new ArgError("who must be a name or a list of names");
            if (a.Who.Count > AreasPure.MaxWho) throw new ArgError("who takes at most " + AreasPure.MaxWho + " names");
            a.Release = name.ToLowerInvariant() == AreasPure.NoneWord;
            if (a.Op == "assign")
            {
                if (a.Home) throw new ArgError("the home area is not for pawns; name an allowed area, or none to release them");
                if (a.Who.Count == 0) throw new ArgError("assign needs who: the names to restrict (or release, with area none)");
            }
            else if (a.Release) throw new ArgError("area none goes with assign");
            return new Plan
            {
                What = () => a.Op == "assign" ? (a.Release ? "release " + string.Join(", ", a.Who) : "restrict " + string.Join(", ", a.Who) + " to the " + a.Name + " area")
                    : (a.Op == "add" ? "add " + a.Where + " to the " : "take " + a.Where + " from the ") + (a.Home ? "home" : a.Name) + " area",
                RunLeft = (h, left) =>
                {
                    var ah = h as IAreaHands;
                    return ah != null ? ah.AreaWords(a, left) : HandResult.No("area hands are not available");
                }
            };
        }
    }
}
