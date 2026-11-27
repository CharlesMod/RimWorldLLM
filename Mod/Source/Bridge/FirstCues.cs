using System;
using System.Collections.Generic;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// Verse-free mapping of game defs to the "first_cue" names the agent knows (snow, eclipse, corpse ...).
    /// The agent decides whether a cue is a pawn's first; the mod only reports each cue, once per source.
    /// </summary>
    public static class FirstCues
    {
        public const string EventType = "first_cue";
        /// <summary>Room impressiveness at or above which a room counts as beautiful.</summary>
        public const float BeautifulImpressiveness = 65f;

        public static string ThoughtCue(string defName)
        {
            switch (defName)
            {
                case "ObservedLayingCorpse":
                case "ObservedLayingRottingCorpse": return "corpse";
                case "AteWithoutTable": return "no_table";
            }
            return null;
        }

        public static string ConditionCue(string defName)
        {
            switch (defName)
            {
                case "Eclipse": return "eclipse";
                case "Aurora": return "aurora";
                case "SolarFlare": return "solar_flare";
            }
            return null;
        }

        public static string WeatherCue(string defName)
        {
            return defName == "SnowGentle" || defName == "SnowHard" ? "snow" : null;
        }

        public static string RoomCue(float impressiveness)
        {
            return impressiveness >= BeautifulImpressiveness ? "beautiful_room" : null;
        }

        public static string Summary(string cue, string who)
        {
            switch (cue)
            {
                case "snow": return "Snow began to fall.";
                case "eclipse": return "A solar eclipse darkened the sky.";
                case "aurora": return "An aurora lit up the night sky.";
                case "solar_flare": return "A solar flare knocked out the power.";
                case "corpse": return who + " saw a dead body lying around.";
                case "beautiful_room": return who + " is standing in a strikingly beautiful room.";
                case "no_table": return who + " had to eat without a table.";
            }
            return null;
        }

        /// <summary>Remembers which (key, cue) pairs were already reported, so a poller emits each once.</summary>
        public sealed class Seen
        {
            public const int Cap = 5000;
            readonly BoundedSet set = new BoundedSet(Cap);   // oldest pairs are forgotten, never unbounded
            /// <summary>True the first time a pair is offered.</summary>
            public bool First(int key, string cue) { return set.Add(key + ":" + cue); }
            public void Clear() { set.Clear(); }
            public int Count { get { return set.Count; } }
        }
    }
}
