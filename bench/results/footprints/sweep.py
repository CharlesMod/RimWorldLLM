#!/usr/bin/env python3
"""Footprint sweep: start llama-server at many shapes, record VRAM/RAM and speed. One CSV row per run.
Usage: sweep.py <out.csv> [model-id ...]   (kills only the server pids it started)"""
import csv, json, os, signal, subprocess, sys, threading, time, urllib.request

HOME = os.path.expanduser("~")
EH = HOME + "/.local/share/rimworld-hermes-engine/live-home"
MD = HOME + "/.local/share/rimworld-hermes-engine/gpubench/models"
B = EH + "/backends/cuda-linux-x64/llama-b12680"
PORT = 47611
MODELS = {
    "gemma4-rp-maxxed-q3kxl": ("gemma4-rp-maxxed-Q3_K_XL.gguf", [0, 3, 6, 12, 20, 30]),
    "ling3-tiny-q4km": ("Ling-3.0-tiny-Q4_K_M.gguf", [0, 5, 11, 17, 23]),
    "gemma4-e4b-q4km": ("gemma-4-E4B-it-Q4_K_M.gguf", [0]),
    "gemma4-e2b-q4km": ("gemma-4-E2B-it-Q4_K_M.gguf", [0]),
    "gemma4-e4b-rp-q40": ("gemma-4-E4B-it-qat-rp-Q4_0.gguf", [0]),
    "gemma4-e2b-rp-q40": ("gemma-4-E2B-it-qat-rp-Q4_0.gguf", [0]),
}
PROMPT = ("You are a colonist on a harsh frontier world. " + "The day was long and the work was hard. " * 60 +
          "In one short spoken line, say how you feel about tonight's watch.")


def sh(cmd):
    return subprocess.run(cmd, capture_output=True, text=True, timeout=10).stdout


def gpu_used():
    return int(sh(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"]).split()[0])


def proc_vram(pid):
    for l in sh(["nvidia-smi", "--query-compute-apps=pid,used_memory", "--format=csv,noheader,nounits"]).splitlines():
        p, m = [x.strip() for x in l.split(",")]
        if int(p) == pid:
            return int(m)
    return -1


def rss(pid):
    for l in open(f"/proc/{pid}/status"):
        if l.startswith("VmRSS:"):
            return int(l.split()[1]) // 1024
    return -1


def get(path, timeout=5):
    return urllib.request.urlopen(f"http://127.0.0.1:{PORT}{path}", timeout=timeout)


def chat(out, i):
    body = {"max_tokens": 64, "temperature": 0.8, "cache_prompt": False,
            "messages": [{"role": "user", "content": f"[{i}] " + PROMPT}]}
    try:
        r = urllib.request.urlopen(urllib.request.Request(f"http://127.0.0.1:{PORT}/v1/chat/completions",
                                   json.dumps(body).encode(), {"Content-Type": "application/json"}), timeout=180)
        out[i] = json.load(r).get("timings", {})
    except Exception as e:
        out[i] = {"err": str(e)}


def run(model_id, slots, ctx, kv, ncmoe, others):
    f = MODELS[model_id][0]
    args = [B + "/llama-server", "-m", f"{MD}/{f}", "-ngl", "999", "-np", str(slots), "-c", str(slots * ctx),
            "-ctk", kv, "-ctv", kv, "--no-kv-unified", "-fa", "on", "--jinja", "--reasoning", "off",
            "--cache-reuse", "256", "--no-webui", "--metrics", "--slots", "--host", "127.0.0.1", "--port", str(PORT)]
    if ncmoe:
        args += ["--n-cpu-moe", str(ncmoe)]
    row = dict(model=model_id, slots=slots, ctx_per_slot=ctx, ctx_total=slots * ctx, kv=kv, n_cpu_moe=ncmoe, others=others)
    log = open(HOME + "/.local/share/rimworld-hermes-engine/gpubench/sweep/server.log", "w")
    t0 = time.time()
    p = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, env=dict(os.environ, LD_LIBRARY_PATH=B))
    try:
        ok = False
        while time.time() - t0 < 240 and p.poll() is None:
            try:
                if get("/health", 2).status == 200:
                    ok = True
                    break
            except Exception:
                time.sleep(0.25)
        row["load_s"] = round(time.time() - t0, 1)
        if not ok:
            txt = open(log.name).read()
            row["status"] = "oom" if "out of memory" in txt.lower() or "failed to allocate" in txt.lower() else "fail"
            return row
        time.sleep(1.0)
        row["vram_load"] = proc_vram(p.pid)
        row["rss_load"] = rss(p.pid)
        for rnd in range(2):  # round 0 warms up, round 1 is timed
            out = [None] * slots
            th = [threading.Thread(target=chat, args=(out, i)) for i in range(slots)]
            w = time.time()
            [t.start() for t in th]
            [t.join() for t in th]
            wall = time.time() - w
        good = [o for o in out if o and "err" not in o]
        row["vram_used"] = proc_vram(p.pid)
        row["gpu_total_used"] = gpu_used()
        row["rss_used"] = rss(p.pid)
        if good:
            row["decode_tps"] = round(sum(o["predicted_per_second"] for o in good) / len(good), 1)
            row["prompt_tps"] = round(sum(o["prompt_per_second"] for o in good) / len(good), 1)
            row["prompt_n"] = good[0].get("prompt_n")
            row["wall_s"] = round(wall, 2)
        row["status"] = "ok" if len(good) == slots else "partial"
        return row
    finally:
        if p.poll() is None:
            p.send_signal(signal.SIGTERM)
            try:
                p.wait(20)
            except subprocess.TimeoutExpired:
                p.kill()
                p.wait()
        log.close()
        time.sleep(1.5)


def main():
    out = sys.argv[1]
    ids = sys.argv[2:] or list(MODELS)
    fields = ["model", "slots", "ctx_per_slot", "ctx_total", "kv", "n_cpu_moe", "others", "status", "load_s", "vram_load",
              "vram_used", "gpu_total_used", "rss_load", "rss_used", "decode_tps", "prompt_tps", "prompt_n", "wall_s"]
    new = not os.path.exists(out)
    with open(out, "a", newline="") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        if new:
            w.writeheader()
        for mid in ids:
            grid = [(s, c, "q4_0", 0) for s in (1, 2, 3) for c in (8192, 16384, 32768, 65536)]
            grid += [(3, 16384, "q8_0", 0), (3, 65536, "q8_0", 0), (3, 16384, "f16", 0)]
            grid += [(3, 65536, "q4_0", n) for n in MODELS[mid][1] if n]
            for s, c, kv, n in grid:
                others = gpu_used()
                row = run(mid, s, c, kv, n, others)
                w.writerow(row)
                fh.flush()
                print(json.dumps(row), flush=True)
    print("SWEEPDONE", flush=True)


if __name__ == "__main__":
    main()
