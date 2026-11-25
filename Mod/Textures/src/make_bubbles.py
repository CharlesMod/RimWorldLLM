#!/usr/bin/env python3
"""Generate the speech-balloon textures under Mod/Textures/HermesVoice/Bubbles/.

Run:  python3 -m venv --system-site-packages $SCRATCH/venv && $SCRATCH/venv/bin/python Mod/Textures/src/make_bubbles.py
Needs Pillow only (no numpy). Constants come from bubble_metrics.json (shared with the C# code and preview.py).

Every shape yields three white-on-transparent layers that the game tints at draw time:
  <shape>_fill  the balloon body          (tinted off-white / caption yellow)
  <shape>_ink   the outline, OUTSIDE the body (tinted dark ink)
  <shape>_rim   a thin band just INSIDE the body edge (tinted with the speaker's colour)
Atlases are 9-slice: size = 2*border + mid. For "tiled" shapes the middle strip is exactly one period of the
edge pattern (spike, bump, dash) and the game repeats it; the layout snaps balloon sizes to whole periods.
"""
import json, math, os, sys
from PIL import Image, ImageDraw, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "HermesVoice", "Bubbles")
M = json.load(open(os.path.join(HERE, "bubble_metrics.json")))
B, INK, RIM = M["border"], M["ink"], M["rim"]
SS = 4  # supersampling

def new(w, h):
    return Image.new("L", (w * SS, h * SS), 0)

def poly(draw, pts):
    draw.polygon([(x * SS, y * SS) for x, y in pts], fill=255)

def disk_offsets(r):
    pts = []
    for k in range(1, 5):
        rr = r * k / 4.0
        n = max(8, int(2 * math.pi * rr))
        for i in range(n):
            a = 2 * math.pi * i / n
            pts.append((round(rr * math.cos(a)), round(rr * math.sin(a))))
    return sorted(set(pts))

def dilate(img, r_src):
    r = r_src * SS
    out = img.copy()
    for dx, dy in disk_offsets(r):
        out = ImageChops.lighter(out, ImageChops.offset(img, dx, dy))
    return out

def erode(img, r_src):
    return ImageChops.invert(dilate(ImageChops.invert(img), r_src))

# ---------------------------------------------------------------- body shapes on a canvas with 3 middle periods
def body_balloon(W, H, v, radius):
    im = new(W, H); d = ImageDraw.Draw(im)
    d.rounded_rectangle([v * SS, v * SS, (W - v) * SS - 1, (H - v) * SS - 1], radius * SS, fill=255)
    return im

def body_burst(W, H, v, spike, P):
    im = new(W, H); d = ImageDraw.Draw(im)
    tip = v - spike
    ct = tip + 1  # corner tip
    pts = [(ct, ct)]
    def edge_pts(n_start, n_end, fixed_out, fixed_in, horizontal, forward):
        out = []
        n = int(round((n_end - n_start) / P))
        for k in range(n):
            a = n_start + k * P
            mid = a + P / 2
            seq = [(a, fixed_in), (mid, fixed_out)]
            out += seq
        return out
    # top edge left->right
    n = int(round((W - 2 * B) / P))
    for k in range(n):
        a = B + k * P
        pts += [(a, v), (a + P / 2, tip)]
    pts += [(W - B, v), (W - ct, ct)]
    for k in range(n_v := int(round((H - 2 * B) / P))):
        a = B + k * P
        pts += [(W - v, a), (W - tip, a + P / 2)]
    pts += [(W - v, H - B), (W - ct, H - ct)]
    for k in range(n):
        a = W - B - k * P
        pts += [(a, H - v), (a - P / 2, H - tip)]
    pts += [(B, H - v), (ct, H - ct)]
    for k in range(n_v):
        a = H - B - k * P
        pts += [(v, a), (tip, a - P / 2)]
    pts += [(v, B)]
    poly(d, pts)
    return im

def body_cloud(W, H, v, P):
    im = new(W, H); d = ImageDraw.Draw(im)
    r = P / 2.0
    tip = v
    cy = tip + r
    def circ(cx, cy_, rr):
        d.ellipse([(cx - rr) * SS, (cy_ - rr) * SS, (cx + rr) * SS - 1, (cy_ + rr) * SS - 1], fill=255)
    nx = int(round((W - 2 * B) / P)); ny = int(round((H - 2 * B) / P))
    for k in range(nx):
        cx = B + k * P + r
        circ(cx, tip + r, r); circ(cx, H - tip - r, r)
    for k in range(ny):
        cyy = B + k * P + r
        circ(tip + r, cyy, r); circ(W - tip - r, cyy, r)
    rc = (B - tip) / 2.0   # corner bump fits inside the border
    for cx, cyy in ((tip + rc, tip + rc), (W - tip - rc, tip + rc), (tip + rc, H - tip - rc), (W - tip - rc, H - tip - rc)):
        circ(cx, cyy, rc)
    # solid core
    d.rectangle([(tip + r) * SS, (tip + r) * SS, (W - tip - r) * SS, (H - tip - r) * SS], fill=255)
    d.rectangle([(tip + rc) * SS, (tip + r) * SS, (W - tip - rc) * SS, (H - tip - r) * SS], fill=255)
    d.rectangle([(tip + r) * SS, (tip + rc) * SS, (W - tip - r) * SS, (H - tip - rc) * SS], fill=255)
    return im

def dash_mask(W, H, cfg):
    P, dash, v, R = cfg["mid"], cfg["dash"], cfg["margin"], cfg["radius"]
    im = new(W, H); d = ImageDraw.Draw(im)
    def rect(x0, y0, x1, y1):
        d.rectangle([x0 * SS, y0 * SS, x1 * SS - 1, y1 * SS - 1], fill=255)
    n = int(round((W - 2 * B) / P)); nv = int(round((H - 2 * B) / P))
    for k in range(n):
        a = B + k * P
        rect(a, 0, a + dash, B); rect(a, H - B, a + dash, H)
    for k in range(nv):
        a = B + k * P
        rect(0, a, B, a + dash); rect(W - B, a, W, a + dash)
    # corner arcs. (centre, seam angle for each half-arc, diagonal angle, 'before' = the corner precedes the seam)
    arc = math.pi * R / 2
    corners = [((B, B), [(270, 225, True), (180, 225, True)]),
               ((W - B, B), [(270, 315, False), (0, 315, True)]),
               ((B, H - B), [(90, 135, False), (180, 135, False)]),
               ((W - B, H - B), [(90, 45, False), (0, 45, False)])]
    # a half-arc is delta in [0, arc/2]; delta measured from the seam; dash where the periodic pattern says so
    for (cx, cy), halves in corners:
        for seam, diag, before in halves:
            half = arc / 2
            lo, hi = (P - dash, P) if before else (0, dash)
            # scan delta in 0.25 steps for the dash interval within [0, half]
            a0, a1 = max(lo, 0), min(hi, half)
            if a1 <= a0: continue
            sgn = 1 if ((diag - seam + 540) % 360 - 180) > 0 else -1
            t0 = seam + sgn * math.degrees(a0 / R); t1 = seam + sgn * math.degrees(a1 / R)
            s, e = sorted((t0, t1))
            rr = R + INK + 6
            d.pieslice([(cx - rr) * SS, (cy - rr) * SS, (cx + rr) * SS, (cy + rr) * SS], s, e, fill=255)
    return im

# ---------------------------------------------------------------- layers + atlas assembly
def layers(body, ink_mask=None):
    ink = ImageChops.subtract(dilate(body, INK), body)
    if ink_mask is not None:
        ink = ImageChops.multiply(ink, ink_mask)
    rim = ImageChops.subtract(body, erode(body, RIM))
    return body, ink, rim

def to_rgba(mask, size):
    m = mask.resize(size, Image.LANCZOS)
    out = Image.new("RGBA", size, (255, 255, 255, 0))
    out.putalpha(m)
    return out

def atlas(canvas, mid, S):
    """Crop corners from the 3-period canvas and the middle strip from period 1; assemble B+mid+B."""
    ssB, ssM = B * SS, mid * SS
    W, H = canvas.size
    out = Image.new("L", ((2 * B + mid) * SS, (2 * B + mid) * SS), 0)
    xs = [(0, ssB, 0), (ssB + ssM, ssB + 2 * ssM, ssB), (W - ssB, W, ssB + ssM)]
    ys = [(0, ssB, 0), (ssB + ssM, ssB + 2 * ssM, ssB), (H - ssB, H, ssB + ssM)]
    # Source x ranges for the three columns: left corner, mid period, right corner
    src_x = [(0, ssB), (ssB + ssM, ssB + 2 * ssM), (W - ssB, W)]
    src_y = [(0, ssB), (ssB + ssM, ssB + 2 * ssM), (H - ssB, H)]
    dst_x = [0, ssB, ssB + ssM]; dst_y = [0, ssB, ssB + ssM]
    for i in range(3):
        for j in range(3):
            crop = canvas.crop((src_x[i][0], src_y[j][0], src_x[i][1], src_y[j][1]))
            out.paste(crop, (dst_x[i], dst_y[j]))
    return out

def make_shape(name, cfg):
    mid = cfg["mid"]
    Wc = 2 * B + 3 * mid; Hc = Wc
    v = cfg["margin"]
    if name in ("balloon", "caption"):
        body = body_balloon(Wc, Hc, v, cfg["radius"]); ink_mask = None
    elif name == "burst":
        body = body_burst(Wc, Hc, v, cfg["spike"], mid); ink_mask = None
    elif name == "cloud":
        body = body_cloud(Wc, Hc, v, mid); ink_mask = None
    elif name == "dashed":
        body = body_balloon(Wc, Hc, v, cfg["radius"]); ink_mask = dash_mask(Wc, Hc, cfg)
    fill, ink, rim = layers(body, ink_mask)
    S = 2 * B + mid
    for tag, img in (("fill", fill), ("ink", ink), ("rim", rim)):
        a = atlas(img, mid, S)
        to_rgba(a, (S, S)).save(os.path.join(OUT, "%s_%s.png" % (name, tag)))

def make_tail():
    w, h, stub = M["tail"]["width"], M["tail"]["height"], M["tail"]["stub"]
    PAD = 12  # blank rows above the stub so outline dilation never wraps; cropped away afterwards
    # Tail points down; the top 'stub' rows overlap into the balloon body. Slight S-curve, tip near the bottom.
    def bez(p0, p1, p2, n=24):
        return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
                 (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]) for t in (i / n for i in range(n + 1))]
    tip = (w / 2 + 7, PAD + h - 14)
    left = bez((w / 2 - 20, PAD), (w / 2 - 14, PAD + h * 0.60), tip)
    right = bez(tip, (w / 2 + 16, PAD + h * 0.30), (w / 2 + 20, PAD))
    body = new(w, h + PAD + 8); d = ImageDraw.Draw(body)
    poly(d, left + right)
    ink = ImageChops.subtract(dilate(body, INK), body)
    rim = ImageChops.subtract(body, erode(body, RIM))
    cut = Image.new("L", body.size, 255); ImageDraw.Draw(cut).rectangle([0, 0, w * SS, (PAD + stub + 4) * SS], fill=0)
    rim = ImageChops.multiply(rim, cut)
    box = (0, PAD * SS, w * SS, (PAD + h) * SS)
    for tag, img in (("fill", body), ("ink", ink), ("rim", rim)):
        to_rgba(img.crop(box), (w, h)).save(os.path.join(OUT, "tail_%s.png" % tag))

def make_dot():
    s = M["dot"]["size"]
    body = new(s, s); d = ImageDraw.Draw(body)
    m = INK + 3
    d.ellipse([m * SS, m * SS, (s - m) * SS - 1, (s - m) * SS - 1], fill=255)
    fill = body; ink = ImageChops.subtract(dilate(body, INK), body); rim = ImageChops.subtract(body, erode(body, RIM))
    for tag, img in (("fill", fill), ("ink", ink), ("rim", rim)):
        to_rgba(img, (s, s)).save(os.path.join(OUT, "dot_%s.png" % tag))

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    only = sys.argv[1:] 
    for name, cfg in M["shapes"].items():
        if only and name not in only: continue
        print("shape", name, flush=True); make_shape(name, cfg)
    if not only or "tail" in only: print("tail"); make_tail()
    if not only or "dot" in only: print("dot"); make_dot()
    print("done ->", os.path.normpath(OUT))
