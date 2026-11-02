"""Génère sprites/chouette/*.png et sprites/magicien/*.png (one-off, les PNG restent la source éditable)."""
import sys
from pathlib import Path
from PIL import Image

out_root = Path(sys.argv[1])
PAL = [None, (26, 28, 44), (56, 183, 100), (37, 113, 121), (167, 240, 112),
       (244, 244, 244), (8, 8, 16), (239, 125, 87), (86, 108, 134), (148, 176, 194),
       (150, 95, 50), (95, 60, 35), (235, 205, 150), (255, 200, 40), (70, 90, 200), (40, 50, 120), (250, 200, 160)]
BROWN, DBROWN, CREAM, YELLOW, BLUE, DBLUE, SKIN = 10, 11, 12, 13, 14, 15, 16
OUT, WHITE, BLACK, BLUSH, HS, HS2 = 1, 5, 6, 7, 8, 9

def ell(x, y, cx, cy, rx, ry): return ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1
def tri(x, y, a, b, c):
    def s(p, q, r): return (p[0] - r[0]) * (q[1] - r[1]) - (q[0] - r[0]) * (p[1] - r[1])
    d1, d2, d3 = s((x, y), a, b), s((x, y), b, c), s((x, y), c, a)
    return not ((d1 < 0 or d2 < 0 or d3 < 0) and (d1 > 0 or d2 > 0 or d3 > 0))
def shaded(inside, x, y, base, shade, d=1.4): return shade if not inside(x + d, y + d) else base

FRAMES = {
    "idle": {}, "look": {"look": 1}, "blink": {"eyes": "closed"},
    "squash": {"sx": 1.12, "sy": 0.85, "eyes": "closed"},
    "jump": {"dy": -3, "sx": 0.95, "mouth": "open"},
    "away1": {"eyes": "closed", "zzz": 1}, "away2": {"eyes": "closed", "zzz": 2, "sy": 1.04},
    "call1": {"headset": True}, "call2": {"headset": True, "mouth": "talk"},
    "offline": {"sx": 1.15, "sy": 0.78, "eyes": "closed"},
}

def headset(x, y, ear_l, ear_r, band_cy, band_r, mic_to):
    # écouteurs : rectangles 9 bordés de 8 ; arceau : anneau supérieur ; micro : segment vers la bouche
    for (x0, x1) in (ear_l, ear_r):
        if x0 <= x <= x1 and 14 <= y <= 22:
            return HS2 if x0 + 0.8 < x < x1 - 0.8 and 15 < y < 21 else HS
    if y < band_cy and band_r - 1.6 <= ((x - 16) ** 2 + (y - band_cy) ** 2) ** 0.5 <= band_r: return HS
    mx, my = mic_to
    sx, sy = (ear_l[0] + ear_l[1]) / 2, 22
    t = (x - sx) / (mx - sx) if mx != sx else -1
    if 0 <= t <= 1 and abs(y - (sy + (my - sy) * t * t)) < 0.8: return HS2 if t > 0.8 else HS
    return 0

def owl(x, y, p):
    eyes, look, mouth = p.get("eyes", "open"), p.get("look", 0), p.get("mouth")
    wy = 18 if p.get("dy", 0) < 0 else 22
    body = lambda a, b: ell(a, b, 16, 20, 9.5, 10.5)
    if p.get("headset"):
        if c := headset(x, y, (3.5, 6.5), (25.5, 28.5), 19, 12.3, (12, 24)): return c
    for c in (12.5, 19.5):
        if eyes == "closed" and abs(y - (16.4 + 0.12 * (x - c) ** 2)) < 0.8 and abs(x - c) < 2.6: return BLACK
        if eyes != "closed" and ell(x, y, c + look, 17, 1.4, 1.5): return BLACK
        if eyes != "closed" and ell(x, y, c, 16.6, 2.7, 2.8): return WHITE
    if tri(x, y, (14.6, 19.2), (17.4, 19.2), (16, 21.8)): return YELLOW
    if mouth in ("open", "talk") and ell(x, y, 16, 22.4, 1.2, 0.9 if mouth == "talk" else 1.3): return BLACK
    for c in (12.5, 19.5):
        if ell(x, y, c, 16.8, 4.3, 4.3): return CREAM
    for c in (7.2, 24.8):
        if ell(x, y, c, wy, 2.3, 5.8): return DBROWN
    if ell(x, y, 12.8, 30.4, 1.6, 0.9) or ell(x, y, 19.2, 30.4, 1.6, 0.9): return YELLOW
    if body(x, y):
        if ell(x, y, 16, 26, 5.2, 4.6):
            return DBROWN if any(abs(x - cx) < 0.6 and abs(y - cy) < 0.5 for cx, cy in ((14, 24.5), (18, 24.5), (16, 27))) else CREAM
        return shaded(body, x, y, BROWN, DBROWN)
    return 0

def wizard(x, y, p):
    eyes, look, mouth = p.get("eyes", "open"), p.get("look", 0), p.get("mouth")
    cone = lambda a, b: 0 <= b <= 11 and abs(a - (16 + 4 * ((11 - b) / 11) ** 2)) < 6.5 * (b / 11) + 0.4
    if (abs(x - 15.5) < 0.6 and abs(y - 6.5) < 1.6) or (abs(x - 15.5) < 1.6 and abs(y - 6.5) < 0.6): return YELLOW
    if cone(x, y): return DBLUE if x - (16 + 4 * ((11 - y) / 11) ** 2) > 6.5 * (y / 11) * 0.35 else BLUE
    if ell(x, y, 16, 11.4, 10, 1.7): return shaded(lambda a, b: ell(a, b, 16, 11.4, 10, 1.7), x, y, BLUE, DBLUE, 0.9)
    if p.get("headset"):
        if c := headset(x, y, (6.5, 9.5), (22.5, 25.5), 99, 0, (12.5, 21)): return c
    for c in (13.2, 18.8):
        if abs(y - 13) < 0.7 and abs(x - c) < 1.9: return WHITE
        if eyes == "closed" and abs(y - 15.8) < 0.8 and abs(x - c) < 1.4: return BLACK
        if eyes != "closed" and abs(x - c - look) < 1 and abs(y - 15.3) < 1.2:
            return WHITE if x < c + look and y < 15 else BLACK
    if mouth in ("open", "talk") and ell(x, y, 16, 21, 1.3, 0.8 if mouth == "talk" else 1.2): return BLACK
    beard = lambda a, b: ell(a, b, 16, 17.6, 7.6, 1.3) or (b >= 17.6 and ell(a, b, 16, 20.5, 7.6, 7)) or tri(a, b, (11, 25), (21, 25), (16, 30.8))
    if beard(x, y): return shaded(beard, x, y, WHITE, HS2)
    if ell(x, y, 11, 16.8, 0.9, 0.6) or ell(x, y, 21, 16.8, 0.9, 0.6): return BLUSH
    if ell(x, y, 16, 15.5, 6.6, 5.6): return SKIN
    robe = lambda a, b: ell(a, b, 16, 24.5, 10, 6)
    if robe(x, y): return shaded(robe, x, y, BLUE, DBLUE)
    return 0

def zzz(img, n):
    def z(x0, y0):
        for i in range(4): img[y0][x0 + i] = img[y0 + 3][x0 + i] = WHITE
        img[y0 + 1][x0 + 2] = img[y0 + 2][x0 + 1] = WHITE
    z(25, 5 if n == 1 else 4)
    if n == 2: z(28, 0)

def render(fn, p):
    sx, sy, dy = p.get("sx", 1), p.get("sy", 1), p.get("dy", 0)
    img = [[0] * 32 for _ in range(32)]
    for py in range(32):
        for px in range(32):
            mx = 16 + (px + 0.5 - 16) / sx
            my = 30.5 - (30.5 - (py + 0.5)) / sy - dy
            img[py][px] = fn(mx, my, p)
    filled = [row[:] for row in img]
    for py in range(32):
        for px in range(32):
            if not filled[py][px] and any(0 <= px + a < 32 and 0 <= py + b < 32 and filled[py + b][px + a]
                                          for a, b in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                img[py][px] = OUT
    if p.get("zzz"): zzz(img, p["zzz"])
    return img

for name, fn in (("chouette", owl), ("magicien", wizard)):
    d = out_root / name
    d.mkdir(parents=True, exist_ok=True)
    for frame, p in FRAMES.items():
        img = render(fn, p)
        im = Image.new("RGBA", (32, 32))
        for y in range(32):
            for x in range(32):
                if img[y][x]: im.putpixel((x, y), PAL[img[y][x]] + (255,))
        im.save(d / f"{frame}.png")
print("ok")
