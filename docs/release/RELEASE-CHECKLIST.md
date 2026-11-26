# Release checklist: HermesVoice 2.1.0 beta

From the release-prep branch to a published Nexus page and GitHub release. **[keeper]** marks the steps only the keeper
can do or decide; the rest the coordinator can do.

## Done
- [x] **[keeper]** License: MIT for both repos, copyright (c) 2026 Charleszard (RimWorldLLM f9cfb26, easyEngine d64ec90).
- [x] About.xml description, author Charleszard, modVersion 2.1.0 (packageId stays `charless.HermesVoice`).
- [x] README for players; technical sections moved to docs/DEVELOPING.md.
- [x] Nexus page text: docs/release/NEXUS-PAGE.md.
- [x] CHANGELOG.md, PLAYTEST.md, this file.
- [x] `scripts/package-release.sh` builds and checks the zip.

## Before publishing
1. Land the release branch on main (RimWorldLLM). Make sure the sibling `~/easyEngine` checkout is on the easyEngine
   commit you want to ship, clean.
2. **[keeper] Decisions** (see the release report):
   - Nexus adult-content flag: the mod's adult setting is off by default, but the model cards are tagged
     not-for-all-audiences.
   - Uninstall: settled. Storage has one "Remove downloaded files" button that also offers to delete the helper's memory and logs.
3. Build the zip on a clean tree: `scripts/package-release.sh` (builds under `nice -n 19`). Every line must say PASS.
   Keep the printed size and sha256.
4. **[keeper] Play-test** on Linux + NVIDIA with that zip, following docs/release/PLAYTEST.md (sections A-I). Any FAIL in
   item 36 (age gate) blocks the release; other FAILs are fixed or listed under known limits.
5. **[keeper] Windows run**: PLAYTEST.md sections A-C, 39-41 and W1-W6 on a Windows machine. Until it passes, the page
   keeps "Windows untested". Add the exact SmartScreen/Defender wording it shows to the Nexus page's executable section.
6. Fix what the play-test and Windows run find; rebuild the zip (step 3) and recheck the changed items.
7. **[keeper] Preview image and clip**: `Mod/About/Preview.png` (shown in the game's mod list and on Nexus) and a
   30-60 s clip of real moments. Rebuild the zip after adding Preview.png (it ships in About/).
8. Update the numbers in NEXUS-PAGE.md, README.md and About.xml if the shipped catalog changed (model sizes, llama.cpp
   build).

## Publishing
9. Tag RimWorldLLM `v2.1.0-beta` at the shipped commit (the zip's `BUILD` names it); tag easyEngine at its commit if
   wanted.
10. GitHub release on RimWorldLLM: title "HermesVoice 2.1.0 beta", body = the CHANGELOG entry plus the beta note, assets
    `HermesVoice-2.1.0-beta.zip` and its `.sha256`. Mark it as a pre-release.
11. **[keeper] Nexus upload**: create the mod page (name, summary, category, tags, requirements from NEXUS-PAGE.md),
    paste the BBCode, upload the zip as the main file (version 2.1.0 beta, game 1.7), add the preview image and clip,
    set the adult flag as decided.
12. Check the published page: links work, the zip downloads and its sha256 matches.
13. Later (deferred, LAUNCH-PLAN): Steam Workshop (check its rules for bundled executables), code signing, AMD beta,
    the r/RimWorld, forum and Discord posts.
