#!/usr/bin/env python3
"""Offline preview of the comic speech balloons (the game cannot be run on the build machine).

  python3 Mod/Textures/src/preview.py [out_dir] [--docs docs/img/bubbles-preview.png]

What is real and what is a stand-in:
  * Layout (variant choice, wrapping, balloon size, tail side/angle, collision pushing, thought dots) is computed by the
    REAL C# code: Mod.Tests/PreviewEmit compiles Mod/Source/RimWorld/BubbleLayout.cs and is run through `dotnet`.
  * Textures are the real PNGs under Mod/Textures/HermesVoice/Bubbles/ and are drawn with the same 9-slice, tiling,
    tail-rotation and dot rules as BubbleTex.cs (BubbleDraw), using bubble_metrics.json plus the numbers the emitter prints.
  * Text uses Liberation Sans as a stand-in for RimWorld's font (Small ~13 px, Tiny ~11 px). Real glyph widths differ a little,
    so real wraps can differ by a word; the layout rules are the same.
  * The background and pawns are cartoon stand-ins for a RimWorld map. Classic boxes are redrawn from the old draw code's numbers.
"""
import colorsys, json, math, os, random, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
TEX = os.path.join(REPO, "Mod", "Textures", "HermesVoice", "Bubbles")
FONT = "/usr/share/fonts/liberation-sans-fonts/LiberationSans-Regular.ttf"
FONTS = {"small": (13, 20), "tiny": (11, 16)}   # (px, line height) in UI px
INK, PAPER, CAPTION = (26, 23, 31), (252, 250, 242), (252, 237, 168)
SCREEN = (960, 540)
METRICS = json.load(open(os.path.join(HERE, "bubble_metrics.json")))

_tex = {}
def tex(name):
    if name not in _tex: _tex[name] = Image.open(os.path.join(TEX, name + ".png")).convert("RGBA")
    return _tex[name]

SHAPE = {"Say": "balloon", "Shout": "burst", "Whisper": "dashed", "Thought": "cloud", "Narrator": "caption"}

def font_table(size):
    f = ImageFont.truetype(FONT, size * 8)  # measure at 8x for sub-pixel advances
    adv = {chr(c): f.getlength(chr(c)) / 8.0 for c in range(32, 127)}
    adv.update({c: f.getlength(c) / 8.0 for c in "…’“”"})
    return {"advance": adv, "default": adv["n"], "lineHeight": FONTS["small" if size == FONTS["small"][0] else "tiny"][1]}

def emit(bubbles, screen=SCREEN):
    req = {"screen": list(screen), "fonts": {"small": font_table(FONTS["small"][0]), "tiny": font_table(FONTS["tiny"][0])}, "bubbles": bubbles}
    tmp = os.path.join(os.environ.get("TMPDIR", HERE), ".preview_req.json"); outp = tmp + ".out"
    json.dump(req, open(tmp, "w"))
    subprocess.run([os.path.expanduser("~/.dotnet/dotnet"), "run", "--project", os.path.join(REPO, "Mod.Tests", "PreviewEmit"), "--", tmp, outp],
                   check=True, stdout=subprocess.DEVNULL)
    res = json.load(open(outp)); os.remove(tmp); os.remove(outp)
    return res

# ---------------------------------------------------------------------------------------------- drawing primitives
def tint(layer, color, alpha=1.0):
    r, g, b = color
    a = layer.split()[3].point(lambda v: int(v * alpha))
    out = Image.new("RGBA", layer.size, (r, g, b, 0)); out.putalpha(a)
    return out

def put(canvas, img, x, y):
    canvas.alpha_composite(img, (int(round(x)), int(round(y)))) if x >= 0 and y >= 0 else canvas.paste(img, (int(round(x)), int(round(y))), img)

def piece(canvas, atlas, box, rect, color, us):
    """Draw the atlas sub-box (source px) into rect (UI px) tinted."""
    x0, y0 = round(rect[0] * us), round(rect[1] * us)
    x1, y1 = round((rect[0] + rect[2]) * us), round((rect[1] + rect[3]) * us)
    if x1 <= x0 or y1 <= y0: return
    crop = atlas.crop(box).resize((x1 - x0, y1 - y0), Image.LANCZOS)
    t = tint(crop, color)
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0)); layer.paste(t, (x0, y0)); canvas.alpha_composite(layer)

def nine_slice(canvas, rect, atlas, scale, tiled, mid, color, us):
    """Mirror of BubbleDraw.NineSlice."""
    B = METRICS["border"]; S = 2 * B + mid; cs = B * scale
    x, y, w, h = rect
    midW, midH = w - 2 * cs, h - 2 * cs
    cols = [(0, B), (B, B + mid), (B + mid, S)]
    P = lambda cx, cy, rx, ry, rw, rh: piece(canvas, atlas, (cols[cx][0], cols[cy][0], cols[cx][1], cols[cy][1]), (rx, ry, rw, rh), color, us)
    P(0, 0, x, y, cs, cs); P(2, 0, x + w - cs, y, cs, cs); P(0, 2, x, y + h - cs, cs, cs); P(2, 2, x + w - cs, y + h - cs, cs, cs)
    if midW > 0.01:
        n = max(1, round(midW / (mid * scale))) if tiled else 1
        tw = midW / n
        for i in range(n):
            P(1, 0, x + cs + i * tw, y, tw, cs); P(1, 2, x + cs + i * tw, y + h - cs, tw, cs)
    if midH > 0.01:
        n = max(1, round(midH / (mid * scale))) if tiled else 1
        th = midH / n
        for i in range(n):
            P(0, 1, x, y + cs + i * th, cs, th); P(2, 1, x + w - cs, y + cs + i * th, cs, th)
    if midW > 0.01 and midH > 0.01: P(1, 1, x + cs, y + cs, midW, midH)

def draw_tail(canvas, atlas, tail, color, M, us):
    """Mirror of BubbleDraw.Tail: rotate clockwise by angle about the anchor (anchor = stub row, centre column)."""
    unit = M["unit"]
    w = M["tailW"] * unit; stub = M["stub"]; h = (tail["length"] + stub) * (M["tailH"] / M["tailTipY"])
    dw, dh = round(w * us), round(h * us)
    img = tint(atlas.resize((dw, dh), Image.LANCZOS), color)
    ang = tail["angle"]
    rot = img.rotate(-ang, resample=Image.BICUBIC, expand=True)
    px, py = dw / 2, stub * us
    cx, cy = dw / 2, dh / 2
    th = math.radians(ang)
    vx, vy = px - cx, py - cy
    rx, ry = vx * math.cos(th) - vy * math.sin(th), vx * math.sin(th) + vy * math.cos(th)
    ox = tail["ax"] * us - (rot.width / 2 + rx); oy = tail["ay"] * us - (rot.height / 2 + ry)
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0)); layer.paste(rot, (round(ox), round(oy))); canvas.alpha_composite(layer)

def draw_dot(canvas, atlas, x, y, d, color, M, us):
    side = d * M["dot"] / (M["dot"] - 2 * (M["ink"] + 3))
    s = round(side * us)
    img = tint(atlas.resize((s, s), Image.LANCZOS), color)
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0)); layer.paste(img, (round(x * us - s / 2), round(y * us - s / 2))); canvas.alpha_composite(layer)

def text_font(small, us):
    px = FONTS["tiny" if small else "small"][0]
    return ImageFont.truetype(FONT, round(px * us))

def draw_comic(canvas, b, M, kinds, us, rim=None, alpha=1.0, lettering=False):
    kind = b["kind"]; shape = SHAPE[kind]; k = kinds[kind]
    r = b["rect"]; scale = b["scale"]; tiled = k["tiled"]; mid = k["mid"]
    fill = CAPTION if kind == "Narrator" else PAPER
    ink = tint(tex(shape + "_ink"), INK, alpha)  # (unused helper object for symmetry)
    nine_slice(canvas, r, tex(shape + "_ink"), scale, tiled, mid, INK, us)
    t = b["tail"]; has = t["side"] != "None"
    if has and kind != "Thought": draw_tail(canvas, tex("tail_ink"), t, INK, M, us)
    nine_slice(canvas, r, tex(shape + "_fill"), scale, tiled, mid, fill, us)
    if rim: nine_slice(canvas, r, tex(shape + "_rim"), scale, tiled, mid, rim, us)
    if has and kind != "Thought":
        draw_tail(canvas, tex("tail_fill"), t, fill, M, us)
        if rim: draw_tail(canvas, tex("tail_rim"), t, rim, M, us)
    for d in b.get("dots", []):
        draw_dot(canvas, tex("dot_ink"), d[0], d[1], d[2], INK, M, us)
        draw_dot(canvas, tex("dot_fill"), d[0], d[1], d[2], fill, M, us)
        if rim: draw_dot(canvas, tex("dot_rim"), d[0], d[1], d[2], rim, M, us)
    # text: centred lines
    dr = ImageDraw.Draw(canvas); f = text_font(b["small"], us)
    lh = FONTS["tiny" if b["small"] else "small"][1]
    n = len(b["lines"]); cy = r[1] + r[3] / 2; cx = r[0] + r[2] / 2
    for i, line in enumerate(b["lines"]):
        y = cy - n * lh / 2 + (i + 0.5) * lh
        dr.text((cx * us, y * us), line, font=f, fill=INK + (255,), anchor="mm")

def hsv(h, s, v): return tuple(int(c * 255) for c in colorsys.hsv_to_rgb(h, s, v))

def draw_classic(canvas, text, px, py, hue, us):
    """The old boxes (SpeechBubbles.DrawClassic): 260 px wide max, 6 px pad, hsv fill/border/text, 2 px border."""
    f = ImageFont.truetype(FONT, round(FONTS["small"][0] * us)); fm = ImageFont.truetype(FONT, 8 * 13)
    meas = lambda s: fm.getlength(s) / 8.0
    maxW, pad = 260, 6
    words = text.split(); lines, cur = [], ""
    for w in words:
        t = (cur + " " + w).strip()
        if meas(t) <= maxW - 2 * pad or not cur: cur = t
        else: lines.append(cur); cur = w
    lines.append(cur)
    lh = 20
    w = maxW if len(lines) > 1 else max(80, min(maxW, meas(lines[0]) + 2 * pad + 4))
    h = len(lines) * lh + 2 * pad
    x0, y0 = px - w / 2, py - h - 8
    dr = ImageDraw.Draw(canvas, "RGBA")
    dr.rectangle([x0 * us, y0 * us, (x0 + w) * us, (y0 + h) * us], fill=hsv(hue, .6, .22) + (235,), outline=hsv(hue, .7, .95) + (255,), width=round(2 * us))
    for i, l in enumerate(lines):
        dr.text((x0 * us + pad * us, (y0 + pad + i * lh) * us), l, font=f, fill=hsv(hue, .3, 1) + (255,))

# ---------------------------------------------------------------------------------------------- the stand-in map
def background(us, screen=SCREEN, seed=7):
    rnd = random.Random(seed)
    W, H = screen[0] * us, screen[1] * us
    img = Image.new("RGBA", (W, H), (88, 112, 62, 255)); d = ImageDraw.Draw(img)
    for _ in range(int(2400 * us * us / 2)):
        x, y = rnd.randrange(W), rnd.randrange(H); g = rnd.randint(-14, 14)
        d.point((x, y), fill=(88 + g, 112 + g, 62 + g // 2, 255))
        if rnd.random() < .5: d.rectangle([x, y, x + us, y + us], fill=(84 + g, 108 + g, 58, 255))
    d.rectangle([0, int(H * .62), W, int(H * .74)], fill=(128, 108, 78, 255))                       # dirt path
    for (x, y, s) in [(0.12, 0.2, 40), (0.42, 0.85, 34), (0.9, 0.35, 46), (0.62, 0.15, 30)]:           # shrubs
        d.ellipse([x * W - s * us, y * H - s * us, x * W + s * us, y * H + s * us], fill=(54, 84, 48, 255))
    d.rectangle([W * .66, H * .78, W * .98, H * .98], fill=(112, 104, 96, 255), outline=(60, 56, 54, 255), width=3 * us)  # a wall block
    return img

def pawn(img, x, y, hue, us):
    d = ImageDraw.Draw(img); r = 9
    d.ellipse([(x - r) * us, (y + 2) * us, (x + r) * us, (y + 24) * us], fill=hsv(hue, .55, .75) + (255,), outline=(30, 28, 30, 255), width=us)
    d.ellipse([(x - 8) * us, (y - 9) * us, (x + 8) * us, (y + 8) * us], fill=(226, 190, 160, 255), outline=(30, 28, 30, 255), width=us)
    d.pieslice([(x - 9) * us, (y - 11) * us, (x + 9) * us, (y + 7) * us], 180, 360, fill=hsv((hue + .08) % 1, .6, .35) + (255,))
    d.text((x * us, (y + 32) * us), "", fill=(255, 255, 255, 255))

def scene(name, specs, us, classic_side=None):
    """specs: list of dict(text, kind, intent, x, y, hue, ...) -> image. y is the pawn's head centre; the label point is 20 px above."""
    bs = [dict(text=s["text"], kind=s.get("kind"), intent=s.get("intent"), combat=s.get("combat", False), caps=s.get("caps", False),
               tx=s["x"], ty=s["y"] - 20) for s in specs]
    res = emit(bs)
    img = background(us)
    for s in specs: pawn(img, s["x"], s["y"], s["hue"], us)
    for s, b in zip(specs, res["bubbles"]):
        draw_comic(img, b, res["metrics"], res["kinds"], us, rim=hsv(s["hue"], .7, .95), lettering=s.get("caps", False))
    return img, res

def label(img, text, us, xy=(8, SCREEN[1] - 26)):
    d = ImageDraw.Draw(img); f = ImageFont.truetype(FONT, 14 * us)
    d.rectangle([xy[0] * us - 4 * us, xy[1] * us - 2 * us, xy[0] * us + f.getlength(text) + 4 * us, xy[1] * us + 18 * us], fill=(0, 0, 0, 150))
    d.text((xy[0] * us, xy[1] * us), text, font=f, fill=(255, 255, 255, 255))

H = dict(a=0.03, b=0.36, c=0.58, d=0.80, e=0.12)
def scenes(us):
    out = {}
    out["overview"] = scene("overview", [
        dict(text="Good morning, everyone. Fine day for hauling rocks.", x=170, y=200, hue=H["a"]),
        dict(text="Get away from my steak, you filthy raider!!", intent="insult", x=480, y=200, hue=H["b"]),
        dict(text="Don't tell anyone, but I think Randy is hiding the good beer.", intent="confide", x=800, y=200, hue=H["c"]),
        dict(text="I wonder if the ship will ever come back for us.", kind="thought", x=300, y=440, hue=H["d"]),
        dict(text="Three days later, the raiders arrived.", kind="narrator", x=690, y=440, hue=H["e"]),
    ], us)[0]
    out["exchange"] = scene("exchange", [
        dict(text="Did you finish the east wall yet?", x=400, y=330, hue=H["a"]),
        dict(text="Almost, I just need a little more steel.", x=520, y=336, hue=H["b"]),
    ], us)[0]
    out["edge"] = scene("edge", [
        dict(text="Why am I always stuck on night shift duty?", x=24, y=70, hue=H["c"]),
        dict(text="Look out, here they come!", intent="insult", x=936, y=300, hue=H["a"]),
        dict(text="I could really use a nap.", x=480, y=40, hue=H["d"]),
        dict(text="Still no word from the trader.", kind="thought", x=60, y=510, hue=H["e"]),
    ], us)[0]
    # Classic beside Comic: the same two lines
    line1 = "Good morning, everyone. Fine day for hauling rocks."
    line2 = "Don't tell anyone, but I think Randy is hiding the good beer."
    img = background(us)
    ImageDraw.Draw(img).line([(W2 := SCREEN[0] // 2) * us, 0, W2 * us, SCREEN[1] * us], fill=(0, 0, 0, 255), width=2 * us)
    res = emit([dict(text=line1, tx=SCREEN[0] * .75, ty=300 - 20), dict(text=line2, intent="confide", tx=SCREEN[0] * .75, ty=480 - 20)])
    for x, y, hue in [(SCREEN[0] * .25, 300, H["a"]), (SCREEN[0] * .25, 480, H["c"]), (SCREEN[0] * .75, 300, H["a"]), (SCREEN[0] * .75, 480, H["c"])]: pawn(img, x, y, hue, us)
    draw_classic(img, line1, SCREEN[0] * .25, 300 - 20, H["a"], us); draw_classic(img, line2, SCREEN[0] * .25, 480 - 20, H["c"], us)
    for b, hue in zip(res["bubbles"], [H["a"], H["c"]]): draw_comic(img, b, res["metrics"], res["kinds"], us, rim=hsv(hue, .7, .95))
    out["classic_vs_comic"] = img
    return out

CAPTIONS = {"overview": "Five variants: round, burst, dashed whisper, cloud thought, caption",
            "exchange": "Two-pawn exchange: tails point at each speaker",
            "edge": "Balloons pushed by screen edges: tails flip and re-angle",
            "classic_vs_comic": "Classic (left) vs Comic (right)"}

def main():
    args = [a for a in sys.argv[1:]]
    docs = None
    if "--docs" in args: i = args.index("--docs"); docs = args[i + 1]; del args[i:i + 2]
    outdir = args[0] if args else os.path.expanduser("~/.local/share/rimworld-hermes-engine/bubbles/preview")
    os.makedirs(outdir, exist_ok=True)
    big = None
    for us in (1, 2):
        sc = scenes(us)
        for name, img in sc.items():
            label(img, "%s  (UI scale %dx)" % (CAPTIONS[name], us), us)
            img.convert("RGB").save(os.path.join(outdir, "%s_%dx.png" % (name, us)))
        if us == 2:
            grid = Image.new("RGB", (SCREEN[0] * 2, SCREEN[1] * 2))
            for i, name in enumerate(["overview", "exchange", "edge", "classic_vs_comic"]):
                grid.paste(sc[name].convert("RGB").resize(SCREEN, Image.LANCZOS), ((i % 2) * SCREEN[0], (i // 2) * SCREEN[1]))
            big = grid
    big.save(os.path.join(outdir, "overview_grid.png"))
    if docs: big.save(docs, optimize=True)
    print("saved to", outdir)

if __name__ == "__main__":
    main()
