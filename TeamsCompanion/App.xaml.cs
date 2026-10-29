using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TeamsCompanion;

// Application de la zone de notification : surveille le statut et l'envoie à l'écran.
public partial class App : Application
{
    // Mêmes couleurs que le fond de l'écran de l'ESP (les libellés, eux, sont localisés : voir Localize).
    static readonly Dictionary<string, int> Colors = new()
    {
        ["AVAILABLE"] = 0x6BB700,
        ["AWAY"] = 0xFCD116,
        ["CALL"] = 0xFF8C00,
        ["VIDEO"] = 0xFF8C00,
        ["BUSY"] = 0xC4314B,
        ["DND"] = 0xC4314B,
        ["OFFLINE"] = 0x8A8886,
    };

    readonly CompanionLink link = new();
    internal CompanionLink Link => link;
    readonly Dictionary<string, Drawing.Icon> icons = Colors.ToDictionary(s => s.Key, s => Dot(s.Value));
    Forms.NotifyIcon? tray;
    MainWindow? window;
    Mutex? single;
    readonly TeamsLog teamsLog = new();
    Func<string>? graph;
    bool useGraph;
    string sent = "";

    // Méthode de détection choisie dans la fenêtre : logs du client Teams (défaut) ou Microsoft Graph.
    internal bool UseGraph
    {
        get => useGraph;
        set
        {
            useGraph = value;
            if (value) graph ??= GraphPresence.Start();  // connexion Graph seulement au premier choix
        }
    }

    string lang = Registry.CurrentUser.OpenSubKey(@"Software\OpenCompanion")?.GetValue("Lang") as string ?? "fr";

    // Langue de l'écran, choisie dans la fenêtre : persistée et renvoyée aussitôt à l'ESP.
    internal string Lang
    {
        get => lang;
        set
        {
            lang = value;
            Registry.CurrentUser.CreateSubKey(@"Software\OpenCompanion").SetValue("Lang", value);
            sent = "";  // force le renvoi au prochain Tick pour voir le changement tout de suite
        }
    }
    DateTime lastSent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--test"])
        {
            Presence.SelfTest();
            GraphPresence.SelfTest();
            TeamsLog.SelfTest();
            Localize.SelfTest();
            Shutdown();
            return;
        }

        // Une seule instance : deux applis se disputeraient l'écran USB.
        single = new Mutex(true, "OpenCompanion.TeamsCompanion", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        if (e.Args.Contains("--graph")) UseGraph = true;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => ShowWindow());
        menu.Items.Add("Quitter", null, (_, _) => Shutdown());
        tray = new Forms.NotifyIcon { Icon = icons["OFFLINE"], Text = "OpenCompanion", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();

        // ponytail: détection + envoi HID sur le thread UI (quelques ms), thread dédié si ça saccade
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    void Tick()
    {
        var status = useGraph ? graph!() : teamsLog.Current();

        // Renvoi périodique : l'ESP repasse OFFLINE s'il n'entend plus rien (PC en veille, câble débranché).
        if (status != sent || DateTime.Now - lastSent > TimeSpan.FromSeconds(10))
        {
            // Libellé localisé seulement si le firmware sait l'afficher (>= 1.2) ; sinon statut brut.
            var wire = link.Firmware is { } fw && fw >= CompanionLink.ExpectedFirmware
                ? $"{status}|{Localize.Label(lang, status)}"
                : status;
            sent = link.Send(wire) ? status : "";
            lastSent = DateTime.Now;
        }

        var text = $"OpenCompanion – {Localize.Label(lang, status)}" + (link.Connected ? "" : " (écran non branché)");
        if (tray!.Text != text)
        {
            tray.Text = text;
            tray.Icon = icons[status];
        }
    }

    void ShowWindow()
    {
        if (window == null)
        {
            window = new MainWindow();
            window.Closed += (_, _) => window = null;
        }
        window.Show();
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (tray != null) tray.Visible = false;  // sinon l'icône reste affichée jusqu'au survol
        tray?.Dispose();
        link.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }

    // Pastille de la couleur du statut.
    static Drawing.Icon Dot(int rgb)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(unchecked((int)0xFF000000) | rgb)))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(brush, 3, 3, 26, 26);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());  // une icône par statut pour toute la vie de l'appli : pas de DestroyIcon
    }
}
