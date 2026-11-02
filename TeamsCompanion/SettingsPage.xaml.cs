using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace TeamsCompanion;

public partial class SettingsPage : Page
{
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    bool downloadingApp;  // installeur de l'appli en cours de téléchargement : garder son message

    static App App => (App)Application.Current;

    public SettingsPage()
    {
        InitializeComponent();
        LangBox.ItemsSource = Localize.Languages;
        LangBox.SelectedValue = App.Lang;
        DetectGraph.IsChecked = App.UseGraph;
        DetectClient.IsChecked = !DetectGraph.IsChecked;

        // L'appli détecte l'écran et lit les releases en tâche de fond ; la page affiche juste leur état.
        refresh.Tick += (_, _) => Show();
        Loaded += (_, _) => { refresh.Start(); Show(); };
        Unloaded += (_, _) => refresh.Stop();
    }

    void Show()
    {
        if (!downloadingApp)
        {
            (AppBar.Severity, AppBar.Title, AppBar.Message) = App.NewApp is { } app
                ? (InfoBarSeverity.Informational, "OpenCompanion", Localize.T("AppAvailable", app.Version, AppUpdate.Current))
                : (InfoBarSeverity.Success, "OpenCompanion", Localize.T("AppUpToDate", AppUpdate.Current));
            AppUpdateButton.Visibility = App.NewApp != null ? Visibility.Visible : Visibility.Collapsed;
            AppUpdateButton.Content = Localize.T("AppInstall", App.NewApp?.Version);
        }

        if (App.FirmwareStep is { } step)
        {
            (Device.Severity, Device.Title, Device.Message) = (InfoBarSeverity.Informational, Localize.T("FirmwareUpdate"), step);
            UpdateButton.Visibility = FileButton.Visibility = Visibility.Collapsed;
            return;
        }

        var link = App.Link;
        var latest = App.LatestFirmware;  // lue par l'appli au démarrage puis toutes les 6 h
        var expected = CompanionLink.ExpectedFirmware;
        (Device.Severity, Device.Title, Device.Message) = link.Firmware switch
        {
            null => (InfoBarSeverity.Error, Localize.T("DeviceNoneTitle"), Localize.T("DeviceNone")),
            var v when v < expected => (InfoBarSeverity.Warning, Localize.T("DeviceDetected"), Localize.T("DeviceOld", v, expected)),
            var v when v > expected => (InfoBarSeverity.Warning, Localize.T("DeviceDetected"), Localize.T("DeviceNewer", v, expected)),
            var v => (InfoBarSeverity.Success, Localize.T("DeviceOkTitle"), Localize.T("DeviceOk", v)),
        };
        var update = latest != null && link.Firmware is { } fw && latest.Version > fw;
        if (update) Device.Message += Localize.T("DeviceAvailable", latest!.Version);
        UpdateButton.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        FileButton.Visibility = link.Firmware != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = Localize.T("FirmwareInstall", latest?.Version);
    }

    void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LangBox?.SelectedValue is not string code || code == App.Lang) return;
        App.Lang = code;
        Show();  // textes calculés ici : pas d'attente du prochain tick
    }

    void Detection_Checked(object sender, RoutedEventArgs e)
    {
        if (DetectGraph == null) return;  // déclenché pendant InitializeComponent, avant que les champs existent
        App.UseGraph = DetectGraph.IsChecked == true;
    }

    // L'appli se ferme et l'installeur la relance une fois la nouvelle version en place.
    async void AppUpdate_Click(object sender, RoutedEventArgs e)
    {
        downloadingApp = true;
        AppUpdateButton.IsEnabled = false;
        AppBar.Message = Localize.T("AppDownloading");
        try
        {
            await App.InstallAppAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or IOException)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, Localize.T("AppUpdateTitle"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            downloadingApp = false;
            AppUpdateButton.IsEnabled = true;
        }
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (App.LatestFirmware is { } release) await InstallAsync(progress => FirmwareUpdate.InstallAsync(release, progress));
    }

    // Firmware compilé à la main (pio run : .pio/build/<env>/firmware.bin), vérifié avant de toucher à l'écran.
    async void File_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Localize.T("FirmwareFileDialog"), Filter = "Firmware ESP32-S3 (*.bin)|*.bin" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var image = await File.ReadAllBytesAsync(dialog.FileName);
        if (FirmwareUpdate.Invalid(image) is { } why)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), why, Localize.T("FirmwareUpdate"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        var name = Path.GetFileName(dialog.FileName);
        await InstallAsync(progress => FirmwareUpdate.InstallFileAsync(image, name, progress));
    }

    // L'étape est gardée dans App : si on change de page pendant l'installation, la nouvelle page la reprend.
    async Task InstallAsync(Func<IProgress<string>, Task> install)
    {
        var owner = Window.GetWindow(this);
        App.Updating = true;
        App.FirmwareStep = Localize.T("FirmwarePreparing");
        Show();
        var progress = new Progress<string>(step => { App.FirmwareStep = step; if (IsLoaded) Show(); });
        try
        {
            await Task.Run(() => install(progress));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(owner, ex.Message, Localize.T("FirmwareUpdate"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            App.Updating = false;
            App.FirmwareStep = null;
            if (IsLoaded) Show();
        }
    }
}
