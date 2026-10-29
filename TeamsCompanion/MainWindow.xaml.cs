using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TeamsCompanion;

// MinFirmware : premier firmware qui embarque les sprites de ce compagnon.
public record Companion(string Name, BitmapSource Image, Version MinFirmware)
{
    public string Requirement => $"Firmware {MinFirmware} minimum";

    // ponytail: firmware inconnu (écran débranché) = choix libre, on ne peut rien vérifier
    public bool Supports(Version? firmware) => firmware == null || firmware >= MinFirmware;
}

public partial class MainWindow : FluentWindow
{
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    FirmwareUpdate.Release? latest;  // dernière release du repo
    string? installing;              // étape en cours de la mise à jour du firmware

    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);  // suit le thème clair/sombre de Windows
        Companions.ItemsSource = new[] { new Companion("Pousse", Sprite("idle"), new Version(1, 0)) };

        DetectGraph.IsChecked = ((App)Application.Current).UseGraph;
        DetectClient.IsChecked = !DetectGraph.IsChecked;

        LangBox.ItemsSource = Localize.Languages;
        LangBox.SelectedValue = ((App)Application.Current).Lang;

        // L'appli détecte l'écran en tâche de fond ; la fenêtre affiche juste son état.
        refresh.Tick += (_, _) => ShowDevice();
        refresh.Start();
        _ = CheckUpdateAsync();
        Closed += (_, _) => refresh.Stop();
        ShowDevice();
    }

    void ShowDevice()
    {
        var link = ((App)Application.Current).Link;
        if (installing != null)
        {
            (Device.Severity, Device.Title, Device.Message) = (InfoBarSeverity.Informational, "Mise à jour du firmware", installing);
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        var expected = CompanionLink.ExpectedFirmware;
        (Device.Severity, Device.Title, Device.Message) = link.Firmware switch
        {
            null => (InfoBarSeverity.Error, "Écran non détecté", "Branchez l'ESP32 en USB."),
            var v when v < expected => (InfoBarSeverity.Warning, "Écran détecté", $"Firmware {v} : mise à jour vers {expected} requise."),
            var v when v > expected => (InfoBarSeverity.Warning, "Écran détecté", $"Firmware {v} plus récent que l'application ({expected})."),
            var v => (InfoBarSeverity.Success, "Écran connecté", $"Firmware {v} à jour."),
        };
        var update = latest != null && link.Firmware is { } fw && latest.Version > fw;
        if (update) Device.Message += $" Firmware {latest!.Version} disponible.";
        UpdateButton.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = $"Installer le firmware {latest?.Version}";
        ShowCompanions(link.Firmware);
    }

    async Task CheckUpdateAsync()
    {
        try { latest = await FirmwareUpdate.LatestAsync(); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException) { latest = null; }  // hors ligne, repo privé, pas de release
        ShowDevice();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (latest is not { } release) return;
        installing = "Préparation…";
        ShowDevice();
        var progress = new Progress<string>(step => { installing = step; ShowDevice(); });
        try
        {
            await Task.Run(() => FirmwareUpdate.InstallAsync(release, progress));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Mise à jour du firmware", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            installing = null;
            ShowDevice();
        }
    }

    void Detection_Checked(object sender, RoutedEventArgs e)
    {
        if (DetectGraph == null) return;  // déclenché pendant InitializeComponent, avant que les champs existent
        ((App)Application.Current).UseGraph = DetectGraph.IsChecked == true;
    }

    void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LangBox?.SelectedValue is not string code) return;
        ((App)Application.Current).Lang = code;
    }

    // Grise les compagnons que le firmware branché ne connaît pas encore.
    void ShowCompanions(Version? firmware)
    {
        foreach (Companion c in Companions.Items)
            if (Companions.ItemContainerGenerator.ContainerFromItem(c) is UIElement item) item.IsEnabled = c.Supports(firmware);

        if (Companions.SelectedItem is Companion selected && !selected.Supports(firmware))
            Companions.SelectedItem = Companions.Items.Cast<Companion>().FirstOrDefault(c => c.Supports(firmware));
    }

    // Corps des PNG (vert) -> violet Teams, comme pal[2..4] dans esp32/src/main.cpp.
    static readonly Dictionary<int, int> Body = new() { [0x38B764] = 0x6264A7, [0x257179] = 0x464775, [0xA7F070] = 0x9EA2FF };

    // Sprite de l'ESP (esp32/sprites, embarqué en ressource) tel qu'affiché par l'écran.
    static BitmapSource Sprite(string name)
    {
        var png = new BitmapImage(new Uri($"pack://application:,,,/TeamsCompanion;component/sprites/{name}.png"));
        var src = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new int[w * h];
        src.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i++)
            if (Body.TryGetValue(px[i] & 0xFFFFFF, out var rgb)) px[i] = (px[i] & unchecked((int)0xFF000000)) | rgb;
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
    }
}
