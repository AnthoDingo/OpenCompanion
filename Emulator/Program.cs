using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Emulator;

// Émulateur de l'écran de l'ESP : reproduit draw() de esp32/src/main.cpp à partir des mêmes PNG.
// À garder synchronisé avec main.cpp (séquences, couleurs, disposition).
static class Program
{
    public const int W = 240, H = 280;  // TFT_WIDTH / TFT_HEIGHT de platformio.ini
    const int Scale = 6, CornerRadius = 40;  // ponytail: rayon des coins de l'écran 1,69" estimé

    public record Anim(string[] Frames, int Ms, int Bg, string Label);

    public static readonly Dictionary<string, Anim> Anims = new()
    {
        ["AVAILABLE"] = new(["idle", "idle", "idle", "look", "look", "idle", "blink", "idle",
                             "idle", "squash", "jump", "jump", "squash", "idle"], 150, 0x6BB700, "Disponible"),
        ["AWAY"] = new(["away1", "away2"], 800, 0xFCD116, "Absent"),
        ["BRB"] = new(["look"], 1000, 0xFCD116, "Je reviens vite"),
        ["CALL"] = new(["call1", "call2", "call1", "call2", "call2", "call1"], 180, 0xFF8C00, "En appel"),
        ["VIDEO"] = new(["call1", "call2", "call1", "call2", "call2", "call1"], 180, 0xFF8C00, "En visio"),
        ["BUSY"] = new(["idle", "idle", "idle", "idle", "blink", "idle"], 300, 0xC4314B, "Occupe"),
        ["DND"] = new(["back"], 1000, 0xC4314B, "Ne pas deranger"),
        ["OFFLINE"] = new(["offline"], 1000, 0x8A8886, ""),  // pas de texte, comme main.cpp
        ["UPDATE"] = new(["idle", "idle", "look", "idle", "blink", "idle", "squash", "idle"], 250, 0x3A5078, "Mise a jour"),
    };

    // Corps des PNG (vert) -> violet Teams, comme pal[2..4] dans main.cpp.
    static readonly Dictionary<int, int> Body = new() { [0x38B764] = 0x6264A7, [0x257179] = 0x464775, [0xA7F070] = 0x9EA2FF };

    // Même ordre que COMPANIONS de sprites/png2h.py.
    public static readonly string[] Companions = ["pousse", "chouette", "magicien", "trombone", "calculatrice"];

    // Mains qui tapent en DND (contour, base, reflet), comme HAND dans main.cpp.
    static readonly Dictionary<string, int[]> Hand = new()
    {
        ["pousse"] = [0x1A1C2C, 0x6264A7, 0x9EA2FF],
        ["chouette"] = [0x1A1C2C, 0x965F32, 0xEBCD96],
        ["magicien"] = [0x1A1C2C, 0xFAC8A0, 0xF4F4F4],
        ["trombone"] = [0x1A1C2C, 0x94B0C2, 0xF4F4F4],
        ["calculatrice"] = [0x1A1C2C, 0x465AC8, 0x94B0C2],
    };

    static readonly Dictionary<string, Color?[]> Frames = [];  // clé "compagnon/frame"
    static readonly Dictionary<Color, SolidBrush> BrushCache = [];
    static readonly Font CodeFont = new("Consolas", 16, FontStyle.Bold, GraphicsUnit.Pixel);  // ~ police 1 x2 de TFT_eSPI
    static readonly Font LabelFont = new("Arial", 20, FontStyle.Bold, GraphicsUnit.Pixel);

    [STAThread]
    static void Main(string[] args)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "sprites");  // copiés depuis sprites/<compagnon>/ au build
        foreach (var file in Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories))
            Frames[$"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileNameWithoutExtension(file)}"] = Load(file);

        if (args is ["--snapshot", var output])
        {
            Snapshot(output);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new EmulatorForm());
    }

    static Color?[] Load(string file)
    {
        using var img = new Bitmap(file);
        var px = new Color?[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                var c = img.GetPixel(x, y);
                int rgb = c.ToArgb() & 0xFFFFFF;
                if (c.A >= 128) px[y * 32 + x] = Q(Body.GetValueOrDefault(rgb, rgb));
            }
        return px;
    }

    // RGB565 comme l'écran : les bits perdus sont mis à zéro.
    static Color Q(int rgb) => Color.FromArgb(rgb >> 16 & 0xF8, rgb >> 8 & 0xFC, rgb & 0xF8);

    static SolidBrush B(Color c) => BrushCache.TryGetValue(c, out var b) ? b : BrushCache[c] = new SolidBrush(c);

    // since : début de l'état courant (demi-tour de DND).
    public static void Render(Bitmap screen, string state, long t, long since = 0, string companion = "pousse")
    {
        var a = Anims[state];
        var f = a.Frames[t / a.Ms % a.Frames.Length];
        if (state == "DND" && t - since < 250) f = "look";  // DND : regarde de côté puis tourne le dos
        long dt = t - since;
        if (state == "BRB" && dt >= 700 && (dt - 700) % 8000 / 150 == 39) f = "blink";
        int ox = (W - 32 * Scale) / 2, oy = (H - 32 * Scale - 30) / 2 - (f == "jump" ? 2 * Scale : 0);

        using var g = Graphics.FromImage(screen);
        g.Clear(Q(a.Bg));

        if (state == "OFFLINE") DrawPlug(g, ox, oy);
        if (state == "UPDATE") DrawCodeRain(g, t, ox, oy);
        if (state == "BRB")
        {
            DrawHourglass(g, dt, W / 2, oy + 16 * Scale);
            ox = PeekX(dt, ox);
        }
        int bob = f == "back" && t % 130 < 40 ? 2 : 0;
        if (f == "back") g.FillRectangle(B(Q(0x8C8C96)), ox + 2 * Scale, oy + 29 * Scale, 28 * Scale, 6);
        var px = Frames[$"{companion}/{f}"];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                if (px[y * 32 + x] is Color c) g.FillRectangle(B(c), ox + x * Scale, oy + bob + y * Scale, Scale, Scale);
        if (f == "back") DrawTyping(g, t, ox, oy + bob, companion);
        if (state == "BUSY") DrawLaptop(g, t, ox, oy);
        if (f == "back") DrawStop(g, ox + 16 * Scale, oy + 23 * Scale + bob);

        var white = B(Color.White);
        if (state == "VIDEO")
        {
            g.FillRectangle(white, W - 70, 20, 30, 22);
            g.FillPolygon(white, new Point[] { new(W - 40, 31), new(W - 28, 23), new(W - 28, 39) });
            if (t % 1000 < 500) g.FillEllipse(white, 38 - 7, 31 - 7, 15, 15);
        }

        if (state == "UPDATE") DrawProgress(g, t, oy + 32 * Scale + 4);

        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;  // police bitmap sans lissage, comme TFT_eSPI
        var size = g.MeasureString(a.Label, LabelFont);
        g.DrawString(a.Label, LabelFont, B(state == "UPDATE" ? Color.White : Color.Black), (W - size.Width) / 2, H - 8 - size.Height);

        // coins arrondis de la dalle
        using var bezel = new GraphicsPath { FillMode = FillMode.Alternate };
        bezel.AddRectangle(new Rectangle(0, 0, W, H));
        bezel.AddArc(0, 0, CornerRadius * 2, CornerRadius * 2, 180, 90);
        bezel.AddArc(W - CornerRadius * 2, 0, CornerRadius * 2, CornerRadius * 2, 270, 90);
        bezel.AddArc(W - CornerRadius * 2, H - CornerRadius * 2, CornerRadius * 2, CornerRadius * 2, 0, 90);
        bezel.AddArc(0, H - CornerRadius * 2, CornerRadius * 2, CornerRadius * 2, 90, 90);
        bezel.CloseFigure();
        g.FillPath(B(Color.FromArgb(20, 20, 20)), bezel);
    }

    // Comme drawStop() de main.cpp : feuille en paysage avec STOP en rouge, scotchée dans le dos.
    static void DrawStop(Graphics g, int cx, int cy)
    {
        const int fw = 70, fh = 46;
        int x = cx - fw / 2, y = cy - fh / 2;
        g.FillRectangle(B(Q(0xB4B4BE)), x, y, fw, fh);
        g.FillRectangle(B(Color.White), x + 1, y + 1, fw - 2, fh - 2);
        g.FillRectangle(B(Q(0xD3D3D3)), x - 4, y - 3, 14, 6);
        g.FillRectangle(B(Q(0xD3D3D3)), x + fw - 10, y - 3, 14, 6);
        var size = g.MeasureString("STOP", CodeFont);
        g.DrawString("STOP", CodeFont, B(Q(0xE81123)), cx + 1 - size.Width / 2, cy + 1 - size.Height / 2);
    }

    // Comme drawTyping() de main.cpp : mains qui frappent à tour de rôle, touche qui saute côté frappe.
    static void DrawTyping(Graphics g, long t, int ox, int oy, string companion)
    {
        var c = Hand[companion];
        bool left = t / 130 % 2 == 1;
        long age = t % 130;
        for (int side = 0; side < 2; side++)
        {
            int x = ox + (side == 1 ? 25 : 7) * Scale, y = oy + 27 * Scale - ((side == 0) == left ? 0 : 5);
            g.FillEllipse(B(Q(c[0])), x - 10, y - 10, 21, 21);
            g.FillEllipse(B(Q(c[1])), x - 8, y - 8, 17, 17);
            g.FillEllipse(B(Q(c[2])), x - 6, y - 6, 7, 7);
        }
        int kx = ox + (left ? 4 : 28) * Scale;
        g.FillRectangle(B(Color.White), kx - 3, oy + 25 * Scale - (int)(age / 8), 6, 6);
    }

    // Comme drawLaptop() de main.cpp : portable vu de dos devant le compagnon, touches qui sautent, café qui fume.
    static void DrawLaptop(Graphics g, long t, int ox, int oy)
    {
        int l = ox + 7 * Scale, top = oy + 22 * Scale, lw = 18 * Scale, lh = 9 * Scale, bottom = top + lh;
        var pink = B(Q(0xF4C6CE));
        for (int i = 0; i < 3; i++)
        {
            long j = t / 130 - i, age = t % 130 + i * 130;
            int sz = i == 0 ? 6 : 4;
            g.FillRectangle(i == 0 ? B(Color.White) : pink, ox + Scale * (10 + (int)(j * 7 % 13)), top - 4 - (int)(age / 20), sz, sz);
        }
        g.FillRectangle(B(Q(0x5A5A64)), l, top, lw, lh);
        g.FillRectangle(B(Q(0xC8C8D2)), l + 2, top + 2, lw - 4, lh - 4);
        g.FillEllipse(B(Color.White), l + lw / 2 - 6, top + lh / 2 - 6, 13, 13);
        g.FillRectangle(B(Q(0x8C8C96)), ox + 5 * Scale, bottom, 22 * Scale, 6);

        using var handle = new Pen(Color.White, 3);
        g.DrawEllipse(handle, 50 - 6, bottom - 12 - 6, 12, 12);
        g.FillRectangle(B(Color.White), 30, bottom - 24, 20, 24);
        g.FillRectangle(B(Q(0x6F4E37)), 32, bottom - 22, 16, 4);
        for (int w = 0; w < 2; w++)
            for (int k = 0; k < 5; k++)
            {
                int y = bottom - 30 - (int)((k * 6 + t / 40) % 30);
                g.FillEllipse(pink, 36 + w * 8 + (int)(3 * Math.Sin((y + w * 10) / 5.0)) - 2, y - 2, 5, 5);
            }
    }

    // Comme drawCodeRain() de main.cpp : 0 et 1 qui tombent et disparaissent derrière le compagnon.
    static void DrawCodeRain(Graphics g, long t, int ox, int oy)
    {
        int fall = oy + 22 * Scale + 40;
        int[] shades = [0xB6FFB6, 0x3CD86E, 0x1F7A3D];
        for (int col = 0; col < 5; col++)
        {
            long period = 900 + col * 170, ph = t + col * 377;
            int head = (int)(ph % period * fall / period) - 20;
            long cycle = ph / period;
            for (int k = 0; k < 3; k++)
            {
                var digit = (cycle + col * 3 + k) * 7 % 3 != 0 ? "1" : "0";
                var size = g.MeasureString(digit, CodeFont);
                g.DrawString(digit, CodeFont, B(Q(shades[k])), ox + (8 + 4 * col) * Scale - size.Width / 2, head - k * 18);
            }
        }
    }

    // Comme drawProgress() de main.cpp : barre en boucle, la vraie progression n'est pas connue de l'écran.
    static void DrawProgress(Graphics g, long t, int y)
    {
        const int bw = 140, seg = 10;
        int x = (W - bw) / 2;
        using var pen = new Pen(Color.White);
        g.DrawRectangle(pen, x - 3, y - 3, bw + 5, 13);
        long filled = t % 2400 * (bw / seg + 1) / 2400;
        for (int i = 0; i < filled && i < bw / seg; i++) g.FillRectangle(B(Q(0x3CD86E)), x + i * seg + 1, y, seg - 2, 8);
    }

    // Comme drawHourglass() de main.cpp : le sable du haut s'écoule en 4 s puis le sablier repart plein.
    static void DrawHourglass(Graphics g, long t, int cx, int cy)
    {
        const int hh = 54, hw = 30, gh = hh - 2;
        int top = cy - hh, bot = cy + hh;
        var ink = B(Q(0x1A1C2C)); var wood = B(Q(0x965F32)); var sand = B(Q(0xC8783C)); var glass = B(Q(0xDCEBF5));
        void Tri(Brush b, int x0, int y0, int x1, int y1, int x2, int y2) => g.FillPolygon(b, new Point[] { new(x0, y0), new(x1, y1), new(x2, y2) });
        float k = t % 4000 / 4000f;
        Tri(ink, cx - hw - 3, top, cx + hw + 3, top, cx, cy + 4);
        Tri(ink, cx - hw - 3, bot, cx + hw + 3, bot, cx, cy - 4);
        Tri(glass, cx - hw, top + 2, cx + hw, top + 2, cx, cy);
        Tri(glass, cx - hw, bot - 2, cx + hw, bot - 2, cx, cy);
        int s = (int)((gh - 6) * MathF.Sqrt(1 - k));
        if (s > 0) Tri(sand, cx - hw * s / gh, cy - s, cx + hw * s / gh, cy - s, cx, cy);
        int m = (int)(gh * 0.7f * MathF.Sqrt(k));
        if (m > 0) Tri(sand, cx - hw, bot - 2, cx + hw, bot - 2, cx, bot - 2 - m);
        if (s > 0) g.FillRectangle(sand, cx - 1, cy, 2, bot - 2 - m - cy);
        foreach (int y in new[] { top - 10, bot })
        {
            g.FillRectangle(ink, cx - hw - 12, y - 1, 2 * hw + 24, 12);
            g.FillRectangle(wood, cx - hw - 10, y + 1, 2 * hw + 20, 8);
        }
        g.FillRectangle(wood, cx - hw - 7, top, 4, 2 * hh);
        g.FillRectangle(wood, cx + hw + 3, top, 4, 2 * hh);
    }

    // Comme peekX() de main.cpp : sort par la gauche, puis repasse une tête par le bord toutes les 8 s.
    static int PeekX(long dt, int center)
    {
        const int hidden = -32 * Scale + 20, peek = -70;
        static float Ease(float u) => (1 - MathF.Cos(u * MathF.PI)) / 2;
        if (dt < 700) return center + (int)((hidden - center) * Ease(dt / 700f));
        long p = (dt - 700) % 8000;
        float e = p < 4500 ? 0 : p < 5100 ? Ease((p - 4500) / 600f) : p < 6800 ? 1 : p < 7400 ? 1 - Ease((p - 6800) / 600f) : 0;
        return hidden + (int)((peek - hidden) * e);
    }

    // Comme drawPlug() de main.cpp : câble + prise débranchée, derrière le compagnon OFFLINE.
    static void DrawPlug(Graphics g, int ox, int oy)
    {
        float x0 = ox + 25 * Scale, y0 = oy + 26 * Scale, x1 = ox + 25 * Scale, y1 = oy + 36 * Scale,
              x2 = ox + 28 * Scale, y2 = oy + 33 * Scale;
        for (int i = 0; i <= 24; i++)
        {
            float k = i / 24f, a = (1 - k) * (1 - k), b = 2 * k * (1 - k), c = k * k;
            g.FillEllipse(B(Color.Black), (int)(a * x0 + b * x1 + c * x2) - 3, (int)(a * y0 + b * y1 + c * y2) - 3, 7, 7);
        }
        int px = (int)x2, py = (int)y2;
        g.FillRectangle(B(Q(0x444444)), px - 2, py - 9, 20, 18);
        g.FillRectangle(B(Q(0xD3D3D3)), px + 18, py - 6, 9, 4);
        g.FillRectangle(B(Q(0xD3D3D3)), px + 18, py + 2, 9, 4);
    }

    // Une capture par statut (colonnes) et par compagnon (lignes), t choisi pour montrer une frame typique.
    static void Snapshot(string output)
    {
        using var sheet = new Bitmap(Anims.Count * (W + 10) + 10, Companions.Length * (H + 10) + 10);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.White);
        for (int row = 0; row < Companions.Length; row++)
        {
            int i = 0;
            foreach (var state in Anims.Keys)
            {
                using var screen = new Bitmap(W, H);
                Render(screen, state, 1600, 0, Companions[row]);
                g.DrawImage(screen, 10 + i++ * (W + 10), 10 + row * (H + 10));
            }
        }
        sheet.Save(output);
    }
}

class EmulatorForm : Form
{
    static readonly Color Bg = Color.FromArgb(27, 27, 31), Card = Color.FromArgb(40, 40, 46),
        Hover = Color.FromArgb(58, 58, 66), Text1 = Color.FromArgb(235, 235, 240), Text2 = Color.FromArgb(140, 140, 150),
        Accent = Color.FromArgb(0x62, 0x64, 0xA7), Case = Color.FromArgb(20, 20, 20);  // Case = coins de Render()
    const int PanelW = 220, Bezel = 16;

    // Libellés de l'UI : ceux de Anims suivent la police de l'ESP, sans accents.
    static readonly Dictionary<string, string> Names = new()
    {
        ["BUSY"] = "Occupé", ["DND"] = "Ne pas déranger", ["OFFLINE"] = "Hors ligne", ["UPDATE"] = "Mise à jour", ["BRB"] = "Je reviens vite",
    };

    readonly Bitmap screen = new(Program.W, Program.H);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly List<Button> buttons = [];
    string state = "OFFLINE";  // comme l'ESP au démarrage, avant le premier message du PC
    long since;
    string companion = "pousse";

    public EmulatorForm()
    {
        Text = "OpenCompanion – émulateur";
        DoubleBuffered = true;
        BackColor = Bg;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(Program.W * 2 + 2 * Bezel + 80 + PanelW, Program.H * 2 + 2 * Bezel + 80);
        MinimumSize = new Size(Program.W + 2 * Bezel + 80 + PanelW, Program.H + 2 * Bezel + 120);

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = PanelW, BackColor = Card, Padding = new Padding(16, 8, 16, 12),
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        };
        panel.Controls.Add(Header("COMPAGNON"));
        foreach (var c in Program.Companions)
            panel.Controls.Add(Add(c, char.ToUpper(c[0]) + c[1..], null, () => companion = c));
        panel.Controls.Add(Header("STATUT"));
        foreach (var (s, a) in Program.Anims)
            panel.Controls.Add(Add(s, Names.GetValueOrDefault(s, a.Label), Dot(Color.FromArgb(255, Color.FromArgb(a.Bg))),
                () => { if (state != s) since = clock.ElapsedMilliseconds; state = s; }));
        Controls.Add(panel);
        Highlight();

        var timer = new System.Windows.Forms.Timer { Interval = 20 };  // delay(20) de loop()
        timer.Tick += (_, _) => Invalidate();
        timer.Start();
    }

    static Label Header(string text) => new()
    {
        Text = text, ForeColor = Text2, AutoSize = true, Margin = new Padding(0, 16, 0, 6),
        Font = new Font("Segoe UI Semibold", 8.5f),
    };

    Button Add(string key, string text, Image? icon, Action select)
    {
        var b = new Button
        {
            Tag = key, Text = text, Image = icon, Width = PanelW - 32, Height = 36, Margin = new Padding(0, 2, 0, 2),
            FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText, Padding = new Padding(10, 0, 0, 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => { select(); Highlight(); };
        buttons.Add(b);
        return b;
    }

    static Bitmap Dot(Color c)
    {
        var bmp = new Bitmap(20, 12);  // la marge à droite sépare la pastille du texte
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(c);
        g.FillEllipse(brush, 0, 0, 11, 11);
        return bmp;
    }

    // Clés de compagnons et de statuts distinctes : un seul test suffit pour les deux groupes.
    void Highlight()
    {
        foreach (var b in buttons)
        {
            bool on = (string)b.Tag! == state || (string)b.Tag! == companion;
            b.BackColor = on ? Accent : Card;
            b.ForeColor = on ? Color.White : Text1;
            b.FlatAppearance.MouseOverBackColor = on ? Accent : Hover;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Program.Render(screen, state, clock.ElapsedMilliseconds, since, companion);
        var g = e.Graphics;

        // plus grand zoom entier qui tient dans la zone libre : pixels nets
        int areaW = ClientSize.Width - PanelW, areaH = ClientSize.Height;
        int zoom = Math.Max(1, Math.Min((areaW - 2 * Bezel - 40) / Program.W, (areaH - 2 * Bezel - 40) / Program.H));
        int sw = Program.W * zoom, sh = Program.H * zoom, x = (areaW - sw) / 2, y = (areaH - sh) / 2;

        // boîtier : ombre, coque et liseré, rayon prolongeant celui de la dalle
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = new Rectangle(x - Bezel, y - Bezel, sw + 2 * Bezel, sh + 2 * Bezel);
        var radius = new Size(40 * zoom + Bezel, 40 * zoom + Bezel);  // 40 = CornerRadius de Render()
        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            g.FillRoundedRectangle(shadow, body with { Y = body.Y + 8 }, radius);
        using (var fill = new SolidBrush(Case)) g.FillRoundedRectangle(fill, body, radius);
        using (var edge = new Pen(Color.FromArgb(62, 62, 70), 1.5f)) g.DrawRoundedRectangle(edge, body, radius);

        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(screen, x, y, sw, sh);
    }
}
