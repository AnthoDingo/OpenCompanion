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
        ["CALL"] = new(["call1", "call2", "call1", "call2", "call2", "call1"], 180, 0xFF8C00, "En appel"),
        ["VIDEO"] = new(["call1", "call2", "call1", "call2", "call2", "call1"], 180, 0xFF8C00, "En visio"),
        ["BUSY"] = new(["idle", "idle", "look", "look", "idle", "blink"], 300, 0xC4314B, "Occupe"),
        ["DND"] = new(["idle", "idle", "look", "look", "idle", "blink"], 300, 0xC4314B, "Ne pas deranger"),
        ["OFFLINE"] = new(["offline"], 1000, 0x8A8886, "Hors ligne"),
    };

    // Corps des PNG (vert) -> violet Teams, comme pal[2..4] dans main.cpp.
    static readonly Dictionary<int, int> Body = new() { [0x38B764] = 0x6264A7, [0x257179] = 0x464775, [0xA7F070] = 0x9EA2FF };

    static readonly Dictionary<string, Color?[]> Frames = [];
    static readonly Dictionary<Color, SolidBrush> BrushCache = [];
    static readonly Font LabelFont = new("Arial", 20, FontStyle.Bold, GraphicsUnit.Pixel);

    [STAThread]
    static void Main(string[] args)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "sprites");  // copiés depuis esp32/sprites au build
        foreach (var file in Directory.GetFiles(dir, "*.png")) Frames[Path.GetFileNameWithoutExtension(file)] = Load(file);

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

    // since : début de l'état courant (animation du panneau DND).
    public static void Render(Bitmap screen, string state, long t, long since = 0)
    {
        var a = Anims[state];
        var f = a.Frames[t / a.Ms % a.Frames.Length];
        int ox = (W - 32 * Scale) / 2, oy = (H - 32 * Scale - 30) / 2 - (f == "jump" ? 2 * Scale : 0);

        using var g = Graphics.FromImage(screen);
        g.Clear(Q(a.Bg));

        // DND : de sa main, le compagnon sort le panneau de derrière lui en boucle par la droite (monte le long
        // du flanc, revient par-dessus la tête), départ et arrivée en douceur, puis petit rebond amorti en le posant.
        // Au départ, panneau, manche et main sont entièrement cachés derrière le corps (startY).
        int lowX = W / 2, lowY = oy + 20 * Scale, startY = oy + 17 * Scale;
        long dt = t - since;
        double u = 0.5 - 0.5 * Math.Cos(Math.PI * Math.Min(dt, 900) / 900);
        double s = Math.Sin(Math.PI * u);
        bool signFront = u > 0.5;
        double bob = dt > 900 ? 6 * Math.Sin((dt - 900) / 50.0) * Math.Exp((900.0 - dt) / 150) : 0;
        int signX = (int)(lowX + 60 * s);
        double baseY = startY + (lowY - startY) * u;
        int signY = (int)(baseY - (baseY - 70) * s + 15 * Math.Sin(2 * Math.PI * u) + bob);
        if (state == "DND" && !signFront) { DrawStop(g, signX, signY); DrawHand(g, signX, signY + 66); }
        var px = Frames[f];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                if (px[y * 32 + x] is Color c) g.FillRectangle(B(c), ox + x * Scale, oy + y * Scale, Scale, Scale);
        if (state == "DND" && signFront) { DrawStop(g, signX, signY); DrawHand(g, signX, signY + 66); }

        var white = B(Color.White);
        if (state == "VIDEO")
        {
            g.FillRectangle(white, W - 70, 20, 30, 22);
            g.FillPolygon(white, new Point[] { new(W - 40, 31), new(W - 28, 23), new(W - 28, 39) });
            if (t % 1000 < 500) g.FillEllipse(white, 38 - 7, 31 - 7, 15, 15);
        }

        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;  // police bitmap sans lissage, comme TFT_eSPI
        var size = g.MeasureString(a.Label, LabelFont);
        g.DrawString(a.Label, LabelFont, B(Color.Black), (W - size.Width) / 2, H - 8 - size.Height);

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

    // Comme drawStop() de main.cpp.
    static void DrawStop(Graphics g, int cx, int cy)
    {
        g.FillRectangle(B(Q(0xD3D3D3)), cx - 4, cy, 8, 80);
        Point[] Oct(int r) => Enumerable.Range(0, 8).Select(k => (22.5 + 45 * k) * Math.PI / 180)
            .Select(a => new Point(cx + (int)(r * Math.Cos(a)), cy + (int)(r * Math.Sin(a)))).ToArray();
        g.FillPolygon(B(Color.White), Oct(44));
        g.FillPolygon(B(Q(0xE81123)), Oct(39));
        var size = g.MeasureString("STOP", LabelFont);
        g.DrawString("STOP", LabelFont, B(Color.White), cx - size.Width / 2, cy - size.Height / 2);
    }

    // Comme drawHand() de main.cpp : contour (SPR_PALETTE[1]), corps, reflet.
    static void DrawHand(Graphics g, int x, int y)
    {
        g.FillEllipse(B(Q(0x181C28)), x - 10, y - 10, 21, 21);
        g.FillEllipse(B(Q(0x6264A7)), x - 8, y - 8, 17, 17);
        g.FillEllipse(B(Q(0x9EA2FF)), x - 6, y - 6, 7, 7);
    }

    // Une capture par statut, côte à côte (t choisi pour montrer une frame typique).
    static void Snapshot(string output)
    {
        using var sheet = new Bitmap(Anims.Count * (W + 10) + 10, H + 20);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.White);
        int i = 0;
        foreach (var state in Anims.Keys)
        {
            using var screen = new Bitmap(W, H);
            Render(screen, state, 1600);
            g.DrawImage(screen, 10 + i++ * (W + 10), 10);
        }
        sheet.Save(output);
    }
}

class EmulatorForm : Form
{
    const int Zoom = 2;
    readonly Bitmap screen = new(Program.W, Program.H);
    readonly Stopwatch clock = Stopwatch.StartNew();
    string state = "AVAILABLE";
    long since;

    public EmulatorForm()
    {
        Text = "OpenCompanion – émulateur";
        DoubleBuffered = true;
        BackColor = Color.FromArgb(40, 40, 40);
        ClientSize = new Size(Program.W * Zoom + 20, Program.H * Zoom + 60);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, BackColor = SystemColors.Control };
        foreach (var s in Program.Anims.Keys)
        {
            var b = new Button { Text = s, AutoSize = true };
            b.Click += (_, _) => { if (state != s) since = clock.ElapsedMilliseconds; state = s; };
            bar.Controls.Add(b);
        }
        Controls.Add(bar);

        var timer = new System.Windows.Forms.Timer { Interval = 20 };  // delay(20) de loop()
        timer.Tick += (_, _) => Invalidate();
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Program.Render(screen, state, clock.ElapsedMilliseconds, since);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(screen, 10, 10, Program.W * Zoom, Program.H * Zoom);
    }
}
