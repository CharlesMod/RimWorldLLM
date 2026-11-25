# Launch plan

Decided with the keeper on 2026-11-25. This is the order of work from here to a public release.

## Priorities

1. **Optimise for gemma4-rp-maxxed (the 26B)** on a 16 GB card, or a 12 GB card with expert
   offload. This is the reference experience, and it is the keeper's own setup.
2. **A final pass with E4B before publishing.** E4B is what most other players will get.
   Everything that ships must be checked on it: lines, conversations, the intent pass, the tool
   harness's conformance gate.
3. **E2B is a fallback only**, with an in-game warning.
4. **Windows testing is deferred.** Nothing below depends on it, but a public release does.
   It stays on the list as the first blocker to clear when we come back to publishing.

## Before release

| Item | State |
|---|---|
| One-click engine (easyEngine): install, model pick, ctx balancer, lifetime | built, live-checked on Linux + RTX 5080 |
| Measured sizing for every catalog model | done, within 4 % |
| Per-model output bench (`docs/MODEL-BENCH.md`) | done |
| Tool harness resistant to empty replies and bad arguments, with a conformance gate | done |
| E2B fallback warning in game | done |
| Comic-style speech bubbles | done (in-game look unchecked) |
| Repetition control for conversation loops | done: loop guard (11/15 caught offline, 0 false alarms) |
| Age gate: no romantic or sexual content for or about any pawn under 18, enforced in code | done |
| Adult content off by default, behind a setting | built, needs in-game check: mod setting `adultContent` (default off, confirmation dialog on enabling, GET /config `adult_content`); agent core/adult.py reads it on each config refresh, adds a fade-to-black prompt rule when off, drops explicit lines in the sanitiser (reason `adult_off`, own vocabulary apart from the age gate's, covers speech, diary, chronicle, art, dream, thought), counted in Director.gate_stats()["adult_off_dropped"]; the age gate is untouched in both modes |
| Fast first run: start on the small model, swap to the big one when its download finishes | to do |
| "Share this moment": export an exchange or diary page as an image or text, with the mod name | to do |
| Tune effect sizes in a real colony (SPEC milestone 4) | to do |
| Aliveness: inner monologue, concerns, grudges, gossip, lore, art, dreams, habits, firsts, bystanders, gatherings, director camera, recap | built; tuning needs a play-test |
| Wants with follow-through (/social_action) | built; in-game unchecked |
| Speed robustness (2x-4x) | done in simulation; in-game unchecked |
| Swap the catalog's stock E4B/E2B for the rp builds, measured footprints | to do |
| Sampler A/B on the GPU, injection re-measure | to do (waits for the GPU) |
| rp builds of E4B and E2B (QAT, abliterated, vocabulary-pruned) | E2B done (2.12 GB); E4B built (3.65 GB), final evals running |
| Final E4B pass over all of the above | last step before publishing |
| Windows and AMD beta, code signing, Workshop rules for bundled executables | deferred |

## The first five minutes

The pitch is "subscribe, click once, colonists talk". The number to optimise is the time from
subscribing to the first spoken line.

- The Workshop page says exactly what is downloaded, how big it is, where it lives and how to
  remove it.
- The mod works on the small model within a minute or two while the big model downloads.
- An honest hardware table: what each card size gets, and that E2B means simpler speech.

## Being found

- Title that matches what people search for: "HermesVoice: Local AI Colonist Dialogue (one click,
  no API key)".
- Describe the tone as "matches RimWorld's own tone". Do not lead with "uncensored".
- Say plainly how it relates to RimTalk: it can coexist, it is free, local and needs no key.
- Link both repos (RimWorldLLM and easyEngine). A GitHub release serves GOG and non-Steam players.

## Stories do the advertising

- "Share this moment" on the chronicle and diary views.
- A 30–60 second clip of real emergent moments for the Workshop page.

## Launch sequence (when publishing resumes)

1. Windows and AMD closed beta (deferred item above).
2. Workshop release with the clip, on a current game version tag.
3. r/RimWorld post showing moments, the Ludeon forum, the RimWorld Discord.
4. Mod-showcase YouTubers, with a ready-made save that produces good moments quickly.
5. Answer every comment and fix fast for the first two weeks.

## Known limits on reach

- Most players do not have 16 GB cards.
- English first; the top model's vocabulary prune makes it worse at other scripts.
