// NEW (not in the Python): the IChatLive the Director hands to the Steward when streaming is on. It maps Begin/Progress/Typing/
// Partial/End/Cancel onto POST /chat/status and POST /chat/partial (docs/BRIDGE-API.md, "The contract a streaming client follows").
// Calls never block: every post goes through one serial queue, partials coalesce to the latest text, errors are swallowed
// (an older mod answers not_found: live status is then switched off). The lead may later swap in an in-process ChatLive.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RimWorldLLM.Mind
{
    public sealed class BridgeChatLive : IChatLive
    {
        private readonly IBridge _bridge;
        private readonly object _gate = new object();
        private Task _tail = Task.CompletedTask;
        private int _n;
        private readonly string _salt = Guid.NewGuid().ToString("N").Substring(0, 8);
        private readonly Dictionary<string, (string Who, int? To, string Name)> _open = new Dictionary<string, (string, int?, string)>();
        private readonly Dictionary<string, string> _pending = new Dictionary<string, string>();
        /// <summary>False once the mod answered not_found: no more posts.</summary>
        public bool Supported = true;

        public BridgeChatLive(IBridge bridge) { _bridge = bridge; }

        /// <summary>Waits for every queued post (tests).</summary>
        public Task FlushAsync() { lock (_gate) return _tail; }

        private void Enqueue(Func<Task> post)
        {
            lock (_gate)
            {
                _tail = _tail.ContinueWith(async _ =>
                {
                    if (!Supported) return;
                    try { await post().ConfigureAwait(false); }
                    catch (BridgeError e) { if (e.Code == "not_found") Supported = false; }
                    catch (Exception) { }
                }, TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>The wire name for a speaker that is not the narrator (the mod names it).</summary>
        private const string ActorWho = "col" + "onist";

        public string Begin(string who, int? pawnId, string name)
        {
            string id = "s" + (++_n).ToString(CultureInfo.InvariantCulture) + "." + _salt;
            string w = who == "narrator" ? "narrator" : ActorWho;
            lock (_gate) _open[id] = (w, pawnId, name);
            Enqueue(() => _bridge.PostChatStatusAsync(w, "thinking", id, w != "narrator" ? pawnId : null));
            return id;
        }

        public void Progress(string id, double progress)
        {
            (string Who, int? To, string Name) o;
            lock (_gate) { if (!_open.TryGetValue(id, out o)) return; }
            double? p = progress < 0 ? (double?)null : Math.Min(1.0, progress);
            Enqueue(() => _bridge.PostChatStatusAsync(o.Who, "thinking", id, o.Who != "narrator" ? o.To : null, p));
        }

        public void Typing(string id)
        {
            (string Who, int? To, string Name) o;
            lock (_gate) { if (!_open.TryGetValue(id, out o)) return; }
            Enqueue(() => _bridge.PostChatStatusAsync(o.Who, "typing", id, o.Who != "narrator" ? o.To : null));
        }

        public void Partial(string id, string wholeText)
        {
            if (string.IsNullOrEmpty(wholeText)) return;
            string text = wholeText.Length > 500 ? wholeText.Substring(0, 500) : wholeText;
            lock (_gate) { if (!_open.ContainsKey(id)) return; _pending[id] = text; }
            Enqueue(async () =>
            {
                string t;
                lock (_gate) { if (!_pending.TryGetValue(id, out t)) return; _pending.Remove(id); }   // coalesced: a later partial already went
                await _bridge.PostChatPartialAsync(id, t).ConfigureAwait(false);
            });
        }

        public void End(string id, string finalText)
        {
            (string Who, int? To, string Name) o;
            lock (_gate) { if (!_open.TryGetValue(id, out o)) return; _open.Remove(id); _pending.Remove(id); }
            if (finalText != null) return;      // the final line was posted to /chat with this stream id: it ends the stream
            Enqueue(() => _bridge.PostChatStatusAsync(o.Who, "idle", id));
        }

        public void Cancel(string id)
        {
            (string Who, int? To, string Name) o;
            lock (_gate) { if (!_open.TryGetValue(id, out o)) return; _open.Remove(id); _pending.Remove(id); }
            Enqueue(() => _bridge.PostChatStatusAsync(o.Who, "idle", id));
        }
    }
}
