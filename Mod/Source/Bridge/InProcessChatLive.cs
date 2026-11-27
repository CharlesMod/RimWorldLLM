// The IChatLive the Director hands to the Steward when the mind runs inside the mod: it drives the game's ChatLive (the panel's "is thinking /
// typing" lines) directly, with no HTTP and no bridge round trip. Thread-safe (ChatLive is); never throws; never blocks.
// The final line is posted to /chat with the stream id, which already ends the live line there, so End(id, text) does nothing more
// (a second End with text would log the line twice).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimWorldLLM.Bridge
{
    public sealed class InProcessChatLive : RimWorldLLM.Mind.IChatLive
    {
        readonly ChatLive _live;
        readonly Action _onNarratorCycle;
        readonly object _gate = new object();
        readonly Dictionary<string, (string Who, int To, string Name)> _open = new Dictionary<string, (string, int, string)>();
        int _dead;

        /// <param name="onNarratorCycle">Runs when a narrator line begins (the steward cycle starts: fresh build caps, as the /chat/status route does). It must hand the work to the game thread.</param>
        public InProcessChatLive(ChatLive live, Action onNarratorCycle = null) { _live = live; _onNarratorCycle = onNarratorCycle; }

        public string Begin(string who, int? pawnId, string name)
        {
            string w = who == "narrator" ? "narrator" : "colonist";
            int to = w == "narrator" ? 0 : (pawnId ?? 0);
            try
            {
                string id = _live.Begin(w, to, name);
                lock (_gate) _open[id] = (w, to, name);
                if (w == "narrator" && _onNarratorCycle != null) { try { _onNarratorCycle(); } catch (Exception) { } }
                return id;
            }
            catch (Exception) { return "dead-" + (++_dead).ToString(CultureInfo.InvariantCulture); }
        }

        public void Progress(string id, double progress)
        {
            try
            {
                if (progress < 0)
                {
                    (string Who, int To, string Name) o;
                    lock (_gate) { if (!_open.TryGetValue(id, out o)) return; }
                    _live.Status(id, o.Who, o.To, o.Name, "thinking");   // a refresh: keeps the line from timing out
                }
                else _live.Progress(id, (float)Math.Min(1.0, progress));
            }
            catch (Exception) { }
        }

        public void Typing(string id) { try { _live.Typing(id); } catch (Exception) { } }

        public void Partial(string id, string wholeText)
        {
            try { if (!string.IsNullOrEmpty(wholeText)) _live.Partial(id, wholeText); } catch (Exception) { }
        }

        public void End(string id, string finalText)
        {
            try
            {
                lock (_gate) _open.Remove(id);
                if (finalText != null) return;     // posted to /chat with this stream id: that ended the live line
                _live.End(id, null);
            }
            catch (Exception) { }
        }

        public void Cancel(string id)
        {
            try { lock (_gate) _open.Remove(id); _live.End(id, null); } catch (Exception) { }
        }
    }
}
