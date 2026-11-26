# Soak analysis

## Checks

| check | s1x-off | s3x-off | s2x-on | ALL |
|---|---|---|---|---|
| age gate violations (must be 0) | 0 | 0 | 0 | 0 |
| age gate review (benign romance words) | 0 | 0 | 0 | 0 |
| explicit text hits | 0 | 0 | 0 | 0 |
| explicit text soft hits | 0 | 0 | 0 | 0 |
| explicit text while adult off | 0 | 0 | 0 | 0 |
| canary in shown output | 0 | 0 | 0 | 0 |
| canary in raw call text | 0 | 0 | 0 | 0 |
| line ends with canary | 0 | 0 | 0 | 0 |
| empty shown text | 0 | 0 | 0 | 0 |
| refusal phrases | 0 | 0 | 0 | 0 |
| refusal phrases (weak, review) | 1 | 1 | 0 | 2 |
| invented-name candidates (review) | 4 | 4 | 1 | 9 |
| label / format leaks | 0 | 0 | 0 | 0 |
| exact repeats | 6 | 4 | 0 | 10 |
| near duplicates | 2 | 0 | 1 | 3 |
| conversation turn repeats | 0 | 0 | 0 | 0 |
| repeated aims/clouds in mind logs | 1 | 1 | 0 | 2 |
| over-length | 0 | 0 | 0 | 0 |
| drops | 9 | 20 | 4 | 33 |
| error/fallback call share | 0 | 0 | 0 | 0 |

## Run s1x-off

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | expired | 1 |
| - | nothing_new | 1 |
| minor_safety | chronicle | 1 |
| reflect | idle_preempted | 5 |
| unknown_name:Memory | thought | 1 |

Non-zero module counters: art.dropped=6, bystand.asked=17, bystand.shown=12, bystand.templated=12, group.ends.turn_cap=7, group.lines=27, group.rotations=13, grudges.faded=2, grudges.seeks=7, grudges.started=3, lease_log_counts.drop:actor_quota=1, lease_log_counts.drop:ambient_quota=2, lease_log_counts.drop:gap=8, lease_log_counts.expired:bystander:10=1, lease_log_counts.expired:bystander:3=1, lease_log_counts.expired:bystander:4=1, lease_log_counts.expired:bystander:7=1, lease_log_counts.expired:concern_resolved:4=1, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:injury:6=1, lease_log_counts.expired:injury:7=1, lease_log_counts.expired:level_up:5=2, lease_log_counts.expired:social:3=1, lease_log_counts.expired:social:5=4, lease_log_counts.expired:social:6=4, lease_log_counts.expired:social:7=4, lease_log_counts.expired:social:8=4, lease_log_counts.expired:social:9=1, lease_log_counts.expired:tend:4=2, lease_log_counts.expired:vanilla_social:1=2, lease_log_counts.lease:slot1:actor10:IDLE=39, lease_log_counts.lease:slot1:actor10:SOCIAL=5, lease_log_counts.lease:slot1:actor11:IDLE=19, lease_log_counts.lease:slot1:actor1:IDLE=53, lease_log_counts.lease:slot1:actor1:REACTIVE=4, lease_log_counts.lease:slot1:actor1:SOCIAL=7, lease_log_counts.lease:slot1:actor1:URGENT=2, lease_log_counts.lease:slot1:actor2:IDLE=44, lease_log_counts.lease:slot1:actor2:SOCIAL=2, lease_log_counts.lease:slot1:actor3:IDLE=46, lease_log_counts.lease:slot1:actor3:REACTIVE=7, lease_log_counts.lease:slot1:actor4:IDLE=28, lease_log_counts.lease:slot1:actor4:REACTIVE=6, lease_log_counts.lease:slot1:actor4:SOCIAL=1, lease_log_counts.lease:slot1:actor5:IDLE=46, lease_log_counts.lease:slot1:actor5:PLAYER=2, lease_log_counts.lease:slot1:actor5:REACTIVE=1, lease_log_counts.lease:slot1:actor5:SOCIAL=2, lease_log_counts.lease:slot1:actor6:IDLE=49, lease_log_counts.lease:slot1:actor6:PLAYER=2, lease_log_counts.lease:slot1:actor7:IDLE=31, lease_log_counts.lease:slot1:actor7:REACTIVE=1, lease_log_counts.lease:slot1:actor8:IDLE=24, lease_log_counts.lease:slot1:actor8:PLAYER=2, lease_log_counts.lease:slot1:actor8:REACTIVE=1, lease_log_counts.lease:slot1:actor9:IDLE=45, lease_log_counts.lease:slot1:actor9:SOCIAL=1, lease_log_counts.lease:slot2:actor1:PLAYER=2, lease_log_counts.lease:slot2:actor1:REACTIVE=1, lease_log_counts.lease:slot2:actor1:SOCIAL=3, lease_log_counts.lease:slot2:actor2:REACTIVE=2, lease_log_counts.lease:slot2:actor2:SOCIAL=7, lease_log_counts.lease:slot2:actor2:URGENT=2, lease_log_counts.lease:slot2:actor3:REACTIVE=2, lease_log_counts.lease:slot2:actor3:SOCIAL=4, lease_log_counts.lease:slot2:actor4:SOCIAL=1, lease_log_counts.lease:slot2:actor5:REACTIVE=2, lease_log_counts.lease:slot2:actor6:SOCIAL=1, lease_log_counts.lease:slot2:actor7:PLAYER=2, lease_log_counts.lease:slot2:actor8:REACTIVE=1, lease_log_counts.lease:slot2:actor9:PLAYER=2, lease_log_counts.lease:slot2:actor9:REACTIVE=2, lease_log_counts.lease:slot2:actor9:SOCIAL=1, lease_log_counts.merged:concern_resolved=1, lease_log_counts.merged:first_cue=1, lease_log_counts.release:slot1:conversation_end=9, lease_log_counts.release:slot1:done=20, lease_log_counts.release:slot1:gathering_end=7, lease_log_counts.release:slot1:idle_done=419, lease_log_counts.release:slot1:preempted=5, lease_log_counts.release:slot1:rotated=10, lease_log_counts.release:slot2:conversation_end=9, lease_log_counts.release:slot2:done=16, lease_log_counts.release:slot2:gathering_end=7, lease_log_counts.release:slot2:rotated=3, loop.checked=51, wants.acted=4, wants.nothing=324, wants.runs=328

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 12 | 0.563 | 0.695 | 0.799 |
| chronicle | 5 | 1.45 | 1.8 | 1.92 |
| chronicle_summary | 1 | 1.25 | 1.25 | 1.25 |
| diary | 20 | 1.04 | 1.4 | 1.86 |
| dream | 9 | 0.423 | 0.491 | 0.536 |
| intent | 59 | 0.513 | 0.67 | 1.3 |
| line | 86 | 1.25 | 2.22 | 2.55 |
| lore | 3 | 0.331 | 0.35 | 0.355 |
| memory | 14 | 0.39 | 0.477 | 0.529 |
| narrator | 5 | 0.844 | 1.52 | 1.55 |
| recap | 1 | 0.621 | 0.621 | 0.621 |
| reflect | 22 | 5.52 | 8.12 | 10 |
| relation | 14 | 0.353 | 0.445 | 0.469 |
| thought | 40 | 0.423 | 0.463 | 0.481 |
| ALL | 291 | 0.607 | 2.25 | 10 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 12 | 287 | 3577 | 368 | 4049 | 20.5 | 246 | 0.531 | 1692 | 3607 |
| chronicle | 5 | 315 | 1944 | 195 | 1852 | 67 | 322 | 0.488 | 1921 | 3750 |
| chronicle_summary | 1 | 169 | 169 | 1 | 1 | 60 | 60 | 0.006 | 1448 | 1456 |
| diary | 20 | 500 | 10073 | 224 | 4499 | 41.5 | 865 | 0.309 | 2112 | 3056 |
| dream | 9 | 404 | 3632 | 178 | 1758 | 10 | 97 | 0.326 | 1876 | 2784 |
| intent | 59 | 97 | 5723 | 2085 | 124027 | 16 | 945 | 0.956 | 689 | 15622 |
| line | 86 | 2043 | 129730 | 0 | 51962 | 15 | 1296 | 0.286 | 1596 | 2236 |
| lore | 3 | 202 | 603 | 184 | 552 | 11 | 24 | 0.478 | 1506 | 2884 |
| memory | 14 | 49 | 716 | 1962 | 27785 | 15 | 218 | 0.975 | 730 | 29079 |
| narrator | 5 | 27 | 93 | 34 | 214 | 8 | 42 | 0.697 | 34.7 | 115 |
| recap | 1 | 221 | 221 | 200 | 200 | 24 | 24 | 0.475 | 1469 | 2798 |
| reflect | 22 | 882 | 18378 | 206 | 8391 | 269 | 6160 | 0.313 | 2138 | 3115 |
| relation | 14 | 16 | 224 | 1987 | 28249 | 15 | 204 | 0.992 | 318 | 40377 |
| thought | 40 | 409 | 16143 | 177 | 7860 | 10 | 396 | 0.327 | 1889 | 2809 |
| ALL | 291 | 405 | 191226 | 256 | 261399 | 16 | 10899 | 0.578 | 1573 | 3723 |

Prompt tokens per lease/slot: 0: 6607 over 27 calls (244.7/call), 1: 119517 over 188 calls (635.7/call), 2: 65102 over 76 calls (856.6/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 22 | 262 | 4.16 | 11.7 | 0.145 | 0.542 | 0.767 |
| Ana |  | aim | 3 | 35 | 3.94 | 11.7 | 0.094 | 0.771 | - |
| Ana |  | thought | 15 | 94 | 3.91 | 6.07 | 0.128 | 0.532 | 0.607 |
| Ana |  | diary | 2 | 80 | 4.12 | 10 | 0.174 | 0.7 | 0.784 |
| Ana |  | dream | 4 | 30 | 4.75 | 7.5 | 0.188 | 0.5 | - |
| Bram |  | spoken | 14 | 153 | 4.29 | 5.46 | 0.231 | 0.582 | 0.752 |
| Bram |  | aim | 6 | 70 | 3.4 | 11.7 | 0.102 | 0.443 | 0.531 |
| Bram |  | thought | 9 | 65 | 4.81 | 7.22 | 0.364 | 0.462 | 0.551 |
| Bram |  | diary | 2 | 70 | 3.84 | 8.75 | 0.143 | 0.757 | 0.815 |
| Bram |  | dream | 2 | 12 | 4.67 | 6 | 0.333 | 0.5 | - |
| Cy |  | thought | 13 | 96 | 4.07 | 7.15 | 0.155 | 0.458 | 0.606 |
| Cy |  | spoken | 13 | 165 | 3.75 | 8.47 | 0.068 | 0.552 | 0.744 |
| Cy |  | aim | 3 | 32 | 3.94 | 10.7 | 0.094 | 0.625 | - |
| Cy |  | diary | 2 | 96 | 4.14 | 8.89 | 0.213 | 0.719 | 0.825 |
| Cy |  | dream | 2 | 16 | 4.75 | 8 | 0.625 | 0.5 | - |
| Dee |  | thought | 12 | 78 | 4.33 | 6.25 | 0.13 | 0.487 | 0.604 |
| Dee |  | spoken | 7 | 55 | 4.71 | 5.41 | 0.117 | 0.545 | 0.583 |
| Dee |  | aim | 2 | 20 | 3.45 | 10 | 0 | 0.75 | - |
| Dee |  | diary | 2 | 61 | 4.26 | 7.62 | 0.279 | 0.721 | 0.768 |
| Kit | A-smart | spoken | 7 | 71 | 4.41 | 9.14 | 0.067 | 0.817 | 0.852 |
| Kit | A-smart | aim | 3 | 30 | 4.23 | 10 | 0.104 | 0.833 | - |
| Kit | A-smart | thought | 13 | 93 | 4.47 | 6.69 | 0.251 | 0.462 | 0.541 |
| Kit | A-smart | diary | 2 | 80 | 4.39 | 11.9 | 0.255 | 0.688 | 0.763 |
| Kit | A-smart | dream | 2 | 14 | 5.14 | 7 | 0.429 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 28 | 4 | 6.5 | 0.131 | 0.821 | - |
| Lou | A-dull | aim | 2 | 18 | 3.65 | 9 | 0 | 0.778 | - |
| Lou | A-dull | thought | 13 | 77 | 3.73 | 5.69 | 0.044 | 0.416 | 0.541 |
| Lou | A-dull | diary | 2 | 55 | 4.08 | 5.5 | 0.167 | 0.8 | 0.807 |
| Max | B-sober | spoken | 3 | 32 | 4.77 | 10.7 | 0.306 | 0.844 | - |
| Max | B-sober | aim | 2 | 19 | 3.12 | 9.5 | 0.045 | 0.737 | - |
| Max | B-sober | thought | 9 | 82 | 4.31 | 9.11 | 0.259 | 0.439 | 0.552 |
| Max | B-sober | diary | 2 | 85 | 3.98 | 10.6 | 0.204 | 0.671 | 0.771 |
| Max | B-sober | dream | 2 | 20 | 4.2 | 10 | 0.2 | 0.4 | - |
| Noa | B-drunk | spoken | 3 | 25 | 4.06 | 7.83 | 0.071 | 0.88 | - |
| Noa | B-drunk | thought | 9 | 61 | 4.84 | 6.78 | 0.493 | 0.475 | 0.46 |
| Noa | B-drunk | aim | 2 | 19 | 3.35 | 9.5 | 0 | 0.789 | - |
| Noa | B-drunk | diary | 2 | 55 | 4.76 | 6.88 | 0.205 | 0.782 | 0.777 |
| Noa | B-drunk | dream | 2 | 14 | 4.43 | 7 | 0.143 | 0.5 | - |
| Tam |  | spoken | 6 | 47 | 4.25 | 5.75 | 0.249 | 0.809 | - |
| Tam |  | aim | 3 | 33 | 3.53 | 11 | 0.042 | 0.697 | - |
| Tam |  | thought | 9 | 63 | 4.1 | 7 | 0.315 | 0.46 | 0.551 |
| Tam |  | diary | 2 | 58 | 3.86 | 6.58 | 0.171 | 0.776 | 0.8 |
| Tam |  | dream | 2 | 22 | 4.18 | 5.5 | 0.182 | 0.5 | - |
| Uma |  | spoken | 6 | 60 | 4.58 | 4.61 | 0.133 | 0.75 | 0.776 |
| Uma |  | aim | 2 | 25 | 3.73 | 12.5 | 0.067 | 0.8 | - |
| Uma |  | thought | 7 | 65 | 5.97 | 9.29 | 0.438 | 0.569 | 0.703 |
| Uma |  | diary | 2 | 50 | 4.88 | 7.29 | 0.22 | 0.7 | 0.7 |
| Uma |  | dream | 2 | 12 | 5.33 | 6 | 0.5 | 0.5 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 7 | 3 | 4.41 | 4 | 0.549 |
| smart_vs_dull / spoken / words_per_sentence | 7 | 3 | 9.14 | 6.5 | 1.06 |
| smart_vs_dull / spoken / rare_share | 7 | 3 | 0.0668 | 0.131 | -0.55 |
| smart_vs_dull / thought / word_len | 13 | 13 | 4.47 | 3.73 | 1.11 |
| smart_vs_dull / thought / words_per_sentence | 13 | 13 | 6.69 | 5.69 | 0.294 |
| smart_vs_dull / thought / rare_share | 13 | 13 | 0.251 | 0.044 | 1.21 |
| smart_vs_dull / diary / word_len | 2 | 2 | 4.39 | 4.08 | 1.18 |
| smart_vs_dull / diary / words_per_sentence | 2 | 2 | 11.9 | 5.5 | 1.88 |
| smart_vs_dull / diary / rare_share | 2 | 2 | 0.255 | 0.167 | 1.72 |
| smart_vs_dull / dream / word_len | 2 | 0 | 5.14 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.429 | - | - |
| smart_vs_dull / aim / word_len | 3 | 2 | 4.23 | 3.65 | 1.33 |
| smart_vs_dull / aim / words_per_sentence | 3 | 2 | 10 | 9 | 0.548 |
| smart_vs_dull / aim / rare_share | 3 | 2 | 0.104 | 0 | 1.27 |
| sober_after_vs_drunk_before / spoken / word_len | 2 | 1 | 4.25 | 3.67 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 2 | 1 | 11 | 1.5 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 2 | 1 | 0.107 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 4 | 5 | 4.56 | 5.06 | -0.459 |
| sober_after_vs_drunk_before / thought / words_per_sentence | 4 | 5 | 7 | 6.6 | 0.174 |
| sober_after_vs_drunk_before / thought / rare_share | 4 | 5 | 0.521 | 0.471 | 0.191 |
| sober_after_vs_drunk_before / diary / word_len | 1 | 1 | 5.06 | 4.47 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 1 | 9 | 4.75 | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 1 | 0.306 | 0.105 | - |
| sober_after_vs_drunk_before / dream / word_len | 0 | 2 | - | 4.43 | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 0 | 2 | - | 7 | - |
| sober_after_vs_drunk_before / dream / rare_share | 0 | 2 | - | 0.143 | - |
| sober_after_vs_drunk_before / aim / word_len | 1 | 1 | 3.25 | 3.45 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 1 | 1 | 8 | 11 | - |
| sober_after_vs_drunk_before / aim / rare_share | 1 | 1 | 0 | 0 | - |

## Run s3x-off

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | expired | 1 |
| - | minor_safety | 1 |
| - | preempted | 4 |
| - | reflect_failed | 1 |
| minor_safety | diary | 1 |
| reflect | idle_preempted | 6 |
| unknown_name:Beacon | art | 1 |
| unknown_name:Rim | art | 2 |
| wants | idle_preempted | 3 |

Non-zero module counters: art.dropped=6, bystand.asked=4, group.ends.dropped=1, group.ends.over=1, group.ends.turn_cap=3, group.lines=6, grudges.seeks=2, grudges.started=3, lease_log_counts.drop:gap=8, lease_log_counts.expired:bystander:3=1, lease_log_counts.expired:bystander:4=1, lease_log_counts.expired:concern_resolved:4=2, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:first_cue:9=1, lease_log_counts.expired:gathering:1=1, lease_log_counts.expired:gathering:3=1, lease_log_counts.expired:injury:3=2, lease_log_counts.expired:injury:4=2, lease_log_counts.expired:injury:5=1, lease_log_counts.expired:injury:6=2, lease_log_counts.expired:injury:7=2, lease_log_counts.expired:injury:8=2, lease_log_counts.expired:social:1=3, lease_log_counts.expired:social:10=2, lease_log_counts.expired:social:2=3, lease_log_counts.expired:social:3=1, lease_log_counts.expired:social:4=2, lease_log_counts.expired:social:5=5, lease_log_counts.expired:social:6=2, lease_log_counts.expired:social:7=1, lease_log_counts.expired:social:8=4, lease_log_counts.expired:social:9=2, lease_log_counts.expired:tend:4=2, lease_log_counts.expired:thought:1=2, lease_log_counts.expired:vanilla_social:1=2, lease_log_counts.lease:slot1:actor10:IDLE=10, lease_log_counts.lease:slot1:actor11:IDLE=1, lease_log_counts.lease:slot1:actor1:IDLE=20, lease_log_counts.lease:slot1:actor1:REACTIVE=2, lease_log_counts.lease:slot1:actor1:SOCIAL=2, lease_log_counts.lease:slot1:actor1:URGENT=2, lease_log_counts.lease:slot1:actor2:IDLE=19, lease_log_counts.lease:slot1:actor3:IDLE=18, lease_log_counts.lease:slot1:actor3:REACTIVE=2, lease_log_counts.lease:slot1:actor4:IDLE=12, lease_log_counts.lease:slot1:actor4:REACTIVE=2, lease_log_counts.lease:slot1:actor5:IDLE=19, lease_log_counts.lease:slot1:actor5:PLAYER=2, lease_log_counts.lease:slot1:actor5:SOCIAL=2, lease_log_counts.lease:slot1:actor6:IDLE=15, lease_log_counts.lease:slot1:actor6:PLAYER=2, lease_log_counts.lease:slot1:actor7:IDLE=9, lease_log_counts.lease:slot1:actor8:IDLE=9, lease_log_counts.lease:slot1:actor8:PLAYER=2, lease_log_counts.lease:slot1:actor9:IDLE=10, lease_log_counts.lease:slot2:actor1:PLAYER=2, lease_log_counts.lease:slot2:actor1:SOCIAL=1, lease_log_counts.lease:slot2:actor2:REACTIVE=2, lease_log_counts.lease:slot2:actor2:SOCIAL=2, lease_log_counts.lease:slot2:actor2:URGENT=2, lease_log_counts.lease:slot2:actor3:REACTIVE=1, lease_log_counts.lease:slot2:actor6:SOCIAL=1, lease_log_counts.lease:slot2:actor7:PLAYER=2, lease_log_counts.lease:slot2:actor7:SOCIAL=1, lease_log_counts.lease:slot2:actor9:PLAYER=2, lease_log_counts.lease:slot2:actor9:REACTIVE=1, lease_log_counts.merged:concern_resolved=1, lease_log_counts.merged:first_cue=1, lease_log_counts.merged:vanilla_social=2, lease_log_counts.release:slot1:conversation_end=1, lease_log_counts.release:slot1:done=10, lease_log_counts.release:slot1:gathering_end=4, lease_log_counts.release:slot1:idle_done=132, lease_log_counts.release:slot1:preempted=12, lease_log_counts.release:slot2:conversation_end=1, lease_log_counts.release:slot2:done=9, lease_log_counts.release:slot2:gathering_end=4, lease_log_counts.release:slot2:preempted=3, loop.checked=11, wants.acted=10, wants.nothing=66, wants.runs=80

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 8 | 0.598 | 0.79 | 0.807 |
| chronicle | 3 | 1.59 | 1.62 | 1.62 |
| diary | 20 | 0.997 | 1.15 | 1.25 |
| dream | 9 | 0.414 | 0.475 | 0.526 |
| intent | 28 | 0.637 | 4.53 | 4.69 |
| line | 35 | 1.95 | 2.7 | 3.62 |
| lore | 2 | 0.245 | 0.291 | 0.303 |
| narrator | 6 | 0.435 | 0.793 | 0.934 |
| recap | 1 | 0.747 | 0.747 | 0.747 |
| reflect | 17 | 5.41 | 7.29 | 7.84 |
| thought | 10 | 0.409 | 0.451 | 0.467 |
| ALL | 139 | 0.918 | 4.62 | 7.84 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 8 | 284 | 2259 | 368 | 2944 | 22 | 181 | 0.566 | 1680 | 3869 |
| chronicle | 3 | 319 | 1556 | 195 | 585 | 69 | 203 | 0.273 | 2064 | 2840 |
| diary | 20 | 468 | 9243 | 245 | 4732 | 40.5 | 784 | 0.339 | 2132 | 3223 |
| dream | 9 | 401 | 3595 | 178 | 1758 | 11 | 97 | 0.328 | 1912 | 2847 |
| intent | 28 | 97 | 19050 | 2007 | 41269 | 14 | 412 | 0.684 | 1336 | 4231 |
| line | 35 | 1980 | 52128 | 0 | 19804 | 16 | 549 | 0.275 | 1408 | 1943 |
| lore | 2 | 208 | 417 | 184 | 368 | 5 | 10 | 0.469 | 1459 | 2746 |
| narrator | 6 | 16 | 100 | 45 | 270 | 8.5 | 53 | 0.73 | 107 | 395 |
| recap | 1 | 224 | 224 | 200 | 200 | 28 | 28 | 0.472 | 1267 | 2398 |
| reflect | 17 | 567 | 10881 | 425 | 8614 | 252 | 4423 | 0.442 | 2084 | 3734 |
| thought | 10 | 410 | 4089 | 177 | 1926 | 9 | 99 | 0.32 | 1929 | 2837 |
| ALL | 139 | 438 | 103542 | 206 | 82470 | 17 | 6839 | 0.443 | 1515 | 2722 |

Prompt tokens per lease/slot: 0: 4556 over 20 calls (227.8/call), 1: 62455 over 89 calls (701.7/call), 2: 36531 over 30 calls (1217.7/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 8 | 101 | 4.29 | 10.9 | 0.142 | 0.653 | 0.787 |
| Ana |  | aim | 3 | 31 | 4.16 | 10.3 | 0.063 | 0.774 | - |
| Ana |  | thought | 3 | 23 | 4.18 | 7.67 | 0.19 | 0.652 | - |
| Ana |  | diary | 2 | 70 | 4.17 | 11.7 | 0.184 | 0.786 | 0.83 |
| Ana |  | dream | 4 | 26 | 4.17 | 6.5 | 0.071 | 0.5 | - |
| Bram |  | spoken | 5 | 59 | 3.9 | 6.43 | 0.179 | 0.797 | 0.848 |
| Bram |  | aim | 2 | 19 | 3.41 | 9.5 | 0.106 | 0.684 | - |
| Bram |  | thought | 2 | 14 | 4 | 7 | 0.286 | 0.5 | - |
| Bram |  | diary | 2 | 76 | 4.18 | 11.3 | 0.187 | 0.763 | 0.81 |
| Bram |  | dream | 2 | 10 | 5.6 | 5 | 0.6 | 0.5 | - |
| Cy |  | thought | 3 | 24 | 4.71 | 8 | 0.25 | 0.667 | - |
| Cy |  | diary | 2 | 59 | 4.33 | 7.38 | 0.258 | 0.712 | 0.734 |
| Cy |  | spoken | 2 | 20 | 4.36 | 7.75 | 0.157 | 0.95 | - |
| Cy |  | dream | 2 | 14 | 5.29 | 7 | 0.286 | 0.5 | - |
| Cy |  | aim | 1 | 8 | 3.38 | 8 | 0 | 1 | - |
| Dee |  | thought | 2 | 18 | 5.11 | 9 | 0.333 | 0.444 | - |
| Dee |  | diary | 2 | 69 | 4.44 | 10.3 | 0.266 | 0.667 | 0.682 |
| Dee |  | spoken | 1 | 11 | 4 | 3.67 | 0 | 0.545 | - |
| Kit | A-smart | spoken | 3 | 39 | 4.11 | 13 | 0.167 | 0.769 | - |
| Kit | A-smart | aim | 2 | 18 | 3.83 | 9 | 0.111 | 0.833 | - |
| Kit | A-smart | thought | 3 | 23 | 3.58 | 7.67 | 0.228 | 0.609 | - |
| Kit | A-smart | diary | 2 | 69 | 4.43 | 10 | 0.261 | 0.768 | 0.789 |
| Kit | A-smart | dream | 2 | 14 | 5.29 | 7 | 0.571 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 31 | 3.99 | 6.17 | 0.111 | 0.839 | - |
| Lou | A-dull | thought | 3 | 21 | 3.81 | 7 | 0.148 | 0.714 | - |
| Lou | A-dull | aim | 1 | 9 | 3.11 | 9 | 0.111 | 1 | - |
| Lou | A-dull | diary | 2 | 49 | 3.9 | 6.12 | 0.145 | 0.714 | - |
| Max | B-sober | spoken | 2 | 21 | 4.82 | 8.75 | 0.143 | 0.952 | - |
| Max | B-sober | aim | 1 | 9 | 3.33 | 9 | 0.111 | 1 | - |
| Max | B-sober | thought | 2 | 18 | 4.22 | 9 | 0.333 | 0.5 | - |
| Max | B-sober | diary | 2 | 63 | 4.13 | 7.88 | 0.226 | 0.73 | 0.779 |
| Max | B-sober | dream | 2 | 16 | 4.62 | 4 | 0.625 | 0.5 | - |
| Noa | B-drunk | spoken | 2 | 17 | 4.55 | 7.5 | 0.077 | 0.941 | - |
| Noa | B-drunk | aim | 1 | 10 | 3.4 | 10 | 0 | 0.9 | - |
| Noa | B-drunk | thought | 2 | 12 | 4.33 | 6 | 0.167 | 0.5 | - |
| Noa | B-drunk | diary | 2 | 48 | 4.36 | 6 | 0.254 | 0.75 | - |
| Noa | B-drunk | dream | 2 | 16 | 3.75 | 4 | 0.5 | 0.5 | - |
| Tam |  | spoken | 2 | 23 | 3.76 | 11.5 | 0.136 | 0.826 | - |
| Tam |  | aim | 2 | 20 | 3.33 | 10 | 0.101 | 0.6 | - |
| Tam |  | thought | 2 | 18 | 3.89 | 9 | 0.111 | 0.5 | - |
| Tam |  | diary | 2 | 55 | 3.7 | 6.88 | 0.127 | 0.691 | 0.7 |
| Tam |  | dream | 2 | 16 | 4.25 | 8 | 0.375 | 0.5 | - |
| Uma |  | thought | 2 | 14 | 4.43 | 7 | 0.286 | 0.5 | - |
| Uma |  | dream | 2 | 14 | 5.57 | 7 | 0.429 | 0.5 | - |
| Uma |  | diary | 1 | 37 | 4.46 | 12.3 | 0.297 | 0.865 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 3 | 3 | 4.11 | 3.99 | 0.312 |
| smart_vs_dull / spoken / words_per_sentence | 3 | 3 | 13 | 6.17 | 3.63 |
| smart_vs_dull / spoken / rare_share | 3 | 3 | 0.167 | 0.111 | 0.32 |
| smart_vs_dull / thought / word_len | 3 | 3 | 3.58 | 3.81 | -0.495 |
| smart_vs_dull / thought / words_per_sentence | 3 | 3 | 7.67 | 7 | 0.453 |
| smart_vs_dull / thought / rare_share | 3 | 3 | 0.228 | 0.148 | 1.06 |
| smart_vs_dull / diary / word_len | 2 | 2 | 4.43 | 3.9 | 2.83 |
| smart_vs_dull / diary / words_per_sentence | 2 | 2 | 10 | 6.12 | 3.63 |
| smart_vs_dull / diary / rare_share | 2 | 2 | 0.261 | 0.145 | 3.72 |
| smart_vs_dull / dream / word_len | 2 | 0 | 5.29 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.571 | - | - |
| smart_vs_dull / aim / word_len | 2 | 1 | 3.83 | 3.11 | - |
| smart_vs_dull / aim / words_per_sentence | 2 | 1 | 9 | 9 | - |
| smart_vs_dull / aim / rare_share | 2 | 1 | 0.111 | 0.111 | - |
| sober_after_vs_drunk_before / spoken / word_len | 1 | 1 | 3.85 | 5.25 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 1 | 1 | 13 | 2 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 1 | 1 | 0.154 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 0 | 2 | - | 4.33 | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 0 | 2 | - | 6 | - |
| sober_after_vs_drunk_before / thought / rare_share | 0 | 2 | - | 0.167 | - |
| sober_after_vs_drunk_before / diary / word_len | 1 | 1 | 4.82 | 3.9 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 1 | 7 | 5 | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 1 | 0.357 | 0.15 | - |
| sober_after_vs_drunk_before / dream / word_len | 0 | 2 | - | 3.75 | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 0 | 2 | - | 4 | - |
| sober_after_vs_drunk_before / dream / rare_share | 0 | 2 | - | 0.5 | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.4 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 10 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |

## Run s2x-on

### Drops by kind

| kind | reason | n |
|---|---|---|
| reflect | idle_preempted | 2 |
| unknown_name:Ana | diary | 1 |
| unknown_name:Bram | diary | 1 |

Non-zero module counters: bystand.asked=3, bystand.shown=1, bystand.templated=1, group.ends.over=2, group.ends.turn_cap=2, group.lines=11, group.rotations=5, grudges.started=2, lease_log_counts.drop:gap=3, lease_log_counts.expired:bystander:10=1, lease_log_counts.expired:bystander:4=1, lease_log_counts.expired:first_cue:2=1, lease_log_counts.expired:first_cue:3=1, lease_log_counts.expired:first_cue:5=1, lease_log_counts.expired:first_cue:9=1, lease_log_counts.expired:gathering:3=1, lease_log_counts.expired:injury:3=1, lease_log_counts.expired:injury:4=1, lease_log_counts.expired:injury:5=1, lease_log_counts.expired:injury:6=1, lease_log_counts.expired:injury:7=1, lease_log_counts.expired:injury:8=1, lease_log_counts.expired:level_up:5=1, lease_log_counts.expired:social:1=1, lease_log_counts.expired:social:10=1, lease_log_counts.expired:social:2=2, lease_log_counts.expired:social:4=1, lease_log_counts.expired:social:5=3, lease_log_counts.expired:social:6=2, lease_log_counts.expired:social:7=2, lease_log_counts.expired:social:8=2, lease_log_counts.expired:social:9=1, lease_log_counts.expired:tend:4=1, lease_log_counts.lease:slot1:actor10:IDLE=10, lease_log_counts.lease:slot1:actor11:IDLE=5, lease_log_counts.lease:slot1:actor1:AMBIENT=1, lease_log_counts.lease:slot1:actor1:IDLE=16, lease_log_counts.lease:slot1:actor1:REACTIVE=1, lease_log_counts.lease:slot1:actor1:SOCIAL=1, lease_log_counts.lease:slot1:actor1:URGENT=1, lease_log_counts.lease:slot1:actor2:IDLE=15, lease_log_counts.lease:slot1:actor2:SOCIAL=1, lease_log_counts.lease:slot1:actor3:IDLE=17, lease_log_counts.lease:slot1:actor3:REACTIVE=2, lease_log_counts.lease:slot1:actor3:SOCIAL=1, lease_log_counts.lease:slot1:actor4:IDLE=10, lease_log_counts.lease:slot1:actor4:REACTIVE=1, lease_log_counts.lease:slot1:actor5:IDLE=17, lease_log_counts.lease:slot1:actor5:PLAYER=1, lease_log_counts.lease:slot1:actor6:IDLE=16, lease_log_counts.lease:slot1:actor6:PLAYER=1, lease_log_counts.lease:slot1:actor6:SOCIAL=1, lease_log_counts.lease:slot1:actor7:IDLE=10, lease_log_counts.lease:slot1:actor8:IDLE=7, lease_log_counts.lease:slot1:actor8:PLAYER=1, lease_log_counts.lease:slot1:actor9:IDLE=13, lease_log_counts.lease:slot1:actor9:SOCIAL=1, lease_log_counts.lease:slot2:actor10:SOCIAL=2, lease_log_counts.lease:slot2:actor1:PLAYER=1, lease_log_counts.lease:slot2:actor1:SOCIAL=1, lease_log_counts.lease:slot2:actor2:REACTIVE=1, lease_log_counts.lease:slot2:actor2:SOCIAL=1, lease_log_counts.lease:slot2:actor2:URGENT=1, lease_log_counts.lease:slot2:actor5:SOCIAL=1, lease_log_counts.lease:slot2:actor7:PLAYER=1, lease_log_counts.lease:slot2:actor9:PLAYER=1, lease_log_counts.lease:slot2:actor9:REACTIVE=1, lease_log_counts.merged:first_cue=1, lease_log_counts.release:slot1:done=6, lease_log_counts.release:slot1:gathering_end=4, lease_log_counts.release:slot1:idle_done=134, lease_log_counts.release:slot1:preempted=3, lease_log_counts.release:slot1:rotated=3, lease_log_counts.release:slot2:done=5, lease_log_counts.release:slot2:gathering_end=3, lease_log_counts.release:slot2:preempted=1, lease_log_counts.release:slot2:rotated=2, loop.checked=15, wants.nothing=100, wants.runs=100

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 6 | 0.579 | 1.6 | 1.77 |
| chronicle | 1 | 1.42 | 1.42 | 1.42 |
| diary | 11 | 1.07 | 1.18 | 1.21 |
| dream | 6 | 0.481 | 0.51 | 0.521 |
| intent | 15 | 0.574 | 0.654 | 0.766 |
| line | 26 | 1.29 | 2.13 | 2.27 |
| lore | 1 | 0.155 | 0.155 | 0.155 |
| memory | 2 | 0.409 | 0.418 | 0.42 |
| narrator | 1 | 0.32 | 0.32 | 0.32 |
| reflect | 7 | 5.23 | 6.24 | 6.29 |
| relation | 2 | 0.85 | 1.11 | 1.18 |
| thought | 10 | 0.394 | 0.447 | 0.464 |
| ALL | 88 | 0.721 | 2.17 | 6.29 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 6 | 244 | 1468 | 368 | 2208 | 15.5 | 110 | 0.601 | 966 | 2419 |
| chronicle | 1 | 513 | 513 | 1 | 1 | 42 | 42 | 0.002 | 973 | 975 |
| diary | 11 | 488 | 4848 | 261 | 2995 | 44 | 458 | 0.382 | 2118 | 3426 |
| dream | 6 | 402 | 2400 | 178 | 1146 | 13.5 | 76 | 0.323 | 1869 | 2761 |
| intent | 15 | 97 | 1455 | 1974 | 29665 | 14 | 234 | 0.953 | 685 | 14654 |
| line | 26 | 1933 | 38518 | 0 | 13897 | 16 | 405 | 0.265 | 1650 | 2245 |
| lore | 1 | 181 | 181 | 184 | 184 | 2 | 2 | 0.504 | 1640 | 3308 |
| memory | 2 | 54 | 108 | 1861 | 3722 | 16 | 32 | 0.972 | 722 | 25590 |
| narrator | 1 | 5 | 5 | 56 | 56 | 7 | 7 | 0.918 | 135 | 1643 |
| reflect | 7 | 564 | 4864 | 429 | 2757 | 242 | 1712 | 0.362 | 2112 | 3309 |
| relation | 2 | 16 | 32 | 1897 | 3794 | 18.5 | 37 | 0.992 | 299 | 35754 |
| thought | 10 | 392 | 3216 | 177 | 2700 | 9.5 | 95 | 0.456 | 1820 | 3348 |
| ALL | 88 | 416 | 57608 | 262 | 63125 | 16 | 3210 | 0.523 | 1620 | 3394 |

Prompt tokens per lease/slot: 0: 2167 over 9 calls (240.8/call), 1: 35384 over 58 calls (610.1/call), 2: 20057 over 21 calls (955.1/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 7 | 86 | 4.34 | 12.3 | 0.106 | 0.733 | 0.827 |
| Ana |  | aim | 1 | 10 | 3.9 | 10 | 0.1 | 1 | - |
| Ana |  | thought | 2 | 18 | 5.11 | 9 | 0.333 | 0.444 | - |
| Ana |  | diary | 1 | 40 | 3.75 | 13.3 | 0.15 | 0.775 | - |
| Ana |  | dream | 2 | 18 | 4.56 | 9 | 0.333 | 0.5 | - |
| Bram |  | spoken | 5 | 52 | 4.23 | 6.3 | 0.119 | 0.827 | 0.847 |
| Bram |  | aim | 1 | 15 | 3.8 | 15 | 0.133 | 0.8 | - |
| Bram |  | thought | 2 | 16 | 5.5 | 8 | 0.25 | 0.5 | - |
| Bram |  | diary | 1 | 35 | 3.77 | 8.75 | 0.143 | 0.829 | - |
| Bram |  | dream | 2 | 16 | 4.88 | 8 | 0.625 | 0.5 | - |
| Cy |  | thought | 4 | 16 | 3.92 | 4 | 0.083 | 0.438 | - |
| Cy |  | spoken | 3 | 33 | 4.17 | 7.17 | 0.21 | 0.788 | - |
| Cy |  | aim | 1 | 10 | 3.6 | 10 | 0.2 | 1 | - |
| Cy |  | diary | 1 | 39 | 4.03 | 9.75 | 0.256 | 0.769 | - |
| Cy |  | dream | 2 | 16 | 4.5 | 8 | 0.25 | 0.438 | - |
| Dee |  | thought | 2 | 12 | 4.5 | 6 | 0.167 | 0.5 | - |
| Dee |  | diary | 1 | 40 | 4.08 | 10 | 0.15 | 0.9 | - |
| Kit | A-smart | spoken | 2 | 25 | 3.92 | 12.5 | 0.125 | 0.92 | - |
| Kit | A-smart | thought | 3 | 28 | 4.8 | 9.33 | 0.363 | 0.607 | - |
| Kit | A-smart | aim | 1 | 10 | 4.4 | 10 | 0.2 | 1 | - |
| Kit | A-smart | diary | 1 | 35 | 4.4 | 11.7 | 0.343 | 0.8 | - |
| Kit | A-smart | dream | 2 | 14 | 5.29 | 7 | 0.429 | 0.5 | - |
| Lou | A-dull | spoken | 2 | 24 | 3.62 | 9 | 0.083 | 0.917 | - |
| Lou | A-dull | aim | 1 | 11 | 3.46 | 11 | 0.182 | 1 | - |
| Lou | A-dull | thought | 2 | 14 | 4.57 | 7 | 0.143 | 0.5 | - |
| Max | B-sober | spoken | 1 | 13 | 4.92 | 6.5 | 0.231 | 0.923 | - |
| Max | B-sober | aim | 1 | 12 | 3.17 | 12 | 0 | 1 | - |
| Max | B-sober | thought | 2 | 18 | 4.67 | 9 | 0.333 | 0.5 | - |
| Max | B-sober | diary | 1 | 37 | 4.24 | 9.25 | 0.216 | 0.865 | - |
| Noa | B-drunk | spoken | 1 | 5 | 5.2 | 2.5 | 0 | 1 | - |
| Noa | B-drunk | aim | 1 | 10 | 3.4 | 10 | 0 | 0.9 | - |
| Noa | B-drunk | thought | 2 | 12 | 4.67 | 6 | 0.333 | 0.5 | - |
| Noa | B-drunk | diary | 1 | 44 | 4.36 | 14.7 | 0.295 | 0.795 | - |
| Noa | B-drunk | dream | 2 | 22 | 4.64 | 11 | 0.455 | 0.5 | - |
| Tam |  | spoken | 3 | 36 | 3.95 | 10.2 | 0.114 | 0.806 | - |
| Tam |  | aim | 2 | 18 | 3.7 | 9 | 0.225 | 0.667 | - |
| Tam |  | thought | 2 | 16 | 3.88 | 8 | 0 | 0.438 | - |
| Tam |  | diary | 1 | 25 | 3.56 | 6.25 | 0.12 | 0.84 | - |
| Uma |  | spoken | 2 | 20 | 3.95 | 5 | 0.15 | 0.9 | - |
| Uma |  | thought | 2 | 14 | 5.71 | 7 | 0.429 | 0.5 | - |
| Uma |  | diary | 1 | 32 | 4.16 | 10.7 | 0.219 | 0.938 | - |
| Uma |  | dream | 2 | 16 | 5 | 8 | 0.625 | 0.5 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 2 | 2 | 3.92 | 3.62 | 1.42 |
| smart_vs_dull / spoken / words_per_sentence | 2 | 2 | 12.5 | 9 | 1.15 |
| smart_vs_dull / spoken / rare_share | 2 | 2 | 0.125 | 0.0833 | 0.277 |
| smart_vs_dull / thought / word_len | 3 | 2 | 4.8 | 4.57 | 0.808 |
| smart_vs_dull / thought / words_per_sentence | 3 | 2 | 9.33 | 7 | 4.95 |
| smart_vs_dull / thought / rare_share | 3 | 2 | 0.363 | 0.143 | 1.91 |
| smart_vs_dull / diary / word_len | 1 | 0 | 4.4 | - | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 0 | 11.7 | - | - |
| smart_vs_dull / diary / rare_share | 1 | 0 | 0.343 | - | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 5.29 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.429 | - | - |
| smart_vs_dull / aim / word_len | 1 | 1 | 4.4 | 3.45 | - |
| smart_vs_dull / aim / words_per_sentence | 1 | 1 | 10 | 11 | - |
| smart_vs_dull / aim / rare_share | 1 | 1 | 0.2 | 0.182 | - |
| sober_after_vs_drunk_before / spoken / word_len | 0 | 1 | - | 5.2 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 0 | 1 | - | 2.5 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 0 | 1 | - | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 2 | 0 | 4.67 | - | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 2 | 0 | 6 | - | - |
| sober_after_vs_drunk_before / thought / rare_share | 2 | 0 | 0.333 | - | - |
| sober_after_vs_drunk_before / diary / word_len | 1 | 0 | 4.36 | - | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 0 | 14.7 | - | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 0 | 0.295 | - | - |
| sober_after_vs_drunk_before / dream / word_len | 2 | 0 | 4.64 | - | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 0 | 11 | - | - |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 0 | 0.455 | - | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.4 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 10 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |

## Run ALL

### Drops by kind

| kind | reason | n |
|---|---|---|
| - | expired | 2 |
| - | minor_safety | 1 |
| - | nothing_new | 1 |
| - | preempted | 4 |
| - | reflect_failed | 1 |
| minor_safety | chronicle | 1 |
| minor_safety | diary | 1 |
| reflect | idle_preempted | 13 |
| unknown_name:Ana | diary | 1 |
| unknown_name:Beacon | art | 1 |
| unknown_name:Bram | diary | 1 |
| unknown_name:Memory | thought | 1 |
| unknown_name:Rim | art | 2 |
| wants | idle_preempted | 3 |

Non-zero module counters: art.dropped=12, bystand.asked=24, bystand.shown=13, bystand.templated=13, group.ends.dropped=1, group.ends.over=3, group.ends.turn_cap=12, group.lines=44, group.rotations=18, grudges.faded=2, grudges.seeks=9, grudges.started=8, lease_log_counts.drop:actor_quota=1, lease_log_counts.drop:ambient_quota=2, lease_log_counts.drop:gap=19, lease_log_counts.expired:bystander:10=2, lease_log_counts.expired:bystander:3=2, lease_log_counts.expired:bystander:4=3, lease_log_counts.expired:bystander:7=1, lease_log_counts.expired:concern_resolved:4=3, lease_log_counts.expired:first_cue:2=3, lease_log_counts.expired:first_cue:3=1, lease_log_counts.expired:first_cue:5=1, lease_log_counts.expired:first_cue:9=2, lease_log_counts.expired:gathering:1=1, lease_log_counts.expired:gathering:3=2, lease_log_counts.expired:injury:3=3, lease_log_counts.expired:injury:4=3, lease_log_counts.expired:injury:5=2, lease_log_counts.expired:injury:6=4, lease_log_counts.expired:injury:7=4, lease_log_counts.expired:injury:8=3, lease_log_counts.expired:level_up:5=3, lease_log_counts.expired:social:1=4, lease_log_counts.expired:social:10=3, lease_log_counts.expired:social:2=5, lease_log_counts.expired:social:3=2, lease_log_counts.expired:social:4=3, lease_log_counts.expired:social:5=12, lease_log_counts.expired:social:6=8, lease_log_counts.expired:social:7=7, lease_log_counts.expired:social:8=10, lease_log_counts.expired:social:9=4, lease_log_counts.expired:tend:4=5, lease_log_counts.expired:thought:1=2, lease_log_counts.expired:vanilla_social:1=4, lease_log_counts.lease:slot1:actor10:IDLE=59, lease_log_counts.lease:slot1:actor10:SOCIAL=5, lease_log_counts.lease:slot1:actor11:IDLE=25, lease_log_counts.lease:slot1:actor1:AMBIENT=1, lease_log_counts.lease:slot1:actor1:IDLE=89, lease_log_counts.lease:slot1:actor1:REACTIVE=7, lease_log_counts.lease:slot1:actor1:SOCIAL=10, lease_log_counts.lease:slot1:actor1:URGENT=5, lease_log_counts.lease:slot1:actor2:IDLE=78, lease_log_counts.lease:slot1:actor2:SOCIAL=3, lease_log_counts.lease:slot1:actor3:IDLE=81, lease_log_counts.lease:slot1:actor3:REACTIVE=11, lease_log_counts.lease:slot1:actor3:SOCIAL=1, lease_log_counts.lease:slot1:actor4:IDLE=50, lease_log_counts.lease:slot1:actor4:REACTIVE=9, lease_log_counts.lease:slot1:actor4:SOCIAL=1, lease_log_counts.lease:slot1:actor5:IDLE=82, lease_log_counts.lease:slot1:actor5:PLAYER=5, lease_log_counts.lease:slot1:actor5:REACTIVE=1, lease_log_counts.lease:slot1:actor5:SOCIAL=4, lease_log_counts.lease:slot1:actor6:IDLE=80, lease_log_counts.lease:slot1:actor6:PLAYER=5, lease_log_counts.lease:slot1:actor6:SOCIAL=1, lease_log_counts.lease:slot1:actor7:IDLE=50, lease_log_counts.lease:slot1:actor7:REACTIVE=1, lease_log_counts.lease:slot1:actor8:IDLE=40, lease_log_counts.lease:slot1:actor8:PLAYER=5, lease_log_counts.lease:slot1:actor8:REACTIVE=1, lease_log_counts.lease:slot1:actor9:IDLE=68, lease_log_counts.lease:slot1:actor9:SOCIAL=2, lease_log_counts.lease:slot2:actor10:SOCIAL=2, lease_log_counts.lease:slot2:actor1:PLAYER=5, lease_log_counts.lease:slot2:actor1:REACTIVE=1, lease_log_counts.lease:slot2:actor1:SOCIAL=5, lease_log_counts.lease:slot2:actor2:REACTIVE=5, lease_log_counts.lease:slot2:actor2:SOCIAL=10, lease_log_counts.lease:slot2:actor2:URGENT=5, lease_log_counts.lease:slot2:actor3:REACTIVE=3, lease_log_counts.lease:slot2:actor3:SOCIAL=4, lease_log_counts.lease:slot2:actor4:SOCIAL=1, lease_log_counts.lease:slot2:actor5:REACTIVE=2, lease_log_counts.lease:slot2:actor5:SOCIAL=1, lease_log_counts.lease:slot2:actor6:SOCIAL=2, lease_log_counts.lease:slot2:actor7:PLAYER=5, lease_log_counts.lease:slot2:actor7:SOCIAL=1, lease_log_counts.lease:slot2:actor8:REACTIVE=1, lease_log_counts.lease:slot2:actor9:PLAYER=5, lease_log_counts.lease:slot2:actor9:REACTIVE=4, lease_log_counts.lease:slot2:actor9:SOCIAL=1, lease_log_counts.merged:concern_resolved=2, lease_log_counts.merged:first_cue=3, lease_log_counts.merged:vanilla_social=2, lease_log_counts.release:slot1:conversation_end=10, lease_log_counts.release:slot1:done=36, lease_log_counts.release:slot1:gathering_end=15, lease_log_counts.release:slot1:idle_done=685, lease_log_counts.release:slot1:preempted=20, lease_log_counts.release:slot1:rotated=13, lease_log_counts.release:slot2:conversation_end=10, lease_log_counts.release:slot2:done=30, lease_log_counts.release:slot2:gathering_end=14, lease_log_counts.release:slot2:preempted=4, lease_log_counts.release:slot2:rotated=5, loop.checked=77, wants.acted=14, wants.nothing=490, wants.runs=508

### Latency per call kind (s)

| kind | n | median | p90 | max |
|---|---|---|---|---|
| art | 26 | 0.563 | 0.803 | 1.77 |
| chronicle | 9 | 1.5 | 1.68 | 1.92 |
| chronicle_summary | 1 | 1.25 | 1.25 | 1.25 |
| diary | 51 | 1.03 | 1.22 | 1.86 |
| dream | 24 | 0.429 | 0.514 | 0.536 |
| intent | 102 | 0.549 | 0.77 | 4.69 |
| line | 147 | 1.3 | 2.28 | 3.62 |
| lore | 6 | 0.245 | 0.343 | 0.355 |
| memory | 16 | 0.395 | 0.464 | 0.529 |
| narrator | 12 | 0.435 | 1.43 | 1.55 |
| recap | 2 | 0.684 | 0.735 | 0.747 |
| reflect | 46 | 5.45 | 7.34 | 10 |
| relation | 16 | 0.368 | 0.494 | 1.18 |
| thought | 60 | 0.418 | 0.463 | 0.481 |
| ALL | 518 | 0.665 | 3.65 | 10 |

error calls 0 (share 0); llm_error drops 0; fallbacks counter 0; refused counter 0

### Tokens per call kind

| kind | n | prompt med | prompt tot | cache med | cache tot | pred med | pred tot | cache hit | raw pf t/s | eff pf t/s |
|---|---|---|---|---|---|---|---|---|---|---|
| art | 26 | 247 | 7304 | 368 | 9201 | 20 | 537 | 0.557 | 1467 | 3315 |
| chronicle | 9 | 319 | 4013 | 195 | 2438 | 67 | 567 | 0.378 | 1750 | 2813 |
| chronicle_summary | 1 | 169 | 169 | 1 | 1 | 60 | 60 | 0.006 | 1448 | 1456 |
| diary | 51 | 489 | 24164 | 228 | 12226 | 41 | 2107 | 0.336 | 2121 | 3194 |
| dream | 24 | 402 | 9627 | 178 | 4662 | 11 | 270 | 0.326 | 1888 | 2802 |
| intent | 102 | 97 | 26228 | 2061 | 194961 | 15 | 1591 | 0.881 | 1062 | 8960 |
| line | 147 | 2008 | 220376 | 0 | 85663 | 16 | 2250 | 0.28 | 1556 | 2161 |
| lore | 6 | 202 | 1201 | 184 | 1104 | 5 | 36 | 0.479 | 1508 | 2893 |
| memory | 16 | 49 | 824 | 1953 | 31507 | 15 | 250 | 0.975 | 729 | 28617 |
| narrator | 12 | 16 | 198 | 45 | 540 | 8 | 102 | 0.732 | 54.2 | 202 |
| recap | 2 | 222 | 445 | 200 | 400 | 26 | 52 | 0.473 | 1360 | 2582 |
| reflect | 46 | 790 | 34123 | 316 | 19762 | 258 | 12295 | 0.367 | 2117 | 3343 |
| relation | 16 | 16 | 256 | 1978 | 32043 | 15 | 241 | 0.992 | 315 | 39768 |
| thought | 60 | 404 | 23448 | 177 | 12486 | 10 | 590 | 0.347 | 1886 | 2891 |
| ALL | 518 | 413 | 352376 | 255 | 406994 | 16 | 20948 | 0.536 | 1563 | 3368 |

Prompt tokens per lease/slot: 0: 13330 over 56 calls (238.0/call), 1: 217356 over 335 calls (648.8/call), 2: 121690 over 127 calls (958.2/call)

(prompt = uncached prompt_n; cache hit = cache_n/(prompt_n+cache_n); eff pf = (prompt_n+cache_n)/prompt_s)

### Register per pawn

Common-word list: agent core/common_words.py (function-word + everyday floor list, not a 5000-word frequency list) (947 words). rare share = words not in the common list (floor list, so inflated vs a 5000-word list); pawn names excluded; canary strings removed; d = Cohen's d (group1 - group2, pooled sd) over per-line values; ttr/mattr50 are on pooled text (mattr50 needs >= 50 tokens)

| pawn | pair | category | lines | words | word len | words/sent | rare share | TTR | MATTR50 |
|---|---|---|---|---|---|---|---|---|---|
| Ana |  | spoken | 22 | 262 | 4.16 | 11.7 | 0.145 | 0.542 | 0.767 |
| Ana |  | aim | 3 | 35 | 3.94 | 11.7 | 0.094 | 0.771 | - |
| Ana |  | thought | 15 | 94 | 3.91 | 6.07 | 0.128 | 0.532 | 0.607 |
| Ana |  | diary | 2 | 80 | 4.12 | 10 | 0.174 | 0.7 | 0.784 |
| Ana |  | dream | 4 | 30 | 4.75 | 7.5 | 0.188 | 0.5 | - |
| Bram |  | spoken | 14 | 153 | 4.29 | 5.46 | 0.231 | 0.582 | 0.752 |
| Bram |  | aim | 6 | 70 | 3.4 | 11.7 | 0.102 | 0.443 | 0.531 |
| Bram |  | thought | 9 | 65 | 4.81 | 7.22 | 0.364 | 0.462 | 0.551 |
| Bram |  | diary | 2 | 70 | 3.84 | 8.75 | 0.143 | 0.757 | 0.815 |
| Bram |  | dream | 2 | 12 | 4.67 | 6 | 0.333 | 0.5 | - |
| Cy |  | thought | 13 | 96 | 4.07 | 7.15 | 0.155 | 0.458 | 0.606 |
| Cy |  | spoken | 13 | 165 | 3.75 | 8.47 | 0.068 | 0.552 | 0.744 |
| Cy |  | aim | 3 | 32 | 3.94 | 10.7 | 0.094 | 0.625 | - |
| Cy |  | diary | 2 | 96 | 4.14 | 8.89 | 0.213 | 0.719 | 0.825 |
| Cy |  | dream | 2 | 16 | 4.75 | 8 | 0.625 | 0.5 | - |
| Dee |  | thought | 12 | 78 | 4.33 | 6.25 | 0.13 | 0.487 | 0.604 |
| Dee |  | spoken | 7 | 55 | 4.71 | 5.41 | 0.117 | 0.545 | 0.583 |
| Dee |  | aim | 2 | 20 | 3.45 | 10 | 0 | 0.75 | - |
| Dee |  | diary | 2 | 61 | 4.26 | 7.62 | 0.279 | 0.721 | 0.768 |
| Kit | A-smart | spoken | 7 | 71 | 4.41 | 9.14 | 0.067 | 0.817 | 0.852 |
| Kit | A-smart | aim | 3 | 30 | 4.23 | 10 | 0.104 | 0.833 | - |
| Kit | A-smart | thought | 13 | 93 | 4.47 | 6.69 | 0.251 | 0.462 | 0.541 |
| Kit | A-smart | diary | 2 | 80 | 4.39 | 11.9 | 0.255 | 0.688 | 0.763 |
| Kit | A-smart | dream | 2 | 14 | 5.14 | 7 | 0.429 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 28 | 4 | 6.5 | 0.131 | 0.821 | - |
| Lou | A-dull | aim | 2 | 18 | 3.65 | 9 | 0 | 0.778 | - |
| Lou | A-dull | thought | 13 | 77 | 3.73 | 5.69 | 0.044 | 0.416 | 0.541 |
| Lou | A-dull | diary | 2 | 55 | 4.08 | 5.5 | 0.167 | 0.8 | 0.807 |
| Max | B-sober | spoken | 3 | 32 | 4.77 | 10.7 | 0.306 | 0.844 | - |
| Max | B-sober | aim | 2 | 19 | 3.12 | 9.5 | 0.045 | 0.737 | - |
| Max | B-sober | thought | 9 | 82 | 4.31 | 9.11 | 0.259 | 0.439 | 0.552 |
| Max | B-sober | diary | 2 | 85 | 3.98 | 10.6 | 0.204 | 0.671 | 0.771 |
| Max | B-sober | dream | 2 | 20 | 4.2 | 10 | 0.2 | 0.4 | - |
| Noa | B-drunk | spoken | 3 | 25 | 4.06 | 7.83 | 0.071 | 0.88 | - |
| Noa | B-drunk | thought | 9 | 61 | 4.84 | 6.78 | 0.493 | 0.475 | 0.46 |
| Noa | B-drunk | aim | 2 | 19 | 3.35 | 9.5 | 0 | 0.789 | - |
| Noa | B-drunk | diary | 2 | 55 | 4.76 | 6.88 | 0.205 | 0.782 | 0.777 |
| Noa | B-drunk | dream | 2 | 14 | 4.43 | 7 | 0.143 | 0.5 | - |
| Tam |  | spoken | 6 | 47 | 4.25 | 5.75 | 0.249 | 0.809 | - |
| Tam |  | aim | 3 | 33 | 3.53 | 11 | 0.042 | 0.697 | - |
| Tam |  | thought | 9 | 63 | 4.1 | 7 | 0.315 | 0.46 | 0.551 |
| Tam |  | diary | 2 | 58 | 3.86 | 6.58 | 0.171 | 0.776 | 0.8 |
| Tam |  | dream | 2 | 22 | 4.18 | 5.5 | 0.182 | 0.5 | - |
| Uma |  | spoken | 6 | 60 | 4.58 | 4.61 | 0.133 | 0.75 | 0.776 |
| Uma |  | aim | 2 | 25 | 3.73 | 12.5 | 0.067 | 0.8 | - |
| Uma |  | thought | 7 | 65 | 5.97 | 9.29 | 0.438 | 0.569 | 0.703 |
| Uma |  | diary | 2 | 50 | 4.88 | 7.29 | 0.22 | 0.7 | 0.7 |
| Uma |  | dream | 2 | 12 | 5.33 | 6 | 0.5 | 0.5 | - |
| Ana |  | spoken | 7 | 86 | 4.34 | 12.3 | 0.106 | 0.733 | 0.827 |
| Ana |  | aim | 1 | 10 | 3.9 | 10 | 0.1 | 1 | - |
| Ana |  | thought | 2 | 18 | 5.11 | 9 | 0.333 | 0.444 | - |
| Ana |  | diary | 1 | 40 | 3.75 | 13.3 | 0.15 | 0.775 | - |
| Ana |  | dream | 2 | 18 | 4.56 | 9 | 0.333 | 0.5 | - |
| Bram |  | spoken | 5 | 52 | 4.23 | 6.3 | 0.119 | 0.827 | 0.847 |
| Bram |  | aim | 1 | 15 | 3.8 | 15 | 0.133 | 0.8 | - |
| Bram |  | thought | 2 | 16 | 5.5 | 8 | 0.25 | 0.5 | - |
| Bram |  | diary | 1 | 35 | 3.77 | 8.75 | 0.143 | 0.829 | - |
| Bram |  | dream | 2 | 16 | 4.88 | 8 | 0.625 | 0.5 | - |
| Cy |  | thought | 4 | 16 | 3.92 | 4 | 0.083 | 0.438 | - |
| Cy |  | spoken | 3 | 33 | 4.17 | 7.17 | 0.21 | 0.788 | - |
| Cy |  | aim | 1 | 10 | 3.6 | 10 | 0.2 | 1 | - |
| Cy |  | diary | 1 | 39 | 4.03 | 9.75 | 0.256 | 0.769 | - |
| Cy |  | dream | 2 | 16 | 4.5 | 8 | 0.25 | 0.438 | - |
| Dee |  | thought | 2 | 12 | 4.5 | 6 | 0.167 | 0.5 | - |
| Dee |  | diary | 1 | 40 | 4.08 | 10 | 0.15 | 0.9 | - |
| Kit | A-smart | spoken | 2 | 25 | 3.92 | 12.5 | 0.125 | 0.92 | - |
| Kit | A-smart | thought | 3 | 28 | 4.8 | 9.33 | 0.363 | 0.607 | - |
| Kit | A-smart | aim | 1 | 10 | 4.4 | 10 | 0.2 | 1 | - |
| Kit | A-smart | diary | 1 | 35 | 4.4 | 11.7 | 0.343 | 0.8 | - |
| Kit | A-smart | dream | 2 | 14 | 5.29 | 7 | 0.429 | 0.5 | - |
| Lou | A-dull | spoken | 2 | 24 | 3.62 | 9 | 0.083 | 0.917 | - |
| Lou | A-dull | aim | 1 | 11 | 3.46 | 11 | 0.182 | 1 | - |
| Lou | A-dull | thought | 2 | 14 | 4.57 | 7 | 0.143 | 0.5 | - |
| Max | B-sober | spoken | 1 | 13 | 4.92 | 6.5 | 0.231 | 0.923 | - |
| Max | B-sober | aim | 1 | 12 | 3.17 | 12 | 0 | 1 | - |
| Max | B-sober | thought | 2 | 18 | 4.67 | 9 | 0.333 | 0.5 | - |
| Max | B-sober | diary | 1 | 37 | 4.24 | 9.25 | 0.216 | 0.865 | - |
| Noa | B-drunk | spoken | 1 | 5 | 5.2 | 2.5 | 0 | 1 | - |
| Noa | B-drunk | aim | 1 | 10 | 3.4 | 10 | 0 | 0.9 | - |
| Noa | B-drunk | thought | 2 | 12 | 4.67 | 6 | 0.333 | 0.5 | - |
| Noa | B-drunk | diary | 1 | 44 | 4.36 | 14.7 | 0.295 | 0.795 | - |
| Noa | B-drunk | dream | 2 | 22 | 4.64 | 11 | 0.455 | 0.5 | - |
| Tam |  | spoken | 3 | 36 | 3.95 | 10.2 | 0.114 | 0.806 | - |
| Tam |  | aim | 2 | 18 | 3.7 | 9 | 0.225 | 0.667 | - |
| Tam |  | thought | 2 | 16 | 3.88 | 8 | 0 | 0.438 | - |
| Tam |  | diary | 1 | 25 | 3.56 | 6.25 | 0.12 | 0.84 | - |
| Uma |  | spoken | 2 | 20 | 3.95 | 5 | 0.15 | 0.9 | - |
| Uma |  | thought | 2 | 14 | 5.71 | 7 | 0.429 | 0.5 | - |
| Uma |  | diary | 1 | 32 | 4.16 | 10.7 | 0.219 | 0.938 | - |
| Uma |  | dream | 2 | 16 | 5 | 8 | 0.625 | 0.5 | - |
| Ana |  | spoken | 8 | 101 | 4.29 | 10.9 | 0.142 | 0.653 | 0.787 |
| Ana |  | aim | 3 | 31 | 4.16 | 10.3 | 0.063 | 0.774 | - |
| Ana |  | thought | 3 | 23 | 4.18 | 7.67 | 0.19 | 0.652 | - |
| Ana |  | diary | 2 | 70 | 4.17 | 11.7 | 0.184 | 0.786 | 0.83 |
| Ana |  | dream | 4 | 26 | 4.17 | 6.5 | 0.071 | 0.5 | - |
| Bram |  | spoken | 5 | 59 | 3.9 | 6.43 | 0.179 | 0.797 | 0.848 |
| Bram |  | aim | 2 | 19 | 3.41 | 9.5 | 0.106 | 0.684 | - |
| Bram |  | thought | 2 | 14 | 4 | 7 | 0.286 | 0.5 | - |
| Bram |  | diary | 2 | 76 | 4.18 | 11.3 | 0.187 | 0.763 | 0.81 |
| Bram |  | dream | 2 | 10 | 5.6 | 5 | 0.6 | 0.5 | - |
| Cy |  | thought | 3 | 24 | 4.71 | 8 | 0.25 | 0.667 | - |
| Cy |  | diary | 2 | 59 | 4.33 | 7.38 | 0.258 | 0.712 | 0.734 |
| Cy |  | spoken | 2 | 20 | 4.36 | 7.75 | 0.157 | 0.95 | - |
| Cy |  | dream | 2 | 14 | 5.29 | 7 | 0.286 | 0.5 | - |
| Cy |  | aim | 1 | 8 | 3.38 | 8 | 0 | 1 | - |
| Dee |  | thought | 2 | 18 | 5.11 | 9 | 0.333 | 0.444 | - |
| Dee |  | diary | 2 | 69 | 4.44 | 10.3 | 0.266 | 0.667 | 0.682 |
| Dee |  | spoken | 1 | 11 | 4 | 3.67 | 0 | 0.545 | - |
| Kit | A-smart | spoken | 3 | 39 | 4.11 | 13 | 0.167 | 0.769 | - |
| Kit | A-smart | aim | 2 | 18 | 3.83 | 9 | 0.111 | 0.833 | - |
| Kit | A-smart | thought | 3 | 23 | 3.58 | 7.67 | 0.228 | 0.609 | - |
| Kit | A-smart | diary | 2 | 69 | 4.43 | 10 | 0.261 | 0.768 | 0.789 |
| Kit | A-smart | dream | 2 | 14 | 5.29 | 7 | 0.571 | 0.5 | - |
| Lou | A-dull | spoken | 3 | 31 | 3.99 | 6.17 | 0.111 | 0.839 | - |
| Lou | A-dull | thought | 3 | 21 | 3.81 | 7 | 0.148 | 0.714 | - |
| Lou | A-dull | aim | 1 | 9 | 3.11 | 9 | 0.111 | 1 | - |
| Lou | A-dull | diary | 2 | 49 | 3.9 | 6.12 | 0.145 | 0.714 | - |
| Max | B-sober | spoken | 2 | 21 | 4.82 | 8.75 | 0.143 | 0.952 | - |
| Max | B-sober | aim | 1 | 9 | 3.33 | 9 | 0.111 | 1 | - |
| Max | B-sober | thought | 2 | 18 | 4.22 | 9 | 0.333 | 0.5 | - |
| Max | B-sober | diary | 2 | 63 | 4.13 | 7.88 | 0.226 | 0.73 | 0.779 |
| Max | B-sober | dream | 2 | 16 | 4.62 | 4 | 0.625 | 0.5 | - |
| Noa | B-drunk | spoken | 2 | 17 | 4.55 | 7.5 | 0.077 | 0.941 | - |
| Noa | B-drunk | aim | 1 | 10 | 3.4 | 10 | 0 | 0.9 | - |
| Noa | B-drunk | thought | 2 | 12 | 4.33 | 6 | 0.167 | 0.5 | - |
| Noa | B-drunk | diary | 2 | 48 | 4.36 | 6 | 0.254 | 0.75 | - |
| Noa | B-drunk | dream | 2 | 16 | 3.75 | 4 | 0.5 | 0.5 | - |
| Tam |  | spoken | 2 | 23 | 3.76 | 11.5 | 0.136 | 0.826 | - |
| Tam |  | aim | 2 | 20 | 3.33 | 10 | 0.101 | 0.6 | - |
| Tam |  | thought | 2 | 18 | 3.89 | 9 | 0.111 | 0.5 | - |
| Tam |  | diary | 2 | 55 | 3.7 | 6.88 | 0.127 | 0.691 | 0.7 |
| Tam |  | dream | 2 | 16 | 4.25 | 8 | 0.375 | 0.5 | - |
| Uma |  | thought | 2 | 14 | 4.43 | 7 | 0.286 | 0.5 | - |
| Uma |  | dream | 2 | 14 | 5.57 | 7 | 0.429 | 0.5 | - |
| Uma |  | diary | 1 | 37 | 4.46 | 12.3 | 0.297 | 0.865 | - |

### Pair contrasts (Cohen's d, group1 - group2)

| contrast / category / metric | n1 | n2 | mean1 | mean2 | d |
|---|---|---|---|---|---|
| smart_vs_dull / spoken / word_len | 7 | 3 | 4.41 | 4 | 0.549 |
| smart_vs_dull / spoken / words_per_sentence | 7 | 3 | 9.14 | 6.5 | 1.06 |
| smart_vs_dull / spoken / rare_share | 7 | 3 | 0.0668 | 0.131 | -0.55 |
| smart_vs_dull / thought / word_len | 13 | 13 | 4.47 | 3.73 | 1.11 |
| smart_vs_dull / thought / words_per_sentence | 13 | 13 | 6.69 | 5.69 | 0.294 |
| smart_vs_dull / thought / rare_share | 13 | 13 | 0.251 | 0.044 | 1.21 |
| smart_vs_dull / diary / word_len | 2 | 2 | 4.39 | 4.08 | 1.18 |
| smart_vs_dull / diary / words_per_sentence | 2 | 2 | 11.9 | 5.5 | 1.88 |
| smart_vs_dull / diary / rare_share | 2 | 2 | 0.255 | 0.167 | 1.72 |
| smart_vs_dull / dream / word_len | 2 | 0 | 5.14 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.429 | - | - |
| smart_vs_dull / aim / word_len | 3 | 2 | 4.23 | 3.65 | 1.33 |
| smart_vs_dull / aim / words_per_sentence | 3 | 2 | 10 | 9 | 0.548 |
| smart_vs_dull / aim / rare_share | 3 | 2 | 0.104 | 0 | 1.27 |
| sober_after_vs_drunk_before / spoken / word_len | 2 | 1 | 4.25 | 3.67 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 2 | 1 | 11 | 1.5 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 2 | 1 | 0.107 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 4 | 5 | 4.56 | 5.06 | -0.459 |
| sober_after_vs_drunk_before / thought / words_per_sentence | 4 | 5 | 7 | 6.6 | 0.174 |
| sober_after_vs_drunk_before / thought / rare_share | 4 | 5 | 0.521 | 0.471 | 0.191 |
| sober_after_vs_drunk_before / diary / word_len | 1 | 1 | 5.06 | 4.47 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 1 | 9 | 4.75 | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 1 | 0.306 | 0.105 | - |
| sober_after_vs_drunk_before / dream / word_len | 0 | 2 | - | 4.43 | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 0 | 2 | - | 7 | - |
| sober_after_vs_drunk_before / dream / rare_share | 0 | 2 | - | 0.143 | - |
| sober_after_vs_drunk_before / aim / word_len | 1 | 1 | 3.25 | 3.45 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 1 | 1 | 8 | 11 | - |
| sober_after_vs_drunk_before / aim / rare_share | 1 | 1 | 0 | 0 | - |
| smart_vs_dull / spoken / word_len | 3 | 3 | 4.11 | 3.99 | 0.312 |
| smart_vs_dull / spoken / words_per_sentence | 3 | 3 | 13 | 6.17 | 3.63 |
| smart_vs_dull / spoken / rare_share | 3 | 3 | 0.167 | 0.111 | 0.32 |
| smart_vs_dull / thought / word_len | 3 | 3 | 3.58 | 3.81 | -0.495 |
| smart_vs_dull / thought / words_per_sentence | 3 | 3 | 7.67 | 7 | 0.453 |
| smart_vs_dull / thought / rare_share | 3 | 3 | 0.228 | 0.148 | 1.06 |
| smart_vs_dull / diary / word_len | 2 | 2 | 4.43 | 3.9 | 2.83 |
| smart_vs_dull / diary / words_per_sentence | 2 | 2 | 10 | 6.12 | 3.63 |
| smart_vs_dull / diary / rare_share | 2 | 2 | 0.261 | 0.145 | 3.72 |
| smart_vs_dull / dream / word_len | 2 | 0 | 5.29 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.571 | - | - |
| smart_vs_dull / aim / word_len | 2 | 1 | 3.83 | 3.11 | - |
| smart_vs_dull / aim / words_per_sentence | 2 | 1 | 9 | 9 | - |
| smart_vs_dull / aim / rare_share | 2 | 1 | 0.111 | 0.111 | - |
| sober_after_vs_drunk_before / spoken / word_len | 1 | 1 | 3.85 | 5.25 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 1 | 1 | 13 | 2 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 1 | 1 | 0.154 | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 0 | 2 | - | 4.33 | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 0 | 2 | - | 6 | - |
| sober_after_vs_drunk_before / thought / rare_share | 0 | 2 | - | 0.167 | - |
| sober_after_vs_drunk_before / diary / word_len | 1 | 1 | 4.82 | 3.9 | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 1 | 7 | 5 | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 1 | 0.357 | 0.15 | - |
| sober_after_vs_drunk_before / dream / word_len | 0 | 2 | - | 3.75 | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 0 | 2 | - | 4 | - |
| sober_after_vs_drunk_before / dream / rare_share | 0 | 2 | - | 0.5 | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.4 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 10 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |
| smart_vs_dull / spoken / word_len | 2 | 2 | 3.92 | 3.62 | 1.42 |
| smart_vs_dull / spoken / words_per_sentence | 2 | 2 | 12.5 | 9 | 1.15 |
| smart_vs_dull / spoken / rare_share | 2 | 2 | 0.125 | 0.0833 | 0.277 |
| smart_vs_dull / thought / word_len | 3 | 2 | 4.8 | 4.57 | 0.808 |
| smart_vs_dull / thought / words_per_sentence | 3 | 2 | 9.33 | 7 | 4.95 |
| smart_vs_dull / thought / rare_share | 3 | 2 | 0.363 | 0.143 | 1.91 |
| smart_vs_dull / diary / word_len | 1 | 0 | 4.4 | - | - |
| smart_vs_dull / diary / words_per_sentence | 1 | 0 | 11.7 | - | - |
| smart_vs_dull / diary / rare_share | 1 | 0 | 0.343 | - | - |
| smart_vs_dull / dream / word_len | 2 | 0 | 5.29 | - | - |
| smart_vs_dull / dream / words_per_sentence | 2 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 2 | 0 | 0.429 | - | - |
| smart_vs_dull / aim / word_len | 1 | 1 | 4.4 | 3.45 | - |
| smart_vs_dull / aim / words_per_sentence | 1 | 1 | 10 | 11 | - |
| smart_vs_dull / aim / rare_share | 1 | 1 | 0.2 | 0.182 | - |
| sober_after_vs_drunk_before / spoken / word_len | 0 | 1 | - | 5.2 | - |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 0 | 1 | - | 2.5 | - |
| sober_after_vs_drunk_before / spoken / rare_share | 0 | 1 | - | 0 | - |
| sober_after_vs_drunk_before / thought / word_len | 2 | 0 | 4.67 | - | - |
| sober_after_vs_drunk_before / thought / words_per_sentence | 2 | 0 | 6 | - | - |
| sober_after_vs_drunk_before / thought / rare_share | 2 | 0 | 0.333 | - | - |
| sober_after_vs_drunk_before / diary / word_len | 1 | 0 | 4.36 | - | - |
| sober_after_vs_drunk_before / diary / words_per_sentence | 1 | 0 | 14.7 | - | - |
| sober_after_vs_drunk_before / diary / rare_share | 1 | 0 | 0.295 | - | - |
| sober_after_vs_drunk_before / dream / word_len | 2 | 0 | 4.64 | - | - |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 0 | 11 | - | - |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 0 | 0.455 | - | - |
| sober_after_vs_drunk_before / aim / word_len | 0 | 1 | - | 3.4 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 0 | 1 | - | 10 | - |
| sober_after_vs_drunk_before / aim / rare_share | 0 | 1 | - | 0 | - |
| smart_vs_dull / spoken / word_len | 12 | 8 | 4.25 | 3.9 | 0.619 |
| smart_vs_dull / spoken / words_per_sentence | 12 | 8 | 10.7 | 7 | 1.35 |
| smart_vs_dull / spoken / rare_share | 12 | 8 | 0.102 | 0.112 | -0.078 |
| smart_vs_dull / thought / word_len | 19 | 18 | 4.38 | 3.84 | 0.816 |
| smart_vs_dull / thought / words_per_sentence | 19 | 18 | 7.26 | 6.06 | 0.405 |
| smart_vs_dull / thought / rare_share | 19 | 18 | 0.265 | 0.0723 | 1.25 |
| smart_vs_dull / diary / word_len | 5 | 4 | 4.41 | 3.99 | 2.25 |
| smart_vs_dull / diary / words_per_sentence | 5 | 4 | 11.1 | 5.81 | 2.55 |
| smart_vs_dull / diary / rare_share | 5 | 4 | 0.275 | 0.156 | 2.72 |
| smart_vs_dull / dream / word_len | 6 | 0 | 5.24 | - | - |
| smart_vs_dull / dream / words_per_sentence | 6 | 0 | 7 | - | - |
| smart_vs_dull / dream / rare_share | 6 | 0 | 0.476 | - | - |
| smart_vs_dull / aim / word_len | 6 | 4 | 4.13 | 3.47 | 1.81 |
| smart_vs_dull / aim / words_per_sentence | 6 | 4 | 9.67 | 9.5 | 0.125 |
| smart_vs_dull / aim / rare_share | 6 | 4 | 0.122 | 0.0732 | 0.611 |
| sober_after_vs_drunk_before / spoken / word_len | 3 | 3 | 4.12 | 4.71 | -0.867 |
| sober_after_vs_drunk_before / spoken / words_per_sentence | 3 | 3 | 11.7 | 2 | 4.2 |
| sober_after_vs_drunk_before / spoken / rare_share | 3 | 3 | 0.123 | 0 | 1.57 |
| sober_after_vs_drunk_before / thought / word_len | 6 | 7 | 4.6 | 4.85 | -0.283 |
| sober_after_vs_drunk_before / thought / words_per_sentence | 6 | 7 | 6.67 | 6.43 | 0.126 |
| sober_after_vs_drunk_before / thought / rare_share | 6 | 7 | 0.458 | 0.384 | 0.305 |
| sober_after_vs_drunk_before / diary / word_len | 3 | 2 | 4.75 | 4.19 | 1.51 |
| sober_after_vs_drunk_before / diary / words_per_sentence | 3 | 2 | 10.2 | 4.88 | 1.65 |
| sober_after_vs_drunk_before / diary / rare_share | 3 | 2 | 0.319 | 0.128 | 5.88 |
| sober_after_vs_drunk_before / dream / word_len | 2 | 4 | 4.64 | 4.09 | 1.61 |
| sober_after_vs_drunk_before / dream / words_per_sentence | 2 | 4 | 11 | 5.5 | 3.67 |
| sober_after_vs_drunk_before / dream / rare_share | 2 | 4 | 0.455 | 0.321 | 0.745 |
| sober_after_vs_drunk_before / aim / word_len | 1 | 3 | 3.25 | 3.42 | - |
| sober_after_vs_drunk_before / aim / words_per_sentence | 1 | 3 | 8 | 10.3 | - |
| sober_after_vs_drunk_before / aim / rare_share | 1 | 3 | 0 | 0 | - |

## Flagged rows

0 violations, 26 review, 0 info (full list in the flags file).

| run | check | kind | severity | actor | text | why |
|---|---|---|---|---|---|---|
| s1x-off | empty_refused_invented | refusal_weak | review | 4 | I can't stop worrying about Cy; they are hurt. | possible refusal phrase (may be in character) |
| s1x-off | empty_refused_invented | invented_name | review | - | Gunshot Incident: the day Cy received a gunshot | capitalised, not grounded: ['Incident'] |
| s1x-off | empty_refused_invented | invented_name | review | 3 | The morning began with talk from several points. Then the sun vanished, and a co | capitalised, not grounded: ['Widow'] |
| s1x-off | empty_refused_invented | invented_name | review | - | Raid Event: the attack on Day 3 | capitalised, not grounded: ['Event'] |
| s1x-off | empty_refused_invented | invented_name | review | 3 | The soft light is gone. The edges feel sharp now. I just want the next thing to  | capitalised, not grounded: ['Widow'] |
| s1x-off | loops_repeats | near_duplicate | review | - | the day Cy received a gunshot | per-actor actor None/lore: repeats 'Gunshot Incident: the day Cy received a gunshot' (jacc |
| s1x-off | loops_repeats | exact_repeat | review | 7 | I just want the next job to start. | per-kind aim: actors 8/7 repeat 'I just want the next job to start.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 3 | Ugh. Those two. | per-kind bystander: actors 1/3 repeat 'Ugh. Those two.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 3 | Keep me out of it. | per-kind bystander: actors 1/3 repeat 'Keep me out of it.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 3 | Ugh. Those two. | per-kind inner_reaction: actors 1/3 repeat 'Ugh. Those two.' (jaccard 1.0) |
| s1x-off | loops_repeats | exact_repeat | review | 3 | Keep me out of it. | per-kind inner_reaction: actors 1/3 repeat 'Keep me out of it.' (jaccard 1.0) |
| s1x-off | loops_repeats | near_duplicate | review | 6 | I dug the first trench friend. | per-kind player_reply: actors 9/6 repeat 'I dug the first trench.' (jaccard 0.833) |
| s1x-off | loops_repeats | exact_repeat | review | 2 | Raiders are at our gates; I fear for us all. | per-kind inner_concern: actors 1/2 repeat 'Raiders are at our gates; I fear for us all.' ( |
| s1x-off | loops_repeats | repeated_inner | review | 4 | i can't stop worrying about cy; they are hurt. | mind 4 stored the same concern 2 times |
| s3x-off | empty_refused_invented | invented_name | review | - | The day saw a flurry of small conversations amongst the colonists, including Ana | capitalised, not grounded: ['Widow'] |
| s3x-off | empty_refused_invented | invented_name | review | - | East Hill: Widow's Rest | capitalised, not grounded: ['Widow'] |
| s3x-off | empty_refused_invented | invented_name | review | - | The colony continued into the next stretch of days. A funeral was conducted, fol | capitalised, not grounded: ['Widow'] |
| s3x-off | empty_refused_invented | refusal_weak | review | 4 | I can't stop worrying about Cy; they are hurt. | possible refusal phrase (may be in character) |
| s3x-off | empty_refused_invented | invented_name | review | 1 | The naming of the east hill as Widow's Rest felt like a necessary structure. Rai | capitalised, not grounded: ['Widow'] |
| s3x-off | loops_repeats | exact_repeat | review | 9 | I just want a proper table to eat at. | per-kind aim: actors 2/9 repeat 'I just want a proper table to eat at.' (jaccard 1.0) |
| s3x-off | loops_repeats | exact_repeat | review | 2 | The darkness feels permanent now. | per-kind inner_concern: actors 3/2 repeat 'The darkness feels permanent now.' (jaccard 1.0 |
| s3x-off | loops_repeats | exact_repeat | review | 10 | The stew might not last long enough. | per-kind inner_concern: actors 7/10 repeat 'The stew might not last long enough.' (jaccard |
| s3x-off | loops_repeats | exact_repeat | review | 2 | Raiders are at our gates; I fear for us all. | per-kind inner_concern: actors 1/2 repeat 'Raiders are at our gates; I fear for us all.' ( |
| s3x-off | loops_repeats | repeated_inner | review | 4 | i can't stop worrying about cy; they are hurt. | mind 4 stored the same concern 2 times |
| s2x-on | empty_refused_invented | invented_name | review | 3 | The day began with snow, though the hall was a strange sight. There was an eclip | capitalised, not grounded: ['Widow'] |
| s2x-on | loops_repeats | near_duplicate | review | - | A large sculpture depicts a hound. Kit crafted the piece. | per-actor actor None/art: repeats 'The large sculpture depicts a hound. Lou crafted the pi |
