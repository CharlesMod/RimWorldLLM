using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Verse-free logic of the narrator's area tool: the words for where ("room 12", "#4", "base"), the box math that turns a room, a zone or
// the built base into a padded, clamped box, the names of pawns and animals, the restriction marks and the notes. The game side
// (Mod/Source/RimWorld/VivariumAreaHands.cs) reads the cells and asks these functions to decide, so the decisions are tested without the game.
namespace RimWorldLLM.Bridge
{
    /// <summary>Where an area change goes, by word: a digest room id, a digest zone id or the base.</summary>
    public sealed class AreaWhere
    {
        public const string Room = "room", Zone = "zone", Base = "base";
        public string Kind;
        public int Id;
        public override string ToString() { return Kind == Base ? "base" : Kind == Zone ? "#" + Id : "room " + Id; }
    }

    /// <summary>POST /act/area with words. op: add, remove or assign. Home: the home area (name "home"). Who: pawn or animal names;
    /// Release: free them of their area (op assign with the area word "none").</summary>
    public sealed class AreaWordArgs
    {
        public string Op, Name;
        public bool Home, Release;
        public AreaWhere Where;
        public List<string> Who = new List<string>();
    }

    /// <summary>The hands of the word form of the area route (separate from IVivariumHands; a hands object without it refuses).</summary>
    public interface IAreaHands
    {
        HandResult AreaWords(AreaWordArgs a, BuildBudget left);
    }

    public static class AreasPure
    {
        public const int MaxBox = 60, RoomPad = 1, BasePad = 6, MaxWho = 12, MaxName = 40;
        public const string HomeWord = "home", NoneWord = "none";
        public const string RestrictKind = "area_restrict";

        /// <summary>"room 12" / "12" -> room, "#4" / "zone 4" -> zone, "base" -> the base. False with the refusal.</summary>
        public static bool ParseWhere(string s, out AreaWhere w, out string refusal)
        {
            w = null; refusal = null;
            string t = (s ?? "").Trim().ToLowerInvariant();
            string usage = "where must be a room id from ROOMS (room 12), a zone id from ZONES (#4) or base";
            if (t.Length == 0) { refusal = usage; return false; }
            if (t == "base" || t == "the base") { w = new AreaWhere { Kind = AreaWhere.Base }; return true; }
            string kind = AreaWhere.Room;
            if (t.StartsWith("room", StringComparison.Ordinal)) t = t.Substring(4).Trim();
            else if (t.StartsWith("#", StringComparison.Ordinal)) { kind = AreaWhere.Zone; t = t.Substring(1).Trim(); }
            else if (t.StartsWith("zone", StringComparison.Ordinal)) { kind = AreaWhere.Zone; t = t.Substring(4).Trim().TrimStart('#'); }
            int id;
            if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0) { refusal = usage; return false; }
            w = new AreaWhere { Kind = kind, Id = id };
            return true;
        }

        /// <summary>The box of a room's interior bounding box, one cell wider all round, inside the map and at most 60 x 60.</summary>
        public static CellBox BoxFromRoom(int minX, int minZ, int maxX, int maxZ, int mapW, int mapH)
        {
            return Fit(minX - RoomPad, minZ - RoomPad, maxX + RoomPad, maxZ + RoomPad, mapW, mapH);
        }

        /// <summary>The bounding box of cells ({x, z} pairs), padded all round; null with no cells.</summary>
        public static CellBox BoxFromCells(IEnumerable<int[]> cells, int pad, int mapW, int mapH)
        {
            bool any = false;
            int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
            foreach (var c in cells)
            {
                if (c == null || c.Length < 2) continue;
                any = true;
                x0 = Math.Min(x0, c[0]); x1 = Math.Max(x1, c[0]); z0 = Math.Min(z0, c[1]); z1 = Math.Max(z1, c[1]);
            }
            return any ? Fit(x0 - pad, z0 - pad, x1 + pad, z1 + pad, mapW, mapH) : null;
        }

        /// <summary>A square box of the given half-width around a centre (the base when nothing is built yet).</summary>
        public static CellBox BoxAround(int cx, int cz, int half, int mapW, int mapH)
        {
            return Fit(cx - half, cz - half, cx + half, cz + half, mapW, mapH);
        }

        /// <summary>Clamps a box (corner to corner, inclusive) to the map, then shrinks it about its centre to at most 60 x 60.
        /// Null when it lies wholly off the map.</summary>
        public static CellBox Fit(int x0, int z0, int x1, int z1, int mapW, int mapH)
        {
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            if (z1 < z0) { int t = z0; z0 = z1; z1 = t; }
            if (x1 < 0 || z1 < 0 || x0 >= mapW || z0 >= mapH) return null;
            int w = x1 - x0 + 1, h = z1 - z0 + 1;
            if (w > MaxBox) { x0 += (w - MaxBox) / 2; x1 = x0 + MaxBox - 1; }
            if (h > MaxBox) { z0 += (h - MaxBox) / 2; z1 = z0 + MaxBox - 1; }
            x0 = Math.Max(0, x0); z0 = Math.Max(0, z0); x1 = Math.Min(mapW - 1, x1); z1 = Math.Min(mapH - 1, z1);
            return new CellBox(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        }

        /// <summary>Names from a comma or "and" list ("Biscuit, Rex"), trimmed, de-duplicated, in order.</summary>
        public static List<string> SplitNames(string s)
        {
            var l = new List<string>();
            foreach (var part in (s ?? "").Replace(" and ", ",").Replace(";", ",").Split(','))
            {
                string w = part.Trim();
                if (w.Length > 0 && !l.Contains(w, StringComparer.OrdinalIgnoreCase)) l.Add(w);
            }
            return l;
        }

        /// <summary>Names -> pawn ids from the pool (name, id), matching without case. False with "no pawn named X; ...".</summary>
        public static bool ResolveWho(IList<string> who, IList<KeyValuePair<string, int>> pool, out List<int> ids, out List<string> names, out string refusal)
        {
            ids = new List<int>(); names = new List<string>(); refusal = null;
            foreach (var w in who)
            {
                int i = -1;
                for (int k = 0; k < pool.Count; k++) if (string.Equals(pool[k].Key, w, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
                if (i < 0)
                {
                    refusal = "no pawn named " + w + "; " + (pool.Count == 0 ? "there are none" : "they are: " + string.Join(", ", pool.Select(p => p.Key).Take(14)));
                    return false;
                }
                if (!ids.Contains(pool[i].Value)) { ids.Add(pool[i].Value); names.Add(pool[i].Key); }
            }
            return true;
        }

        // ---- the restriction marks: Extra "id:old area label|id:" (an empty label = they had no area)
        public static string EncodeRestrict(IEnumerable<KeyValuePair<int, string>> olds)
        {
            return string.Join("|", olds.Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + ":" + (p.Value ?? "").Replace("|", " ")));
        }

        public static List<KeyValuePair<int, string>> DecodeRestrict(string extra)
        {
            var l = new List<KeyValuePair<int, string>>();
            foreach (var part in (extra ?? "").Split('|'))
            {
                int c = part.IndexOf(':');
                int id;
                if (c <= 0 || !int.TryParse(part.Substring(0, c), NumberStyles.None, CultureInfo.InvariantCulture, out id)) continue;
                l.Add(new KeyValuePair<int, string>(id, part.Substring(c + 1)));
            }
            return l;
        }

        // ---- the notes (the greentext reads them)
        public static string AreaLabel(string name, bool home) { return home ? "home" : name; }
        public static string AddNote(string where, string area, int changed)
        {
            return changed > 0 ? "added " + where + " to the " + area + " area" : "the " + area + " area already covers " + where;
        }
        public static string RemoveNote(string where, string area) { return "took " + where + " out of the " + area + " area"; }
        public static string RestrictNote(IList<string> names, string area) { return "restricted " + string.Join(", ", names) + " to the " + area + " area"; }
        public static string ReleaseNote(IList<string> names) { return "let " + string.Join(", ", names) + " roam free"; }
        /// <summary>The undo note's piece.</summary>
        public static string RestrictUndoNote(int n) { return "put " + (n == 1 ? "a pawn" : "pawns") + " back in their old area"; }
        public static string CapHint(string reason, AreaWhere w)
        {
            if (reason != null && reason.Contains("left this cycle")) return reason + (w != null && w.Kind == AreaWhere.Base ? "; name a room or a zone instead of the base" : "");
            return reason;
        }
    }
}
