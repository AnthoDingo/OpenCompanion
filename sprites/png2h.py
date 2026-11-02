"""Convertit sprites/<compagnon>/*.png (32x32, couleurs de PALETTE, fond transparent) en sprites/sprites.h.

Usage : python sprites/png2h.py   (à relancer après chaque modif d'un PNG)
Le nom du fichier donne le nom de la frame : idle.png -> F_IDLE. Chaque compagnon a les mêmes frames.
L'ordre de COMPANIONS donne le numéro envoyé par l'appli (STATUT|Libellé|N) : ne pas le changer, ajouter à la fin.
Recopiez ensuite sprites.h dans src/ du dépôt OpenCompanion-Firmware.
"""
import sys
from pathlib import Path
from PIL import Image

COMPANIONS = ["pousse", "chouette", "magicien", "trombone", "calculatrice"]

# Index 2-4 = corps de Pousse (base, ombre, reflet) : l'ESP les recolore en violet Teams.
# 10-16 : chouette (brun, brun foncé, crème, jaune) et magicien (bleu, bleu foncé, peau).
# Trombone n'utilise que des couleurs communes (gris acier 8-9, blanc 5, noir 6, bleu foncé 15).
# 17 : écran LCD de la calculatrice (corps bleu 14-15, touches blanches 5 et orange 7).
PALETTE = [None, (26, 28, 44), (56, 183, 100), (37, 113, 121), (167, 240, 112),
           (244, 244, 244), (8, 8, 16), (239, 125, 87), (86, 108, 134), (148, 176, 194),
           (150, 95, 50), (95, 60, 35), (235, 205, 150), (255, 200, 40), (70, 90, 200), (40, 50, 120), (250, 200, 160),
           (160, 190, 140)]
SIZE = 32

here = Path(__file__).parent


def load(f):
    img = Image.open(f).convert("RGBA")
    if img.size != (SIZE, SIZE):
        sys.exit(f"{f} : {img.size[0]}x{img.size[1]}, attendu {SIZE}x{SIZE}")
    px = []
    for i, (r, g, b, a) in enumerate(zip(*[iter(img.tobytes())] * 4)):
        if a < 128:
            px.append(0)
        elif (r, g, b) in PALETTE:
            px.append(PALETTE.index((r, g, b)))
        else:
            sys.exit(f"{f} : couleur {(r, g, b)} hors palette en ({i % SIZE}, {i // SIZE})")
    return px


sets = {c: {"F_" + f.stem.upper(): load(f) for f in sorted((here / c).glob("*.png"))} for c in COMPANIONS}
names = list(sets[COMPANIONS[0]])
for c, frames in sets.items():
    if list(frames) != names:
        sys.exit(f"{c} : frames {sorted(frames)}, attendu {sorted(names)}")


def rgb565(c):
    return 0 if c is None else ((c[0] & 0xF8) << 8) | ((c[1] & 0xFC) << 3) | (c[2] >> 3)


out = ["// Généré par sprites/png2h.py, ne pas éditer à la main.", "#pragma once", "#include <stdint.h>", "",
       f"const int SPR_SIZE = {SIZE};",
       "const uint16_t SPR_PALETTE[] = {" + ", ".join(f"0x{rgb565(c):04X}" for c in PALETTE) + "};",
       "enum Frame : uint8_t { " + ", ".join(names) + ", F_COUNT };",
       "enum Companion : uint8_t { " + ", ".join("C_" + c.upper() for c in COMPANIONS) + ", C_COUNT };",
       f"const uint8_t SPR_FRAMES[C_COUNT][F_COUNT][{SIZE * SIZE}] = {{"]
for c, frames in sets.items():
    out.append(f"  {{ // C_{c.upper()}")
    for name, px in frames.items():
        out.append(f"    {{ // {name}")
        out += ["      " + ",".join(map(str, px[y * SIZE:(y + 1) * SIZE])) + "," for y in range(SIZE)]
        out.append("    },")
    out.append("  },")
out.append("};")
(here / "sprites.h").write_text("\n".join(out) + "\n", encoding="utf-8")
print(f"{len(COMPANIONS)} compagnons x {len(names)} frames -> sprites/sprites.h (à recopier dans src/ du dépôt firmware)")
