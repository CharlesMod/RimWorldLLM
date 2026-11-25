"""Write the gpubench engine home's catalog.json: embedded catalog, version+1, plus the two rp E builds.
Footprints come from rp-footprints.json when present (else the stock entry's)."""
import copy, json, os
H = os.path.expanduser("~")
c = json.load(open(H + "/easyEngine/catalog/catalog.json"))
c["version"] += 1
fp = {}
p = H + "/.local/share/rimworld-hermes-engine/bench/rp-footprints.json"
if os.path.exists(p):
    fp = json.load(open(p))
byid = {m["id"]: m for m in c["models"]}
for stock, new, f, sha, name in [
    ("gemma4-e4b-q4km", "gemma4-e4b-rp-q40", "gemma-4-E4B-it-qat-rp-Q4_0.gguf",
     "f272d8be50efb4cedeb6895b8fa1e451dad529b2423d99603f8c247a33b0445d", "Gemma 4 E4B RP (QAT Q4_0, bench)"),
    ("gemma4-e2b-q4km", "gemma4-e2b-rp-q40", "gemma-4-E2B-it-qat-rp-Q4_0.gguf",
     "6a4e36a222c832ba6e95fd230dc61bf2e9fb655520a9bc3786db9afefe911c95", "Gemma 4 E2B RP (QAT Q4_0, bench)")]:
    m = copy.deepcopy(byid[stock])
    m.update(id=new, name=name, file=f, sha256=sha, url="http://invalid.example/" + f,
             size=os.path.getsize(H + "/.hive/models/gemma4-e-rp/" + f))
    m.pop("fallback", None); m.pop("fallback_note", None)
    if new in fp:
        m["footprint"] = fp[new]
    c["models"].append(m)
json.dump(c, open(H + "/.local/share/rimworld-hermes-engine/gpubench/home/catalog.json", "w"), indent=1)
print([m["id"] for m in c["models"]], c["version"])
