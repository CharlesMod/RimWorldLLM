using System;

namespace RimWorldLLM.Bridge
{
    /// <summary>
    /// The chat log's line kinds (ChatEntry.Who) and the five filter groups the panel shows as chips.
    /// Verse-free so the mapping is tested. Data shape per kind:
    ///   player    Text = what the player typed; To/Name = the @colonist addressed, or 0/"".
    ///   narrator  Text = the narrator's line.            note  Text = a system note.
    ///   colonist  Name = speaker label, To = speaker id, Listener = addressee label or "", Text = the spoken line.
    ///   thought   Name = pawn label, To = pawn id, Text = the thought.   dream  same shape.
    ///   diary     Name = pawn label, To = pawn id, Text = the entry (shortened).
    ///   chronicle Text = the chronicle entry (Name = "Day N" when known).
    ///   recap     Text = the recap letter's lines joined.
    ///   art       Name = artist label or "", Text = the artwork's one-liner (title and/or description).
    ///   action    Text = plain-English what the narrator did, with ", since why" when given (no leading "> ").
    ///   refused   Text = "couldn't {what}: {reason}" (no leading "> ").
    ///   camera    Text = the camera director's short caption ("Camera: a raid arrives from the north").
    ///   comment   Text = the narrator's documentary commentary or daily recap line (low priority).
    /// </summary>
    public static class ChatKinds
    {
        public const string Player = "player", Narrator = "narrator", Note = "note", Colonist = "colonist",
            Thought = "thought", Dream = "dream", Diary = "diary", Chronicle = "chronicle", Recap = "recap",
            Art = "art", Action = "action", Refused = "refused", Camera = "camera", Comment = "comment";

        /// <summary>Filter groups, in chip order.</summary>
        public const string Talk = "talk", Thoughts = "thoughts", NarratorGroup = "narrator", Actions = "actions", Story = "story";
        public static readonly string[] Groups = { Talk, Thoughts, NarratorGroup, Actions, Story };

        /// <summary>The filter group a line kind belongs to; unknown kinds count as narrator (notes).</summary>
        public static string Group(string who)
        {
            switch (who)
            {
                case Player: case Colonist: return Talk;
                case Thought: case Dream: case Diary: return Thoughts;
                case Action: case Refused: return Actions;
                case Chronicle: case Recap: case Art: case Camera: case Comment: return Story;
                default: return NarratorGroup;
            }
        }
    }
}
