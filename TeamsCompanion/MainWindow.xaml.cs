using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TeamsCompanion;

public partial class MainWindow : FluentWindow
{
    Type start = typeof(CompanionsPage);  // page affichée à l'ouverture

    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);  // suit le thème clair/sombre de Windows
        _ = ((App)Application.Current).CheckReleasesAsync();  // relues à l'ouverture : vérification à la demande (firmware et appli)
        Loaded += (_, _) => Nav.Navigate(start);
    }

    // Clic sur une notification de mise à jour : ouvre directement les paramètres.
    public void ShowSettings()
    {
        if (IsLoaded) Nav.Navigate(typeof(SettingsPage));
        else start = typeof(SettingsPage);
    }
}
