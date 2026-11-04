using System.Diagnostics;
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
        ["BRB"] = 0xFCD116,
        ["CALL"] = 0xFF8C00,
        ["VIDEO"] = 0xFF8C00,
        ["BUSY"] = 0xC4314B,
        ["DND"] = 0xC4314B,
        ["OFFLINE"] = 0x8A8886,
        ["NOTEAMS"] = 0xD0D2EC,
        ["UPDATE"] = 0x3A5078,
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

    // Langue de l'appli et de l'écran, choisie dans les paramètres : persistée, appliquée à la fenêtre et renvoyée aussitôt à l'ESP.
    internal string Lang
    {
        get => lang;
        set
        {
            lang = value;
            Registry.CurrentUser.CreateSubKey(@"Software\OpenCompanion").SetValue("Lang", value);
            ApplyLang();
            sent = "";  // force le renvoi au prochain Tick pour voir le changement tout de suite
        }
    }
    Forms.ToolStripItem? openItem, quitItem;

    void ApplyLang()
    {
        Localize.Lang = lang;
        Tr.I.Refresh();
        if (openItem != null) (openItem.Text, quitItem!.Text) = (Localize.T("TrayOpen"), Localize.T("TrayQuit"));
    }
    int companion = Registry.CurrentUser.OpenSubKey(@"Software\OpenCompanion")?.GetValue("Companion") as int? ?? 0;

    // Compagnon choisi dans la fenêtre (index de COMPANIONS dans png2h.py) : persisté et renvoyé aussitôt.
    internal int Companion
    {
        get => companion;
        set
        {
            companion = value;
            Registry.CurrentUser.CreateSubKey(@"Software\OpenCompanion").SetValue("Companion", value);
            sent = "";
        }
    }
    // Même valeur que l'installeur (installer.iss) : la case de l'installation et l'option de la page Paramètres
    // pilotent la même chose. Chemin de l'exe qui tourne : celui de l'installation, ou d'un build de dev.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal bool AutoStart
    {
        get => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("OpenCompanion") != null;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("OpenCompanion", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("OpenCompanion", throwOnMissingValue: false);
        }
    }
    string lockStatus = Registry.CurrentUser.OpenSubKey(@"Software\OpenCompanion")?.GetValue("LockStatus") as string ?? "AWAY";

    // Statut de l'écran quand la session Windows est verrouillée (AWAY ou BRB), choisi dans la page Paramètres.
    internal string LockStatus
    {
        get => lockStatus;
        set
        {
            lockStatus = value;
            Registry.CurrentUser.CreateSubKey(@"Software\OpenCompanion").SetValue("LockStatus", value);
            sent = "";
        }
    }
    bool locked;  // session verrouillée (l'appli démarre à l'ouverture de session : déverrouillée)

    // Session verrouillée : le statut choisi remplace celui de Teams, sauf appel/visio en cours, Teams fermé ou mise à jour.
    internal static string WhenLocked(string status, bool locked, string lockStatus) =>
        locked && status is not ("CALL" or "VIDEO" or "OFFLINE" or "UPDATE") ? lockStatus : status;

    // SystemEvents rappelle sur le thread UI (abonnement fait depuis celui-ci) ; Tick prend le relais dans la seconde.
    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) locked = false;
    }

    bool updating;

    // Étape en cours de la mise à jour du firmware (null : aucune), affichée par la page Paramètres.
    // Dans App et pas dans la page : la page est recréée à chaque navigation, l'installation continue.
    internal string? FirmwareStep { get; set; }

    // Mise à jour du firmware en cours : l'écran montre le compagnon qui télécharge du code,
    // jusqu'au redémarrage en mode téléchargement. Appelé depuis le thread UI (fenêtre).
    internal bool Updating
    {
        set
        {
            updating = value;
            sent = "";
            Tick();  // tout de suite, l'écran va bientôt redémarrer
        }
    }
    DateTime lastSent;

    // Dernière release du firmware sur GitHub (null : pas encore lue, hors ligne ou aucune release). Lue par la fenêtre.
    internal FirmwareUpdate.Release? LatestFirmware { get; private set; }

    // Dernière release de l'appli plus récente que celle qui tourne (null sinon) : la fenêtre propose de l'installer.
    internal AppUpdate.Release? NewApp { get; private set; }
    bool installingApp;  // installeur en cours de téléchargement : pas de deuxième clic en parallèle

    // Versions déjà annoncées par notification : une seule notification par version, même après redémarrage.
    string? notifiedFirmware = Registry.CurrentUser.OpenSubKey(@"Software\OpenCompanion")?.GetValue("NotifiedFirmware") as string;
    string? notifiedApp = Registry.CurrentUser.OpenSubKey(@"Software\OpenCompanion")?.GetValue("NotifiedApp") as string;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--test"])
        {
            Presence.SelfTest();
            GraphPresence.SelfTest();
            TeamsLog.SelfTest();
            Localize.SelfTest();
            FirmwareUpdate.SelfTest();
            AppUpdate.SelfTest();
            CompanionLink.SelfTest();
            Trace.Assert(WhenLocked("AVAILABLE", true, "BRB") == "BRB");
            Trace.Assert(WhenLocked("DND", true, "AWAY") == "AWAY");
            Trace.Assert(WhenLocked("CALL", true, "AWAY") == "CALL");      // appel en cours : reste affiché
            Trace.Assert(WhenLocked("OFFLINE", true, "AWAY") == "OFFLINE"); // Teams fermé
            Trace.Assert(WhenLocked("BUSY", false, "AWAY") == "BUSY");
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
        openItem = menu.Items.Add("", null, (_, _) => ShowWindow());
        quitItem = menu.Items.Add("", null, (_, _) => Shutdown());
        ApplyLang();
        tray = new Forms.NotifyIcon { Icon = icons["OFFLINE"], Text = "OpenCompanion", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        tray.BalloonTipClicked += (_, _) => ShowWindow(settings: true);  // les paramètres proposent d'installer le firmware ou la nouvelle version

        // ponytail: détection + envoi HID sur le thread UI (quelques ms), thread dédié si ça saccade
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();

        // API GitHub anonyme limitée à 60 requêtes/h : une lecture au démarrage puis toutes les 6 h suffit.
        var releases = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        releases.Tick += (_, _) => _ = CheckReleasesAsync();
        releases.Start();
        _ = CheckReleasesAsync();
    }

    // Hors ligne, repo privé, pas de release : on garde la dernière lue.
    internal async Task CheckReleasesAsync()
    {
        try { LatestFirmware = await FirmwareUpdate.LatestAsync(); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException) { }
        try
        {
            if (await AppUpdate.LatestAsync() is { } app && app.Version > AppUpdate.Current) NotifyApp(NewApp = app);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException) { }
    }

    void NotifyApp(AppUpdate.Release app)
    {
        var version = app.Version.ToString();
        if (version == notifiedApp) return;
        notifiedApp = version;
        Registry.CurrentUser.CreateSubKey(@"Software\OpenCompanion").SetValue("NotifiedApp", version);
        tray!.ShowBalloonTip(10000, Localize.T("NotifyAppTitle"), Localize.T("NotifyApp", version, AppUpdate.Current), Forms.ToolTipIcon.Info);
    }

    // Bouton de la fenêtre : télécharge, vérifie la signature, installe en silencieux ; l'installeur relance l'appli.
    // Erreur réseau ou signature invalide : exception pour la fenêtre, rien n'est lancé.
    internal async Task InstallAppAsync()
    {
        if (NewApp is not { } app || installingApp || updating) return;  // pas pendant le flash du firmware
        installingApp = true;
        string setup;
        try { setup = await AppUpdate.DownloadAsync(app); }
        finally { installingApp = false; }

        // AppMutex de l'installeur = notre Mutex : le libérer, sinon il refuse de continuer en silencieux.
        single?.ReleaseMutex();
        single?.Dispose();
        single = null;
        // /SILENT : barre de progression seule, cases (démarrage auto…) de l'installation précédente ;
        // /CLOSEAPPLICATIONS ferme ce processus s'il tient encore ses fichiers ; installer.iss relance l'appli à la fin.
        Process.Start(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS");
        Shutdown();
    }

    // Écran branché avec un firmware plus ancien que la dernière release : notification Windows, une fois par version.
    void NotifyFirmware()
    {
        if (updating || LatestFirmware is not { } latest || link.Firmware is not { } current || latest.Version <= current) return;
        var version = latest.Version.ToString();
        if (version == notifiedFirmware) return;
        notifiedFirmware = version;
        Registry.CurrentUser.CreateSubKey(@"Software\OpenCompanion").SetValue("NotifiedFirmware", version);
        tray!.ShowBalloonTip(10000, Localize.T("NotifyFirmwareTitle"), Localize.T("NotifyFirmware", version, current), Forms.ToolTipIcon.Info);
    }

    void Tick()
    {
        var status = updating ? "UPDATE" : WhenLocked(useGraph ? graph!() : teamsLog.Current(), locked, lockStatus);

        // Renvoi périodique : l'ESP repasse OFFLINE s'il n'entend plus rien (PC en veille, câble débranché).
        if (status != sent || DateTime.Now - lastSent > TimeSpan.FromSeconds(10))
        {
            sent = link.Send(CompanionLink.Wire(status, Localize.Label(lang, status), companion)) ? status : "";
            lastSent = DateTime.Now;
        }
        NotifyFirmware();

        var text = $"OpenCompanion – {Localize.Label(lang, status)}" + (link.Connected ? "" : Localize.T("TrayUnplugged"));
        if (tray!.Text != text)
        {
            tray.Text = text;
            tray.Icon = icons[status];
        }
    }

    void ShowWindow(bool settings = false)
    {
        if (window == null)
        {
            window = new MainWindow();
            window.Closed += (_, _) => window = null;
        }
        if (settings) window.ShowSettings();
        window.Show();
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;  // événement statique : se désabonner, sinon fuite
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
