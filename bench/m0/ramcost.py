"""RAM prompt-cache cost: RssAnon of the server after displacing k distinct pawn leases from slot 1."""
import copy, sys
from lib import *
pid = int(open(os.path.expanduser("~/.local/share/rimworld-hermes-m0/srv.pid")).read())
def rss():
    for l in open(f"/proc/{pid}/status"):
        if l.startswith("RssAnon"): return int(l.split()[1]) / 1024
ids = list(PAWNS); wipe_slots()
print("RssAnon MiB at start", round(rss()))
for k in range(12):
    p = copy.deepcopy(PAWNS[ids[k % 6]]); p["age"] += 300 + k
    sx, us = build(p, SCENES[k]); complete(raw(sx, us), 1, n_predict=4)
    print(k + 1, "leases:", round(rss()), "MiB")
