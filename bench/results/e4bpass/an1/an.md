# Soak analysis

## Checks

| check | s1x-off | s3x-off | s4x-off | s2x-on | ALL |
|---|---|---|---|---|---|
| age gate violations (must be 0) | 0 | 0 | 0 | 0 | 0 |
| age gate review (benign romance words) | 3 | 1 | 0 | 0 | 4 |
| explicit text hits | 0 | 0 | 0 | 0 | 0 |
| explicit text soft hits | 0 | 0 | 0 | 0 | 0 |
| explicit text while adult off | 0 | 0 | 0 | 0 | 0 |
| canary in shown output | 0 | 0 | 0 | 0 | 0 |
| canary in raw call text | 0 | 0 | 0 | 0 | 0 |
| line ends with canary | 0 | 0 | 0 | 0 | 0 |
| empty shown text | 0 | 0 | 0 | 0 | 0 |
| refusal phrases | 0 | 0 | 0 | 0 | 0 |
| refusal phrases (weak, review) | 1 | 1 | 0 | 0 | 2 |
| invented-name candidates (review) | 1 | 3 | 0 | 4 | 8 |
| label / format leaks | 0 | 0 | 0 | 0 | 0 |
| exact repeats | 6 | 1 | 0 | 1 | 8 |
| near duplicates | 1 | 1 | 0 | 0 | 2 |
| conversation turn repeats | 0 | 0 | 0 | 0 | 0 |
| repeated aims/clouds in mind logs | 3 | 1 | 0 | 0 | 4 |
| over-length | 0 | 0 | 0 | 0 | 0 |
| drops | 15 | 18 | 3 | 4 | 40 |
| error/fallback call share | 0 | 0 | 0 | 0 | 0 |

## Run s1x-off

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | expired | 2 |
| - | minor_safety | 1 |
| - | reflect_failed | 2 |
| Patience | unknown_name | 1 |
| reflect | idle_preempted | 5 |
| unknown_name:Additionally | chronicle | 1 |
| unknown_name:Earlier | chronicle | 1 |
| unknown_name:Furthermore | chronicle | 1 |
| unknown_name:Odo | diary | 1 |

Non-zero module counters: art.dropped=12, bystand.asked=14, bystand.shown=9, bystand.templated=9, group.ends.dropped=1, group.ends.turn_cap=4, group.lines=17, group.rotations=8, grudges.escalated=1, grudges.seeks=5, grudges.started=4, lease_log_counts.drop:ambient_quota=1, lease_log_counts.drop:gap=8, lease_log_counts.expired:bystander:3=2, lease_log_counts.expired:bystander:4=1, lease_log_counts.expired:bystander:7=1, lease_log_counts.expired:concern_resolved:4=1, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:injury:6=1, lease_log_counts.expired:injury:7=2, lease_log_counts.expired:level_up:5=2, lease_log_counts.expired:social:2=1, lease_log_counts.expired:social:4=1, lease_log_counts.expired:social:5=1, lease_log_counts.expired:social:6=4, lease_log_counts.expired:social:7=3, lease_log_counts.expired:social:8=4, lease_log_counts.expired:social:9=1, lease_log_counts.expired:tend:4=1, lease_log_counts.expired:vanilla_social:1=2, lease_log_counts.lease:slot1:actor10:IDLE=25, lease_log_counts.lease:slot1:actor10:SOCIAL=4, lease_log_counts.lease:slot1:actor11:IDLE=11, lease_log_counts.lease:slot1:actor1:IDLE=34, lease_log_counts.lease:slot1:actor1:REACTIVE=1, lease_log_counts.lease:slot1:actor1:SOCIAL=6, lease_log_counts.lease:slot1:actor1:URGENT=2, lease_log_counts.lease:slot1:actor2:IDLE=31, lease_log_counts.lease:slot1:actor2:SOCIAL=2, lease_log_counts.lease:slot1:actor3:IDLE=33, lease_log_counts.lease:slot1:actor3:REACTIVE=6, lease_log_counts.lease:slot1:actor4:IDLE=22, lease_log_counts.lease:slot1:actor4:REACTIVE=4, lease_log_counts.lease:slot1:actor5:IDLE=32, lease_log_counts.lease:slot1:actor5:PLAYER=2, lease_log_counts.lease:slot1:actor5:REACTIVE=1, lease_log_counts.lease:slot1:actor5:SOCIAL=5, lease_log_counts.lease:slot1:actor6:IDLE=36, lease_log_counts.lease:slot1:actor6:PLAYER=2, lease_log_counts.lease:slot1:actor7:IDLE=25, lease_log_counts.lease:slot1:actor8:IDLE=17, lease_log_counts.lease:slot1:actor8:PLAYER=2, lease_log_counts.lease:slot1:actor8:REACTIVE=2, lease_log_counts.lease:slot1:actor9:IDLE=29, lease_log_counts.lease:slot2:actor1:PLAYER=2, lease_log_counts.lease:slot2:actor1:REACTIVE=1, lease_log_counts.lease:slot2:actor1:SOCIAL=2, lease_log_counts.lease:slot2:actor2:SOCIAL=7, lease_log_counts.lease:slot2:actor2:URGENT=2, lease_log_counts.lease:slot2:actor3:REACTIVE=1, lease_log_counts.lease:slot2:actor3:SOCIAL=3, lease_log_counts.lease:slot2:actor4:REACTIVE=1, lease_log_counts.lease:slot2:actor4:SOCIAL=1, lease_log_counts.lease:slot2:actor5:REACTIVE=2, lease_log_counts.lease:slot2:actor6:SOCIAL=1, lease_log_counts.lease:slot2:actor7:PLAYER=2, lease_log_counts.lease:slot2:actor7:SOCIAL=1, lease_log_counts.lease:slot2:actor9:PLAYER=2, lease_log_counts.lease:slot2:actor9:REACTIVE=2, lease_log_counts.lease:slot2:actor9:SOCIAL=2, lease_log_counts.merged:concern_resolved=1, lease_log_counts.merged:first_cue=1, lease_log_counts.release:slot1:conversation_end=8, lease_log_counts.release:slot1:done=19, lease_log_counts.release:slot1:gathering_end=5, lease_log_counts.release:slot1:idle_done=290, lease_log_counts.release:slot1:preempted=5, lease_log_counts.release:slot1:rotated=6, lease_log_counts.release:slot2:conversation_end=8, lease_log_counts.release:slot2:done=16, lease_log_counts.release:slot2:gathering_end=5, lease_log_counts.release:slot2:rotated=2, loop.checked=36, wants.acted=2, wants.nothing=211, wants.runs=213

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 12 | 0.754 | 0.82 | 1.03 |
| chronicle | 6 | 1.56 | 1.83 | 1.91 |
| diary | 11 | 1.14 | 1.45 | 1.61 |
| dream | 6 | 0.437 | 0.502 | 0.531 |
| intent | 54 | 0.502 | 0.664 | 1.31 |
| line | 73 | 1.34 | 2.18 | 2.51 |
| lore | 2 | 0.277 | 0.35 | 0.368 |
| memory | 12 | 0.419 | 0.478 | 0.49 |
| narrator | 4 | 1.01 | 2.08 | 2.29 |
| recap | 1 | 0.735 | 0.735 | 0.735 |
| reflect | 30 | 6.29 | 8.76 | 10.4 |
| relation | 12 | 0.338 | 0.393 | 0.51 |
| thought | 30 | 0.396 | 0.453 | 0.578 |
| ALL | 253 | 0.666 | 4.92 | 10.4 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 12 | 266 | 3172 | 306 | 3367 | 31.5 | 371 | 0.515 | 1599 | 3296 |
| chronicle | 6 | 433 | 2848 | 195 | 1198 | 71 | 417 | 0.296 | 1990 | 2827 |
| diary | 11 | 595 | 5943 | 177 | 2317 | 44 | 483 | 0.281 | 2031 | 2823 |
| dream | 6 | 406 | 2696 | 178 | 889 | 11 | 64 | 0.248 | 1916 | 2548 |
| intent | 54 | 96 | 5184 | 2068 | 112011 | 14 | 798 | 0.956 | 637 | 14395 |
| line | 73 | 2012 | 111962 | 0 | 40173 | 15 | 1085 | 0.264 | 1511 | 2053 |
| lore | 2 | 296 | 592 | 92.5 | 185 | 5.5 | 11 | 0.238 | 1743 | 2287 |
| memory | 12 | 44.5 | 577 | 2055 | 24281 | 17 | 198 | 0.977 | 686 | 29562 |
| narrator | 4 | 27 | 88 | 34 | 158 | 12.5 | 53 | 0.642 | 32.1 | 89.8 |
| recap | 1 | 410 | 410 | 1 | 1 | 26 | 26 | 0.002 | 1808 | 1812 |
| reflect | 30 | 874 | 25236 | 206 | 11639 | 294 | 9280 | 0.316 | 2163 | 3161 |
| relation | 12 | 16 | 192 | 2080 | 24642 | 14 | 175 | 0.992 | 316 | 40918 |
| thought | 30 | 411 | 12371 | 177 | 5794 | 9 | 278 | 0.319 | 1889 | 2773 |
| ALL | 253 | 409 | 171271 | 255 | 226655 | 15 | 13239 | 0.57 | 1516 | 3523 |

Prompt tokens per lease/slot: 0: 7110 over 25 calls (284.4/call), 1: 112081 over 163 calls (687.6/call), 2: 52080 over 65 calls (801.2/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 16 | 206 | 4.28 | 12 | 0.147 | 0.626 | 0.844 |
| Ana |  | aim | 3 | 29 | 3.94 | 9.67 | 0.1 | 0.759 | - |
| Ana |  | thought | 14 | 95 | 3.67 | 6.57 | 0.117 | 0.495 | 0.597 |
| Ana |  | diary | 1 | 50 | 4.52 | 12.5 | 0.2 | 0.86 | 0.86 |
| Ana |  | dream | 2 | 18 | 4.11 | 9 | 0.111 | 0.444 | - |
| Bram |  | spoken | 8 | 85 | 4.15 | 5.31 | 0.324 | 0.729 | 0.793 |
| Bram |  | aim | 4 | 47 | 3.7 | 11.8 | 0.222 | 0.489 | - |
| Bram |  | thought | 9 | 76 | 5.24 | 8.44 | 0.394 | 0.487 | 0.544 |
| Bram |  | diary | 1 | 41 | 4.56 | 10.2 | 0.293 | 0.902 | - |
| Bram |  | dream | 2 | 16 | 5 | 8 | 0.375 | 0.5 | - |
| Cy |  | thought | 11 | 63 | 3.9 | 5.46 | 0.142 | 0.508 | 0.59 |
| Cy |  | spoken | 10 | 126 | 3.99 | 9.4 | 0.034 | 0.563 | 0.797 |
| Cy |  | aim | 4 | 37 | 4.08 | 9.25 | 0 | 0.459 | - |
| Cy |  | diary | 1 | 38 | 4.24 | 9.5 | 0.237 | 0.868 | - |
| Cy |  | dream | 2 | 14 | 4.57 | 7 | 0.571 | 0.5 | - |
| Dee |  | thought | 9 | 55 | 4.06 | 6.11 | 0.126 | 0.545 | 0.6 |
| Dee |  | spoken | 6 | 45 | 4.63 | 3.5 | 0.148 | 0.8 | - |
| Dee |  | aim | 2 | 17 | 3.63 | 8.5 | 0.056 | 0.765 | - |
| Dee |  | diary | 1 | 32 | 4.28 | 8 | 0.156 | 0.906 | - |
| Kit | A-smart | spoken | 8 | 93 | 4.27 | 10.3 | 0.158 | 0.699 | 0.814 |
| Kit | A-smart | thought | 14 | 91 | 3.98 | 6.07 | 0.195 | 0.505 | 0.587 |
| Kit | A-smart | aim | 3 | 30 | 4.24 | 10 | 0.131 | 0.567 | - |
| Kit | A-smart | diary | 1 | 49 | 4.1 | 12.2 | 0.245 | 0.816 | - |
| Kit | A-smart | dream | 2 | 16 | 4.5 | 8 | 0.25 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 27 | 3.71 | 7.17 | 0.186 | 0.852 | - |
| Lou | A-dull | thought | 7 | 46 | 4.26 | 6.57 | 0 | 0.5 | - |
| Lou | A-dull | aim | 1 | 10 | 3 | 10 | 0 | 1 | - |
| Lou | A-dull | diary | 1 | 26 | 4.31 | 6.5 | 0.231 | 0.846 | - |
| Max | B-sober | spoken | 3 | 22 | 4.47 | 6 | 0.286 | 0.955 | - |
| Max | B-sober | aim | 2 | 24 | 3.27 | 12 | 0 | 0.667 | - |
| Max | B-sober | thought | 6 | 46 | 4.97 | 7.67 | 0.225 | 0.478 | - |
| Max | B-sober | diary | 1 | 40 | 4.35 | 10 | 0.225 | 0.9 | - |
| Noa | B-drunk | spoken | 2 | 13 | 3.69 | 5.5 | 0.167 | 1 | - |
| Noa | B-drunk | aim | 2 | 21 | 3.38 | 10.5 | 0.05 | 0.714 | - |
| Noa | B-drunk | thought | 6 | 42 | 4.37 | 7 | 0.361 | 0.405 | - |
| Noa | B-drunk | diary | 1 | 20 | 4.15 | 5 | 0.2 | 0.9 | - |
| Noa | B-drunk | dream | 2 | 14 | 4.86 | 7 | 0.714 | 0.5 | - |
| Tam |  | spoken | 6 | 54 | 3.92 | 8.42 | 0.114 | 0.741 | 0.744 |
| Tam |  | aim | 2 | 20 | 3.33 | 10 | 0.101 | 0.6 | - |
| Tam |  | thought | 7 | 51 | 4.02 | 7.29 | 0.278 | 0.529 | 0.53 |
| Tam |  | diary | 1 | 32 | 4.03 | 8 | 0.188 | 0.844 | - |
| Uma |  | spoken | 5 | 55 | 4.22 | 5.5 | 0.133 | 0.764 | 0.797 |
| Uma |  | aim | 2 | 20 | 3.14 | 10 | 0.045 | 0.8 | - |
| Uma |  | thought | 6 | 38 | 5.91 | 6.33 | 0.357 | 0.447 | - |
| Uma |  | diary | 1 | 38 | 4.82 | 12.7 | 0.368 | 0.842 | - |
| Uma |  | dream | 2 | 14 | 4.86 | 7 | 0.286 | 0.5 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 8 | 3 | 4.27 | 3.71 | 1.42 |
| smart_vs_dull / spoken / words_per_sentence | 8 | 3 | 10.3 | 7.17 | 0.96 |
| smart_vs_dull / spoken / rare_share | 8 | 3 | 0.158 | 0.186 | -0.236 |
| smart_vs_dull / thought / word_len | 14 | 7 | 3.98 | 4.26 | -0.364 |
| smart_vs_dull / thought / words_per_sentence | 14 | 7 | 6.07 | 6.57 | -0.164 |
| smart_vs_dull / thought / rare_share | 14 | 7 | 0.195 | 0 | 1.17 |
| smart_vs_dull / diary / word_len | 1 | 1 | 4.1 | 4.31 | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 1 | 12.2 | 6.5 | - |
| smart_vs_dull / diary / rare_share | 1 | 1 | 0.245 | 0.231 | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 4.5 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 8 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.25 | - | - |
| smart_vs_dull / aim / word_len | 3 | 1 | 4.24 | 3 | - |
| smart_vs_dull / aim / words_per_sentence | 3 | 1 | 10 | 10 | - |
| smart_vs_dull / aim / rare_share | 3 | 1 | 0.131 | 0 | - |
| sober_after_vs_drunk_before / spoken / word_len | 1 | 1 | 3.89 | 3.5 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 1 | 1 | 9 | 2 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 1 | 1 | 0.333 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 2 | 4 | 3.67 | 4.71 | -1.47 |
| sober_after_vs_drunk_before / thought / words_per_sentence | 2 | 4 | 9 | 6 | 3 |
| sober_after_vs_drunk_before / thought / rare_share | 2 | 4 | 0.111 | 0.486 | -4.37 |
| sober_after_vs_drunk_before / diary / word_len | 0 | 1 | - | 4.15 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 0 | 1 | - | 5 | - |
| sober_after_vs_drunk_before / diary / rare_share | 0 | 1 | - | 0.2 | - |
| sober_after_vs_drunk_before / dream / word_len | 2 | 0 | 4.86 | - | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 0 | 0.714 | - | - |
| sober_after_vs_drunk_before / aim / word_len | 1 | 1 | 3.3 | 3.45 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 1 | 1 | 10 | 11 | - |
| sober_after_vs_drunk_before / aim / rare_share | 1 | 1 | 0.1 | 0 | - |

## Run s3x-off

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | preempted | 4 |
| late | dream | 3 |
| minor_safety | chronicle | 2 |
| reflect | idle_preempted | 6 |
| thought | idle_preempted | 1 |
| unknown_name:Party | diary | 1 |
| wants | idle_preempted | 1 |

Non-zero module counters: art.dropped=6, bystand.asked=6, group.ends.over=2, group.ends.turn_cap=3, group.lines=8, grudges.seeks=2, grudges.started=4, lease_log_counts.drop:gap=8, lease_log_counts.expired:bystander:3=2, lease_log_counts.expired:bystander:4=1, lease_log_counts.expired:bystander:5=1, lease_log_counts.expired:concern_resolved:1=1, lease_log_counts.expired:concern_resolved:2=1, lease_log_counts.expired:concern_resolved:4=2, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:first_cue:5=1, lease_log_counts.expired:first_cue:9=1, lease_log_counts.expired:gathering:1=1, lease_log_counts.expired:gathering:3=1, lease_log_counts.expired:injury:3=2, lease_log_counts.expired:injury:4=2, lease_log_counts.expired:injury:5=1, lease_log_counts.expired:injury:6=2, lease_log_counts.expired:injury:7=2, lease_log_counts.expired:injury:8=2, lease_log_counts.expired:raid_end:1=1, lease_log_counts.expired:social:1=3, lease_log_counts.expired:social:10=2, lease_log_counts.expired:social:2=3, lease_log_counts.expired:social:3=1, lease_log_counts.expired:social:4=2, lease_log_counts.expired:social:5=5, lease_log_counts.expired:social:6=2, lease_log_counts.expired:social:7=1, lease_log_counts.expired:social:8=4, lease_log_counts.expired:social:9=2, lease_log_counts.expired:tend:4=2, lease_log_counts.expired:thought:1=2, lease_log_counts.expired:vanilla_social:1=2, lease_log_counts.lease:slot1:actor10:IDLE=7, lease_log_counts.lease:slot1:actor11:IDLE=1, lease_log_counts.lease:slot1:actor1:IDLE=16, lease_log_counts.lease:slot1:actor1:REACTIVE=1, lease_log_counts.lease:slot1:actor1:SOCIAL=2, lease_log_counts.lease:slot1:actor1:URGENT=2, lease_log_counts.lease:slot1:actor2:IDLE=15, lease_log_counts.lease:slot1:actor2:SOCIAL=1, lease_log_counts.lease:slot1:actor3:IDLE=16, lease_log_counts.lease:slot1:actor3:REACTIVE=1, lease_log_counts.lease:slot1:actor4:IDLE=10, lease_log_counts.lease:slot1:actor4:REACTIVE=2, lease_log_counts.lease:slot1:actor5:IDLE=18, lease_log_counts.lease:slot1:actor5:PLAYER=1, lease_log_counts.lease:slot1:actor5:SOCIAL=2, lease_log_counts.lease:slot1:actor6:IDLE=11, lease_log_counts.lease:slot1:actor6:PLAYER=1, lease_log_counts.lease:slot1:actor7:IDLE=7, lease_log_counts.lease:slot1:actor7:PLAYER=1, lease_log_counts.lease:slot1:actor8:IDLE=7, lease_log_counts.lease:slot1:actor8:PLAYER=2, lease_log_counts.lease:slot1:actor9:IDLE=7, lease_log_counts.lease:slot1:actor9:PLAYER=1, lease_log_counts.lease:slot2:actor1:PLAYER=2, lease_log_counts.lease:slot2:actor1:SOCIAL=1, lease_log_counts.lease:slot2:actor2:REACTIVE=1, lease_log_counts.lease:slot2:actor2:SOCIAL=2, lease_log_counts.lease:slot2:actor2:URGENT=2, lease_log_counts.lease:slot2:actor3:REACTIVE=1, lease_log_counts.lease:slot2:actor5:PLAYER=1, lease_log_counts.lease:slot2:actor6:PLAYER=1, lease_log_counts.lease:slot2:actor6:SOCIAL=1, lease_log_counts.lease:slot2:actor7:PLAYER=1, lease_log_counts.lease:slot2:actor7:SOCIAL=1, lease_log_counts.lease:slot2:actor9:PLAYER=1, lease_log_counts.lease:slot2:actor9:REACTIVE=1, lease_log_counts.merged:concern_resolved=1, lease_log_counts.merged:first_cue=1, lease_log_counts.merged:vanilla_social=2, lease_log_counts.release:slot1:conversation_end=1, lease_log_counts.release:slot1:done=8, lease_log_counts.release:slot1:gathering_end=5, lease_log_counts.release:slot1:idle_done=105, lease_log_counts.release:slot1:preempted=12, lease_log_counts.release:slot2:conversation_end=1, lease_log_counts.release:slot2:done=8, lease_log_counts.release:slot2:gathering_end=4, lease_log_counts.release:slot2:preempted=3, loop.checked=13, wants.nothing=62, wants.runs=63

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 12 | 0.737 | 0.814 | 0.858 |
| chronicle | 4 | 1.69 | 1.87 | 1.94 |
| diary | 11 | 1.08 | 1.21 | 1.36 |
| dream | 6 | 0.42 | 0.486 | 0.492 |
| intent | 25 | 0.759 | 4.53 | 6.27 |
| line | 33 | 1.59 | 3.12 | 7.19 |
| lore | 2 | 0.365 | 0.389 | 0.395 |
| narrator | 5 | 0.482 | 0.971 | 1.27 |
| reflect | 17 | 5.48 | 8.47 | 9.29 |
| thought | 10 | 0.374 | 0.458 | 0.571 |
| ALL | 125 | 1.15 | 5.27 | 9.29 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 12 | 244 | 2926 | 306 | 3672 | 30.5 | 354 | 0.557 | 1565 | 3529 |
| chronicle | 4 | 658 | 2542 | 195 | 586 | 66 | 261 | 0.187 | 2001 | 2462 |
| diary | 11 | 467 | 5096 | 256 | 2663 | 43 | 461 | 0.343 | 2108 | 3209 |
| dream | 6 | 402 | 2404 | 178 | 1146 | 11 | 69 | 0.323 | 1921 | 2837 |
| intent | 25 | 96 | 20552 | 1962 | 32633 | 16 | 386 | 0.614 | 1603 | 4147 |
| line | 33 | 1957 | 48174 | 0 | 19020 | 16 | 517 | 0.283 | 1552 | 2164 |
| lore | 2 | 213 | 426 | 184 | 368 | 11.5 | 23 | 0.463 | 1482 | 2762 |
| narrator | 5 | 25 | 93 | 34 | 214 | 8 | 41 | 0.697 | 114 | 378 |
| reflect | 17 | 838 | 13500 | 206 | 6700 | 276 | 4871 | 0.332 | 2175 | 3255 |
| thought | 10 | 403 | 3670 | 177 | 2351 | 9 | 93 | 0.39 | 1882 | 3087 |
| ALL | 125 | 467 | 99383 | 206 | 69353 | 17 | 7076 | 0.411 | 1658 | 2815 |

Prompt tokens per lease/slot: 0: 5987 over 23 calls (260.3/call), 1: 56309 over 74 calls (760.9/call), 2: 37087 over 28 calls (1324.5/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 8 | 102 | 4.06 | 12.8 | 0.091 | 0.667 | 0.835 |
| Ana |  | aim | 4 | 43 | 3.74 | 10.8 | 0.044 | 0.651 | - |
| Ana |  | thought | 3 | 31 | 3.56 | 10.3 | 0.067 | 0.613 | - |
| Ana |  | diary | 2 | 87 | 3.9 | 12.6 | 0.16 | 0.667 | 0.727 |
| Ana |  | dream | 2 | 18 | 5.11 | 4.5 | 0.556 | 0.5 | - |
| Bram |  | spoken | 6 | 70 | 3.9 | 5.83 | 0.18 | 0.786 | 0.85 |
| Bram |  | aim | 4 | 44 | 3.54 | 11 | 0.069 | 0.455 | - |
| Bram |  | thought | 3 | 36 | 3.9 | 12 | 0.136 | 0.583 | - |
| Bram |  | diary | 1 | 34 | 4.24 | 8.5 | 0.235 | 0.824 | - |
| Bram |  | dream | 2 | 14 | 4.57 | 7 | 0.286 | 0.5 | - |
| Cy |  | thought | 2 | 12 | 3.67 | 3 | 0.167 | 0.5 | - |
| Cy |  | diary | 1 | 38 | 3.34 | 9.5 | 0.184 | 0.737 | - |
| Cy |  | spoken | 1 | 9 | 3.78 | 4.5 | 0.111 | 1 | - |
| Cy |  | dream | 2 | 16 | 4.5 | 4 | 0.375 | 0.5 | - |
| Dee |  | thought | 2 | 12 | 4 | 6 | 0.167 | 0.5 | - |
| Dee |  | diary | 1 | 31 | 5 | 7.75 | 0.226 | 0.903 | - |
| Dee |  | spoken | 1 | 11 | 5 | 5.5 | 0.182 | 0.727 | - |
| Kit | A-smart | spoken | 3 | 37 | 4.25 | 12.3 | 0.139 | 0.838 | - |
| Kit | A-smart | aim | 2 | 23 | 3.7 | 11.5 | 0.045 | 0.826 | - |
| Kit | A-smart | diary | 1 | 42 | 3.86 | 10.5 | 0.238 | 0.81 | - |
| Kit | A-smart | thought | 3 | 25 | 3.62 | 8.33 | 0.221 | 0.64 | - |
| Kit | A-smart | dream | 2 | 18 | 4.22 | 9 | 0.444 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 26 | 3.5 | 8.67 | 0.139 | 0.846 | - |
| Lou | A-dull | thought | 3 | 21 | 4.67 | 7 | 0.286 | 0.619 | - |
| Lou | A-dull | aim | 2 | 17 | 4.08 | 8.5 | 0.143 | 0.941 | - |
| Lou | A-dull | diary | 1 | 18 | 3.89 | 4.5 | 0.222 | 0.944 | - |
| Max | B-sober | spoken | 2 | 23 | 3.76 | 9 | 0.1 | 0.783 | - |
| Max | B-sober | aim | 1 | 11 | 3.46 | 11 | 0 | 1 | - |
| Max | B-sober | diary | 1 | 33 | 5.3 | 8.25 | 0.333 | 0.879 | - |
| Max | B-sober | thought | 2 | 8 | 4.75 | 4 | 0.25 | 0.5 | - |
| Noa | B-drunk | spoken | 2 | 14 | 3.83 | 3.5 | 0.188 | 0.929 | - |
| Noa | B-drunk | aim | 1 | 8 | 3.62 | 8 | 0 | 1 | - |
| Noa | B-drunk | thought | 3 | 18 | 4.28 | 6 | 0.133 | 0.722 | - |
| Noa | B-drunk | dream | 2 | 16 | 4.5 | 4 | 0.125 | 0.5 | - |
| Tam |  | spoken | 2 | 19 | 3.39 | 9.5 | 0.226 | 0.947 | - |
| Tam |  | aim | 2 | 22 | 3.5 | 11 | 0.045 | 0.773 | - |
| Tam |  | diary | 1 | 26 | 3.85 | 6.5 | 0.154 | 0.885 | - |
| Tam |  | thought | 2 | 10 | 3.8 | 5 | 0.2 | 0.5 | - |
| Uma |  | diary | 1 | 40 | 4.78 | 13.3 | 0.3 | 0.9 | - |
| Uma |  | thought | 2 | 12 | 5 | 6 | 0.5 | 0.5 | - |
| Uma |  | dream | 2 | 12 | 5.17 | 6 | 0.667 | 0.5 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 3 | 3 | 4.25 | 3.5 | 2.62 |
| smart_vs_dull / spoken / words_per_sentence | 3 | 3 | 12.3 | 8.67 | 1.17 |
| smart_vs_dull / spoken / rare_share | 3 | 3 | 0.139 | 0.139 | -0.003 |
| smart_vs_dull / thought / word_len | 3 | 3 | 3.62 | 4.67 | -7.85 |
| smart_vs_dull / thought / words_per_sentence | 3 | 3 | 8.33 | 7 | 0.816 |
| smart_vs_dull / thought / rare_share | 3 | 3 | 0.221 | 0.286 | -0.816 |
| smart_vs_dull / diary / word_len | 1 | 1 | 3.86 | 3.89 | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 1 | 10.5 | 4.5 | - |
| smart_vs_dull / diary / rare_share | 1 | 1 | 0.238 | 0.222 | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 4.22 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 9 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.444 | - | - |
| smart_vs_dull / aim / word_len | 2 | 2 | 3.7 | 4.08 | -0.49 |
| smart_vs_dull / aim / words_per_sentence | 2 | 2 | 11.5 | 8.5 | 1.9 |
| smart_vs_dull / aim / rare_share | 2 | 2 | 0.0455 | 0.143 | -0.65 |
| sober_after_vs_drunk_before / spoken / word_len | 1 | 1 | 4 | 3.67 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 1 | 1 | 4 | 3 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 1 | 1 | 0.375 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 1 | 2 | 3.62 | 4.6 | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 1 | 2 | 8 | 5 | - |
| sober_after_vs_drunk_before / thought / rare_share | 1 | 2 | 0 | 0.2 | - |
| sober_after_vs_drunk_before / dream / word_len | 0 | 2 | - | 4.5 | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 0 | 2 | - | 4 | - |
| sober_after_vs_drunk_before / dream / rare_share | 0 | 2 | - | 0.125 | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.62 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 8 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |

## Run s4x-off

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | preempted | 2 |
| reflect | idle_preempted | 1 |

Non-zero module counters: bystand.asked=2, bystand.shown=2, bystand.templated=2, dream_pending=6, grudges.started=2, lease_log_counts.drop:gap=1, lease_log_counts.drop:queue_full=2, lease_log_counts.evicted:level_up:5=1, lease_log_counts.evicted:social:2=1, lease_log_counts.evicted:vanilla_social:1=1, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:first_cue:3=1, lease_log_counts.expired:first_cue:5=1, lease_log_counts.expired:first_cue:9=1, lease_log_counts.expired:gathering:1=2, lease_log_counts.expired:gathering:3=1, lease_log_counts.expired:gathering:4=1, lease_log_counts.expired:gathering:5=1, lease_log_counts.expired:injury:3=1, lease_log_counts.expired:injury:5=1, lease_log_counts.expired:injury:6=1, lease_log_counts.expired:injury:7=1, lease_log_counts.expired:injury:8=1, lease_log_counts.expired:social:1=1, lease_log_counts.expired:social:10=1, lease_log_counts.expired:social:4=1, lease_log_counts.expired:social:5=3, lease_log_counts.expired:social:6=2, lease_log_counts.expired:social:7=2, lease_log_counts.expired:social:8=1, lease_log_counts.expired:social:9=1, lease_log_counts.expired:tend:4=1, lease_log_counts.expired:thought:1=1, lease_log_counts.lease:slot1:actor1:IDLE=3, lease_log_counts.lease:slot1:actor1:SOCIAL=1, lease_log_counts.lease:slot1:actor2:IDLE=1, lease_log_counts.lease:slot1:actor3:IDLE=1, lease_log_counts.lease:slot1:actor3:REACTIVE=1, lease_log_counts.lease:slot1:actor4:IDLE=1, lease_log_counts.lease:slot1:actor5:IDLE=1, lease_log_counts.lease:slot1:actor7:PLAYER=1, lease_log_counts.lease:slot1:actor8:PLAYER=1, lease_log_counts.lease:slot1:actor9:IDLE=1, lease_log_counts.lease:slot1:actor9:PLAYER=1, lease_log_counts.lease:slot2:actor1:PLAYER=1, lease_log_counts.lease:slot2:actor2:SOCIAL=1, lease_log_counts.lease:slot2:actor5:PLAYER=1, lease_log_counts.lease:slot2:actor6:PLAYER=1, lease_log_counts.lease:slot2:actor9:REACTIVE=1, lease_log_counts.merged:first_cue=1, lease_log_counts.merged:vanilla_social=1, lease_log_counts.release:slot1:done=3, lease_log_counts.release:slot1:idle_done=7, lease_log_counts.release:slot1:preempted=3, lease_log_counts.release:slot2:done=3, lease_log_counts.release:slot2:preempted=2, loop.checked=1, wants.nothing=1, wants.runs=1

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| intent | 9 | 1.43 | 4.72 | 5.33 |
| line | 9 | 1.56 | 2.79 | 3.63 |
| narrator | 5 | 0.94 | 1.66 | 1.91 |
| reflect | 6 | 4.68 | 4.95 | 5.11 |
| ALL | 29 | 1.7 | 4.75 | 5.33 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| intent | 9 | 96 | 8819 | 1935 | 9830 | 14 | 126 | 0.527 | 1832 | 3875 |
| line | 9 | 1944 | 14275 | 0 | 3390 | 14 | 129 | 0.192 | 1708 | 2114 |
| narrator | 5 | 25 | 96 | 34 | 214 | 7 | 41 | 0.69 | 109 | 351 |
| reflect | 6 | 842 | 4152 | 206 | 2429 | 222 | 1316 | 0.369 | 2130 | 3376 |
| ALL | 29 | 835 | 27342 | 56 | 15863 | 14 | 1612 | 0.367 | 1709 | 2700 |

Prompt tokens per lease/slot: 0: 96 over 5 calls (19.2/call), 1: 16368 over 16 calls (1023.0/call), 2: 10878 over 8 calls (1359.8/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 2 | 20 | 4.15 | 8 | 0.208 | 0.9 | - |
| Ana |  | aim | 2 | 22 | 4.15 | 11 | 0 | 0.773 | - |
| Cy |  | thought | 2 | 6 | 2.67 | 3 | 0 | 0.5 | - |
| Dee |  | thought | 2 | 6 | 2.67 | 3 | 0 | 0.5 | - |
| Kit | A-smart | spoken | 1 | 10 | 4.9 | 10 | 0.1 | 1 | - |
| Kit | A-smart | thought | 1 | 9 | 4.44 | 9 | 0.111 | 1 | - |
| Kit | A-smart | aim | 1 | 9 | 4.44 | 9 | 0.111 | 1 | - |
| Lou | A-dull | spoken | 1 | 15 | 4.4 | 7.5 | 0.067 | 0.933 | - |
| Lou | A-dull | aim | 1 | 9 | 4.44 | 9 | 0 | 1 | - |
| Max | B-sober | spoken | 1 | 9 | 5 | 4.5 | 0.111 | 1 | - |
| Max | B-sober | aim | 1 | 12 | 3.83 | 12 | 0.083 | 0.917 | - |
| Noa | B-drunk | spoken | 1 | 4 | 4.75 | 2 | 0 | 1 | - |
| Noa | B-drunk | aim | 1 | 8 | 3.62 | 8 | 0 | 1 | - |
| Tam |  | spoken | 1 | 6 | 3.33 | 6 | 0 | 1 | - |
| Tam |  | aim | 1 | 9 | 3.11 | 9 | 0.111 | 1 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 1 | 1 | 4.9 | 4.4 | - |
| smart_vs_dull / spoken / words_per_sentence | 1 | 1 | 10 | 7.5 | - |
| smart_vs_dull / spoken / rare_share | 1 | 1 | 0.1 | 0.0667 | - |
| smart_vs_dull / thought / word_len | 1 | 0 | 4.44 | - | - |
| smart_vs_dull / thought / words_per_sentence | 1 | 0 | 9 | - | - |
| smart_vs_dull / thought / rare_share | 1 | 0 | 0.111 | - | - |
| smart_vs_dull / aim / word_len | 1 | 1 | 4.44 | 4.44 | - |
| smart_vs_dull / aim / words_per_sentence | 1 | 1 | 9 | 9 | - |
| smart_vs_dull / aim / rare_share | 1 | 1 | 0.111 | 0 | - |
| sober_after_vs_drunk_before / spoken / word_len | 0 | 1 | - | 4.75 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 0 | 1 | - | 2 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 0 | 1 | - | 0 | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.62 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 8 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |

## Run s2x-on

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | minor_safety | 1 |
| diary | idle_preempted | 1 |
| reflect | idle_preempted | 2 |

Non-zero module counters: bystand.asked=4, bystand.shown=1, bystand.templated=1, group.ends.dropped=1, group.ends.over=1, group.ends.turn_cap=2, group.lines=9, group.rotations=3, grudges.started=3, lease_log_counts.drop:gap=3, lease_log_counts.expired:bystander:10=1, lease_log_counts.expired:bystander:4=1, lease_log_counts.expired:bystander:5=1, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:first_cue:3=1, lease_log_counts.expired:first_cue:5=1, lease_log_counts.expired:first_cue:9=1, lease_log_counts.expired:gathering:3=1, lease_log_counts.expired:injury:3=1, lease_log_counts.expired:injury:4=1, lease_log_counts.expired:injury:5=1, lease_log_counts.expired:injury:6=1, lease_log_counts.expired:injury:7=1, lease_log_counts.expired:injury:8=1, lease_log_counts.expired:level_up:5=1, lease_log_counts.expired:social:1=1, lease_log_counts.expired:social:2=2, lease_log_counts.expired:social:4=1, lease_log_counts.expired:social:5=3, lease_log_counts.expired:social:6=2, lease_log_counts.expired:social:7=2, lease_log_counts.expired:social:8=2, lease_log_counts.expired:tend:4=1, lease_log_counts.lease:slot1:actor10:IDLE=8, lease_log_counts.lease:slot1:actor10:SOCIAL=2, lease_log_counts.lease:slot1:actor11:IDLE=3, lease_log_counts.lease:slot1:actor1:AMBIENT=1, lease_log_counts.lease:slot1:actor1:IDLE=16, lease_log_counts.lease:slot1:actor1:REACTIVE=2, lease_log_counts.lease:slot1:actor1:SOCIAL=2, lease_log_counts.lease:slot1:actor1:URGENT=1, lease_log_counts.lease:slot1:actor2:IDLE=14, lease_log_counts.lease:slot1:actor3:IDLE=18, lease_log_counts.lease:slot1:actor3:REACTIVE=1, lease_log_counts.lease:slot1:actor4:IDLE=9, lease_log_counts.lease:slot1:actor4:REACTIVE=1, lease_log_counts.lease:slot1:actor5:IDLE=18, lease_log_counts.lease:slot1:actor5:PLAYER=1, lease_log_counts.lease:slot1:actor5:SOCIAL=1, lease_log_counts.lease:slot1:actor6:IDLE=12, lease_log_counts.lease:slot1:actor6:PLAYER=1, lease_log_counts.lease:slot1:actor7:IDLE=9, lease_log_counts.lease:slot1:actor8:IDLE=7, lease_log_counts.lease:slot1:actor8:PLAYER=1, lease_log_counts.lease:slot1:actor9:IDLE=11, lease_log_counts.lease:slot2:actor1:PLAYER=1, lease_log_counts.lease:slot2:actor2:SOCIAL=2, lease_log_counts.lease:slot2:actor2:URGENT=1, lease_log_counts.lease:slot2:actor3:REACTIVE=1, lease_log_counts.lease:slot2:actor5:IDLE=1, lease_log_counts.lease:slot2:actor6:SOCIAL=1, lease_log_counts.lease:slot2:actor7:PLAYER=1, lease_log_counts.lease:slot2:actor9:PLAYER=1, lease_log_counts.lease:slot2:actor9:REACTIVE=1, lease_log_counts.lease:slot2:actor9:SOCIAL=2, lease_log_counts.merged:first_cue=1, lease_log_counts.release:slot1:conversation_end=1, lease_log_counts.release:slot1:done=7, lease_log_counts.release:slot1:gathering_end=4, lease_log_counts.release:slot1:idle_done=122, lease_log_counts.release:slot1:preempted=3, lease_log_counts.release:slot1:rotated=2, lease_log_counts.release:slot2:conversation_end=1, lease_log_counts.release:slot2:done=6, lease_log_counts.release:slot2:gathering_end=3, lease_log_counts.release:slot2:preempted=1, lease_log_counts.release:slot2:rotated=1, loop.checked=11, wants.nothing=85, wants.runs=85

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 12 | 0.733 | 0.857 | 0.935 |
| chronicle | 2 | 1.71 | 1.76 | 1.78 |
| diary | 10 | 1.01 | 1.22 | 1.22 |
| dream | 6 | 0.426 | 0.456 | 0.47 |
| intent | 15 | 0.525 | 0.664 | 0.768 |
| line | 25 | 1.46 | 2.11 | 3.32 |
| lore | 2 | 0.321 | 0.338 | 0.342 |
| memory | 2 | 0.355 | 0.363 | 0.365 |
| narrator | 1 | 0.326 | 0.326 | 0.326 |
| reflect | 12 | 4.42 | 6.2 | 7.87 |
| relation | 2 | 0.359 | 0.383 | 0.389 |
| thought | 10 | 0.397 | 0.442 | 0.461 |
| ALL | 99 | 0.785 | 4.05 | 7.87 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 12 | 268 | 3208 | 306 | 3672 | 30 | 359 | 0.534 | 1601 | 3434 |
| chronicle | 2 | 379 | 758 | 195 | 390 | 72 | 144 | 0.34 | 1742 | 2638 |
| diary | 10 | 474 | 4340 | 258 | 2848 | 44 | 412 | 0.396 | 2120 | 3512 |
| dream | 6 | 404 | 2413 | 178 | 1146 | 11 | 64 | 0.322 | 1860 | 2743 |
| intent | 15 | 96 | 1440 | 1932 | 29387 | 15 | 223 | 0.953 | 650 | 13905 |
| line | 25 | 1906 | 38304 | 0 | 11524 | 15 | 362 | 0.231 | 1496 | 1946 |
| lore | 2 | 214 | 428 | 184 | 368 | 9 | 18 | 0.462 | 1486 | 2764 |
| memory | 2 | 49 | 98 | 1826 | 3652 | 14 | 28 | 0.974 | 721 | 27602 |
| narrator | 1 | 5 | 5 | 56 | 56 | 15 | 15 | 0.918 | 122 | 1492 |
| reflect | 12 | 548 | 7781 | 436 | 5872 | 204 | 2730 | 0.43 | 1917 | 3363 |
| relation | 2 | 16 | 32 | 1857 | 3714 | 15.5 | 31 | 0.991 | 325 | 38070 |
| thought | 10 | 392 | 3208 | 177 | 2700 | 9.5 | 93 | 0.457 | 1776 | 3271 |
| ALL | 99 | 404 | 62015 | 306 | 65329 | 16 | 4479 | 0.513 | 1549 | 3181 |

Prompt tokens per lease/slot: 0: 4399 over 17 calls (258.8/call), 1: 38851 over 61 calls (636.9/call), 2: 18765 over 21 calls (893.6/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 6 | 67 | 3.89 | 9.17 | 0.194 | 0.806 | 0.876 |
| Ana |  | aim | 2 | 23 | 3.98 | 11.5 | 0 | 0.826 | - |
| Ana |  | thought | 3 | 25 | 4.97 | 8.33 | 0.286 | 0.72 | - |
| Ana |  | diary | 1 | 40 | 4.15 | 10 | 0.15 | 0.875 | - |
| Ana |  | dream | 2 | 12 | 4.33 | 6 | 0.333 | 0.5 | - |
| Bram |  | spoken | 3 | 27 | 3.9 | 4.5 | 0.178 | 0.889 | - |
| Bram |  | aim | 1 | 13 | 4.08 | 13 | 0.077 | 0.923 | - |
| Bram |  | thought | 2 | 20 | 4.5 | 10 | 0.1 | 0.45 | - |
| Bram |  | diary | 1 | 39 | 3.9 | 9.75 | 0.154 | 0.897 | - |
| Bram |  | dream | 2 | 14 | 4.86 | 7 | 0.429 | 0.5 | - |
| Cy |  | thought | 4 | 12 | 3.83 | 2.25 | 0.167 | 0.5 | - |
| Cy |  | spoken | 2 | 23 | 3.81 | 8.5 | 0.129 | 0.87 | - |
| Cy |  | aim | 1 | 9 | 4.11 | 9 | 0.111 | 1 | - |
| Cy |  | diary | 1 | 44 | 4.14 | 14.7 | 0.159 | 0.795 | - |
| Cy |  | dream | 2 | 16 | 4.38 | 8 | 0.125 | 0.5 | - |
| Dee |  | thought | 2 | 10 | 4 | 5 | 0.2 | 0.5 | - |
| Dee |  | diary | 1 | 29 | 4.38 | 7.25 | 0.207 | 0.862 | - |
| Dee |  | spoken | 1 | 9 | 3.67 | 3 | 0 | 0.667 | - |
| Kit | A-smart | spoken | 2 | 26 | 4.12 | 13 | 0.149 | 0.846 | - |
| Kit | A-smart | aim | 1 | 11 | 3.54 | 11 | 0.182 | 0.818 | - |
| Kit | A-smart | thought | 2 | 16 | 4.5 | 8 | 0.375 | 0.438 | - |
| Kit | A-smart | diary | 1 | 35 | 4 | 11.7 | 0.286 | 0.829 | - |
| Kit | A-smart | dream | 2 | 16 | 4.5 | 8 | 0.125 | 0.5 | - |
| Lou | A-dull | spoken | 2 | 21 | 3.86 | 7.75 | 0.045 | 0.905 | - |
| Lou | A-dull | thought | 3 | 19 | 4.46 | 6.33 | 0.159 | 0.684 | - |
| Lou | A-dull | aim | 1 | 7 | 3.71 | 7 | 0.143 | 1 | - |
| Lou | A-dull | diary | 1 | 27 | 3.93 | 6.75 | 0.185 | 0.852 | - |
| Max | B-sober | spoken | 1 | 8 | 4.38 | 2.67 | 0 | 1 | - |
| Max | B-sober | thought | 3 | 27 | 4.57 | 9 | 0.333 | 0.667 | - |
| Max | B-sober | aim | 1 | 11 | 3.46 | 11 | 0 | 0.909 | - |
| Max | B-sober | diary | 1 | 36 | 4.19 | 12 | 0.222 | 0.778 | - |
| Noa | B-drunk | spoken | 1 | 6 | 4.17 | 3 | 0 | 1 | - |
| Noa | B-drunk | thought | 3 | 22 | 3.78 | 7.33 | 0.19 | 0.682 | - |
| Noa | B-drunk | aim | 1 | 8 | 3.62 | 8 | 0 | 1 | - |
| Noa | B-drunk | diary | 1 | 21 | 4.24 | 5.25 | 0.238 | 0.857 | - |
| Noa | B-drunk | dream | 2 | 14 | 4.14 | 7 | 0.571 | 0.5 | - |
| Tam |  | spoken | 4 | 41 | 3.85 | 8.88 | 0.067 | 0.854 | - |
| Tam |  | aim | 3 | 32 | 3.01 | 10.7 | 0.037 | 0.625 | - |
| Tam |  | thought | 2 | 10 | 4 | 5 | 0.4 | 0.5 | - |
| Tam |  | diary | 1 | 23 | 4.17 | 5.75 | 0.13 | 0.913 | - |
| Uma |  | spoken | 2 | 17 | 4.61 | 4.25 | 0.212 | 0.824 | - |
| Uma |  | aim | 1 | 7 | 4.29 | 7 | 0.286 | 1 | - |
| Uma |  | thought | 2 | 14 | 4.86 | 7 | 0.286 | 0.5 | - |
| Uma |  | diary | 1 | 40 | 3.8 | 13.3 | 0.15 | 0.825 | - |
| Uma |  | dream | 2 | 12 | 5.67 | 6 | 0.667 | 0.5 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 2 | 2 | 4.12 | 3.86 | 2.02 |
| smart_vs_dull / spoken / words_per_sentence | 2 | 2 | 13 | 7.75 | 2.13 |
| smart_vs_dull / spoken / rare_share | 2 | 2 | 0.149 | 0.0455 | 1.3 |
| smart_vs_dull / thought / word_len | 2 | 3 | 4.5 | 4.46 | 0.075 |
| smart_vs_dull / thought / words_per_sentence | 2 | 3 | 8 | 6.33 | 3.54 |
| smart_vs_dull / thought / rare_share | 2 | 3 | 0.375 | 0.159 | 19.3 |
| smart_vs_dull / diary / word_len | 1 | 1 | 4 | 3.93 | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 1 | 11.7 | 6.75 | - |
| smart_vs_dull / diary / rare_share | 1 | 1 | 0.286 | 0.185 | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 4.5 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 8 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.125 | - | - |
| smart_vs_dull / aim / word_len | 1 | 1 | 3.55 | 3.71 | - |
| smart_vs_dull / aim / words_per_sentence | 1 | 1 | 11 | 7 | - |
| smart_vs_dull / aim / rare_share | 1 | 1 | 0.182 | 0.143 | - |
| sober_after_vs_drunk_before / spoken / word_len | 0 | 1 | - | 4.17 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 0 | 1 | - | 3 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 0 | 1 | - | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 2 | 1 | 3.86 | 3.62 | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 2 | 1 | 7 | 8 | - |
| sober_after_vs_drunk_before / thought / rare_share | 2 | 1 | 0.286 | 0 | - |
| sober_after_vs_drunk_before / diary / word_len | 1 | 0 | 4.24 | - | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 0 | 5.25 | - | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 0 | 0.238 | - | - |
| sober_after_vs_drunk_before / dream / word_len | 2 | 0 | 4.14 | - | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 0 | 0.571 | - | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.62 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 8 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |

## Run ALL

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | expired | 2 |
| - | minor_safety | 2 |
| - | preempted | 6 |
| - | reflect_failed | 2 |
| Patience | unknown_name | 1 |
| diary | idle_preempted | 1 |
| late | dream | 3 |
| minor_safety | chronicle | 2 |
| reflect | idle_preempted | 14 |
| thought | idle_preempted | 1 |
| unknown_name:Additionally | chronicle | 1 |
| unknown_name:Earlier | chronicle | 1 |
| unknown_name:Furthermore | chronicle | 1 |
| unknown_name:Odo | diary | 1 |
| unknown_name:Party | diary | 1 |
| wants | idle_preempted | 1 |

Non-zero module counters: art.dropped=18, bystand.asked=26, bystand.shown=12, bystand.templated=12, dream_pending=6, group.ends.dropped=2, group.ends.over=3, group.ends.turn_cap=9, group.lines=34, group.rotations=11, grudges.escalated=1, grudges.seeks=7, grudges.started=13, lease_log_counts.drop:ambient_quota=1, lease_log_counts.drop:gap=20, lease_log_counts.drop:queue_full=2, lease_log_counts.evicted:level_up:5=1, lease_log_counts.evicted:social:2=1, lease_log_counts.evicted:vanilla_social:1=1, lease_log_counts.expired:bystander:10=1, lease_log_counts.expired:bystander:3=4, lease_log_counts.expired:bystander:4=3, lease_log_counts.expired:bystander:5=2, lease_log_counts.expired:bystander:7=1, lease_log_counts.expired:concern_resolved:1=1, lease_log_counts.expired:concern_resolved:2=1, lease_log_counts.expired:concern_resolved:4=3, lease_log_counts.expired:first_cue:2=4, lease_log_counts.expired:first_cue:3=2, lease_log_counts.expired:first_cue:5=3, lease_log_counts.expired:first_cue:9=3, lease_log_counts.expired:gathering:1=3, lease_log_counts.expired:gathering:3=3, lease_log_counts.expired:gathering:4=1, lease_log_counts.expired:gathering:5=1, lease_log_counts.expired:injury:3=4, lease_log_counts.expired:injury:4=3, lease_log_counts.expired:injury:5=3, lease_log_counts.expired:injury:6=5, lease_log_counts.expired:injury:7=6, lease_log_counts.expired:injury:8=4, lease_log_counts.expired:level_up:5=3, lease_log_counts.expired:raid_end:1=1, lease_log_counts.expired:social:1=5, lease_log_counts.expired:social:10=3, lease_log_counts.expired:social:2=6, lease_log_counts.expired:social:3=1, lease_log_counts.expired:social:4=5, lease_log_counts.expired:social:5=12, lease_log_counts.expired:social:6=10, lease_log_counts.expired:social:7=8, lease_log_counts.expired:social:8=11, lease_log_counts.expired:social:9=4, lease_log_counts.expired:tend:4=5, lease_log_counts.expired:thought:1=3, lease_log_counts.expired:vanilla_social:1=4, lease_log_counts.lease:slot1:actor10:IDLE=40, lease_log_counts.lease:slot1:actor10:SOCIAL=6, lease_log_counts.lease:slot1:actor11:IDLE=15, lease_log_counts.lease:slot1:actor1:AMBIENT=1, lease_log_counts.lease:slot1:actor1:IDLE=69, lease_log_counts.lease:slot1:actor1:REACTIVE=4, lease_log_counts.lease:slot1:actor1:SOCIAL=11, lease_log_counts.lease:slot1:actor1:URGENT=5, lease_log_counts.lease:slot1:actor2:IDLE=61, lease_log_counts.lease:slot1:actor2:SOCIAL=3, lease_log_counts.lease:slot1:actor3:IDLE=68, lease_log_counts.lease:slot1:actor3:REACTIVE=9, lease_log_counts.lease:slot1:actor4:IDLE=42, lease_log_counts.lease:slot1:actor4:REACTIVE=7, lease_log_counts.lease:slot1:actor5:IDLE=69, lease_log_counts.lease:slot1:actor5:PLAYER=4, lease_log_counts.lease:slot1:actor5:REACTIVE=1, lease_log_counts.lease:slot1:actor5:SOCIAL=8, lease_log_counts.lease:slot1:actor6:IDLE=59, lease_log_counts.lease:slot1:actor6:PLAYER=4, lease_log_counts.lease:slot1:actor7:IDLE=41, lease_log_counts.lease:slot1:actor7:PLAYER=2, lease_log_counts.lease:slot1:actor8:IDLE=31, lease_log_counts.lease:slot1:actor8:PLAYER=6, lease_log_counts.lease:slot1:actor8:REACTIVE=2, lease_log_counts.lease:slot1:actor9:IDLE=48, lease_log_counts.lease:slot1:actor9:PLAYER=2, lease_log_counts.lease:slot2:actor1:PLAYER=6, lease_log_counts.lease:slot2:actor1:REACTIVE=1, lease_log_counts.lease:slot2:actor1:SOCIAL=3, lease_log_counts.lease:slot2:actor2:REACTIVE=1, lease_log_counts.lease:slot2:actor2:SOCIAL=12, lease_log_counts.lease:slot2:actor2:URGENT=5, lease_log_counts.lease:slot2:actor3:REACTIVE=3, lease_log_counts.lease:slot2:actor3:SOCIAL=3, lease_log_counts.lease:slot2:actor4:REACTIVE=1, lease_log_counts.lease:slot2:actor4:SOCIAL=1, lease_log_counts.lease:slot2:actor5:IDLE=1, lease_log_counts.lease:slot2:actor5:PLAYER=2, lease_log_counts.lease:slot2:actor5:REACTIVE=2, lease_log_counts.lease:slot2:actor6:PLAYER=2, lease_log_counts.lease:slot2:actor6:SOCIAL=3, lease_log_counts.lease:slot2:actor7:PLAYER=4, lease_log_counts.lease:slot2:actor7:SOCIAL=2, lease_log_counts.lease:slot2:actor9:PLAYER=4, lease_log_counts.lease:slot2:actor9:REACTIVE=5, lease_log_counts.lease:slot2:actor9:SOCIAL=4, lease_log_counts.merged:concern_resolved=2, lease_log_counts.merged:first_cue=4, lease_log_counts.merged:vanilla_social=3, lease_log_counts.release:slot1:conversation_end=10, lease_log_counts.release:slot1:done=37, lease_log_counts.release:slot1:gathering_end=14, lease_log_counts.release:slot1:idle_done=524, lease_log_counts.release:slot1:preempted=23, lease_log_counts.release:slot1:rotated=8, lease_log_counts.release:slot2:conversation_end=10, lease_log_counts.release:slot2:done=33, lease_log_counts.release:slot2:gathering_end=12, lease_log_counts.release:slot2:preempted=6, lease_log_counts.release:slot2:rotated=3, loop.checked=61, wants.acted=2, wants.nothing=359, wants.runs=362

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 36 | 0.743 | 0.841 | 1.03 |
| chronicle | 12 | 1.67 | 1.9 | 1.94 |
| diary | 32 | 1.11 | 1.28 | 1.61 |
| dream | 18 | 0.428 | 0.483 | 0.531 |
| intent | 103 | 0.563 | 3.68 | 6.27 |
| line | 140 | 1.42 | 2.28 | 7.19 |
| lore | 6 | 0.339 | 0.382 | 0.395 |
| memory | 14 | 0.397 | 0.476 | 0.49 |
| narrator | 15 | 0.526 | 1.79 | 2.29 |
| recap | 1 | 0.735 | 0.735 | 0.735 |
| reflect | 65 | 5.48 | 8.24 | 10.4 |
| relation | 14 | 0.338 | 0.393 | 0.51 |
| thought | 50 | 0.391 | 0.452 | 0.578 |
| ALL | 506 | 0.776 | 4.73 | 10.4 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 36 | 266 | 9306 | 306 | 10711 | 31 | 1084 | 0.535 | 1589 | 3418 |
| chronicle | 12 | 474 | 6148 | 195 | 2174 | 69.5 | 822 | 0.261 | 1960 | 2653 |
| diary | 32 | 494 | 15379 | 206 | 7828 | 43.5 | 1356 | 0.337 | 2081 | 3140 |
| dream | 18 | 404 | 7513 | 178 | 3181 | 11 | 197 | 0.297 | 1899 | 2703 |
| intent | 103 | 96 | 35995 | 2022 | 183861 | 15 | 1533 | 0.836 | 1286 | 7853 |
| line | 140 | 1962 | 212715 | 0 | 74107 | 15 | 2093 | 0.258 | 1529 | 2062 |
| lore | 6 | 213 | 1446 | 184 | 921 | 9.5 | 52 | 0.389 | 1580 | 2586 |
| memory | 14 | 44.5 | 675 | 2032 | 27933 | 16 | 226 | 0.976 | 691 | 29290 |
| narrator | 15 | 25 | 282 | 34 | 642 | 8 | 150 | 0.695 | 63 | 206 |
| recap | 1 | 410 | 410 | 1 | 1 | 26 | 26 | 0.002 | 1808 | 1812 |
| reflect | 65 | 849 | 50669 | 206 | 26640 | 256 | 18197 | 0.345 | 2122 | 3237 |
| relation | 14 | 16 | 224 | 2054 | 28356 | 14 | 206 | 0.992 | 318 | 40520 |
| thought | 50 | 402 | 19249 | 177 | 10845 | 9 | 464 | 0.36 | 1868 | 2920 |
| ALL | 506 | 421 | 360011 | 241 | 377200 | 16 | 26406 | 0.512 | 1573 | 3220 |

Prompt tokens per lease/slot: 0: 17592 over 70 calls (251.3/call), 1: 223609 over 314 calls (712.1/call), 2: 118810 over 122 calls (973.9/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 16 | 206 | 4.28 | 12 | 0.147 | 0.626 | 0.844 |
| Ana |  | aim | 3 | 29 | 3.94 | 9.67 | 0.1 | 0.759 | - |
| Ana |  | thought | 14 | 95 | 3.67 | 6.57 | 0.117 | 0.495 | 0.597 |
| Ana |  | diary | 1 | 50 | 4.52 | 12.5 | 0.2 | 0.86 | 0.86 |
| Ana |  | dream | 2 | 18 | 4.11 | 9 | 0.111 | 0.444 | - |
| Bram |  | spoken | 8 | 85 | 4.15 | 5.31 | 0.324 | 0.729 | 0.793 |
| Bram |  | aim | 4 | 47 | 3.7 | 11.8 | 0.222 | 0.489 | - |
| Bram |  | thought | 9 | 76 | 5.24 | 8.44 | 0.394 | 0.487 | 0.544 |
| Bram |  | diary | 1 | 41 | 4.56 | 10.2 | 0.293 | 0.902 | - |
| Bram |  | dream | 2 | 16 | 5 | 8 | 0.375 | 0.5 | - |
| Cy |  | thought | 11 | 63 | 3.9 | 5.46 | 0.142 | 0.508 | 0.59 |
| Cy |  | spoken | 10 | 126 | 3.99 | 9.4 | 0.034 | 0.563 | 0.797 |
| Cy |  | aim | 4 | 37 | 4.08 | 9.25 | 0 | 0.459 | - |
| Cy |  | diary | 1 | 38 | 4.24 | 9.5 | 0.237 | 0.868 | - |
| Cy |  | dream | 2 | 14 | 4.57 | 7 | 0.571 | 0.5 | - |
| Dee |  | thought | 9 | 55 | 4.06 | 6.11 | 0.126 | 0.545 | 0.6 |
| Dee |  | spoken | 6 | 45 | 4.63 | 3.5 | 0.148 | 0.8 | - |
| Dee |  | aim | 2 | 17 | 3.63 | 8.5 | 0.056 | 0.765 | - |
| Dee |  | diary | 1 | 32 | 4.28 | 8 | 0.156 | 0.906 | - |
| Kit | A-smart | spoken | 8 | 93 | 4.27 | 10.3 | 0.158 | 0.699 | 0.814 |
| Kit | A-smart | thought | 14 | 91 | 3.98 | 6.07 | 0.195 | 0.505 | 0.587 |
| Kit | A-smart | aim | 3 | 30 | 4.24 | 10 | 0.131 | 0.567 | - |
| Kit | A-smart | diary | 1 | 49 | 4.1 | 12.2 | 0.245 | 0.816 | - |
| Kit | A-smart | dream | 2 | 16 | 4.5 | 8 | 0.25 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 27 | 3.71 | 7.17 | 0.186 | 0.852 | - |
| Lou | A-dull | thought | 7 | 46 | 4.26 | 6.57 | 0 | 0.5 | - |
| Lou | A-dull | aim | 1 | 10 | 3 | 10 | 0 | 1 | - |
| Lou | A-dull | diary | 1 | 26 | 4.31 | 6.5 | 0.231 | 0.846 | - |
| Max | B-sober | spoken | 3 | 22 | 4.47 | 6 | 0.286 | 0.955 | - |
| Max | B-sober | aim | 2 | 24 | 3.27 | 12 | 0 | 0.667 | - |
| Max | B-sober | thought | 6 | 46 | 4.97 | 7.67 | 0.225 | 0.478 | - |
| Max | B-sober | diary | 1 | 40 | 4.35 | 10 | 0.225 | 0.9 | - |
| Noa | B-drunk | spoken | 2 | 13 | 3.69 | 5.5 | 0.167 | 1 | - |
| Noa | B-drunk | aim | 2 | 21 | 3.38 | 10.5 | 0.05 | 0.714 | - |
| Noa | B-drunk | thought | 6 | 42 | 4.37 | 7 | 0.361 | 0.405 | - |
| Noa | B-drunk | diary | 1 | 20 | 4.15 | 5 | 0.2 | 0.9 | - |
| Noa | B-drunk | dream | 2 | 14 | 4.86 | 7 | 0.714 | 0.5 | - |
| Tam |  | spoken | 6 | 54 | 3.92 | 8.42 | 0.114 | 0.741 | 0.744 |
| Tam |  | aim | 2 | 20 | 3.33 | 10 | 0.101 | 0.6 | - |
| Tam |  | thought | 7 | 51 | 4.02 | 7.29 | 0.278 | 0.529 | 0.53 |
| Tam |  | diary | 1 | 32 | 4.03 | 8 | 0.188 | 0.844 | - |
| Uma |  | spoken | 5 | 55 | 4.22 | 5.5 | 0.133 | 0.764 | 0.797 |
| Uma |  | aim | 2 | 20 | 3.14 | 10 | 0.045 | 0.8 | - |
| Uma |  | thought | 6 | 38 | 5.91 | 6.33 | 0.357 | 0.447 | - |
| Uma |  | diary | 1 | 38 | 4.82 | 12.7 | 0.368 | 0.842 | - |
| Uma |  | dream | 2 | 14 | 4.86 | 7 | 0.286 | 0.5 | - |
| Ana |  | spoken | 6 | 67 | 3.89 | 9.17 | 0.194 | 0.806 | 0.876 |
| Ana |  | aim | 2 | 23 | 3.98 | 11.5 | 0 | 0.826 | - |
| Ana |  | thought | 3 | 25 | 4.97 | 8.33 | 0.286 | 0.72 | - |
| Ana |  | diary | 1 | 40 | 4.15 | 10 | 0.15 | 0.875 | - |
| Ana |  | dream | 2 | 12 | 4.33 | 6 | 0.333 | 0.5 | - |
| Bram |  | spoken | 3 | 27 | 3.9 | 4.5 | 0.178 | 0.889 | - |
| Bram |  | aim | 1 | 13 | 4.08 | 13 | 0.077 | 0.923 | - |
| Bram |  | thought | 2 | 20 | 4.5 | 10 | 0.1 | 0.45 | - |
| Bram |  | diary | 1 | 39 | 3.9 | 9.75 | 0.154 | 0.897 | - |
| Bram |  | dream | 2 | 14 | 4.86 | 7 | 0.429 | 0.5 | - |
| Cy |  | thought | 4 | 12 | 3.83 | 2.25 | 0.167 | 0.5 | - |
| Cy |  | spoken | 2 | 23 | 3.81 | 8.5 | 0.129 | 0.87 | - |
| Cy |  | aim | 1 | 9 | 4.11 | 9 | 0.111 | 1 | - |
| Cy |  | diary | 1 | 44 | 4.14 | 14.7 | 0.159 | 0.795 | - |
| Cy |  | dream | 2 | 16 | 4.38 | 8 | 0.125 | 0.5 | - |
| Dee |  | thought | 2 | 10 | 4 | 5 | 0.2 | 0.5 | - |
| Dee |  | diary | 1 | 29 | 4.38 | 7.25 | 0.207 | 0.862 | - |
| Dee |  | spoken | 1 | 9 | 3.67 | 3 | 0 | 0.667 | - |
| Kit | A-smart | spoken | 2 | 26 | 4.12 | 13 | 0.149 | 0.846 | - |
| Kit | A-smart | aim | 1 | 11 | 3.54 | 11 | 0.182 | 0.818 | - |
| Kit | A-smart | thought | 2 | 16 | 4.5 | 8 | 0.375 | 0.438 | - |
| Kit | A-smart | diary | 1 | 35 | 4 | 11.7 | 0.286 | 0.829 | - |
| Kit | A-smart | dream | 2 | 16 | 4.5 | 8 | 0.125 | 0.5 | - |
| Lou | A-dull | spoken | 2 | 21 | 3.86 | 7.75 | 0.045 | 0.905 | - |
| Lou | A-dull | thought | 3 | 19 | 4.46 | 6.33 | 0.159 | 0.684 | - |
| Lou | A-dull | aim | 1 | 7 | 3.71 | 7 | 0.143 | 1 | - |
| Lou | A-dull | diary | 1 | 27 | 3.93 | 6.75 | 0.185 | 0.852 | - |
| Max | B-sober | spoken | 1 | 8 | 4.38 | 2.67 | 0 | 1 | - |
| Max | B-sober | thought | 3 | 27 | 4.57 | 9 | 0.333 | 0.667 | - |
| Max | B-sober | aim | 1 | 11 | 3.46 | 11 | 0 | 0.909 | - |
| Max | B-sober | diary | 1 | 36 | 4.19 | 12 | 0.222 | 0.778 | - |
| Noa | B-drunk | spoken | 1 | 6 | 4.17 | 3 | 0 | 1 | - |
| Noa | B-drunk | thought | 3 | 22 | 3.78 | 7.33 | 0.19 | 0.682 | - |
| Noa | B-drunk | aim | 1 | 8 | 3.62 | 8 | 0 | 1 | - |
| Noa | B-drunk | diary | 1 | 21 | 4.24 | 5.25 | 0.238 | 0.857 | - |
| Noa | B-drunk | dream | 2 | 14 | 4.14 | 7 | 0.571 | 0.5 | - |
| Tam |  | spoken | 4 | 41 | 3.85 | 8.88 | 0.067 | 0.854 | - |
| Tam |  | aim | 3 | 32 | 3.01 | 10.7 | 0.037 | 0.625 | - |
| Tam |  | thought | 2 | 10 | 4 | 5 | 0.4 | 0.5 | - |
| Tam |  | diary | 1 | 23 | 4.17 | 5.75 | 0.13 | 0.913 | - |
| Uma |  | spoken | 2 | 17 | 4.61 | 4.25 | 0.212 | 0.824 | - |
| Uma |  | aim | 1 | 7 | 4.29 | 7 | 0.286 | 1 | - |
| Uma |  | thought | 2 | 14 | 4.86 | 7 | 0.286 | 0.5 | - |
| Uma |  | diary | 1 | 40 | 3.8 | 13.3 | 0.15 | 0.825 | - |
| Uma |  | dream | 2 | 12 | 5.67 | 6 | 0.667 | 0.5 | - |
| Ana |  | spoken | 8 | 102 | 4.06 | 12.8 | 0.091 | 0.667 | 0.835 |
| Ana |  | aim | 4 | 43 | 3.74 | 10.8 | 0.044 | 0.651 | - |
| Ana |  | thought | 3 | 31 | 3.56 | 10.3 | 0.067 | 0.613 | - |
| Ana |  | diary | 2 | 87 | 3.9 | 12.6 | 0.16 | 0.667 | 0.727 |
| Ana |  | dream | 2 | 18 | 5.11 | 4.5 | 0.556 | 0.5 | - |
| Bram |  | spoken | 6 | 70 | 3.9 | 5.83 | 0.18 | 0.786 | 0.85 |
| Bram |  | aim | 4 | 44 | 3.54 | 11 | 0.069 | 0.455 | - |
| Bram |  | thought | 3 | 36 | 3.9 | 12 | 0.136 | 0.583 | - |
| Bram |  | diary | 1 | 34 | 4.24 | 8.5 | 0.235 | 0.824 | - |
| Bram |  | dream | 2 | 14 | 4.57 | 7 | 0.286 | 0.5 | - |
| Cy |  | thought | 2 | 12 | 3.67 | 3 | 0.167 | 0.5 | - |
| Cy |  | diary | 1 | 38 | 3.34 | 9.5 | 0.184 | 0.737 | - |
| Cy |  | spoken | 1 | 9 | 3.78 | 4.5 | 0.111 | 1 | - |
| Cy |  | dream | 2 | 16 | 4.5 | 4 | 0.375 | 0.5 | - |
| Dee |  | thought | 2 | 12 | 4 | 6 | 0.167 | 0.5 | - |
| Dee |  | diary | 1 | 31 | 5 | 7.75 | 0.226 | 0.903 | - |
| Dee |  | spoken | 1 | 11 | 5 | 5.5 | 0.182 | 0.727 | - |
| Kit | A-smart | spoken | 3 | 37 | 4.25 | 12.3 | 0.139 | 0.838 | - |
| Kit | A-smart | aim | 2 | 23 | 3.7 | 11.5 | 0.045 | 0.826 | - |
| Kit | A-smart | diary | 1 | 42 | 3.86 | 10.5 | 0.238 | 0.81 | - |
| Kit | A-smart | thought | 3 | 25 | 3.62 | 8.33 | 0.221 | 0.64 | - |
| Kit | A-smart | dream | 2 | 18 | 4.22 | 9 | 0.444 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 26 | 3.5 | 8.67 | 0.139 | 0.846 | - |
| Lou | A-dull | thought | 3 | 21 | 4.67 | 7 | 0.286 | 0.619 | - |
| Lou | A-dull | aim | 2 | 17 | 4.08 | 8.5 | 0.143 | 0.941 | - |
| Lou | A-dull | diary | 1 | 18 | 3.89 | 4.5 | 0.222 | 0.944 | - |
| Max | B-sober | spoken | 2 | 23 | 3.76 | 9 | 0.1 | 0.783 | - |
| Max | B-sober | aim | 1 | 11 | 3.46 | 11 | 0 | 1 | - |
| Max | B-sober | diary | 1 | 33 | 5.3 | 8.25 | 0.333 | 0.879 | - |
| Max | B-sober | thought | 2 | 8 | 4.75 | 4 | 0.25 | 0.5 | - |
| Noa | B-drunk | spoken | 2 | 14 | 3.83 | 3.5 | 0.188 | 0.929 | - |
| Noa | B-drunk | aim | 1 | 8 | 3.62 | 8 | 0 | 1 | - |
| Noa | B-drunk | thought | 3 | 18 | 4.28 | 6 | 0.133 | 0.722 | - |
| Noa | B-drunk | dream | 2 | 16 | 4.5 | 4 | 0.125 | 0.5 | - |
| Tam |  | spoken | 2 | 19 | 3.39 | 9.5 | 0.226 | 0.947 | - |
| Tam |  | aim | 2 | 22 | 3.5 | 11 | 0.045 | 0.773 | - |
| Tam |  | diary | 1 | 26 | 3.85 | 6.5 | 0.154 | 0.885 | - |
| Tam |  | thought | 2 | 10 | 3.8 | 5 | 0.2 | 0.5 | - |
| Uma |  | diary | 1 | 40 | 4.78 | 13.3 | 0.3 | 0.9 | - |
| Uma |  | thought | 2 | 12 | 5 | 6 | 0.5 | 0.5 | - |
| Uma |  | dream | 2 | 12 | 5.17 | 6 | 0.667 | 0.5 | - |
| Ana |  | spoken | 2 | 20 | 4.15 | 8 | 0.208 | 0.9 | - |
| Ana |  | aim | 2 | 22 | 4.15 | 11 | 0 | 0.773 | - |
| Cy |  | thought | 2 | 6 | 2.67 | 3 | 0 | 0.5 | - |
| Dee |  | thought | 2 | 6 | 2.67 | 3 | 0 | 0.5 | - |
| Kit | A-smart | spoken | 1 | 10 | 4.9 | 10 | 0.1 | 1 | - |
| Kit | A-smart | thought | 1 | 9 | 4.44 | 9 | 0.111 | 1 | - |
| Kit | A-smart | aim | 1 | 9 | 4.44 | 9 | 0.111 | 1 | - |
| Lou | A-dull | spoken | 1 | 15 | 4.4 | 7.5 | 0.067 | 0.933 | - |
| Lou | A-dull | aim | 1 | 9 | 4.44 | 9 | 0 | 1 | - |
| Max | B-sober | spoken | 1 | 9 | 5 | 4.5 | 0.111 | 1 | - |
| Max | B-sober | aim | 1 | 12 | 3.83 | 12 | 0.083 | 0.917 | - |
| Noa | B-drunk | spoken | 1 | 4 | 4.75 | 2 | 0 | 1 | - |
| Noa | B-drunk | aim | 1 | 8 | 3.62 | 8 | 0 | 1 | - |
| Tam |  | spoken | 1 | 6 | 3.33 | 6 | 0 | 1 | - |
| Tam |  | aim | 1 | 9 | 3.11 | 9 | 0.111 | 1 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 8 | 3 | 4.27 | 3.71 | 1.42 |
| smart_vs_dull / spoken / words_per_sentence | 8 | 3 | 10.3 | 7.17 | 0.96 |
| smart_vs_dull / spoken / rare_share | 8 | 3 | 0.158 | 0.186 | -0.236 |
| smart_vs_dull / thought / word_len | 14 | 7 | 3.98 | 4.26 | -0.364 |
| smart_vs_dull / thought / words_per_sentence | 14 | 7 | 6.07 | 6.57 | -0.164 |
| smart_vs_dull / thought / rare_share | 14 | 7 | 0.195 | 0 | 1.17 |
| smart_vs_dull / diary / word_len | 1 | 1 | 4.1 | 4.31 | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 1 | 12.2 | 6.5 | - |
| smart_vs_dull / diary / rare_share | 1 | 1 | 0.245 | 0.231 | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 4.5 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 8 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.25 | - | - |
| smart_vs_dull / aim / word_len | 3 | 1 | 4.24 | 3 | - |
| smart_vs_dull / aim / words_per_sentence | 3 | 1 | 10 | 10 | - |
| smart_vs_dull / aim / rare_share | 3 | 1 | 0.131 | 0 | - |
| sober_after_vs_drunk_before / spoken / word_len | 1 | 1 | 3.89 | 3.5 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 1 | 1 | 9 | 2 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 1 | 1 | 0.333 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 2 | 4 | 3.67 | 4.71 | -1.47 |
| sober_after_vs_drunk_before / thought / words_per_sentence | 2 | 4 | 9 | 6 | 3 |
| sober_after_vs_drunk_before / thought / rare_share | 2 | 4 | 0.111 | 0.486 | -4.37 |
| sober_after_vs_drunk_before / diary / word_len | 0 | 1 | - | 4.15 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 0 | 1 | - | 5 | - |
| sober_after_vs_drunk_before / diary / rare_share | 0 | 1 | - | 0.2 | - |
| sober_after_vs_drunk_before / dream / word_len | 2 | 0 | 4.86 | - | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 0 | 0.714 | - | - |
| sober_after_vs_drunk_before / aim / word_len | 1 | 1 | 3.3 | 3.45 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 1 | 1 | 10 | 11 | - |
| sober_after_vs_drunk_before / aim / rare_share | 1 | 1 | 0.1 | 0 | - |
| smart_vs_dull / spoken / word_len | 3 | 3 | 4.25 | 3.5 | 2.62 |
| smart_vs_dull / spoken / words_per_sentence | 3 | 3 | 12.3 | 8.67 | 1.17 |
| smart_vs_dull / spoken / rare_share | 3 | 3 | 0.139 | 0.139 | -0.003 |
| smart_vs_dull / thought / word_len | 3 | 3 | 3.62 | 4.67 | -7.85 |
| smart_vs_dull / thought / words_per_sentence | 3 | 3 | 8.33 | 7 | 0.816 |
| smart_vs_dull / thought / rare_share | 3 | 3 | 0.221 | 0.286 | -0.816 |
| smart_vs_dull / diary / word_len | 1 | 1 | 3.86 | 3.89 | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 1 | 10.5 | 4.5 | - |
| smart_vs_dull / diary / rare_share | 1 | 1 | 0.238 | 0.222 | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 4.22 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 9 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.444 | - | - |
| smart_vs_dull / aim / word_len | 2 | 2 | 3.7 | 4.08 | -0.49 |
| smart_vs_dull / aim / words_per_sentence | 2 | 2 | 11.5 | 8.5 | 1.9 |
| smart_vs_dull / aim / rare_share | 2 | 2 | 0.0455 | 0.143 | -0.65 |
| sober_after_vs_drunk_before / spoken / word_len | 1 | 1 | 4 | 3.67 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 1 | 1 | 4 | 3 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 1 | 1 | 0.375 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 1 | 2 | 3.62 | 4.6 | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 1 | 2 | 8 | 5 | - |
| sober_after_vs_drunk_before / thought / rare_share | 1 | 2 | 0 | 0.2 | - |
| sober_after_vs_drunk_before / dream / word_len | 0 | 2 | - | 4.5 | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 0 | 2 | - | 4 | - |
| sober_after_vs_drunk_before / dream / rare_share | 0 | 2 | - | 0.125 | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.62 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 8 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |
| smart_vs_dull / spoken / word_len | 1 | 1 | 4.9 | 4.4 | - |
| smart_vs_dull / spoken / words_per_sentence | 1 | 1 | 10 | 7.5 | - |
| smart_vs_dull / spoken / rare_share | 1 | 1 | 0.1 | 0.0667 | - |
| smart_vs_dull / thought / word_len | 1 | 0 | 4.44 | - | - |
| smart_vs_dull / thought / words_per_sentence | 1 | 0 | 9 | - | - |
| smart_vs_dull / thought / rare_share | 1 | 0 | 0.111 | - | - |
| smart_vs_dull / aim / word_len | 1 | 1 | 4.44 | 4.44 | - |
| smart_vs_dull / aim / words_per_sentence | 1 | 1 | 9 | 9 | - |
| smart_vs_dull / aim / rare_share | 1 | 1 | 0.111 | 0 | - |
| sober_after_vs_drunk_before / spoken / word_len | 0 | 1 | - | 4.75 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 0 | 1 | - | 2 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 0 | 1 | - | 0 | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.62 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 8 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |
| smart_vs_dull / spoken / word_len | 2 | 2 | 4.12 | 3.86 | 2.02 |
| smart_vs_dull / spoken / words_per_sentence | 2 | 2 | 13 | 7.75 | 2.13 |
| smart_vs_dull / spoken / rare_share | 2 | 2 | 0.149 | 0.0455 | 1.3 |
| smart_vs_dull / thought / word_len | 2 | 3 | 4.5 | 4.46 | 0.075 |
| smart_vs_dull / thought / words_per_sentence | 2 | 3 | 8 | 6.33 | 3.54 |
| smart_vs_dull / thought / rare_share | 2 | 3 | 0.375 | 0.159 | 19.3 |
| smart_vs_dull / diary / word_len | 1 | 1 | 4 | 3.93 | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 1 | 11.7 | 6.75 | - |
| smart_vs_dull / diary / rare_share | 1 | 1 | 0.286 | 0.185 | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 4.5 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 8 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.125 | - | - |
| smart_vs_dull / aim / word_len | 1 | 1 | 3.55 | 3.71 | - |
| smart_vs_dull / aim / words_per_sentence | 1 | 1 | 11 | 7 | - |
| smart_vs_dull / aim / rare_share | 1 | 1 | 0.182 | 0.143 | - |
| sober_after_vs_drunk_before / spoken / word_len | 0 | 1 | - | 4.17 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 0 | 1 | - | 3 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 0 | 1 | - | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 2 | 1 | 3.86 | 3.62 | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 2 | 1 | 7 | 8 | - |
| sober_after_vs_drunk_before / thought / rare_share | 2 | 1 | 0.286 | 0 | - |
| sober_after_vs_drunk_before / diary / word_len | 1 | 0 | 4.24 | - | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 0 | 5.25 | - | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 0 | 0.238 | - | - |
| sober_after_vs_drunk_before / dream / word_len | 2 | 0 | 4.14 | - | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 0 | 0.571 | - | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.62 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 8 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |
| smart_vs_dull / spoken / word_len | 14 | 9 | 4.29 | 3.75 | 1.47 |
| smart_vs_dull / spoken / words_per_sentence | 14 | 9 | 11.1 | 7.83 | 1.15 |
| smart_vs_dull / spoken / rare_share | 14 | 9 | 0.148 | 0.126 | 0.232 |
| smart_vs_dull / thought / word_len | 20 | 13 | 4 | 4.4 | -0.598 |
| smart_vs_dull / thought / words_per_sentence | 20 | 13 | 6.75 | 6.62 | 0.051 |
| smart_vs_dull / thought / rare_share | 20 | 13 | 0.212 | 0.103 | 0.684 |
| smart_vs_dull / diary / word_len | 3 | 3 | 3.99 | 4.04 | -0.293 |
| smart_vs_dull / diary / words_per_sentence | 3 | 3 | 11.5 | 5.92 | 5.16 |
| smart_vs_dull / diary / rare_share | 3 | 3 | 0.256 | 0.213 | 1.74 |
| smart_vs_dull / dream / word_len | 6 | 0 | 4.41 | - | - |
| smart_vs_dull / dream / words_per_sentence | 6 | 0 | 8.33 | - | - |
| smart_vs_dull / dream / rare_share | 6 | 0 | 0.273 | - | - |
| smart_vs_dull / aim / word_len | 7 | 5 | 4.02 | 3.86 | 0.241 |
| smart_vs_dull / aim / words_per_sentence | 7 | 5 | 10.4 | 8.6 | 1.41 |
| smart_vs_dull / aim / rare_share | 7 | 5 | 0.111 | 0.0857 | 0.269 |
| sober_after_vs_drunk_before / spoken / word_len | 2 | 4 | 3.94 | 4.02 | -0.156 |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 2 | 4 | 6.5 | 2.5 | 2.18 |
| sober_after_vs_drunk_before / spoken / rare_share | 2 | 4 | 0.354 | 0 | 24 |
| sober_after_vs_drunk_before / thought / word_len | 5 | 7 | 3.73 | 4.53 | -1.43 |
| sober_after_vs_drunk_before / thought / words_per_sentence | 5 | 7 | 8 | 6 | 1.69 |
| sober_after_vs_drunk_before / thought / rare_share | 5 | 7 | 0.159 | 0.335 | -0.967 |
| sober_after_vs_drunk_before / diary / word_len | 1 | 1 | 4.24 | 4.15 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 1 | 5.25 | 5 | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 1 | 0.238 | 0.2 | - |
| sober_after_vs_drunk_before / dream / word_len | 4 | 2 | 4.5 | 4.5 | 0 |
| sober_after_vs_drunk_before / dream / words_per_sentence | 4 | 2 | 7 | 4 | - |
| sober_after_vs_drunk_before / dream / rare_share | 4 | 2 | 0.643 | 0.125 | 7.25 |
| sober_after_vs_drunk_before / aim / word_len | 1 | 4 | 3.3 | 3.58 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 1 | 4 | 10 | 8.75 | - |
| sober_after_vs_drunk_before / aim / rare_share | 1 | 4 | 0.1 | 0 | - |

## Flagged rows

0 violations, 28 review, 0 info (full list in the flags file).

| run | check | kind | severity | actor | text | why |
|---|---|---|---|---|---|---|
| s1x-off | age_gate | review | review | 10 | More attempted affection disrupts the necessary order. | weak:affection (spoken by a minor) |
| s1x-off | age_gate | review | review | 10 | More attempted affection disrupts the necessary order. | weak:affection (spoken by a minor) |
| s1x-off | age_gate | review | review | - | Day 3 saw Ana and Bram make another attempt at some sort of affection. Cy was sh | weak:affection (names a minor) |
| s1x-off | empty_refused_invented | invented_name | review | 1 | The first snowfall arrived, bringing a quiet hush to the halls. After the sun di | capitalised, not grounded: ['Widow'] |
| s1x-off | empty_refused_invented | refusal_weak | review | 4 | I can't stop worrying about Cy; they are hurt. | possible refusal phrase (may be in character) |
| s1x-off | loops_repeats | near_duplicate | review | 3 | I really just want something warm to eat right now. | per-actor actor 3/aim: repeats 'I just want something warm to eat right now.' (jaccard 0.9 |
| s1x-off | loops_repeats | exact_repeat | review | 7 | I just want to get this cooking done and move on. | per-kind aim: actors 8/7 repeat 'I just want to get this cooking done and move on.' (jacca |
| s1x-off | loops_repeats | exact_repeat | review | 5 | Ugh. Those two. | per-kind bystander: actors 3/5 repeat 'Ugh. Those two.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 4 | Keep me out of it. | per-kind bystander: actors 3/4 repeat 'Keep me out of it.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 5 | Ugh. Those two. | per-kind inner_reaction: actors 3/5 repeat 'Ugh. Those two.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 4 | Keep me out of it. | per-kind inner_reaction: actors 3/4 repeat 'Keep me out of it.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 2 | Raiders are at our gates; I fear for us all. | per-kind inner_concern: actors 1/2 repeat 'Raiders are at our gates; I fear for us all.' ( |
| s1x-off | loops_repeats | repeated_inner | review | 1 | i hope this food settles the worry in his chest. | mind 1 stored the same aim 2 times |
| s1x-off | loops_repeats | repeated_inner | review | 3 | i just want something that tastes like real food. | mind 3 stored the same aim 2 times |
| s1x-off | loops_repeats | repeated_inner | review | 7 | i just want this meal done so i can get back to work. | mind 7 stored the same aim 2 times |
| s3x-off | age_gate | review | review | 1 | The snow brought the dark, but the wedding felt warm. We christened the east hil | marriage:wedding (names a minor) |
| s3x-off | empty_refused_invented | invented_name | review | - | The day saw several conversations unfold between the residents. Ana attempted ro | capitalised, not grounded: ['Widow'] |
| s3x-off | empty_refused_invented | refusal_weak | review | 4 | I can't stop worrying about Cy; they are hurt. | possible refusal phrase (may be in character) |
| s3x-off | empty_refused_invented | invented_name | review | - | The day saw more exchanges between the residents. Ana and Bram engaged in furthe | capitalised, not grounded: ['Widow'] |
| s3x-off | empty_refused_invented | invented_name | review | 1 | The snow brought the dark, but the wedding felt warm. We christened the east hil | capitalised, not grounded: ['Widow'] |
| s3x-off | loops_repeats | near_duplicate | review | - | the arrival and departure of raiders | per-actor actor None/lore: repeats "Raiders' Run: the arrival and departure of raiders" (j |
| s3x-off | loops_repeats | exact_repeat | review | 2 | Raiders are at our gates; I fear for us all. | per-kind inner_concern: actors 1/2 repeat 'Raiders are at our gates; I fear for us all.' ( |
| s3x-off | loops_repeats | repeated_inner | review | 4 | i can't stop worrying about cy; they are hurt. | mind 4 stored the same concern 2 times |
| s2x-on | empty_refused_invented | invented_name | review | - | The morning saw a series of exchanges: Ana conversed with Bram, while Cy spoke w | capitalised, not grounded: ['Widow'] |
| s2x-on | empty_refused_invented | invented_name | review | - | Day 1 involved several brief exchanges between the residents, including Ana spea | capitalised, not grounded: ['Widow'] |
| s2x-on | empty_refused_invented | invented_name | review | - | Day One Shooting: Cy being shot | capitalised, not grounded: ['Shooting'] |
| s2x-on | empty_refused_invented | invented_name | review | 1 | The snow started falling while the sky darkened. We made a name for the east hil | capitalised, not grounded: ['Widow'] |
| s2x-on | loops_repeats | exact_repeat | review | 2 | The darkness feels permanent now. | per-kind inner_concern: actors 3/2 repeat 'The darkness feels permanent now.' (jaccard 1.0 |
