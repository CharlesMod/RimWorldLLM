// NEW (the narrator can look at the screen): OpenAI image content parts in a loop's history, and their redaction for any log.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind.ToolLoop
{
    public static class ImageParts
    {
        public const string Dropped = "(an earlier picture was dropped)";
        public const string NativeLead = "The picture from look:";

        /// <summary>{"type":"image_url","image_url":{"url":"data:&lt;mime&gt;;base64,&lt;b64&gt;"}}.</summary>
        public static JObject Part(string mime, string b64) => new JObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JObject { ["url"] = "data:" + (string.IsNullOrEmpty(mime) ? "image/jpeg" : mime) + ";base64," + b64 },
        };

        public static bool IsImage(JToken p) => p is JObject o && (string)o["type"] == "image_url";

        /// <summary>Attach part to the history for the next request. Any earlier picture is replaced by a text note first, so a request
        /// carries exactly one. grammar/json: the last message (the user's "Result of ...") becomes [text, image]. native: a user
        /// message [text, image] after the tool rows.</summary>
        public static void Attach(List<JObject> hist, string protocol, JObject part)
        {
            Strip(hist);
            if (protocol == "native")
                hist.Add(new JObject { ["role"] = "user", ["content"] = new JArray(Text(NativeLead), part) });
            else if (hist.Count > 0 && (string)hist[hist.Count - 1]["role"] == "user" && hist[hist.Count - 1]["content"].Type == JTokenType.String)
            {
                var last = hist[hist.Count - 1];
                last["content"] = new JArray(Text((string)last["content"]), part);
            }
            else
                hist.Add(new JObject { ["role"] = "user", ["content"] = new JArray(Text(NativeLead), part) });
        }

        private static JObject Text(string s) => new JObject { ["type"] = "text", ["text"] = s };

        /// <summary>Replace every image part in the history with a short text part.</summary>
        public static void Strip(List<JObject> hist)
        {
            foreach (var m in hist)
                if (m["content"] is JArray a)
                    for (int i = 0; i < a.Count; i++)
                        if (IsImage(a[i])) a[i] = Text(Dropped);
        }

        public static int Count(IEnumerable<JObject> msgs)
            => msgs.Sum(m => m["content"] is JArray a ? a.Count(IsImage) : 0);

        /// <summary>A copy of a message (or body) for a log: every image data URI becomes "data:image/jpeg;base64,&lt;N bytes&gt;".</summary>
        public static JToken Redact(JToken t)
        {
            var c = t.DeepClone();
            RedactInPlace(c);
            return c;
        }

        private static void RedactInPlace(JToken t)
        {
            if (t is JObject o)
            {
                if (IsImage(o) && o["image_url"] is JObject iu && iu["url"]?.Type == JTokenType.String)
                    iu["url"] = RedactUrl((string)iu["url"]);
                foreach (var p in o.Properties().ToList()) RedactInPlace(p.Value);
            }
            else if (t is JArray a) foreach (var x in a) RedactInPlace(x);
        }

        public static string RedactUrl(string url)
        {
            int i = url.IndexOf(";base64,", System.StringComparison.Ordinal);
            if (!url.StartsWith("data:", System.StringComparison.Ordinal) || i < 0) return url;
            return url.Substring(0, i + 8) + "<" + (url.Length - i - 8).ToString(CultureInfo.InvariantCulture) + " bytes>";
        }
    }
}
