// Port of agent/rimworldllm_agent/core/speech.py: the speaker's register (how simple or articulate the words are), asked of the
// game adapter. Core references nothing from a game. The Python took the Director for its adapter and bridge; here they are
// passed directly.
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    public static class Speech
    {
        /// <summary>The effective register phrase for one actor now: the adapter reads a fresh snapshot (base plus any temporary
        /// state); with no snapshot to read, the base phrase saved in the fixed bio. Null when the game has no register.</summary>
        public static async Task<string> RegisterPhraseAsync(IGameAdapter adapter, IBridge bridge, int aid, Mind mind, JObject snap = null)
        {
            if (snap == null)
            {
                try { snap = await bridge.ActorAsync(aid).ConfigureAwait(false); }
                catch (BridgeError) { snap = null; }
            }
            if (snap != null) return adapter.SpeechRegister(snap);
            JToken w = mind.Fixed.Extras == null ? null : mind.Fixed.Extras["words"];
            string s = Py.PyJson.Truthy(w) ? Py.PyJson.Str(w) : null;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>One line for the inline prompts (thought, diary, dream, bystander); empty when there is no phrase.</summary>
        public static string RegisterLine(string name, string phrase)
        {
            return !string.IsNullOrEmpty(phrase) ? "Write it the way " + GameData.Name(name) + " would: " + GameData.Clean(phrase, "desc") + ".\n" : "";
        }
    }
}
