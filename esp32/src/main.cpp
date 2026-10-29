#include <TFT_eSPI.h>
#include "USB.h"
#include "USBHIDVendor.h"
#include "sprites.h"

// Libellés par défaut, en autonome : l'appli envoie les siens dans la langue choisie (STATUT|Libellé).
// Police 4 sans accents.
#define TXT_AVAILABLE "Disponible"
#define TXT_AWAY "Absent"
#define TXT_CALL "En appel"
#define TXT_VIDEO "En visio"
#define TXT_BUSY "Occupe"
#define TXT_DND "Ne pas deranger"

// Reçoit une ligne par statut (OFFLINE, AVAILABLE, AWAY, CALL, VIDEO, BUSY, DND) en HID USB,
// ou sur le port série USB pour tester à la main depuis un moniteur série, et anime le compagnon (sprites 32x32 de sprites/*.png, agrandis x SCALE).

// Version en BCD (0x0120 = 1.2), à garder égale à CompanionLink.ExpectedFirmware de l'appli PC.
#define FW_VERSION 0x0120

enum State { OFFLINE, AVAILABLE, AWAY, CALL, VIDEO, OCCUPIED, DND };

struct Anim { const uint8_t* frames; uint8_t count; uint16_t ms; };
const uint8_t A_AVAILABLE[] = {F_IDLE, F_IDLE, F_IDLE, F_LOOK, F_LOOK, F_IDLE, F_BLINK, F_IDLE,
                               F_IDLE, F_SQUASH, F_JUMP, F_JUMP, F_SQUASH, F_IDLE};
const uint8_t A_AWAY[] = {F_AWAY1, F_AWAY2};
const uint8_t A_CALL[] = {F_CALL1, F_CALL2, F_CALL1, F_CALL2, F_CALL2, F_CALL1};
const uint8_t A_BUSY[] = {F_IDLE, F_IDLE, F_LOOK, F_LOOK, F_IDLE, F_BLINK};
const uint8_t A_OFFLINE[] = {F_OFFLINE};

const int SCALE = 6;  // 32 x 6 = 192 px

USBHIDVendor hid;
USBCDC usbSerial;
TFT_eSPI tft;
TFT_eSprite spr(&tft);
State state = OFFLINE;
uint32_t lastMsg = 0;
String line;
String recvLabel;               // libellé localisé reçu de l'appli (vide = libellé par défaut)
State recvLabelState = OFFLINE;  // statut auquel recvLabel se rapporte

State parse(const String& s) {
  if (s == "AVAILABLE") return AVAILABLE;
  if (s == "AWAY") return AWAY;
  if (s == "CALL") return CALL;
  if (s == "VIDEO") return VIDEO;
  if (s == "BUSY") return OCCUPIED;
  if (s == "DND") return DND;
  return OFFLINE;
}

void readLines(Stream& in) {
  while (in.available()) {
    char c = in.read();
    if (c == 0) continue;  // rapports HID complétés par des zéros
    if (c == '\n') {
      line.trim();
      // Format "STATUT" ou "STATUT|Libellé" : l'appli PC envoie le texte localisé après la barre.
      int bar = line.indexOf('|');
      state = parse(bar < 0 ? line : line.substring(0, bar));
      recvLabel = bar < 0 ? "" : line.substring(bar + 1);  // vide => libellé compilé (test au moniteur série)
      recvLabelState = state;
      lastMsg = millis();
      line = "";
    } else if (line.length() < 32) {  // "AVAILABLE|Videochiamata" et autres libellés localisés
      line += c;
    }
  }
}

void readStatus() {
  readLines(hid);
  readLines(usbSerial);
  // Le PC renvoie toutes les 10 s : silence prolongé => PC éteint ou en veille.
  if (millis() - lastMsg > 35000) state = OFFLINE;
}

void drawFrame(Frame f, int ox, int oy, const uint16_t* pal) {
  const uint8_t* p = SPR_FRAMES[f];
  for (int y = 0; y < SPR_SIZE; y++)
    for (int x = 0; x < SPR_SIZE; x++)
      if (uint8_t c = p[y * SPR_SIZE + x]) spr.fillRect(ox + x * SCALE, oy + y * SCALE, SCALE, SCALE, pal[c]);
}

// Panneau stop octogonal (bord blanc) sur un manche, centré en (cx, cy).
void drawStop(int cx, int cy) {
  const int r = 44;
  spr.fillRect(cx - 4, cy, 8, 80, TFT_LIGHTGREY);
  for (int k = 0; k < 8; k++)
    for (int ring = 0; ring < 2; ring++) {
      float a0 = (22.5f + 45 * k) * DEG_TO_RAD, a1 = a0 + 45 * DEG_TO_RAD;
      int rr = ring ? r - 5 : r;
      spr.fillTriangle(cx, cy, cx + rr * cosf(a0), cy + rr * sinf(a0), cx + rr * cosf(a1), cy + rr * sinf(a1),
                       ring ? tft.color24to16(0xE81123) : TFT_WHITE);
    }
  spr.setTextColor(TFT_WHITE);
  spr.setTextDatum(MC_DATUM);
  spr.drawString("STOP", cx, cy, 4);
}

// Main du compagnon (contour, corps, reflet de sa palette) qui tient le manche.
void drawHand(int x, int y, const uint16_t* pal) {
  spr.fillCircle(x, y, 10, pal[1]);
  spr.fillCircle(x, y, 8, pal[2]);
  spr.fillCircle(x - 3, y - 3, 3, pal[4]);
}

// OFFLINE : câble qui sort de derrière le compagnon (bas droit) et finit par une prise débranchée posée au sol.
// Coordonnées pour le sprite F_OFFLINE en (ox, oy).
void drawPlug(int ox, int oy) {
  const float x0 = ox + 25 * SCALE, y0 = oy + 26 * SCALE, x1 = ox + 25 * SCALE, y1 = oy + 36 * SCALE,
              x2 = ox + 28 * SCALE, y2 = oy + 33 * SCALE;  // courbe de Bézier : départ, creux, arrivée
  for (int i = 0; i <= 24; i++) {
    float k = i / 24.f, a = (1 - k) * (1 - k), b = 2 * k * (1 - k), c = k * k;
    spr.fillCircle(a * x0 + b * x1 + c * x2, a * y0 + b * y1 + c * y2, 3, TFT_BLACK);
  }
  int px = x2, py = y2;
  spr.fillRoundRect(px - 2, py - 9, 20, 18, 4, tft.color24to16(0x444444));
  spr.fillRect(px + 18, py - 6, 9, 4, TFT_LIGHTGREY);
  spr.fillRect(px + 18, py + 2, 9, 4, TFT_LIGHTGREY);
}

// Rend true pendant l'animation du panneau, pour que loop() redessine sans attendre.
bool draw(uint32_t t) {
  static State shown = OFFLINE;
  static uint32_t since = 0;  // début de l'état courant, pour l'animation du panneau
  if (state != shown) { shown = state; since = t; }

  const int w = spr.width(), h = spr.height();
  Anim anim = {A_OFFLINE, 1, 1000};
  uint16_t pal[sizeof SPR_PALETTE / sizeof *SPR_PALETTE];
  memcpy(pal, SPR_PALETTE, sizeof pal);
  const char* label = "";  // hors ligne : pas de texte
  uint32_t bg = 0x8A8886;  // couleurs de présence Teams (orange au lieu du rouge pour « occupé »)

  // Corps violet Teams (index 2-4 : base, ombre, reflet) pour ressortir sur tous les fonds.
  pal[2] = tft.color24to16(0x6264A7); pal[3] = tft.color24to16(0x464775); pal[4] = tft.color24to16(0x9EA2FF);
  switch (state) {
    case AVAILABLE: anim = {A_AVAILABLE, sizeof A_AVAILABLE, 150}; label = TXT_AVAILABLE; bg = 0x6BB700; break;
    case AWAY: anim = {A_AWAY, sizeof A_AWAY, 800}; label = TXT_AWAY; bg = 0xFCD116; break;
    case CALL:
    case VIDEO:
      anim = {A_CALL, sizeof A_CALL, 180};
      label = state == VIDEO ? TXT_VIDEO : TXT_CALL;
      bg = 0xFF8C00;
      break;
    case OCCUPIED: anim = {A_BUSY, sizeof A_BUSY, 300}; label = TXT_BUSY; bg = 0xC4314B; break;
    case DND: anim = {A_BUSY, sizeof A_BUSY, 300}; label = TXT_DND; bg = 0xC4314B; break;
    default: break;
  }

  // Libellé envoyé par l'appli (langue choisie côté PC) ; sinon libellé compilé ci-dessus.
  if (recvLabel.length() && recvLabelState == state) label = recvLabel.c_str();

  Frame f = (Frame)anim.frames[(t / anim.ms) % anim.count];
  // compagnon centré au-dessus du texte (30 px)
  int ox = (w - SPR_SIZE * SCALE) / 2, oy = (h - SPR_SIZE * SCALE - 30) / 2 - (f == F_JUMP ? 2 * SCALE : 0);
  spr.fillSprite(tft.color24to16(bg));

  // DND : de sa main, le compagnon sort le panneau de derrière lui en boucle par la droite (monte le long
  // du flanc, revient par-dessus la tête), départ et arrivée en douceur, puis petit rebond amorti en le posant.
  // Au départ, panneau, manche et main sont entièrement cachés derrière le corps (startY).
  const int lowX = w / 2, lowY = oy + 20 * SCALE, startY = oy + 17 * SCALE;
  uint32_t dt = t - since;
  float u = 0.5f - 0.5f * cosf(PI * min(dt, 900u) / 900);  // 0 -> 1, accélère puis freine
  float s = sinf(PI * u);                                   // 0 -> 1 (sur le côté) -> 0
  bool signFront = u > 0.5f;
  float bob = dt > 900 ? 6 * sinf((dt - 900) / 50.f) * expf((900.f - dt) / 150) : 0;
  int signX = lowX + 60 * s;
  float baseY = startY + (lowY - startY) * u;
  int signY = baseY - (baseY - 70) * s + 15 * sinf(2 * PI * u) + bob;
  if (state == DND && !signFront) { drawStop(signX, signY); drawHand(signX, signY + 66, pal); }
  if (state == OFFLINE) drawPlug(ox, oy);
  drawFrame(f, ox, oy, pal);
  if (state == DND && signFront) { drawStop(signX, signY); drawHand(signX, signY + 66, pal); }

  if (state == VIDEO) {  // en retrait des coins arrondis de l'écran
    spr.fillRoundRect(w - 70, 20, 30, 22, 4, TFT_WHITE);
    spr.fillTriangle(w - 40, 31, w - 28, 23, w - 28, 39, TFT_WHITE);
    if (t % 1000 < 500) spr.fillCircle(38, 31, 7, TFT_WHITE);
  }

  spr.setTextColor(TFT_BLACK);
  spr.setTextDatum(BC_DATUM);
  if (state != OFFLINE) spr.drawString(label, w / 2, h - 8, 4);  // hors ligne : la prise débranchée suffit
  spr.pushSprite(0, 0);
  return state == DND && dt < 1500;
}

void setup() {
  // Waveshare 1.69 : SYS_EN haut garde la carte alimentée, buzzer passif à couper sinon il chauffe.
  pinMode(SYS_EN_PIN, OUTPUT);
  digitalWrite(SYS_EN_PIN, HIGH);
  pinMode(BUZZER_PIN, OUTPUT);
  digitalWrite(BUZZER_PIN, LOW);

  USB.productName("OpenCompanion");  // nom cherché par l'appli PC
  USB.firmwareVersion(FW_VERSION);   // lue par l'appli pour vérifier la version
  hid.begin();
  usbSerial.begin();
  USB.begin();
  tft.init();
  tft.fillScreen(TFT_BLACK);
  spr.setColorDepth(16);  // 240x280 en 16 bits = 134 Ko : couleurs fidèles (le 8 bits ternit les gris)
  spr.createSprite(tft.width(), tft.height());
}

void loop() {
  readStatus();
  if (!draw(millis())) delay(20);
}
