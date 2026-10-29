"""Convertit sprites/*.png (32x32, couleurs de PALETTE, fond transparent) en sprites/sprites.h.

Usage : python sprites/png2h.py   (à relancer après chaque modif d'un PNG)
Le nom du fichier donne le nom de la frame : idle.png -> F_IDLE.
Recopiez ensuite sprites.h dans src/ du dépôt OpenCompanion-Firmware.
"""
import sys
from pathlib import Path
from PIL import Image

# Index 2-4 = corps (base, ombre, reflet) : l'ESP les recolore selon le statut.
PALETTE = [None, (26, 28, 44), (56, 183, 100), (37, 113, 121), (167, 240, 112),
           (244, 244, 244), (8, 8, 16), (239, 125, 87), (86, 108, 134), (148, 176, 194)]
SIZE = 32

here = Path(__file__).parent
frames = {}
for f in sorted(here.glob("*.png")):
    img = Image.open(f).convert("RGBA")
    if img.size != (SIZE, SIZE):
        sys.exit(f"{f.name} : {img.size[0]}x{img.size[1]}, attendu {SIZE}x{SIZE}")
    raw = img.tobytes()
    px = []
    for i, (r, g, b, a) in enumerate(zip(*[iter(raw)] * 4)):
        if a < 128:
            px.append(0)
        elif (r, g, b) in PALETTE:
            px.append(PALETTE.index((r, g, b)))
        else:
            sys.exit(f"{f.name} : couleur {(r, g, b)} hors palette en ({i % SIZE}, {i // SIZE})")
    frames["F_" + f.stem.upper()] = px


def rgb565(c):
    return 0 if c is None else ((c[0] & 0xF8) << 8) | ((c[1] & 0xFC) << 3) | (c[2] >> 3)


out = ["// Généré par sprites/png2h.py, ne pas éditer à la main.", "#pragma once", "#include <stdint.h>", "",
       f"const int SPR_SIZE = {SIZE};",
       "const uint16_t SPR_PALETTE[] = {" + ", ".join(f"0x{rgb565(c):04X}" for c in PALETTE) + "};",
       "enum Frame : uint8_t { " + ", ".join(frames) + ", F_COUNT };",
       f"const uint8_t SPR_FRAMES[F_COUNT][{SIZE * SIZE}] = {{"]
for name, px in frames.items():
    out.append(f"  {{ // {name}")
    out += ["    " + ",".join(map(str, px[y * SIZE:(y + 1) * SIZE])) + "," for y in range(SIZE)]
    out.append("  },")
out.append("};")
(here / "sprites.h").write_text("\n".join(out) + "\n", encoding="utf-8")
print(f"{len(frames)} frames -> sprites/sprites.h (à recopier dans src/ du dépôt firmware)")
