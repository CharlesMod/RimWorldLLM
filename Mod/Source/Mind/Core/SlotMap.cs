// The one place that chooses an engine slot. Slot 0 (NarratorSlot) belongs to the narrator, steward and chronicle;
// slots ActorSlots are leased to actors. An actor prefers the slot it last held (so its cached prompt card is still
// there), else the first free one. A later engine "attach profile" supplies the slot count by constructing this map.
using System.Collections.Generic;
using System.Linq;

namespace RimWorldLLM.Mind
{
    public sealed class SlotMap
    {
        public readonly List<int> ActorSlots;
        public readonly int NarratorSlot;
        readonly Dictionary<int, int> _last = new Dictionary<int, int>();   // actor -> the slot it last held

        public SlotMap(IEnumerable<int> actorSlots, int narratorSlot = 0)
        {
            ActorSlots = actorSlots.ToList();
            NarratorSlot = narratorSlot;
        }

        /// <summary>The slot a narrator-class call (narrator, steward, chronicle, lore, recap) runs in.</summary>
        public int ForNarrator() { return NarratorSlot; }

        /// <summary>The slot this actor last held, or -1 when it never held one.</summary>
        public int LastOf(int actor) { int s; return _last.TryGetValue(actor, out s) ? s : -1; }

        /// <summary>The preferred free slot for the actor: its last one when free, else the first free; -1 when none is free.</summary>
        public int Pick(int actor, IList<int> free)
        {
            if (free.Count == 0) return -1;
            int last = LastOf(actor);
            return last >= 0 && free.Contains(last) ? last : free[0];
        }

        /// <summary>Record that the actor now holds the slot.</summary>
        public void Remember(int actor, int slot) { _last[actor] = slot; }
    }
}
