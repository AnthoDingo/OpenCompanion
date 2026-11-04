"""Convertit sprites/<compagnon>/*.png (32x32, couleurs de PALETTE, fond transparent) en en-têtes C du firmware :
src/sprites.h (palette, enums, table SPR_FRAMES) et src/companions/<compagnon>.h (les frames d'un compagnon).

Usage : python sprites/png2h.py [dossier src du firmware]   (à relancer après chaque modif d'un PNG)
Par défaut : ../OpenCompanion-Firmware/src, le dépôt firmware cloné à côté de celui-ci.
Le nom du fichier donne le nom de la frame : idle.png -> F_IDLE. Chaque compagnon a les mêmes frames.
L'ordre de COMPANIONS donne le numéro envoyé par l'appli (STATUT|Libellé|N) : ne pas le changer, ajouter à la fin.
"""
import sys
from pathlib import Path
from PIL import Image

COMPANIONS = ["pousse", "chouette", "magicien", "trombone", "calculatrice", "panda"]

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


src = Path(sys.argv[1]) if len(sys.argv) > 1 else here.parent.parent / "OpenCompanion-Firmware" / "src"
if not (src / "main.cpp").exists():
    sys.exit(f"{src} : pas de main.cpp, donnez le dossier src du dépôt OpenCompanion-Firmware")
HEADER = "// Généré par sprites/png2h.py (dépôt OpenCompanion), ne pas éditer à la main."

# Un fichier par compagnon : SPR_<NOM>[F_COUNT][...], inclus par sprites.h qui définit Frame et SPR_SIZE.
(src / "companions").mkdir(exist_ok=True)
for c, frames in sets.items():
    out = [HEADER, "#pragma once", "", f"const uint8_t SPR_{c.upper()}[F_COUNT][SPR_SIZE * SPR_SIZE] = {{"]
    for name, px in frames.items():
        out.append(f"  {{ // {name}")
        out += ["    " + ",".join(map(str, px[y * SIZE:(y + 1) * SIZE])) + "," for y in range(SIZE)]
        out.append("  },")
    out.append("};")
    (src / "companions" / f"{c}.h").write_text("\n".join(out) + "\n", encoding="utf-8")

out = [HEADER, "#pragma once", "#include <stdint.h>", "",
       f"const int SPR_SIZE = {SIZE};",
       "const uint16_t SPR_PALETTE[] = {" + ", ".join(f"0x{rgb565(c):04X}" for c in PALETTE) + "};",
       "enum Frame : uint8_t { " + ", ".join(names) + ", F_COUNT };",
       "enum Companion : uint8_t { " + ", ".join("C_" + c.upper() for c in COMPANIONS) + ", C_COUNT };", ""]
out += [f'#include "companions/{c}.h"' for c in COMPANIONS]
out += ["", "// SPR_FRAMES[compagnon][frame] : pixels (index de SPR_PALETTE) de la frame, ligne par ligne.",
        "const uint8_t (*const SPR_FRAMES[C_COUNT])[SPR_SIZE * SPR_SIZE] = {" + ", ".join(f"SPR_{c.upper()}" for c in COMPANIONS) + "};"]
(src / "sprites.h").write_text("\n".join(out) + "\n", encoding="utf-8")
print(f"{len(COMPANIONS)} compagnons x {len(names)} frames -> {src / 'sprites.h'} + companions/*.h")
