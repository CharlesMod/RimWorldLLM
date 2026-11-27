# bench/

Benchmarks, soak runs and probes from the M0 / model-bench / creative-round work. They import the
Python agent package (`rimworldllm_agent`), which no longer exists in this tree: the mind now runs
inside the mod as C# (`Mod/Source/Mind`, see `docs/CSHARP-MIND.md`).

**bench/ runs against the Python agent at `36515840a97cbbbf576ffdfd782d0726f07b7443`; check that
commit out (the coordinator tags it as the last commit with the Python agent) to run it.**

Decision: pinned, not ported. The scripts drive the Python `Director`, `FakeLLM` and tool loop
directly and write result files that the docs quote; porting them would change the instrument
between runs. Fresh measurements of the C# mind use `tools/MindHarness` (see `docs/CSHARP-MIND.md`,
"The harness"). The results under `bench/*/results*` stay as the record.
