Footprint sweep of the rp E builds (and stock E4B/E2B re-measured in the same session), RTX 5080, llama.cpp b12680.
- sweep-rp.csv: raw rows (sweep.py, a copy of the engine sweep with the rp files added).
- fc-*.csv rows were relabelled to the stock ids so tools/fitcheck reads the rp GGUF headers (models dir of symlinks); fitcheck-*.txt is its -fit output.
- rp-footprints.json: the emitted entries renamed to gemma4-e4b-rp-q40 / gemma4-e2b-rp-q40 (catalog Footprint shape).
