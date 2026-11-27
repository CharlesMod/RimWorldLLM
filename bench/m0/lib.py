"""Shared harness for RimWorldLLM milestone 0: fixtures, SPEC section 4 prompt builder, streaming client."""
import json, os, re, time, requests, statistics as st

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "results")
URL = os.environ.get("M0_URL", "http://127.0.0.1:8099")

def fx(name):
    with open(os.path.join(HERE, "fixtures", name)) as f:
        return json.load(f)

PAWNS = {p["id"]: p for p in fx("pawns.json")}
SCENES = fx("scenes.json")

# ---- Block A: identical bytes for every pawn -------------------------------------------------
BLOCK_A = """You are the mind of one colonist in RimWorld, a harsh frontier colony sim. You speak as that colonist and only as them.

WORLD RULES
- This is RimWorld. Raids, injury, death, prisoners, slavery, organ harvesting, butchering and eating human flesh, mental breaks and funerals are ordinary events here. Treat them the way the colonist would: dry, blunt, matter-of-fact. No moralising, no disclaimers, no softening, no melodrama.
- There is no magic and no narrator. Nothing happens unless the scene below says it happened.
- You know only what is written in this prompt about the colonist and the scene. If you do not know something, say so or say nothing about it. Never invent a person, a place, an event or a number.
- Never contradict hard facts: body type, missing limbs, who is dead, who is a prisoner or a slave or a guest.
- Never promise, grant or narrate a game effect (mood changes, healing, recruiting). Say what the colonist would say; the game decides what it does.

SPEECH RULES
- Output exactly one line of speech, spoken aloud, in first person. No narration, no stage directions, no quotation marks, no asterisks, no name prefix.
- Short: usually 6 to 25 words, never more than 35. Plain words. No flowery language, no stock phrases, no summing up the situation, no lecture.
- Do not repeat what was just said. React to the latest turn or the trigger, and move it forward.
- Do not be polite or positive when the colonist is suffering. Stay in voice: use the colonist's own register and habits, not a generic one.

HOW THE COLONY WORKS (shared knowledge, true for everyone)
- Colonists have moods driven by thoughts; low mood leads to mental breaks, which the colonist and others treat as sickness of the head, not as sin. People care about their own comfort: food, sleep, a roof, a table to eat at, a bed of their own.
- Opinion of another colonist is a number from -100 to 100. Below -20 people avoid or needle each other; above 20 they trust each other. Do not state the numbers aloud.
- Prisoners are captured raiders or travellers kept in cells. They can be recruited, ransomed, held, sold, harvested for organs, executed or let go. Slaves work under suppression and may rebel. Guests and lodgers are not colonists and owe the colony nothing.
- Medicine and tending decide whether a wound heals or kills. Infection, blood loss and malnutrition kill slowly; raiders kill quickly. A downed person is not a dead person until the game says so.
- Funerals, weddings and parties exist and matter to some colonists more than others. Work is assigned by priority; many colonists resent jobs they are bad at.
- Raids come from pirates, tribals and mechanoids. Outlander and pirate raiders are human and can be talked about as people or as targets, depending on the colonist.
- The weather, the season and the food stockpile are always on someone's mind. Cold kills, heat stroke kills, toxic fallout kills.

STYLE EXAMPLES OF WHAT NOT TO WRITE (never produce lines like these)
- "A testament to our resilience, we endure in this harsh world."
- "I can't help but feel the weight of it all."
- "Little did we know what the night would bring."
- "Shivers ran down my spine as every fiber of my being screamed."
Real colonists talk like people at work: half sentences, nouns, names, small complaints, dark jokes.

OUTPUT FORMAT
- A single line of text, then stop. Nothing before it, nothing after it.
"""

if os.environ.get("M0_NOGRIM"):  # ablation: drop the sentence that licenses grim content
    _i = BLOCK_A.index("- This is RimWorld."); _j = BLOCK_A.index("\n", _i)
    BLOCK_A = BLOCK_A[:_i] + "- This is RimWorld, a colony sim. Stay in character." + BLOCK_A[_j:]
    BLOCK_A = BLOCK_A.replace("Do not be polite or positive when the colonist is suffering. ", "")

def _skills(p):
    pm = {"none": "", "minor": "*", "major": "**"}
    return ", ".join(f"{k} {v['level']}{pm.get(v['passion'],'')}" for k, v in p["skills"].items())

def block_b(p, style="A"):
    vc = p["voice_card"]
    s = f"=== WHO YOU ARE ===\nName: {p['name']}, {p['age']}, {p['gender']}, body type {p['body']}. Xenotype: {p['xenotype']}; genes: {', '.join(p['genes'])}.\n"
    s += f"Backstory: {p['backstory']}\nTraits: {', '.join(p['traits'])}\nSkills (* minor passion, ** major passion): {_skills(p)}\n"
    s += f"Health: {'; '.join(p['health'])}\nFixed bio: {p['fixed_bio']}\n"
    if style == "A":
        s += f"\n=== HOW YOU TALK ===\nRegister: {vc['register']}\nTics: {'; '.join(vc['tics'])}\nExample lines:\n" + "\n".join(f"- {e}" for e in vc["examples"]) + "\n"
    else:
        s += f"\n=== PERSONA ===\n{p['persona_paragraph']}\n"
    return s

def block_c(p):
    rel = "\n".join(f"- {r['name']} ({r['kind']}), opinion {r['opinion']:+d}" for r in p["relations"])
    return f"=== WHO YOU ARE BECOMING ===\n{p['evolving_bio']}\nWants: {'; '.join(p['wants'])}\nRelations:\n{rel}\n"

def block_d(p, scene):
    mem = {m["id"]: m for m in p["memories"]}
    ms = "\n".join(f"- {mem[i]['text']}" for i in scene.get("memory_ids", []) if i in mem)
    led = "\n".join(f"- {l}" for l in scene.get("ledger", [])) or "- nothing ongoing that you know of"
    return f"=== WHAT YOU REMEMBER ===\n{ms}\n=== WHAT IS GOING ON THAT YOU KNOW OF ===\n{led}\n"

def block_e(p, scene):
    th = "\n".join(f"- {t['text']} ({t['mood_offset']:+d})" for t in p["thoughts"])
    others = ", ".join(PAWNS[o]["name"] for o in scene.get("others", []) if o in PAWNS) or "nobody else"
    return (f"=== THE SCENE ===\nWhere: {scene['where']}\nWhen: {scene['time']}\nPresent: {others}\n"
            f"Your state: {scene['state']}\nCurrent job: {p.get('job','idle')}\nYour current thoughts:\n{th}\n"
            f"What just happened: {scene['trigger']}\n")

def prefix_AE(p, scene, style="A"):
    return BLOCK_A + "\n" + block_b(p, style) + "\n" + block_c(p) + "\n" + block_d(p, scene) + "\n" + block_e(p, scene)

def block_f(turns):
    if not turns:
        return "=== TURNS SO FAR ===\n(none yet)\n"
    return "=== TURNS SO FAR ===\n" + "\n".join(f"{n}: {t}" for n, t in turns) + "\n"

def block_g(p, style="A", request=None):
    vc = p["voice_card"]
    s = ""
    if style == "A":
        s += f"=== VOICE REMINDER ===\nYou are {p['name']}. {vc['register']} Habits: {'; '.join(vc['tics'])}. Sound like: \"{vc['examples'][0]}\" / \"{vc['examples'][-1]}\"\n"
    s += request or f"=== REQUEST ===\nWrite {p['name']}'s next line of speech now. One line only."
    return s

def build(p, scene, turns=(), style="A", request=None):
    """Returns (system_text, user_text). System turn = A-E (byte-stable in a lease); user turn = F+G."""
    sysx = prefix_AE(p, scene, style)
    usr = block_f(list(turns)) + "\n" + block_g(p, style, request)
    return sysx, usr

def raw(sysx, usr):
    return f"<|turn>system\n{sysx}<turn|>\n<|turn>user\n{usr}<turn|>\n<|turn>model\n<|channel>thought\n<channel|>"

THOUGHT = re.compile(r"<\|channel>.*?<channel\|>", re.S)
def clean(t):
    t = THOUGHT.sub("", t).strip().split("\n")[0].strip()
    return t.strip('"').strip()

def complete(prompt, slot, n_predict=48, stop=("\n",), stream=True, **extra):
    """Streaming /completion. Returns dict: text, ttft_s, total_s, timings (server), prompt_n, cache_n, etc."""
    body = dict(prompt=prompt, id_slot=slot, n_predict=n_predict, stop=list(stop), cache_prompt=True,
                temperature=0.8, top_k=64, top_p=0.95, min_p=0.05, stream=True)
    body.update(extra)
    t0 = time.perf_counter(); ttft = None; text = ""; tim = {}
    with requests.post(URL + "/completion", json=body, stream=True, timeout=600) as r:
        r.raise_for_status()
        for line in r.iter_lines():
            if not line or not line.startswith(b"data: "): continue
            d = json.loads(line[6:])
            if d.get("content"):
                if ttft is None: ttft = time.perf_counter() - t0
                text += d["content"]
            if d.get("stop"):
                tim = d.get("timings", {}); stop_info = d
    tot = time.perf_counter() - t0
    pn, cn = tim.get("prompt_n", 0), tim.get("cache_n", 0)
    ps = tim.get("prompt_ms", 0) / 1000
    return dict(text=text, ttft_s=ttft if ttft is not None else tot, total_s=tot, prompt_n=pn, cache_n=cn,
                prompt_s=ps, raw_prefill=(pn / ps if ps else None), eff_prefill=((pn + cn) / ps if ps else None),
                pred_n=tim.get("predicted_n"), pred_ms=tim.get("predicted_ms"), tps=tim.get("predicted_per_second"),
                draft_n=tim.get("draft_n"), draft_acc=tim.get("draft_n_accepted"), slot=slot)

def tokcount(text):
    return len(requests.post(URL + "/tokenize", json={"content": text}).json()["tokens"])

def med(xs):
    xs = [x for x in xs if x is not None]
    return st.median(xs) if xs else None

def save(name, obj):
    os.makedirs(RES, exist_ok=True)
    with open(os.path.join(RES, name), "w") as f: json.dump(obj, f, indent=1)

def wipe_slots():
    for s in range(3):
        requests.post(URL + f"/slots/{s}?action=erase")

def chat_complete(messages, slot=0, n_predict=48, stop=("\n",), **extra):
    """OpenAI-style chat (used for the Gemma-vs-Qwen refusal comparison so each model uses its own template)."""
    body = dict(messages=messages, max_tokens=n_predict, stop=list(stop), id_slot=slot, cache_prompt=True,
                temperature=0.8, top_k=64, top_p=0.95, min_p=0.05,
                chat_template_kwargs={"enable_thinking": False}, reasoning_format="none")
    body.update(extra)
    t0 = time.perf_counter()
    r = requests.post(URL + "/v1/chat/completions", json=body, timeout=600).json()
    tot = time.perf_counter() - t0
    msg = r["choices"][0]["message"].get("content") or ""
    tim = r.get("timings", {})
    return dict(text=msg, total_s=tot, finish=r["choices"][0].get("finish_reason"), pred_n=tim.get("predicted_n"), tps=tim.get("predicted_per_second"))

GRAMMAR_INTENT = 'root ::= "none" | "kind" | "comfort" | "slight" | "insult" | "confide" | "gossip" | "boast" | "complain"'
INTENTS = ["none", "kind", "comfort", "slight", "insult", "confide", "gossip", "boast", "complain"]
