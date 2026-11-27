// Port of agent/rimworldllm_agent/core/adapter.py: everything game-specific the engine needs.
//
// Core event format (what NormalizeEvent returns, a JObject): {"seq", "game" (game time), "type", "actors":
// [{"id", "role"}], "summary", "severity" (0..1), "witnesses": [ids], "ledger_id", "data"}.
// Game time is whatever clock the adapter supplies (frames, seconds, turns); core only compares and subtracts it.
// Characters may appear and vanish; there is no fixed cast.
//
// The core knows actors, scenes, events and minds in the abstract. A game adapter (RimWorldLLM.Mind.<Game>) supplies the
// vocabulary, the prompt's world rules, how a snapshot becomes a bio and a voice card, how events rank, and how a scene
// reads. Core references nothing from a game's namespace.
//
// Python's Protocol had a few members core read with getattr(adapter, name, default) (steward_kit, silent_events, vanilla_social,
// argument_*, concern_*, creative wording, ...). Here they are ordinary members of IGameAdapter, and GameAdapterBase gives them
// the same defaults as the Python's getattr fallbacks, so a small test adapter only overrides what it needs.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimWorldLLM.Mind
{
    /// <summary>How an actor is addressed and every word that names them (for the sanitizer).</summary>
    public sealed class NameInfo
    {
        public readonly string Short;
        public readonly HashSet<string> Tokens;
        public NameInfo(string shortName, IEnumerable<string> tokens)
        {
            Short = shortName;
            Tokens = new HashSet<string>(tokens);
        }
    }

    /// <summary>The adapter contract. Snapshots, scenes and events are JObjects (the bridge's decoded JSON).</summary>
    public interface IGameAdapter : IMemoryAdapter, IFirstsAdapter
    {
        string Name { get; }
        ProtocolNames Protocol { get; }
        /// <summary>Intent labels (and tools) that are romantic or sexual: never with a minor.</summary>
        ISet<string> RomanticIntents { get; }
        /// <summary>The closed effect set; the first one means "nothing".</summary>
        IReadOnlyList<string> IntentLabels { get; }
        /// <summary>World rules, speech rules, output format, label glosses.</summary>
        string BlockA { get; }
        /// <summary>Capitalised words that are never names in this game.</summary>
        ISet<string> CommonWords { get; }
        /// <summary>System text for the narrator's one-line coverage.</summary>
        string NarratorPrompt { get; }

        /// <summary>Game-time units in one "day" (memory decay, quotas).</summary>
        int UnitsPerDay { get; }
        /// <summary>Game units per real second at a game speed (pause = the last running speed).</summary>
        double UnitsPerSecond(int speed);

        // prompt wording for the idle work (core supplies the facts, the adapter the voice and flavour)
        string ReflectionSystem { get; }
        string ReflectionRequest { get; }
        string DiarySystem { get; }
        string DiaryRequest { get; }
        string ChronicleSystem { get; }
        string ChronicleRequest { get; }
        string RecapSystem { get; }
        string RecapRequest { get; }
        string LoreSystem { get; }
        string LoreRequest { get; }
        /// <summary>Block D label for the matching shared names (e.g. "Local names").</summary>
        string LoreLabel { get; }
        string ChronicleSummarySystem { get; }

        // optional wording (Python getattr with a default: null / empty means "the core's own text")
        string ReflectionHabitRequest { get; }
        string ThoughtSystem { get; }
        string ThoughtRequest { get; }
        string DreamSystem { get; }
        string DreamRequest { get; }
        string ReactionSystem { get; }
        string ReactionRequest { get; }
        IReadOnlyList<string> BystanderTemplates { get; }
        ISet<string> ArgumentIntents { get; }
        string ArtSystem { get; }
        string ArtRequest { get; }
        string ArtRequestGround { get; }
        // documentary voice (core/commentary.py); null = the adapter has none
        string CommentaryPrompt { get; }
        string CommentaryRequest { get; }
        string CommentaryRecapRequest { get; }
        /// <summary>commentary_moment(event, director): the documentary moment of an event, or null.</summary>
        Moment CommentaryMoment(JObject e, ICommentaryHost director);
        string RecapRequestGround { get; }
        string LoreRequestGround { get; }
        /// <summary>(pattern, weight): what makes a chronicle entry worth a recap line.</summary>
        IReadOnlyList<(string Pattern, int Weight)> RecapImportance { get; }
        /// <summary>Events the director never voices, remembers or chronicles.</summary>
        ISet<string> SilentEvents { get; }
        /// <summary>The steward's game side (core/steward.py). Typed object until the steward kit is merged in.</summary>
        object StewardKit { get; }

        string DefaultBridgeJson();
        /// <summary>The player's own character: others talk to it, it has no mind here.</summary>
        bool IsControlled(int actorId);

        // ---- events
        Cls Priority(string eventType);
        JObject NormalizeEvent(JObject raw);
        // Importance / IsPinned / FirstPerson / IsPrivate come from IMemoryAdapter; FirstOf from IFirstsAdapter.
        string FirstTrigger(string phrase, string summary);
        (int, int)? ConversationPair(JObject ev);
        bool IsUrgentVisual(JObject ev);
        bool IsLedgerEnd(JObject ev);
        /// <summary>Bypasses the narrator's minimum gap.</summary>
        bool IsMajorThreat(JObject ev);
        /// <summary>Goes into the day's chronicle notes.</summary>
        bool ChronicleWorthy(JObject ev);
        /// <summary>Writes a chronicle entry now (death, big fight ends).</summary>
        bool ChronicleTrigger(JObject ev);
        /// <summary>intent -> visibility of the told memory.</summary>
        IReadOnlyDictionary<string, string> ToldIntents();
        /// <summary>Intents that must name a third actor (gossip).</summary>
        ISet<string> AboutIntents();
        /// <summary>The two actors of a vanilla argument, else null.</summary>
        List<int> ArgumentParties(JObject ev);
        /// <summary>A "gathering" event as plain data for core/group, or null.</summary>
        JObject GatherInfo(JObject ev);
        /// <summary>The game's own interaction between two actors, when the mod asked for words for it; else null.</summary>
        JObject VanillaSocial(JObject ev);
        string VanillaRequest(JObject info, string speaker, string listener);
        /// <summary>A generated line that only remarks on the talking itself (context: the scene's texts).</summary>
        bool DropsMeta(string text, IEnumerable<string> context);
        /// <summary>The idle text filler (Python getattr(adapter, "idle_fill", None)): null when the game has none.</summary>
        Func<ITextFillHost, int, Task<int>> IdleFill();

        // ---- ongoing concerns and grudges
        List<JObject> ConcernBirths(JObject ev, IList<int> holders, Func<int, int, bool> loved);
        List<(string Key, string Text)> ConcernResolutions(JObject ev);
        (int Offender, int Victim, string Verb)? SocialOffence(JObject ev);
        ISet<string> OffenceIntents();
        string GrudgeStyle(JObject snap);

        // ---- roster, snapshots, scenes
        /// <summary>(id, raw name) of every character in a roster reply.</summary>
        List<(int Id, JToken RawName)> Roster(JObject resp);
        List<JObject> RosterLedger(JObject resp);
        NameInfo NameInfo(JToken rawName);
        JToken SnapshotName(JObject snap);
        /// <summary>Biological years, null when unknown.</summary>
        double? SnapshotAge(JObject snap);
        string SnapshotJob(JObject snap);
        /// <summary>(other id, opinion)</summary>
        List<(int Id, int? Opinion)> Relations(JObject snap);
        /// <summary>(id, can hear)</summary>
        List<(int Id, bool CanHear)> ScenePresent(JObject scene);
        Scene BuildScene(JObject snap, JObject scene, string trigger, IDictionary<int, string> names, ISet<int> known, ISet<int> minors = null);
        /// <summary>The player-facing card of cleaned facts for creative prompts (core/grounding); null when the game has none.</summary>
        JObject CreativeCard(JObject snap);

        // ---- minds
        string FactsHash(JObject snap);
        FixedBio BuildFixedBio(JObject snap, IDictionary<int, string> names);
        VoiceCard BuildVoiceCard(JObject snap);
        /// <summary>Chattiness.</summary>
        double TalkWeight(JObject snap);
        /// <summary>The speaker's effective register as a plain phrase (base plus any temporary state); null = the game has none.</summary>
        string SpeechRegister(JObject snap);
        string VoiceExamplePrompt(string bio, string registerLine, string tics);

        // ---- tools
        // TODO(csmind-merge): Replay is ported with core/replay (wave 3); the signature is fixed here.
        Task<List<JObject>> ReplayAsync(string path, object cfg, object llm);
    }

    /// <summary>Defaults for the members core read through getattr (the Python's fallbacks); everything else is abstract.</summary>
    public abstract class GameAdapterBase : IGameAdapter
    {
        public abstract string Name { get; }
        public abstract ProtocolNames Protocol { get; }
        public abstract ISet<string> RomanticIntents { get; }
        public abstract IReadOnlyList<string> IntentLabels { get; }
        public abstract string BlockA { get; }
        public abstract ISet<string> CommonWords { get; }
        public abstract string NarratorPrompt { get; }
        public abstract int UnitsPerDay { get; }
        public abstract double UnitsPerSecond(int speed);
        public abstract string ReflectionSystem { get; }
        public abstract string ReflectionRequest { get; }
        public abstract string DiarySystem { get; }
        public abstract string DiaryRequest { get; }
        public abstract string ChronicleSystem { get; }
        public abstract string ChronicleRequest { get; }
        public abstract string RecapSystem { get; }
        public abstract string RecapRequest { get; }
        public abstract string LoreSystem { get; }
        public abstract string LoreRequest { get; }
        public abstract string LoreLabel { get; }
        public abstract string ChronicleSummarySystem { get; }

        public virtual string ReflectionHabitRequest { get { return ""; } }
        public virtual string ThoughtSystem { get { return null; } }
        public virtual string ThoughtRequest { get { return null; } }
        public virtual string DreamSystem { get { return null; } }
        public virtual string DreamRequest { get { return null; } }
        public virtual string ReactionSystem { get { return null; } }
        public virtual string ReactionRequest { get { return null; } }
        public virtual IReadOnlyList<string> BystanderTemplates { get { return new string[0]; } }
        public virtual ISet<string> ArgumentIntents { get { return new HashSet<string>(); } }
        public virtual string ArtSystem { get { return null; } }
        public virtual string ArtRequest { get { return null; } }
        public virtual string ArtRequestGround { get { return null; } }
        public virtual string CommentaryPrompt { get { return null; } }
        public virtual string CommentaryRequest { get { return null; } }
        public virtual string CommentaryRecapRequest { get { return null; } }
        public virtual Moment CommentaryMoment(JObject e, ICommentaryHost director) { return null; }
        public virtual string RecapRequestGround { get { return null; } }
        public virtual string LoreRequestGround { get { return null; } }
        public virtual IReadOnlyList<(string Pattern, int Weight)> RecapImportance { get { return new (string, int)[0]; } }
        public virtual ISet<string> SilentEvents { get { return new HashSet<string>(); } }
        public virtual object StewardKit { get { return null; } }

        public abstract string DefaultBridgeJson();
        public abstract bool IsControlled(int actorId);

        public abstract Cls Priority(string eventType);
        public abstract JObject NormalizeEvent(JObject raw);
        public abstract double Importance(JObject ev, int actorId);
        public abstract bool IsPinned(JObject ev, int actorId, List<string> firsts);
        public abstract string FirstPerson(JObject ev, int actorId);
        public virtual (string Key, string Phrase)? FirstOf(JObject ev) { return null; }
        public virtual string FirstTrigger(string phrase, string summary) { return ("The first time you have ever " + phrase + ": " + summary).Trim(); }
        public abstract (int, int)? ConversationPair(JObject ev);
        public abstract bool IsUrgentVisual(JObject ev);
        public abstract bool IsLedgerEnd(JObject ev);
        public abstract bool IsPrivate(JObject ev, int actorId);
        public abstract bool IsMajorThreat(JObject ev);
        public abstract bool ChronicleWorthy(JObject ev);
        public abstract bool ChronicleTrigger(JObject ev);
        public abstract IReadOnlyDictionary<string, string> ToldIntents();
        public abstract ISet<string> AboutIntents();
        public virtual List<int> ArgumentParties(JObject ev) { return null; }
        public virtual JObject GatherInfo(JObject ev) { return null; }
        public virtual JObject VanillaSocial(JObject ev) { return null; }
        public virtual string VanillaRequest(JObject info, string speaker, string listener) { throw new NotSupportedException(); }
        public virtual bool DropsMeta(string text, IEnumerable<string> context) { return false; }
        public virtual Func<ITextFillHost, int, Task<int>> IdleFill() { return null; }

        public virtual List<JObject> ConcernBirths(JObject ev, IList<int> holders, Func<int, int, bool> loved) { return new List<JObject>(); }
        public virtual List<(string Key, string Text)> ConcernResolutions(JObject ev) { return new List<(string, string)>(); }
        public virtual (int Offender, int Victim, string Verb)? SocialOffence(JObject ev) { return null; }
        public virtual ISet<string> OffenceIntents() { return new HashSet<string>(); }
        public virtual string GrudgeStyle(JObject snap) { return ""; }

        public abstract List<(int Id, JToken RawName)> Roster(JObject resp);
        public abstract List<JObject> RosterLedger(JObject resp);
        public abstract NameInfo NameInfo(JToken rawName);
        public abstract JToken SnapshotName(JObject snap);
        public virtual double? SnapshotAge(JObject snap) { return null; }
        public abstract string SnapshotJob(JObject snap);
        public abstract List<(int Id, int? Opinion)> Relations(JObject snap);
        public abstract List<(int Id, bool CanHear)> ScenePresent(JObject scene);
        public abstract Scene BuildScene(JObject snap, JObject scene, string trigger, IDictionary<int, string> names, ISet<int> known, ISet<int> minors = null);
        public virtual JObject CreativeCard(JObject snap) { return null; }

        public abstract string FactsHash(JObject snap);
        public abstract FixedBio BuildFixedBio(JObject snap, IDictionary<int, string> names);
        public abstract VoiceCard BuildVoiceCard(JObject snap);
        public abstract double TalkWeight(JObject snap);
        public virtual string SpeechRegister(JObject snap) { return null; }
        public abstract string VoiceExamplePrompt(string bio, string registerLine, string tics);

        public virtual Task<List<JObject>> ReplayAsync(string path, object cfg, object llm) { throw new NotSupportedException(); }
    }
}
