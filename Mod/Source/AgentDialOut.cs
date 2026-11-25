using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace HermesVoice
{
    /// <summary>
    /// Outbound events (mod -> agent). Send() serializes on the game thread and hands the JSON to one
    /// background sender thread draining a bounded queue. The sender never touches Verse (Log, Settings);
    /// its log lines are queued and flushed on the game thread via FlushLogs().
    /// </summary>
    public static class AgentDialOut
    {
        const int MaxQueue = 64;
        const int MaxLogLines = 32;
        static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(30);

        sealed class Item { public string Url; public string Token; public string Json; }

        static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        static readonly object Gate = new object();
        static readonly LinkedList<Item> Queue = new LinkedList<Item>();
        static readonly Queue<string> LogLines = new Queue<string>();
        static Thread _thread;
        static int _dropped;
        static int _failures;
        static string _lastFailure = "";
        static long _lastLogTs = -1;

        /// <summary>Call on the game thread: push an event object (the same one GET /events returns). No-op when pushing is off.</summary>
        public static void Push(JObject ev)
        {
            var s = HermesVoiceMod.Settings;
            if (s == null || !s.enabled || !s.sendColonyEvents || string.IsNullOrWhiteSpace(s.agentUrl)) return;
            if (!EventGate.GlobalCapOk()) return;
            string json = ev.ToString(Formatting.None);
            var item = new Item { Url = s.agentUrl.Trim(), Token = (s.sharedToken ?? "").Trim(), Json = json };
            lock (Gate)
            {
                if (Queue.Count >= MaxQueue)
                {
                    Queue.RemoveFirst();
                    _dropped++;
                }
                Queue.AddLast(item);
                if (_thread == null)
                {
                    _thread = new Thread(SenderLoop) { IsBackground = true, Name = "HermesVoice-DialOut" };
                    _thread.Start();
                }
                Monitor.Pulse(Gate);
            }
        }

        /// <summary>Game thread: emit log lines queued by the sender thread.</summary>
        public static void FlushLogs()
        {
            while (true)
            {
                string line;
                lock (Gate)
                {
                    if (LogLines.Count == 0) return;
                    line = LogLines.Dequeue();
                }
                Log.Warning(line);
            }
        }

        static void SenderLoop()
        {
            while (true)
            {
                Item item;
                lock (Gate)
                {
                    while (Queue.Count == 0) Monitor.Wait(Gate);
                    item = Queue.First.Value;
                    Queue.RemoveFirst();
                }
                try
                {
                    using (var msg = new HttpRequestMessage(HttpMethod.Post, item.Url))
                    {
                        msg.Content = new StringContent(item.Json, Encoding.UTF8, "application/json");
                        if (item.Token.Length > 0)
                            msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + item.Token);
                        using (var resp = Client.SendAsync(msg).GetAwaiter().GetResult())
                        {
                            if (!resp.IsSuccessStatusCode)
                                RecordFailure("HTTP " + (int)resp.StatusCode);
                        }
                    }
                }
                catch (Exception e)
                {
                    RecordFailure(e.GetBaseException().Message);
                }
                MaybeQueueSummary();
            }
        }

        static void RecordFailure(string why)
        {
            lock (Gate) { _failures++; _lastFailure = why; }
        }

        static void MaybeQueueSummary()
        {
            lock (Gate)
            {
                if (_failures == 0 && _dropped == 0) return;
                long now = Stopwatch.GetTimestamp();
                if (_lastLogTs >= 0 && (now - _lastLogTs) < LogInterval.TotalSeconds * Stopwatch.Frequency) return;
                _lastLogTs = now;
                if (LogLines.Count < MaxLogLines)
                    LogLines.Enqueue($"[HermesVoice] Dial-out to agent: {_failures} failure(s), {_dropped} dropped event(s) since last report (last error: {_lastFailure})");
                _failures = 0; _dropped = 0;
            }
        }
    }
}
