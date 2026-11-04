using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TeamsCompanion;

// MinFirmware : premier firmware qui embarque les sprites de ce compagnon.
public record Companion(string Name, BitmapSource Image, Version MinFirmware)
{
    public string Requirement => Localize.T("CompanionRequirement", MinFirmware);

    // ponytail: firmware inconnu (écran débranché) = choix libre, on ne peut rien vérifier
    public bool Supports(Version? firmware) => firmware == null || firmware >= MinFirmware;
}

public partial class CompanionsPage : Page
{
    // Corps de Pousse (vert) -> violet Teams, comme pal[2..4] dans src/main.cpp du firmware.
    // Avant All : les champs statiques sont initialisés dans l'ordre, All en a besoin.
    static readonly Dictionary<int, int> Body = new() { [0x38B764] = 0x6264A7, [0x257179] = 0x464775, [0xA7F070] = 0x9EA2FF };

    // Même ordre que COMPANIONS de sprites/png2h.py : l'index est le numéro envoyé à l'écran.
    static readonly Companion[] All =
    {
        new("Pousse", Sprite("pousse/idle"), new Version(1, 0)),
        new("Chouette", Sprite("chouette/idle"), new Version(1, 0)),
        new("Magicien", Sprite("magicien/idle"), new Version(1, 0)),
        new("Trombone", Sprite("trombone/idle"), new Version(1, 0)),
        new("Calculatrice", Sprite("calculatrice/idle"), new Version(1, 0)),
        new("Panda", Sprite("panda/idle"), new Version(1, 0)),
    };

    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };

    public CompanionsPage()
    {
        InitializeComponent();
        Companions.ItemsSource = All;
        Companions.SelectedIndex = ((App)Application.Current).Companion;
        // L'écran peut être branché ou changé pendant que la page est ouverte : regrise chaque seconde.
        refresh.Tick += (_, _) => ShowCompanions();
        Loaded += (_, _) => { refresh.Start(); ShowCompanions(); };
        Unloaded += (_, _) => refresh.Stop();
    }

    void Companion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Companions.SelectedIndex >= 0) ((App)Application.Current).Companion = Companions.SelectedIndex;
    }

    // Grise les compagnons que le firmware branché ne connaît pas encore.
    void ShowCompanions()
    {
        var firmware = ((App)Application.Current).Link.Firmware;
        foreach (Companion c in Companions.Items)
            if (Companions.ItemContainerGenerator.ContainerFromItem(c) is UIElement item) item.IsEnabled = c.Supports(firmware);

        if (Companions.SelectedItem is Companion selected && !selected.Supports(firmware))
            Companions.SelectedItem = Companions.Items.Cast<Companion>().FirstOrDefault(c => c.Supports(firmware));
    }

    // Sprite de l'ESP (sprites/<compagnon>/, embarqué en ressource) tel qu'affiché par l'écran.
    static BitmapSource Sprite(string name)
    {
        var png = new BitmapImage(new Uri($"pack://application:,,,/TeamsCompanion;component/sprites/{name}.png"));
        var src = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new int[w * h];
        src.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i++)
            if (Body.TryGetValue(px[i] & 0xFFFFFF, out var rgb)) px[i] = (px[i] & unchecked((int)0xFF000000)) | rgb;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
