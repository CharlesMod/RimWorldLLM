# Creative-boost round (E4B rp)

Date: 2026-11-26. Model: **gemma4-e4b-rp-q40** served by easyEngine 1867470 from a private engine home on poe, Tesla P40
(GPU 0 only), 3 slots x 65536 ctx, q4_0 KV, 4564 MiB. Engine-enforced samplers (rp for prose, structured = greedy).
Every speed figure is a P40 figure. Branch agent/creative from main 59b6e87.

Goal (brief): raise the blind-judge score of the weak creative features on E4B (art, lore, recap by about +1.5; aims and
spoken prose by +0.7 to +1.0), keeping only changes that measurably help. Raw data: `bench/results/creative/`
(probe runs, soak runs, analyses, every judge sample, key, verdict file and table, the poe scripts).

## Verdict

| feature | before (main) | after (kept defaults) | what did it | kept |
|---|---|---|---|---|
| Art descriptions | 75% of real-shaped descriptions dropped (unknown_name); shown ones 2.3 | 0% dropped; 2.9-3.0 (four judges) | **name fix** (bug) | fix kept; grounding, plan, best-of: no gain, off. **Art now defaults off on small models** (keeper rule: below 3.0) |
| Lore names | 2.5-3.0 | 3.2-3.8 (paired +0.65 / +0.74) | grounding v2 + plan | kept |
| "Previously on" recap | 2.56-2.61 | 3.11 (paired +0.50 / +0.55) | grounding (ranked, time order) + best of 4, auto pick | kept |
| Funeral eulogy | 2.0-2.17 | 2.45-3.18 (paired +0.45 / +1.0) | grounding + plan | kept |
| Wedding vows | 2.33-2.5 | 2.33 (no arm beat plain) | none | plain |
| Aims / contrast clouds | 2.42-2.67; 47% open "I just want" | 3.0-3.1 (paired +0.33 / +0.63); 0% "I just want" | aim tension + subtext menu (aim_ground) | kept; clouds stay on (3.0, not below) |
| Aim temperature (item 7) | greedy | 0.3 / 0.5: +0.08 / +0.17 n.s.; distinctness .60/.60/.64 | none | **no sampler change** |
| Cliche filter (item 5) | | 0 stock phrases in 677 probe outputs | nothing to filter | off (code kept) |
| Style models (item 6) | | not built | no RimWorld Defs or real tale text on sprinter or poe | blocked, keeper decision |

The +1.5 target was not reached within one judge for art, lore or recap; the gains are +0.5 to +0.75 per feature on the
same seeds, plus the art name fix, which turns a mostly dropped feature into a working one. Absolute judge levels move
between judges by up to 0.5 (the same greedy aim configuration scored 3.0 with one judge and 2.54 with another), so only
paired, within-judge numbers are used for keep/drop.

## Method

1. **Rich soak fixture** (`bench/soak/fixture_rich.py`, `soak.py --fixture rich`, the new default; `--fixture classic`
   reproduces the old runs): 9 pawns with distinct trait sets and 2-3 tales each, a 14-year-old, a 71-year-old who dies of
   age, a married couple, a couple who marry in the script, rivals (opinions -55/-60) who fight, siblings (one is killed in
   a raid), three ideoligions, named raiders (three factions with leaders), 8 distinct art events a day with RimWorld-style
   image text, `material` and `tale` {def, summary, day, pawns}, the four injection canaries in the classic places.
   **One record per piece of text**: the same text from the same actor under two kinds is one record with `also`.
2. **Controlled creative probe** (`bench/soak/creative_probe.py`): drives the agent's own functions (`write_art`,
   `lore.coin`, `recap.build`, `speak` with the aim pass, `group._compose_turn` for the lead's eulogy and the vows) on the
   rich colony with history (chronicle, memories, relations). `--set key=value` flips agent config fields; every arm runs the
   same seed, cases and reps. Per piece: text as the game would show it, drop reason, wall time, calls, slot seconds.
3. **Blind judge** (`bench/soak/judge_probe.py make|score`): one Claude subagent per feature, scoring 1-5 on a rubric,
   reading a shuffled sample that mixes all arms with the data each piece was written from (maker card and memories for art,
   the full chronicle window for recaps, the speaker's memories for aims and ceremonies), never the arm. It never writes
   text. The scorer joins verdicts to the key and gives means, share <= 2 and paired diffs (same case and rep).
   Judge brief: `bench/results/creative/judge/` (samples, keys, verdicts, tables per round r1-r5).
4. Rounds: r1 (items 2-5, 4 reps, 12 per arm: too noisy, and the judge could not see the maker card or full recap window),
   r2 (enriched context, 6 reps, 18-24 per arm, plus an arm that equals another as a noise replicate), r3 (art v2), r4
   (kept configuration vs base, all features), r5 (aim temperature).

## Per item

Means per arm (n), from the round that measured them; "base" is main's behaviour with the name fix unless stated.

### 1. Measurement
Built as above. Baseline soak on the rich fixture with main's code (`soak/before-1x`, `an/before.*`): 272 calls,
0 errors, 0 canaries shown or raw, 0 explicit with adult off, 0 label leaks, 0 over-length. The analyzer flags 2 age-gate
"violations": both are the minor's reflection bio "The hall was beautiful" (an appearance word about a hall: an analyzer
false positive). Art: 5 of 9 descriptions dropped as unknown_name (see below).

**Found on the way (bug, fixed, unconditional):** the art sanitiser refused any colonist the maker did not remember, but the
game's own tale text names the depicted colonists, so most real descriptions were dropped. Probe: 24 of 32 dropped before,
0 after; judge r1 base 2.29 (the 8 that survived) vs fixed 2.92. Names the prompt's own data gives are now allowed; a
colonist the data never names is still refused (test_art).

### 2. Richer grounding
| feature | base | grounding | note |
|---|---|---|---|
| aim (r1, 12 each; five arms carry aim_ground) | 2.42 / 2.50 | 3.00, 3.08, 3.33, 3.08, 2.92 (mean 3.08) | paired vs base +0.42..+0.92 in every arm |
| aim (r4, 24) | 2.67 | 3.00 | paired +0.33 (12 wins, 5 losses); "I just want" 47% -> 0% |
| lore (r2, 23) | 3.04 | 3.30 (replicate 3.30) | r1 version 1.9 ("the X" fragments such as "yard with" as names): replaced by v2 |
| recap (r2, 18) | 2.56 | 2.83 (replicate 3.00) | r1 ranked order made the model merge events ("married while they fought"): v2 keeps time order, biggest marked |
| eulogy (r2, 12) | 2.17 | 2.42 | |
| vows (r2, 12) | 2.50 | 2.33 | not kept |
| art (r2 24 / r3 24) | 3.04 / 2.92 | 2.79 / 2.67; v2 2.88 | v1 leaked the mood label ("middling", "Ate without table"); v2 without labels = base. Not kept |

Aim grounding: the aim request names the scene's tension (liking, grudge, neither), one shared memory, "you are keeping
something from them" when a private memory involves the listener (never its content), and one subtext kind from a menu
(fear, want, secret, judgment) chosen by hash of speaker, line count and game hour, so pawns in the same moment differ under
greedy decoding; "the aim must complicate the spoken line, never restate it, and never open with 'I just want'"; an aim that
still opens that way is dropped (the line never is). 58 words at most.

### 3. Plan-then-write (one private plan line, never shown)
| feature | without plan (grounded) | with plan | slot s (base -> plan) |
|---|---|---|---|
| lore (r2) | 3.30 | **3.78** (paired vs base +0.73, 13 wins / 1 loss) | 0.32 -> 1.00 |
| eulogy (r2) | 2.42 | **3.18** (paired vs base +1.0, 8/1) | 1.31 -> 2.08 |
| recap (r2) | 2.83-3.00 | 2.94 | 0.70 -> 1.43 |
| art (r2) | 2.79 | 2.92 (base 3.04) | 0.83 -> 1.47 |
| vows (r2) | 2.33 | 2.25 | 1.19 -> 1.83 |
Kept for lore and eulogy.

### 4. Best of 4 with rerank
| feature | grounded | auto pick | LLM pick | slot s (auto / LLM) |
|---|---|---|---|---|
| recap (r2) | 2.83-3.00 | **3.11** | 2.94 | 2.40 / 2.59 |
| lore (r2) | 3.30 | 3.46 | 3.38 | 1.11 / 1.25 |
| eulogy (r2) | 2.42 | 2.67 | 2.58 | 2.83 / 2.89 |
| art (r2) | 2.79 | 2.83 | 2.92 | 3.18 / 3.37 |
| vows (r2) | 2.33 | 2.50 | 2.50 | 2.47 / 2.58 |
The automatic score (concrete words from the data, colony names, stock phrases, length, copied runs) matched or beat the
LLM rubric pick everywhere at less slot time. Kept for the recap only (once per load; plan gave lore and eulogy more per
second than best-of).

### 5. Cliche filter
No stock phrase from the lexicon appeared in 677 probe outputs (E4B rp with these prompts writes plain), so the retry never
fired. The code stays (off by default; `cliche_retry`).

### 6. Style models
Not built. There is no RimWorld install, Defs XML or captured real tale text on sprinter or poe (searched both; the
hive-train corpus is template-generated, not game text), and Claude-written examples are not allowed. Needs the keeper:
copy two or three RulePack art/tale texts from a RimWorld install (Core/Defs/RulePackDefs) or real in-game tale text.

### 7. Aim temperature
Engine game profiles `aim3` / `aim5` (temperature 0.3 / 0.5, top_k 64, top_p 0.95; applied header confirmed
`aim3;enforce`), routed to the aim-carrying intent pass only (`aim_sampler`, `core/sampler_route.py`), 8 scenes x 8 reps:

| | greedy | 0.3 | 0.5 |
|---|---|---|---|
| distinct aims (share unique) | 0.604 | 0.596 | 0.638 |
| mean pairwise Jaccard across pawns | 0.099 | 0.098 | 0.094 |
| aims dropped | 11/64 | 7/64 | 6/64 |
| judge (r5, 24 each) | 2.54 | 2.63 | 2.71 (paired +0.17, n.s.) |
| intent accuracy, grammar mode (bench struct, 3 reps) | 83.3 / 83.3 / 83.3% | 83.3 / 80.0 / 86.7% | 80.0 / 83.3 / 80.0% |
| intent accuracy, schema mode | 76.7% (all reps) | 70.0 / 80.0 / 70.0% | 76.7 / 73.3 / 76.7% |
The subtext rotation already gives distinct aims under greedy; temperature adds nothing measurable. **No sampler change.**

## Latency and slot cost (P40)

- Spoken lines: unchanged path (single shot). The line + aim intent pass (probe, same scenes): median 1.62 s -> 1.74 s
  (+7%), p90 1.77 -> 1.87 s: within the 10% budget. The extra cost is about 60 words in the intent request.
- Rare pieces (slot seconds per piece, median): lore 0.32 -> 1.01 (plan), recap 0.70 -> 2.39 (best of 4), eulogy 1.31 ->
  1.96 (plan); vows unchanged; art off on small models (0.85 when on).
- Yield: every extra call (plan, candidate) first checks whether a line is waiting (idle work: actors want windows;
  recap and ceremonies: another actor's item of class REACTIVE or higher is queued or leased) and stops with the best so
  far. Unit-tested (test_creative), not exercised under load.
- SOAK_LATENCY

## Model-tier switch (keeper decision applied)

Art judged 2.88-3.04 across four judges (below 3.0 on average): `art_small` defaults to False, so art descriptions are off
on models whose id contains e2b or e4b (`small_model_patterns`), or while the engine serves a starter model. The agent reads
the serving model from the engine's status (`core/model_tier.py`, every 60 s, off the line path). Clouds judged 3.0-3.1 with
the aim tension: `inner_cloud_small` stays True. Both are config fields the player or keeper can flip.

## Soak before / after

SOAK_TABLE

## Safety on the new paths

Plans, candidates and reranks go through the same game-data wrapping (data sections, DATA_RULE) and the caller's sanitiser
and age gate; a plan with romance vocabulary is dropped whenever it is written (conservative: an adult wedding plan that
says "love" is dropped too; the vows do not use plans). MINOR_RULE is now in the lore and recap system prompts (it was
missing). Aim tension never names a private memory's content. The wedding minor gate is unchanged.

## Still needs an in-game check

- `data.material` and `data.tale` from the mod (CompArt.TaleRef, the private TaleReference.tale read by reflection,
  Tale.ShortSummary, the day from AgeTicks) on a real save.
- Art on real tale text, if art is turned back on for a small model; the 26B path keeps art on.
- Clouds with the aim tension in a live colony (the probe and soak are fixture scenes).
- The model-tier read against a real engine during first-run staging (starter model).
