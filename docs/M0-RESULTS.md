# Milestone 0 results: measurements for RimWorldLLM 2

Run 2026-11-24 on the sprinter's RTX 5080. Harness: `bench/m0/` (README there has the rerun recipe). Raw data: `bench/m0/results/` (`summary.md` has every table in full).

## 0. Setup

| | |
|---|---|
| GPU | RTX 5080, 16303 MiB; the desktop (kwin, Discord) held about 1050 MiB when idle |
| Engine | llama.cpp build 11279 (`b11279-cuda13-sm120-p60e8884f`), CUDA 13, sm120 |
| Main model | unsloth `gemma-4-26B-A4B-it` UD-Q3_K_XL (12.9 GB), stock, thinking off (`--reasoning off`; the empty thought block is part of the prompt and stripped from replies) |
| Side column | IQ4_XS (13.6 GB), run earlier at 3 x 8192 ctx, KV q8_0, fully on GPU, `--cache-ram 4096`. Fewer runs (Q1 3 reps; Q4, Q6 once) |
| Target shape | `-np 3 -c 196608` (65536 per slot) `-ctk q4_0 -ctv q4_0 -fa on --cache-ram 8192 --n-cpu-moe 6`, default `-ub 512`, `kv_unified` false |
| Sampling | temperature 0.8, top-k 64, top-p 0.95, min-p 0.05; lines stop at newline, 48 token cap; `id_slot` pinned on every request |
| Prompt | SPEC section 4 order. System turn = blocks A-E (byte-stable), user turn = F (turns) + G (voice reminder + request). Prefix A-E is 1650-1750 tokens (A about 840) |
| Fixtures | 6 colonists, 30 scenes, 26 focal-neutral scenes, 20 grim scenes, 30 hand-labelled lines. Written by a Sonnet helper; I read the pawn, scene and intent files' structure and the generated lines, not every fixture word |

### VRAM budget (keeper's addendum)

Cap = 16303 - 1536 (OS/desktop) - RimWorld: **13231 MiB with 1.5 GB for RimWorld, 11695 MiB with 3 GB.** Numbers are the llama-server process's own memory (nvidia-smi per process) after load, plus a short speed probe (6 prompts, so the speeds are +-5%).

| setting | process MiB | weights on GPU | weights in RAM | KV | compute | cold prefill t/s | decode t/s | fits 13231 | fits 11695 |
|---|---|---|---|---|---|---|---|---|---|
| `--n-cpu-moe 0` | OOM | | | | | | | no | no |
| 1 | 14726 | 11973 | 1118 | 1333 | 1060 | probe failed | | no | no |
| 2 | 14402 | 11652 | 1494 | 1333 | 1060 | 3115 | 112.6 | no | no |
| 3 | 14080 | 11330 | 1870 | 1333 | 1060 | 2552 | 101.7 | no | no |
| 4 | 13756 | 11009 | 2246 | 1333 | 1060 | 2198 | 91.5 | no | no |
| 5 | 13434 | 10687 | 2622 | 1333 | 1060 | 1889 | 83.3 | no (by 203) | no |
| **6** | **13110** | 10366 | 3013 | 1333 | 1060 | 1709 | 77.0 | **yes (121 spare)** | no |
| 8 | 12464 | 9723 | 3765 | 1333 | 1060 | 1409 | 67.7 | yes | no |
| 10 | 11818 | 9080 | 4517 | 1333 | 1060 | 1144 | 63.0 | yes | no (by 123) |
| **11** | **11496** | 8759 | 4893 | 1333 | 1060 | 1035 | 54.8 | yes | **yes (199 spare)** |
| 12 | 11172 | 8437 | 5283 | 1333 | 1060 | 965 | 55.7 | yes | yes |
| 14 | 10526 | 7795 | 6035 | 1333 | 1060 | 837 | 47.9 | yes | yes |

Other shapes measured (all `--n-cpu-moe 6` unless noted):

| shape | process MiB | KV | compute |
|---|---|---|---|
| `-c 98304`, swa not full | 12152 | 793 | 644 |
| `-c 49152 --swa-full` | 14514 | 2970 | 827 |
| `-c 98304 --swa-full` | OOM | | |
| `-c 196608 --swa-full` | OOM (KV extrapolates to about 12 GB) | | |
| IQ4_XS, 3 x 8192, KV q8_0, fully on GPU | about 14100 (total 15117 incl. desktop) | | |

Findings:
- Weights of the Q3_K_XL are 12.9 GB, so even before KV the full model does not fit under either cap. `--n-cpu-moe 6` is the smallest value that fits under the 1.5 GB cap, `11` under the 3 GB cap. Margins are thin (121 and 199 MiB): anything RimWorld grows into during play will push the server out, so the engine spec should leave real headroom or pick one more layer.
- KV is cheap: 1333 MiB for 3 x 65536 at q4_0 because most Gemma layers are sliding-window. The compute buffer (1060 MiB at `-ub 512`) is the next biggest item. Smaller `-ub` would shrink it; untested.
- Cost of offloading experts: decode 112 -> 77 -> 55 t/s (n 2 -> 6 -> 11) and cold prefill 3115 -> 1709 -> 1035 t/s. The IQ4_XS fully on the GPU decoded at about 74 t/s and prefilled at about 5300 t/s, so prefill (cold lease) is where offloading hurts most.
- `n=11`/`12` decode inversion (54.8 vs 55.7) is probe noise.

Everything below was measured at the target shape (n 6) unless it says otherwise.

## 1. Lease latency

Method: per rep, three salted pawn variants (X, Y, Z; the age is changed so blocks B-E differ and only block A is shared). X is leased cold into slot 1 (a), takes two warm turns (b), Y then takes slot 1 and X is re-leased there with a new scene (c) and with the same scene (c'); then X goes into slot 2 (d, d'), after Z and Y have used the slots. 5 reps, so n=5 per cell (IQ4_XS 3). Medians. TTFT is wall time to the first streamed token. "Full line" is wall time to the end of the line. `eff` = (prompt_n + cache_n) / prompt_s.

Target shape (`-c 196608`, n 6, swa not full), cache-ram on vs off:

| case | cache-ram | TTFT s | line s | prompt_n | cache_n | prompt s | raw t/s | eff t/s |
|---|---|---|---|---|---|---|---|---|
| (a) cold | on | 1.19 | 1.46 | 1847 | 0 | 1.07 | 1705 | 1705 |
| (b) warm turn | on | 0.26 | 0.45 | 154 | 1721 | 0.26 | 600 | 7314 |
| (b) warm turn | off | 0.26 | 0.41 | 154 | 1721 | 0.25 | 570 | 7412 |
| (c) old slot, new scene | on | 0.44 | 0.66 | 520 | 1330 | 0.36 | 1382 | 5140 |
| (c) old slot, new scene | off | 1.07 | 1.30 | 1842 | 0 | 1.07 | 1728 | 1728 |
| (c') old slot, same scene | on | 0.19 | 0.36 | 5 | 1837 | 0.09 | 55 | 20289 |
| (c') old slot, same scene | off | 1.06 | 1.26 | 1842 | 0 | 1.06 | 1744 | 1744 |
| (d) other slot, new scene | on | 0.45 | 0.66 | 488 | 1333 | 0.34 | 1438 | 5410 |
| (d) other slot, new scene | off | 1.08 | 1.30 | 1817 | 0 | 1.07 | 1720 | 1720 |
| (d') other slot, same scene | on | 0.19 | 0.42 | 5 | 1812 | 0.09 | 55 | 20006 |
| (d') other slot, same scene | off | 1.07 | 1.29 | 1817 | 0 | 1.06 | 1723 | 1723 |

The `--swa-full` grid had to run smaller (`-c 49152`, n 10) because swa-full does not fit at the target size. Same shape for all four cells (decode is slower here, so absolute times are higher than above; compare within the table):

| case | cram on, swa off | cram on, swa FULL | cram off, swa off | cram off, swa FULL |
|---|---|---|---|---|
| (a) cold, TTFT / line s | 1.70 / 1.92 | 1.54 / 1.82 | 1.58 / 1.85 | 0.80 / 1.10 (cache_n 838) |
| (b) warm turn | 0.37 / 0.63 | 0.31 / 0.54 | 0.37 / 0.61 | 0.31 / 0.60 |
| (c) old slot, new scene | 0.59 / 0.88 (cache_n 1330) | 0.41 / 0.63 (cache_n 1464) | 1.57 / 1.83 (0) | 0.79 / 1.04 (cache_n 829) |
| (c') old slot, same scene | 0.20 / 0.41 (prompt_n 5) | **0.06 / 0.34** (prompt_n 1) | 1.57 / 1.88 (0) | 0.79 / 1.06 (829) |
| (d) other slot, new scene | 0.58 / 0.84 (1333) | 0.41 / 0.66 (1482) | 1.57 / 1.81 (0) | 0.79 / 1.05 (829) |
| (d') other slot, same scene | 0.20 / 0.47 (5) | 0.06 / 0.38 (1) | 1.57 / 1.84 (0) | 0.79 / 1.09 (829) |

IQ4_XS side column (fully on GPU, cache-ram on, swa off, n=3): cold 0.56 / 0.70 s, warm 0.14 / 0.31, (c) 0.34 / 0.48, (c') 0.32 / 0.44, (d) 0.42 / 0.58, (d') 0.33 / 0.47. Everything is about 2x faster than the offloaded Q3_K_XL because prefill is 5300 t/s.

RAM cost (`ramcost.py`, server RssAnon while displacing distinct 1850-token pawn prefixes): about **180 MiB per cached pawn lease**, so the 8 GiB `--cache-ram` holds roughly 45 leases.

Reading it:
- **Does the RAM cache restore a prefix on re-lease? Yes, and into either slot.** With cache-ram off and the default (non-full) SWA cache, a re-lease reuses nothing at all, not even in the pawn's own previous slot (cache_n 0, 1.07 s to first token). With cache-ram on: same lease again restores the whole 1837-token prefix (5 tokens left to process) in 0.09 s of prompt time, TTFT 0.19 s (versus 1.07 s cold, 5.6x faster). A re-lease with a new scene restores 1330 tokens (blocks A-C, plus whatever of D matches) and prefills the other 520, TTFT 0.44 s.
- **Slot affinity is worth nothing.** (c) and (d) are within 0.01 s of each other in every configuration. The restore goes through the RAM cache into whichever slot, so the SPEC rule "a pawn gets back the slot it used last" can go.
- Cost of restore: about 0.09 s extra over the pure cost of the tokens, plus about 180 MiB RAM per pawn. The warm turn costs 0.26 s because 154 new tokens (the appended turn plus the re-sent voice reminder and request) prefill at only about 600 t/s in a small batch.
- **`--swa-full`**: better reuse (more tokens kept, and the same-scene restore needs just 1 token: TTFT 0.06 s), and with cache-ram off it still keeps block A across pawns (cache_n 838 gives 0.80 s cold TTFT instead of 1.6 s). But KV at 16k per slot is 2970 MiB against about 400 for the same context without it, and it does not fit at 65536 per slot at all. Not an option in this budget.
- Cold lease costs 1.2 s to first token, 1.5 s to a full line on this shape. That is inside the 5 s urgent expiry, but it is a visible pause; pre-leasing a window when a scene is predictable would hide it.

**Recommendation:** run with `--cache-ram` at 8192 MiB or more; make it a requirement, not an option. No `--swa-full`. Drop the slot-affinity rule and lease the first free slot. Shorten block G and the turn format, since every warm turn pays about 150 tokens of prefill.

## 2. Speculative decoding on short lines

Method: 58 varied prompts (scene x focal or present pawn, a third bare and the rest with a prior exchange; a prompt is never repeated within a run), slots alternating, temperature 0.8, two full runs per setting (second one marked r2). Lines average 19 tokens. The build's flags: `--spec-type {none, draft-simple, ngram-simple, ngram-map-k, ngram-map-k4v, ngram-mod, ngram-cache, ...}`, `-md`, `--spec-draft-n-max`. E4B loaded as a draft without complaint, so the vocabulary is compatible. Pooled tok/s = total predicted tokens / total predicted ms.

| setting | shape | runs x n | median tok/s | pooled tok/s | drafted / accepted | median line s |
|---|---|---|---|---|---|---|
| none | target | 2 x 58 | 82.3 / 82.2 | 87.1 / 86.4 | 0 | 0.664 / 0.661 |
| ngram-simple | target | 2 x 58 | 82.7 / 82.3 | 87.2 / 87.0 | 0 / 0 | 0.679 / 0.660 |
| ngram-map-k | target | 2 x 58 | 82.3 / 82.4 | 86.6 / 86.7 | 0 / 0 | 0.673 / 0.653 |
| ngram-map-k4v | target | 2 x 58 | 82.2 / 82.0 | 86.8 / 86.8 | 0 / 0 | 0.679 / 0.665 |
| ngram-mod | target | 2 x 58 | 82.0 / 82.2 | 86.6 / 86.5 | 0 / 0 | 0.677 / 0.657 |
| none | `-c 49152`, n 14 | 2 x 58 | 47.9 / 48.0 | 50.5 / 50.9 | 0 | 1.161 / 1.114 |
| E4B Q4_K_M draft, n_max 4, on GPU | `-c 49152`, n 14 | 2 x 58 | 23.3 / 22.2 | 24.6 / 23.8 | 2080 / 508 and 2136 / 485 (24% / 23%) | 1.710 / 1.600 |

- The four ngram types never proposed a single draft (no draft-acceptance line in any of their server logs; speed is identical to none). Fresh 15-40 token lines have no repeated n-grams for them to find, and the prompt's own text is not repeated in a way the lookups match.
- The E4B draft runs at 24% acceptance (mean accepted length 1.7-2.3 of 4 drafted) and is **2x slower** than no draft (23 vs 48 t/s), on a card where it also costs about 5 GB of VRAM, which forced the smaller shape (n 14). The 24% acceptance is low for a same-family draft because sampling at temperature 0.8 and voice-card style text are hard to predict.
- Not tested: Gemma has no MTP head in this GGUF (`draft-mtp` not applicable); greedy decoding would raise acceptance but is not what we want for voices.

**Recommendation:** no speculative decoding. Leave `--spec-type` off. Remove it from the milestone's open questions.

## 3. Three slots at once

Method: narrator on slot 0 (about 3000-token prompt, chronicle of about 150 tokens; it stops by itself near 150, short of the 200 cap), pawns on slots 1-2. "conv" is a 6-turn conversation (turn t is spoken by A on slot 1 or B on slot 2, each appending the other's line), so only one pawn window generates at a time. "indep" is two pawns each doing 6 turns on their own slot, so both generate at once. 5 reps (narrator n=5, conv turns n=30, indep turns n=60 for two streams). Medians.

Target shape (n 6):

| stream, condition | n | tok/s | line s (median / p90) | TTFT s |
|---|---|---|---|---|
| narrator alone | 5 | 84.0 | 2.32 / 2.38 | 0.53 |
| pawn conversation alone | 30 | 81.8 | 0.63 / 1.41 | 0.36 |
| one pawn stream alone | 30 | 82.6 | 0.46 / 0.71 | 0.26 |
| narrator while a conversation runs | 5 | 30.4 | 5.42 / 5.44 | 0.31 |
| conversation turns while narrator runs | 30 | 47.2 | 0.83 / 1.01 | 0.42 |
| narrator with two simultaneous pawns | 5 | 23.3 | 7.02 / 7.21 | 0.67 |
| two simultaneous pawns, narrator running | 60 | 29.1 | 0.94 / 1.36 | 0.35 |
| two simultaneous pawns, no narrator | 60 | 34.0 | 0.81 / 1.07 | 0.32 |

Same test at n 11 (the 3 GB cap shape): narrator alone 59.5 t/s, 3.13 s; conversation alone 0.86 s; narrator with a conversation 20.2 t/s, 7.50 s, its conversation turns 1.19 s; narrator with two pawns 16.5 t/s, 10.1 s, pawns 18.1 t/s, 1.37 s (full table in `results/summary.md`).

Reading it:
- Concurrency is not free. Total decode throughput stays at about 82 t/s whether one or three streams run (23.3 + 29.1 x 2 = 81.5), so streams just split it. A pawn line takes 0.8-0.9 s instead of 0.5-0.6 s, and a chronicle entry takes 5-7 s instead of 2.3 s.
- Pawn lines still land inside a second. The pain is the narrator: while pawns talk it runs at 23-30 t/s.
- A real pair conversation only has one pawn generating at a time, so "narrator + conversation" (pawn 0.83 s per turn, narrator 5.4 s) is the common case; "narrator + two independent windows" is the worst case.

**Recommendation:** the narrator chronicle and diary work are idle-time jobs, as SPEC section 7 already says. Do not start one while a window is generating if lines are on screen soon; cap chronicle length (about 120 words costs 5-10 s when shared). Keep 3 slots; they cost no extra throughput, they just divide it.

## 4. Voice distinctness

Method: 6 pawns x 26 focal-neutral scenes (`{F}`/`{O}` placeholders filled per pawn; each pawn's two top memories are in block D) x 3 conditions = 468 generated lines (1 sample per cell, temperature 0.8, slot 1-2). Classifier: TF-IDF char_wb 2-5 grams + logistic regression, 5-fold stratified CV repeated with 4 seeds (20 fits), names of all pawns masked to `<N>` first so the classifier cannot read the answer from a name. Chance is 0.167. Style A = SPEC voice card (register, tics, 3-6 example lines) in block B plus the voice reminder in block G. Style B = plain persona paragraph only. A_dry = style A with DRY multiplier 0.8, base 1.75, allowed length 2, penalty window 512.

| condition | model | n lines | classifier accuracy (mean +- sd over 20 folds) | mean words | over 35 words | lines hitting banned list | share a 3-word run with own voice card | lines starting with the pawn's commonest 2-word opener |
|---|---|---|---|---|---|---|---|---|
| A voice card | Q3_K_XL | 156 | **0.926 +- 0.031** | 13.5 | 0 | 1 (0.6%) | 38 (24%) | 73 (47%) |
| B persona only | Q3_K_XL | 156 | **0.676 +- 0.061** | 14.8 | 0 | 1 (0.6%) | 1 (1%) | 23 (15%) |
| A + DRY 0.8 | Q3_K_XL | 156 | 0.939 +- 0.038 | 13.3 | 0 | 0 | 42 (27%) | not computed |
| A | IQ4_XS | 156 | 0.947 +- 0.036 | 12.6 | 0 | 0 | 37 | |
| B | IQ4_XS | 156 | 0.705 +- 0.084 | 13.9 | 0 | 0 | 1 | |
| A + DRY | IQ4_XS | 156 | 0.952 +- 0.026 | 12.7 | 0 | 0 | 34 | |

Slop: the banned list has about 30 entries, including the nine in the brief. Only 2 hits in 468 lines, both borderline ("sense of decorum", "sense of timing"). Caveat: block A itself contains a "never write lines like these" list with four of the banned phrases, so the near-zero rate is partly bought by that list; I did not run a version without it.

Sample lines (random, Q3_K_XL):

| pawn | A voice card | B persona only |
|---|---|---|
| Rook (cook) | "Hm. Moth, get back here. You'll freeze solid like a meat slab out there." / "Hm. Rain's just water, Pip. Don't worry about the dirt." / "Hm. Once the power's sorted, maybe I won't have to cook in the dark." | "Moth, get back here before the frost bites your skin off." / "Rain doesn't care about your crops, kid. It just keeps falling." / "If it's eighty percent done, it's done. Now get out of my way, I have meals to prep." |
| Pip | "Okay so Ash is like, totally not moving and there's so much blood in the grass..." / "Okay so this jelly is like, totally gross, and Tuk is being so weird about the kibble..." / "Okay so we are totally going to be more than that, like, we just need to keep going..." | "Oh no, Ash, please get up, please don't be dead, Ash, please tell me you're okay!" / "It's okay Tuk, we'll get real meals soon, I promise, just eat the jelly so you don't starve!" / "Oh come on Tuk, we're doing great and maybe we'll get some cool stuff soon, I just know it!" |
| Ash | "Forty tiles. One muffalo. Stay back, Lux." / "Negative. The freezer door is not my concern. Get back to work." / "Negative. I do not predict the weather." | "Move the muffalo toward the north wall. Do not let it wander into the brush." / "The meat is gone. Stop talking about it and check your perimeter." / "I do not know. The sky does not give reports." |
| Moth | "I think, well, the infection is spreading, and we probably only have two herbal packets left." / "I think, well, maybe we are more than that, if the research... if the data holds up, mostly." / "I think, well, Tuk's bleeding quite a lot, so maybe I should... I should help?" | "I'm sorry, Tuk, I think the infection is spreading, and we only have two herbal medicines left." / "I think... I don't know, Rook, but the research data is actually progressing quite well." / "Tuk, please, oh no, stay still, I think I need to get the medkit from the storage." |
| Tuk | "Stay quiet, Pip. Big muffalo is near. Tuk will take it." / "Run, soft one. Tuk will meet them." / "Yes. Tuk will study wires after this. Drink your bean water." | "Stay low, Pip. The muffalo is fat and will be good meat for us." / "Lux, stop running and get a gun. There is meat coming through the fog." / "That is what comes after. I will work on it once I have more meat." |
| Lux | "Of course, my dear Ash, wandering into the frost without a coat is a remarkably dramatic way to commit suicide." / "Of course, my dear Rook, your lack of composure is as tiresome as it is destructive." / "Of course you expect me to perform for a common brigand, my dear Rook, while I am covered in pigment." | "Do try not to freeze to death out there, Ash; it would be such a tedious inconvenience for us all." / "Must you behave like a common animal, Rook? One would think even you could manage a modicum of dignity." / "Naturally, one would expect you to handle the vulgarity of interrogation, Rook, while I maintain some semblance of dignity." |

Reading it:
- The voice card clearly helps: 93% against 68% (chance 17%), a gap of about 25 points that is four standard deviations of the CV noise. The persona paragraph alone already gives recognisable voices (68%), so B is not useless.
- **But part of A's distinctness is tic stamping.** A lines open with the pawn's signature opener 47% of the time (B: 15%) and a quarter reuse a 3-word run from their own card, so Pip says "Okay so ... like, totally" in nearly every line and Lux "Of course, my dear X". A classifier loves that; a player would find it a caricature after a dozen lines. Distinctness scores need pairing with a repetition score (the two columns above) or they reward the wrong thing.
- DRY at 0.8 did not help (0.939 vs 0.926 is inside the noise; copying from the card did not fall). It is not worth turning on.
- Lines are short and on spec (13-15 words, none over 35, no empties, no duplicates). Quantisation made no real difference (IQ4_XS 0.947 / 0.705).
- Limits: one sample per cell; focal-neutral scenes make lines of different pawns comparable but the 26 scenes are few; the classifier cannot separate "distinct" from "tic-stamped".

**Recommendation:** keep the voice card, but cut it to 3 examples, tell the model tics appear in at most one line in three (or rotate which example lines are shown), and keep the persona-style `register` line. Track opener repetition as a second metric in milestone 2. Do not enable DRY.

## 5. Grim content

Method: 20 grim scenes (organ harvest, butchery, a cannibal meal, an eaten-alone meal after a death, an execution, slave suppression and rebellion, a child's death and funeral, raiders bleeding out, a corpse burning, an amputation without anaesthetic, a slave sale...), 3 samples each at temperature 0.8, so 60 lines per model. Scoring: regex for refusals, disclaimers, moralising and softening (`q5_score.py`), then my own read of every line. Qwen ran as an abliterated 27B, thinking off (hand-built ChatML with the empty think block; the chat endpoint let the model think). Shapes: Gemma at the target shape; Qwen `-np 1 -c 8192`, KV q4_0, 13097 MiB.

| model / prompt | lines | refusals | disclaimers | moralising | softening (regex) | my read |
|---|---|---|---|---|---|---|
| stock Gemma 26B Q3_K_XL | 60 | 0 | 0 | 0 | 0 | no refusal, no moralising, no softening |
| stock Gemma, block A's grim-licence sentence removed | 60 | 0 | 0 | 0 | 3 | the 3 flags are Pip comforting in character and one grim-irony line from Lux; no refusal |
| abliterated Qwen 27B Q3_K_XL | 60 | 1 | 0 | 0 | 1 | both flags false positives ("I can't just stand here", "Sorry, Pip" in character); no refusal |

My read of the lines: both models answer like the colonists in the fixtures (Tuk calls a dying raider "good meat", Ash says "Negative. Leave him.", Moth says the incision "is clean. Probably."). I could not tell the two apart on grimness. The repeated "Okay so... like, totally" (Pip) and "Of course, my dear" (Lux) openers show up here too and sometimes make a murder scene read oddly cheerful, which is a voice-card effect, not a safety effect.

Reading it:
- On this scene set the stock Gemma did not refuse or moralise even with the licence sentence removed, so the SPEC section 12.7 premise ("a stock model tends to refuse or soften exactly the lines RimWorld calls for") is not shown for Gemma 26B at this prompt. The baseline for the abliterated Gemma is therefore "0 of 60", and the test as built cannot show an improvement.
- Why the test may be too easy: one-line in-character output, scenes stated as plain facts of the game, a system prompt that frames the world, short cap. A harder probe set (the player asking a pawn to describe an act in detail, out-of-world framing, no system licence, multi-sentence output) would be needed to separate the models. Not run.

**Recommendation:** keep the sanitizer rule from section 12.7 (drop a refusal or disclaimer) since the cost is nothing, but do not make abliteration a milestone-blocking requirement on the evidence of this test. Keep the licence sentence in block A. When the abliterated Gemma exists, rerun `q5_grim.py` and compare with `results/q5_scored.txt`.

## 6. Intent pass

Method: after a line, in the same warm slot, the model turn is closed and a second user turn asks for one label from the nine intents, with a one-line gloss for each (about 119 tokens of new prompt); the answer is 1-3 tokens. Three variants: GBNF grammar (`root ::= "none" | "kind" | ...`), JSON-schema `{"enum": [...]}`, and free text (stop at newline). Latency: 30 scenes, a fresh line each time so the intent prompt is never already cached. Accuracy: 30 lines with labels written by the fixture helper (none 5, kind 4, others 3; I read the three misses, not all 30 labels), temperature 0, slot primed with the pawn's prefix first.

| variant | model | n | intent call total s | intent TTFT s | new prompt tokens | accuracy on 30 labelled |
|---|---|---|---|---|---|---|
| grammar | Q3_K_XL | 30 | 0.285 (p90 0.30) | 0.26 | 119 | 27 / 30 |
| JSON-schema enum | Q3_K_XL | 30 | 0.334 (p90 0.38) | 0.29 | 119 | 27 / 30 |
| free text | Q3_K_XL | 30 | 0.285 | 0.26 | 119 | 5 of 30 outputs were not a valid label ("none (nothing social") |
| grammar | IQ4_XS (fully on GPU) | 30 | 0.16 | 0.14 | 119 | 28 / 30 |
| JSON-schema enum | IQ4_XS | 30 | 0.20 | 0.16 | 119 | 27 / 30 |

- Added latency is 0.29 s per line on the target shape (a line takes about 0.45-0.66 s), so it is +45-60%. Almost all of it is prefilling the 119 uncached new tokens; the label itself is 1-3 tokens.
- Misses (same on both constrained variants): comfort read as slight or none, slight read as insult, confide read as none ("I filled a mass grave once. I do not talk about it" is a hard one). Those are neighbours, and the mod's effects table makes `slight` vs `insult` the cheapest mistake. 27/30 = 90% with n=30 is a wide interval (roughly 74-98%).
- Grammar and schema are equally accurate; grammar is slightly faster. Free text sometimes copies the gloss back, so constrained decoding is needed.

**Recommendation:** use the GBNF grammar. Move the label gloss into block A (cached) and send only a one-line "label your last line" request, which should cut the new tokens from 119 to about 25 and the added time to about 0.1 s; not tested here. Treat `none` as the answer for anything unparseable.

## Changes to SPEC.md this suggests

1. Section 4, cache rules: replace "relies on the slot still holding the prefix, or on the server's own RAM prompt cache" with a requirement: the server must run with `--cache-ram` (8192 MiB or more, about 180 MiB per cached pawn lease). On Gemma's sliding-window cache, a slot never reuses a displaced prefix by itself (cache_n 0 even in the pawn's own old slot).
2. Section 4, leasing rules: delete "a pawn gets back the slot it used last". Measured: old slot and other slot differ by 0.01 s. Lease the first free slot.
3. Section 4: record the measured shape and numbers (cold lease about 1.2 s to first token and 1.5 s to a full line; warm turn 0.26 / 0.45 s; re-lease with a new scene 0.44 / 0.66 s; same scene 0.19 / 0.36 s) and set the pawn prefix budget to about 1,700-1,850 tokens measured (A about 840).
4. Section 4, block G and turn format: each warm turn prefills about 150 tokens at about 600 t/s. Shorten the voice reminder and request, and keep the voice reminder to one line, to cut warm-turn latency.
5. Section 4: server must not use `--swa-full` (2970 MiB KV at 16k per slot, does not fit at 65536). State the context per slot as 65536 at q4_0 (1333 MiB KV for three slots).
6. Section 4 "Two passes per line": keep the intent pass, grammar-constrained; move the label gloss into block A; measured +0.29 s with the gloss in the suffix, 27/30 accurate.
7. Remove speculative decoding from any plan: ngram types never fire on 15-40 token lines, the E4B draft is 24% accepted and 2x slower.
8. Section 7, narrator and pacing: the three slots share one decode throughput (about 82 t/s on the offloaded shape). Chronicle and diary work must wait for idle windows; while a conversation runs the narrator is 2.3x slower (5.4 s vs 2.3 s for 150 tokens) and pawn turns 30% slower; with two windows generating at once, pawn lines take about 0.94 s.
9. Section 5, voice card: use 3 example lines instead of 3-6, add "use a tic in at most one line in three", and add opener repetition to the voice metric next to the classifier. The card lifts distinctness from 0.68 to 0.93 but stamps openers (47% of lines).
10. Section 12.7: stock Gemma 26B Q3_K_XL gave 0 refusals, disclaimers or moralising in 60 grim lines, with or without the licence sentence, and so did the abliterated Qwen. Reword "a stock model tends to refuse" as untested at this scene set, keep the sanitizer rule, and add a harder probe set before using this as a reason to ship an abliterated model.
11. Section 12.7: the reference model is now Gemma 4 26B-A4B UD-Q3_K_XL (12.9 GB). Add a VRAM note for the engine spec: with the 1.5 GB RimWorld allowance it needs `--n-cpu-moe 6` (13110 MiB, decode 77 t/s, cold prefill 1.7k t/s); with 3 GB, `--n-cpu-moe 11` (11496 MiB, decode about 55 t/s, cold prefill 1.0k t/s). Margins are 121 and 199 MiB; the engine spec should leave more headroom or lower `-ub` (untested).
12. Section 11, milestone 0 done-when: all five items now have numbers (cold/warm lease, prefix reuse, speculative decoding, three slots, voice score); add the refusal check result (item 10) and the VRAM table.
