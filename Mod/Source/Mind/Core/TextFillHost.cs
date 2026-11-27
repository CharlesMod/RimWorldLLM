// The per-instance text filler's view of the director (Wave 3 convention: a host interface in its own file; Python textfill.py reads d.*).
// The Director implements it; GameAdapter.IdleFill hands core/idle the filler function over this interface.
using System.Collections.Generic;

namespace RimWorldLLM.Mind
{
    public interface ITextFillHost
    {
        IBridge Bridge { get; }
        ILlmClient Llm { get; }
        /// <summary>d.text_fill_wait_s: how long idle waits before the next fill pass (set by every pass).</summary>
        double TextFillWaitS { get; set; }
        /// <summary>d.text_queue_live: true once the game's text queue has answered (the art idle job stands down), false on a 404.</summary>
        bool TextQueueLive { get; set; }
        /// <summary>d.text_tried: ids written once, never asked for again this session. Entries are TextFill.IdKey(id) strings.</summary>
        HashSet<string> TextTried { get; }
        /// <summary>d.dropped: (actor, reason) of generated lines that were dropped; textfill appends (-2, "textfill:KIND:rejected").</summary>
        List<(int Actor, string Reason)> Dropped { get; }
    }
}
