// Boîtier OpenCompanion pour Waveshare ESP32-S3-LCD-1.69 (non tactile).
// Modélisé en position d'impression : face avant sur le plateau (z = 0).
// Vu de face : USB-C à gauche, pied en bas. Boutons accessibles en retirant le couvercle.
// Export : openscad -D 'part="coque"' -o coque.stl boitier.scad  (idem "couvercle")

part = "tout";        // "coque", "couvercle", "tout"

// --- Carte (cotes Waveshare, mm) ---
pcb_w = 30;           // largeur
pcb_h = 37.12;        // hauteur
pcb_t = 1.0;          // épaisseur PCB
holes = [26, 29];     // entraxe des 4 trous
glass = [27.97, 32.63];
glass_top = 0;        // écart haut de la vitre / haut du PCB
front_stack = 2.6;    // vitre + LCD devant le PCB  -> à mesurer
back_gap = 4.6;       // composants derrière le PCB (connecteur BAT)  -> à mesurer
usb_y = 19;           // centre USB-C depuis le haut de la carte

// --- Boîtier ---
tol = 0.3;            // jeu autour de la carte
wall = 2;
front = 1.2;          // épaisseur de la face avant
lip = 1.0;            // recouvrement du bord de la vitre
lid_t = 1.6;
lid_fit = 0.15;       // jeu du couvercle (augmenter s'il force)
snap_l = 14;          // longueur des bossages de clipsage du couvercle
r = 3;                // arrondi extérieur
foot_h = 12;          // pied plein sous la carte, contient la mortaise

// --- Mortaise de clipsage (dessous) ---
sock = [12, 5, 9];    // X, Z, profondeur
detent_y = 5;         // hauteur des crans depuis le dessous
detent = [0.8, 2];    // profondeur, hauteur des crans

$fn = 48;

cav = [pcb_w + 2*tol, pcb_h + 2*tol, front_stack + pcb_t + back_gap];
W = cav.x + 2*wall;
H = foot_h + cav.y + wall;
D = front + cav.z + lid_t;
pcb_top = foot_h + tol + pcb_h;           // y du haut de la carte
pcb_back = front + front_stack + pcb_t;   // z du dos de la carte

module rbox(s, rr) {
    hull() for (x = [rr, s.x - rr], y = [rr, s.y - rr]) translate([x, y, 0]) cylinder(r = rr, h = s.z);
}

module coque() {
    difference() {
        rbox([W, H, D], r);
        // logement carte, ouvert à l'arrière
        translate([wall, foot_h, front]) cube([cav.x, cav.y, D]);
        // fenêtre écran
        translate([(W - glass.x)/2 + lip, pcb_top - glass_top - glass.y + lip, -1])
            rbox([glass.x - 2*lip, glass.y - 2*lip, front + 2], 3);
        // USB-C (côté +X), fiche comprise
        translate([W - wall - 1, pcb_top - usb_y - 6.25, pcb_back - 1.6])
            cube([wall + 2, 12.5, 7]);
        // rainures où se clipse le couvercle (haut et bas, loin de l'USB-C)
        for (y = [foot_h, foot_h + cav.y])
            translate([W/2 - snap_l/2 - 0.5, y, D - lid_t/2]) rotate([0, 90, 0]) cylinder(r = 0.6, h = snap_l + 1);
        // encoche pour soulever le couvercle (tournevis plat)
        translate([wall - 1, H/2 - 3, D - lid_t - 0.5]) cube([1.01, 6, lid_t + 1]);
        // mortaise + crans
        translate([(W - sock.x)/2, -1, (D - sock.y)/2]) cube([sock.x, sock[2] + 1, sock.y]);
        translate([(W - sock.x)/2 - detent[0], detent_y, (D - sock.y)/2])
            cube([sock.x + 2*detent[0], detent[1], sock.y]);
    }
}

module couvercle() {
    s = [cav.x - 2*lid_fit, cav.y - 2*lid_fit];
    cube([s.x, s.y, lid_t]);
    // bossages de clipsage, en face des rainures de la coque
    for (y = [0, s.y])
        translate([s.x/2 - snap_l/2, y, lid_t/2]) rotate([0, 90, 0]) cylinder(r = 0.5, h = snap_l);
    // entretoises (plaquent la carte contre la face avant) + pions dans ses trous
    for (dx = [-1, 1], dy = [-1, 1])
        translate([s.x/2 + dx*holes.x/2, s.y/2 + dy*holes.y/2, lid_t - 0.01]) {
            cylinder(d = 3.6, h = back_gap);
            cylinder(d = 1.8, h = back_gap + pcb_t);
        }
}

// Tenon à unir au support de l'écran (imprimé debout, fente = ressort).
module tenon(play = 0.2) {
    w = sock.x - 2*play; t = sock.y - 2*play; l = sock[2] - 0.5;
    difference() {
        translate([-w/2, -t/2, 0]) cube([w, t, l]);
        translate([-0.75, -t, l - 7]) cube([1.5, 2*t, 8]);
    }
    // crans en rampe : la pente entre en premier
    for (m = [0, 1]) mirror([m, 0, 0])
        translate([w/2 - 0.01, -t/2, detent_y + 0.3]) hull() {
            cube([detent[0] - 0.1, t, 0.4]);
            cube([0.01, t, detent[1] - 0.6]);
        }
}

if (part == "coque") coque();
else if (part == "couvercle") couvercle();
else if (part == "tenon") tenon();
else {
    coque();
    translate([W + 5, 0, 0]) couvercle();
    translate([-15, 10, 0]) tenon();
}
