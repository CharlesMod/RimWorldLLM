// Port of agent/rimworldllm_agent/core/common_words.py.
// Ordinary English words (lower case). A capitalised word at the start of a sentence is only a name candidate
// when it is not one of these. A floor, not a dictionary: a word missing here fails safe (the line is dropped).
using System;
using System.Collections.Generic;

namespace RimWorldLLM.Mind
{
    public static class CommonWords
    {
        private const string Function = @"
a an the this that these those it its it's they them their theirs we us our ours you your yours he him his she her hers
i me my mine myself yourself himself herself itself ourselves themselves one ones someone somebody something anyone anything
everyone everybody everything nobody nothing none each every all both either neither any some many much more most few less
least other another such own same several
is am are was were be been being do does did done doing have has had having will would shall should can could may might must
isn't aren't wasn't weren't don't doesn't didn't haven't hasn't hadn't won't wouldn't can't couldn't shouldn't mustn't
i'm i'll i've i'd you're you'll you've you'd he's he'll he'd she's she'll she'd we're we'll we've we'd they're they'll they've
they'd that's that'll there's there'll here's what's who's how's let's where's when's why's
and or but nor so yet for because since although though unless until while whether if then than as like
of in on at by with without from to into onto over under above below between among through during before after about
against around across behind beside near off out up down away back toward towards upon within along
not no yes yeah yep nope okay ok oh ah ha hm hmm huh ugh ow um uh hey hi hello well fine right sure please sorry thanks thank
damn god gods hell heck wow whoa shh hush psst copy negative affirmative roger
what why how who whom whose where when which whatever whenever wherever however
now just still maybe perhaps never always often sometimes soon later already again ever even only also too very quite rather
almost really actually probably certainly clearly simply finally suddenly slowly quickly quietly carefully nearly barely
here there everywhere somewhere anywhere nowhere today tonight tomorrow yesterday tonight morning evening noon midnight dusk dawn
first second third last next final one two three four five six seven eight nine ten eleven twelve twenty thirty forty fifty
hundred thousand zero half double
monday tuesday wednesday thursday friday saturday sunday january february march april may june july august september october
november december spring summer autumn fall winter
";

        private const string Verbs = @"
look see hear listen watch wait stop stay stand sit lie lay come go get give take bring put set keep hold pass move run walk
climb jump fall drop lift carry pull push throw catch hit shoot fire aim reload cover duck hide flee retreat advance charge
eat drink cook bake chop slice cut peel stir boil fry salt season serve feed sleep rest wake dream
tell say ask answer speak talk shout yell whisper call warn promise swear lie trust believe doubt know think guess wonder
remember forget learn teach show help fix build make repair mend clean wash sweep dig mine haul plant sow harvest hunt skin butcher
craft sew cook grow water tend heal treat bandage cure patch stitch operate
want need like love hate fear miss mind care hope wish try start begin finish end quit leave arrive return join follow lead
send mark note count check test trade buy sell pay owe lend borrow share steal fight kill die live burn freeze bleed hurt break
open close lock unlock turn drive ride fly swim sing play pray beg thank blame forgive
don't do stop mind watch careful easy steady breathe relax calm focus aim fire hold
";

        private const string NounsAdj = @"
meat food stew soup rice corn bread potato potatoes carrot berries berry beans bean salt pepper meal lunch dinner supper
breakfast kitchen table chair bed blanket fire wood stone steel iron metal gold silver cloth leather wool rope tool tools
pick axe shovel hammer knife cleaver bow arrow arrows rifle gun pistol shotgun ammo bullet bullets blade sword spear club
armor helmet wall walls door gate roof floor room house home base camp field fields farm crop crops garden river lake hill
hills mountain mountains forest trees tree grass snow rain storm wind sun moon sky night day week month year season weather
cold hot warm cool dry wet dark light bright quiet loud silence noise sound smell taste
calf muffalo muffalos deer boar thrumbo dog dogs cat cats horse animal animals bird birds rat rats insect insects bug bugs
pirate pirates tribal tribals enemy enemies hostile hostiles target targets mech mechs wolf wolves bear bears
man men woman women boy girl child children kid kids baby friend friends stranger strangers guest guests
doctor sir madam boss chief captain sergeant lieutenant soldier soldiers hunter hunters cook cooks
body arm arms leg legs hand hands foot feet head face eye eyes ear ears mouth teeth back chest belly heart blood skin bone bones
wound wounds scar scars pain fever infection plague cough sickness illness health medicine meds herbal bandage bandages
mood mind thought thoughts word words voice name news story stories plan plans job jobs work chore chores task duty orders order
stance grip shot shots aim range distance meters meter feet yards north south east west left right ahead
time moment minute minutes hour hours second seconds
good bad great poor best worst better worse big small little large long short tall high low heavy light slow fast quick
old new young dead alive empty full whole broken ruined safe dangerous careful clean dirty fresh rotten raw spoiled ready done
hungry tired sick hurt wounded angry afraid scared calm happy sad lonely proud ashamed sorry
easy hard rough soft sharp dull strong weak clear thin thick sweet bitter sour plain simple strange odd real true false
quiet nice kind cruel fair wrong free sure possible impossible
nobody enough plenty lot lots bit piece pieces part parts side sides way ways thing things stuff place places
end edge top bottom middle corner line row pile stack
life death luck chance hope fear trouble mess shame peace war fight
ship ships world planet land ground dust mud ice
fear honor mercy truth lie lies secret secrets
";

        public static readonly HashSet<string> CommonSentenceStart = Build();

        private static HashSet<string> Build()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var seps = new[] { ' ', '\n', '\r', '\t' };
            foreach (var blk in new[] { Function, Verbs, NounsAdj })
                foreach (var w in blk.Split(seps, StringSplitOptions.RemoveEmptyEntries)) set.Add(w);
            return set;
        }
    }
}
