using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLLM
{
    /// <summary>Small helpers shared by the RimWorldLLM windows and tabs. Game thread only.</summary>
    public static class UiUtil
    {
        /// <summary>A keyed string, or the key itself marked when the translation is missing.</summary>
        public static string T(string key, params object[] args)
        {
            if (args == null || args.Length == 0) return key.Translate().ToString();
            var named = new NamedArgument[args.Length];
            for (int i = 0; i < args.Length; i++) named[i] = new NamedArgument(args[i], i.ToString());
            return key.Translate(named).ToString();
        }

        /// <summary>Game tick (TicksGame) as a game date with the hour, e.g. "5th of Aprimay, 5501, 14h".</summary>
        public static string DateOf(int gameTick)
        {
            try
            {
                Vector2 loc = Vector2.zero;
                var map = Find.CurrentMap;
                if (map != null && Find.WorldGrid != null) loc = Find.WorldGrid.LongLatOf(map.Tile);
                return GenDate.DateFullStringWithHourAt(GenDate.TickGameToAbs(gameTick), loc);
            }
            catch (Exception) { return "tick " + gameTick; }
        }

        /// <summary>A game day index (day N starts at tick N x 60000) as a game date without the hour.</summary>
        public static string DateOfDay(int day)
        {
            try
            {
                Vector2 loc = Vector2.zero;
                var map = Find.CurrentMap;
                if (map != null && Find.WorldGrid != null) loc = Find.WorldGrid.LongLatOf(map.Tile);
                return GenDate.DateFullStringAt(GenDate.TickGameToAbs((int)Math.Min((long)day * GenDate.TicksPerDay, int.MaxValue)), loc);
            }
            catch (Exception) { return "day " + (day + 1); }
        }

        public static void Jump(int pawnId, string name)
        {
            var p = Routes.FindPawn(pawnId);
            if (p == null || p.Dead && p.Corpse == null)
            {
                Messages.Message(T("RWL_LogPawnGone", name), MessageTypeDefOf.RejectInput, false);
                return;
            }
            CameraJumper.TryJumpAndSelect(p);
        }

        public static void Tip(Rect r, string text) { TooltipHandler.TipRegion(r, text); }
    }
}
