using System.Collections.Generic;

namespace HermesVoice
{
    public enum EffectTarget { Listener, Speaker, Witnesses, About }

    public sealed class EffectSpec
    {
        public string Def;              // ThoughtDef defName in Defs/ThoughtDefs.xml
        public EffectTarget Target;
        public bool Social;             // opinion thought (otherPawn = the other party) rather than a mood thought
        public float Amount;            // mood or opinion points at normal strength
    }

    /// <summary>The intent -> effect table of SPEC section 8. Sizes live here and in the XML defs; the agent never sets them.</summary>
    public static class EffectTable
    {
        public static readonly string[] Intents = { "none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain" };

        public static bool Known(string intent) { return System.Array.IndexOf(Intents, intent) >= 0; }

        /// <summary>Effects for an intent. Boast and complain are sized by the listener's traits in the game code (sign and size vary).</summary>
        public static List<EffectSpec> For(string intent)
        {
            var l = new List<EffectSpec>();
            switch (intent)
            {
                case "kind":
                    l.Add(new EffectSpec { Def = "HV_Kind_Opinion", Target = EffectTarget.Listener, Social = true, Amount = 5 });
                    l.Add(new EffectSpec { Def = "HV_Kind_Mood", Target = EffectTarget.Listener, Amount = 1 });
                    break;
                case "comfort":
                    l.Add(new EffectSpec { Def = "HV_Comforted_Mood", Target = EffectTarget.Listener, Amount = 3 });
                    l.Add(new EffectSpec { Def = "HV_Comforted_Opinion", Target = EffectTarget.Listener, Social = true, Amount = 5 });
                    break;
                case "slight":
                    l.Add(new EffectSpec { Def = "HV_Slight_Opinion", Target = EffectTarget.Listener, Social = true, Amount = -5 });
                    break;
                case "insult":
                    l.Add(new EffectSpec { Def = "HV_Insulted_Opinion", Target = EffectTarget.Listener, Social = true, Amount = -5 });
                    l.Add(new EffectSpec { Def = "HV_Insulted_Mood", Target = EffectTarget.Listener, Amount = -2 });
                    l.Add(new EffectSpec { Def = "HV_InsultWitnessed_Opinion", Target = EffectTarget.Witnesses, Social = true, Amount = -2 });
                    break;
                case "confide":
                    l.Add(new EffectSpec { Def = "HV_Confided_Opinion", Target = EffectTarget.Listener, Social = true, Amount = 5 });
                    l.Add(new EffectSpec { Def = "HV_Confiding_Opinion", Target = EffectTarget.Speaker, Social = true, Amount = 3 });
                    break;
                case "gossip":
                    l.Add(new EffectSpec { Def = "HV_Gossip_Opinion", Target = EffectTarget.About, Social = true, Amount = -3 });
                    break;
                case "boast":
                    l.Add(new EffectSpec { Def = "HV_Boast_Opinion", Target = EffectTarget.Listener, Social = true, Amount = -2 });
                    break;
                case "complain":
                    l.Add(new EffectSpec { Def = "HV_Complain_Opinion", Target = EffectTarget.Listener, Social = true, Amount = -2 });
                    break;
            }
            return l;
        }

        public static float Strength(string effects)
        {
            switch (effects)
            {
                case "off": return 0f;
                case "light": return 0.5f;
                default: return 1f;
            }
        }
    }
}
