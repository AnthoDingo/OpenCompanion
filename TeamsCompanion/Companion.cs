using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Ports;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HidSharp;
using Microsoft.Identity.Client;
using Microsoft.Win32;

namespace TeamsCompanion;

// Statut déduit localement, sans Graph ni tenant :
//   Teams fermé -> OFFLINE, caméra utilisée par Teams -> VIDEO, micro -> CALL,
//   PC inactif > 5 min -> AWAY (même seuil que Teams), sinon AVAILABLE.
// ponytail: les statuts manuels (Occupé / Ne pas déranger) ne sont pas visibles localement.
static class Presence
{
    static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);

    public static string Decide(bool running, bool mic, bool cam, TimeSpan idle) =>
        !running ? "OFFLINE" : cam ? "VIDEO" : mic ? "CALL" : idle > AwayAfter ? "AWAY" : "AVAILABLE";

    public static string Current() =>
        Decide(TeamsRunning(), TeamsUses("microphone"), TeamsUses("webcam"), Idle());

    public static bool TeamsRunning() => Process.GetProcessesByName("ms-teams").Length > 0;

    public static void SelfTest()
    {
        Trace.Assert(Decide(false, true, true, TimeSpan.Zero) == "OFFLINE");
        Trace.Assert(Decide(true, true, true, TimeSpan.FromHours(1)) == "VIDEO");
        Trace.Assert(Decide(true, true, false, TimeSpan.FromHours(1)) == "CALL");
        Trace.Assert(Decide(true, false, false, TimeSpan.FromMinutes(6)) == "AWAY");
        Trace.Assert(Decide(true, false, false, TimeSpan.FromMinutes(1)) == "AVAILABLE");
    }

    // Windows note chaque usage micro/caméra par appli ; LastUsedTimeStop == 0 => en cours d'utilisation.
    public static bool TeamsUses(string device)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{device}\MSTeams_8wekyb3d8bbwe");
        return key?.GetValue("LastUsedTimeStart") is long start && start > 0
            && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
    }

    static TimeSpan Idle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
            : TimeSpan.Zero;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    struct LastInputInfo { public uint Size; public uint Time; }
}

// Mode « Logs client » : la disponibilité que Teams écrit dans son propre log, statuts manuels compris :
//   native_modules::UserDataCrossCloudModule: Received Action: UserPresenceAction: {cloud_context: ..., availability: Busy}
// Le log ne dit pas si on est en appel : micro / caméra (Presence) priment pour CALL / VIDEO.
// ponytail: format interne à Teams, non documenté ; à revérifier si une mise à jour de Teams le change.
sealed class TeamsLog
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\Logs");
    static readonly Regex Availability = new(@"UserPresenceAction: \{.*availability: (\w+)", RegexOptions.Compiled);

    string? file;
    long position;
    string partial = "";  // fin de ligne pas encore écrite par Teams
    string? availability;

    public string Current()
    {
        Read();
        return Decide(Presence.TeamsRunning(), Presence.TeamsUses("microphone"), Presence.TeamsUses("webcam"),
            availability, Presence.Current);
    }

    // local : détection locale (inactivité) quand le log n'a encore rien dit d'utile.
    public static string Decide(bool running, bool mic, bool cam, string? availability, Func<string> local) =>
        !running ? "OFFLINE" : cam ? "VIDEO" : mic ? "CALL"
        : availability is null or "PresenceUnknown" ? local()
        : GraphPresence.Map(availability, "");

    // Lit seulement ce que Teams a ajouté depuis le dernier passage ; relit tout un nouveau fichier (rotation).
    void Read()
    {
        var dir = new DirectoryInfo(Dir);
        var newest = dir.Exists ? dir.GetFiles("MSTeams_*.log").MaxBy(f => f.LastWriteTimeUtc)?.FullName : null;
        if (newest == null) return;
        if (newest != file)
        {
            file = newest;
            position = 0;
            partial = "";
        }

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < position) position = 0;  // fichier recréé plus court
        fs.Position = position;
        var text = partial + new StreamReader(fs, Encoding.UTF8).ReadToEnd();
        position = fs.Position;

        int end = text.LastIndexOf('\n') + 1;
        partial = text[end..];
        if (Availability.Matches(text[..end]).LastOrDefault() is { } m) availability = m.Groups[1].Value;
    }

    public static void SelfTest()
    {
        Trace.Assert(Availability.Match("2026-10-29T19:33:15.585759+01:00 0x00005860 <INFO> native_modules::UserDataCrossCloudModule: " +
            "Received Action: UserPresenceAction: {cloud_context: https://teams.live.com, availability: DoNotDisturb}").Groups[1].Value == "DoNotDisturb");
        Func<string> local = () => "AWAY";
        Trace.Assert(Decide(false, true, true, "Available", local) == "OFFLINE");
        Trace.Assert(Decide(true, false, true, "Available", local) == "VIDEO");
        Trace.Assert(Decide(true, true, false, "Available", local) == "CALL");
        Trace.Assert(Decide(true, false, false, "DoNotDisturb", local) == "DND");
        Trace.Assert(Decide(true, true, false, "DoNotDisturb", local) == "CALL");
        Trace.Assert(Decide(true, false, false, "BeRightBack", local) == "BRB");
        Trace.Assert(Decide(true, false, false, "Available", local) == "AVAILABLE");
        Trace.Assert(Decide(true, false, false, null, local) == "AWAY");
        Trace.Assert(Decide(true, false, false, "PresenceUnknown", local) == "AWAY");
    }
}

// Mode --graph : le vrai statut Teams (dont Occupé / Ne pas déranger choisis à la main) via
// Microsoft Graph /me/presence. Delegated Presence.Read : consentement utilisateur, aucun admin.
// Client public first-party "Microsoft Graph Command Line Tools" (présent dans tous les tenants),
// donc rien à enregistrer dans Entra. Token mis en cache sur disque (DPAPI) pour éviter de
// reconnecter à chaque lancement. Tourne en tâche de fond : le thread UI n'attend jamais le réseau.
static class GraphPresence
{
    const string ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    static readonly string[] Scopes = { "https://graph.microsoft.com/Presence.Read" };
    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCompanion", "token.bin");

    static readonly HttpClient Http = new();
    static string last = "OFFLINE";

    // Lance la boucle de fond et rend un getter non bloquant pour le Tick de l'UI.
    public static Func<string> Start()
    {
        _ = Task.Run(LoopAsync);
        return () => Volatile.Read(ref last);
    }

    static async Task LoopAsync()
    {
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority("https://login.microsoftonline.com/organizations")
            .Build();
        AttachCache(app.UserTokenCache);

        while (true)
        {
            try
            {
                var token = await TokenAsync(app);
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/presence");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = await Http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                Volatile.Write(ref last, Map(
                    root.GetProperty("availability").GetString() ?? "",
                    root.GetProperty("activity").GetString() ?? ""));
            }
            catch { /* réseau / token expiré : on garde le dernier statut connu jusqu'au prochain tour */ }

            await Task.Delay(TimeSpan.FromSeconds(30));  // Graph limite la fréquence ; l'ESP est renvoyé côté App
        }
    }

    static async Task<string> TokenAsync(IPublicClientApplication app)
    {
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        try
        {
            return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync()).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            // Première connexion : on affiche le lien + code device-code et on ouvre le navigateur.
            return (await app.AcquireTokenWithDeviceCode(Scopes, flow =>
            {
                try { Process.Start(new ProcessStartInfo(flow.VerificationUrl) { UseShellExecute = true }); } catch { }
                System.Windows.Forms.MessageBox.Show(flow.Message, "OpenCompanion – connexion Teams");
                return Task.CompletedTask;
            }).ExecuteAsync()).AccessToken;
        }
    }

    // availability / activity Graph -> vocabulaire de l'ESP (mêmes couleurs).
    public static string Map(string availability, string activity) => activity switch
    {
        "InACall" or "InAConferenceCall" => "CALL",
        "InAMeeting" or "Presenting" => "VIDEO",
        _ => availability switch
        {
            "Available" or "AvailableIdle" => "AVAILABLE",
            "Away" => "AWAY",
            "BeRightBack" => "BRB",
            "Busy" or "BusyIdle" => "BUSY",
            "DoNotDisturb" => "DND",
            _ => "OFFLINE",  // Offline, PresenceUnknown
        },
    };

    public static void SelfTest()
    {
        Trace.Assert(Map("Available", "Available") == "AVAILABLE");
        Trace.Assert(Map("Available", "InACall") == "CALL");
        Trace.Assert(Map("Busy", "InAConferenceCall") == "CALL");
        Trace.Assert(Map("Busy", "InAMeeting") == "VIDEO");
        Trace.Assert(Map("DoNotDisturb", "Presenting") == "VIDEO");
        Trace.Assert(Map("DoNotDisturb", "DoNotDisturb") == "DND");
        Trace.Assert(Map("Busy", "Busy") == "BUSY");
        Trace.Assert(Map("Away", "Away") == "AWAY");
        Trace.Assert(Map("BeRightBack", "BeRightBack") == "BRB");
        Trace.Assert(Map("Offline", "Offline") == "OFFLINE");
        Trace.Assert(Map("PresenceUnknown", "") == "OFFLINE");
    }

    // Cache MSAL chiffré par DPAPI (lié au compte Windows courant) : jamais de token en clair sur disque.
    static void AttachCache(ITokenCache cache)
    {
        cache.SetBeforeAccess(a =>
        {
            if (File.Exists(CachePath))
                a.TokenCache.DeserializeMsalV3(ProtectedData.Unprotect(File.ReadAllBytes(CachePath), null, DataProtectionScope.CurrentUser));
        });
        cache.SetAfterAccess(a =>
        {
            if (!a.HasStateChanged) return;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllBytes(CachePath, ProtectedData.Protect(a.TokenCache.SerializeMsalV3(), null, DataProtectionScope.CurrentUser));
        });
    }
}

// Libellés affichés à l'écran, par langue. Envoyés à l'ESP (format "STATUT|Libellé") et repris dans l'info-bulle.
// ponytail: textes ASCII calibrés pour la police 4 de l'écran (sans accents) ; le français est aussi
//           le libellé par défaut du firmware (TXT_* de esp32/src/main.cpp), à garder en phase.
public record Language(string Code, string Name);

static class Localize
{
    public static readonly Language[] Languages =
    {
        new("fr", "Français"), new("en", "English"), new("de", "Deutsch"),
        new("es", "Español"), new("it", "Italiano"), new("pt", "Português"),
    };

    static readonly Dictionary<string, Dictionary<string, string>> Labels = new()
    {
        ["fr"] = new() { ["OFFLINE"] = "Hors ligne", ["AVAILABLE"] = "Disponible", ["AWAY"] = "Absent", ["BRB"] = "Je reviens vite", ["CALL"] = "En appel", ["VIDEO"] = "En visio", ["BUSY"] = "Occupe", ["DND"] = "Ne pas deranger", ["UPDATE"] = "Mise a jour" },
        ["en"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Available", ["AWAY"] = "Away", ["BRB"] = "Be right back", ["CALL"] = "In a call", ["VIDEO"] = "Video call", ["BUSY"] = "Busy", ["DND"] = "Do not disturb", ["UPDATE"] = "Updating" },
        ["de"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Verfuegbar", ["AWAY"] = "Abwesend", ["BRB"] = "Gleich zurueck", ["CALL"] = "Im Anruf", ["VIDEO"] = "Videoanruf", ["BUSY"] = "Beschaeftigt", ["DND"] = "Nicht stoeren", ["UPDATE"] = "Update" },
        ["es"] = new() { ["OFFLINE"] = "Sin conexion", ["AVAILABLE"] = "Disponible", ["AWAY"] = "Ausente", ["BRB"] = "Ahora vuelvo", ["CALL"] = "En llamada", ["VIDEO"] = "Videollamada", ["BUSY"] = "Ocupado", ["DND"] = "No molestar", ["UPDATE"] = "Actualizando" },
        ["it"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Disponibile", ["AWAY"] = "Assente", ["BRB"] = "Torno subito", ["CALL"] = "In chiamata", ["VIDEO"] = "Videochiamata", ["BUSY"] = "Occupato", ["DND"] = "Non disturbare", ["UPDATE"] = "Aggiornamento" },
        ["pt"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Disponivel", ["AWAY"] = "Ausente", ["BRB"] = "Volto ja", ["CALL"] = "Em chamada", ["VIDEO"] = "Videochamada", ["BUSY"] = "Ocupado", ["DND"] = "Nao incomodar", ["UPDATE"] = "Atualizando" },
    };

    public static string Label(string lang, string status) =>
        (Labels.TryGetValue(lang, out var m) ? m : Labels["fr"]).GetValueOrDefault(status, status);

    public static void SelfTest()
    {
        string[] codes = { "OFFLINE", "AVAILABLE", "AWAY", "BRB", "CALL", "VIDEO", "BUSY", "DND", "UPDATE" };
        foreach (var l in Languages)
            foreach (var c in codes)
                Trace.Assert(Labels[l.Code].ContainsKey(c), $"libellé manquant {l.Code}/{c}");
        Trace.Assert(Label("de", "DND") == "Nicht stoeren");
        Trace.Assert(Label("xx", "AWAY") == "Absent");  // langue inconnue -> français
    }
}

// L'ESP32-S3 se présente comme périphérique HID nommé "OpenCompanion" : pas de port COM à choisir.
sealed class CompanionLink : IDisposable
{
    // Version attendue du firmware : égale à FW_VERSION de esp32/src/main.cpp (annoncée en bcdDevice USB).
    // Quand une version ajoute un statut ou un compagnon, noter ici « X.Y = premier firmware qui ... ».
    public static readonly Version ExpectedFirmware = new(1, 0);

    // Ligne envoyée à l'écran : statut, libellé localisé, numéro du compagnon.
    public static string Wire(string status, string label, int companion) => $"{status}|{label}|{companion}";

    public static void SelfTest()
    {
        Trace.Assert(Wire("DND", "Ne pas deranger", 2) == "DND|Ne pas deranger|2");
    }

    HidStream? hid;

    public bool Connected => hid != null;
    // bcdDevice USB 0xJJMN = JJ.M.N ; HidSharp.ReleaseNumber lirait 0x0120 comme 1.20, d'où le décodage ici.
    public Version? Firmware => hid?.Device.ReleaseNumberBcd is int bcd ? FromBcd(bcd) : null;

    public static Version FromBcd(int bcd) => new((bcd >> 12) * 10 + ((bcd >> 8) & 0xF), (bcd >> 4) & 0xF);

    public bool Send(string status)
    {
        try
        {
            hid ??= Open();
            hid?.Write(Report(hid.Device, status));
            return hid != null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException)
        {
            Dispose();  // écran débranché : on retentera au prochain envoi
            return false;
        }
    }

    public void Dispose()
    {
        hid?.Dispose();
        hid = null;
    }

    static HidStream? Open()
    {
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            try
            {
                // Windows peut renvoyer le nom de l'interface HID, fixé à "TinyUSB HID" par le core Arduino, au lieu du produit.
                // ponytail: tout HID TinyUSB Espressif est pris pour l'écran ; un seul ESP branché à la fois.
                if (d.GetMaxOutputReportLength() > 0 &&
                    d.GetProductName() is "OpenCompanion" or "TinyUSB HID" && d.VendorID == 0x303A) return d.Open();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // HID système verrouillés
        }
        return null;
    }

    static byte[] Report(HidDevice d, string status)
    {
        var buf = new byte[d.GetMaxOutputReportLength()];
        buf[0] = d.GetReportDescriptor().OutputReports.First().ReportID;
        Encoding.ASCII.GetBytes(status + "\n").CopyTo(buf, 1);
        return buf;
    }
}

// Mise à jour du firmware depuis les releases GitHub du repo firmware dédié (publiées sur un tag vX.Y) :
// firmware.bin, un seul pour toutes les langues (libellé envoyé par l'appli), écrit par esptool (exe officiel Espressif, téléchargé une fois).
// firmware.bin est signé (ECDSA P-256 / SHA-256) : on vérifie firmware.bin.sig avec SigningPublicKey avant de flasher, sinon on refuse (fail-closed).
// Aucun support côté firmware : ouvrir son port série à 1200 bauds le redémarre en mode téléchargement
// (core Arduino, comme l'IDE), puis il revient sous un port USB-Serial/JTAG.
// ponytail: repo public requis (API GitHub anonyme) ; un seul ESP Espressif branché à la fois.
static class FirmwareUpdate
{
    const string Repo = "AnthoDingo/OpenCompanion-Firmware";
    const string EsptoolZip = "https://github.com/espressif/esptool/releases/download/v4.8.1/esptool-v4.8.1-win64.zip";

    // Clé publique ECDSA P-256 (SPKI, base64 DER) de l'auteur, pour vérifier firmware.bin avant de flasher.
    // La clé privée signe les releases en CI (secret FIRMWARE_SIGNING_KEY du dépôt firmware) et ne quitte jamais
    // le poste de l'auteur. VIDE = aucune mise à jour release acceptée (fail-closed). Obtenue par
    // openssl ec -in signing.pem -pubout -outform DER | base64 -w0 (signing.pem : racine du dépôt firmware, ignoré par git).
    const string SigningPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEpKuZfHhWU2YgqYnp8a+jIY6OQZb/2PfmoGSQqZj0uUzpJrEuyLunWzRqAY10OnpGCDduZF63yILXRB0g4HF1nw==";

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCompanion");
    static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "OpenCompanion" } } };

    public record Release(Version Version, string Url, string SigUrl);

    // Dernière release qui contient firmware.bin ET sa signature firmware.bin.sig ; null sinon.
    public static async Task<Release?> LatestAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        if (ParseTag(root.GetProperty("tag_name").GetString()) is not { } version) return null;
        string? bin = null, sig = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == "firmware.bin") bin = url;
            else if (name == "firmware.bin.sig") sig = url;
        }
        return bin != null && sig != null ? new Release(version, bin, sig) : null;
    }

    // Vérifie une signature détachée ECDSA P-256 / SHA-256 au format DER (openssl dgst -sha256 -sign).
    static bool Verify(byte[] data, byte[] signature, string spkiBase64)
    {
        if (spkiBase64.Length == 0) return false;  // clé non configurée : refuser plutôt que flasher à l'aveugle
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // "v1.3" -> 1.3, comparable à CompanionLink.Firmware (FW_VERSION 0x0130).
    public static Version? ParseTag(string? tag) =>
        Version.TryParse(tag?.TrimStart('v'), out var v) ? new Version(v.Major, Math.Max(v.Minor, 0)) : null;

    public static async Task InstallAsync(Release release, IProgress<string> progress)
    {
        progress.Report($"Téléchargement du firmware {release.Version}…");
        var download = Http.GetByteArrayAsync(release.Url);
        var signature = Http.GetByteArrayAsync(release.SigUrl);
        await Task.WhenAll(download, signature, Task.Delay(3000));  // au moins 3 s d'animation sur l'écran avant son redémarrage
        var image = await download;
        if (!Verify(image, await signature, SigningPublicKey))
            throw new InvalidOperationException("Signature du firmware invalide : mise à jour refusée.");
        await FlashAsync(image, $"firmware {release.Version}", progress);
    }

    // Fichier choisi par l'utilisateur (déjà validé par Invalid côté fenêtre, revérifié dans FlashAsync).
    public static async Task InstallFileAsync(byte[] image, string name, IProgress<string> progress)
    {
        progress.Report($"Préparation de {name}…");
        await Task.Delay(3000);  // même animation sur l'écran que pour une release
        await FlashAsync(image, name, progress);
    }

    // Partition app0 de la table par défaut (0x10000, 0x330000 octets) : on n'écrit que là.
    const int AppPartitionSize = 0x330000;

    // Raison de refuser l'image, ou null si c'est bien un firmware d'application ESP32-S3.
    public static string? Invalid(byte[] image)
    {
        if (image.Length < 24 || image[0] != 0xE9)
            return "Ce fichier n'est pas un firmware ESP32 : choisissez le firmware.bin produit par PlatformIO.";
        if (BitConverter.ToUInt16(image, 12) != 9)  // chip_id de l'en-tête étendu, 9 = ESP32-S3
            return "Ce firmware n'a pas été compilé pour un ESP32-S3.";
        if (image.Length > 0x8002 && image[0x8000] == 0xAA && image[0x8001] == 0x50)
            return "Image fusionnée (bootloader + partitions + application) : choisissez le firmware.bin de l'application seule (.pio/build/<env>/firmware.bin).";
        if (image.Length > AppPartitionSize)
            return $"Firmware trop gros ({image.Length / 1024} Ko, maximum {AppPartitionSize / 1024} Ko).";
        return null;
    }

    static async Task FlashAsync(byte[] image, string name, IProgress<string> progress)
    {
        if (Invalid(image) is { } why) throw new InvalidOperationException(why);
        Directory.CreateDirectory(Dir);
        var bin = Path.Combine(Dir, "firmware.bin");
        await File.WriteAllBytesAsync(bin, image);
        var esptool = await EsptoolAsync();

        progress.Report("Redémarrage de l'écran en mode téléchargement…");
        var port = EspPorts().FirstOrDefault() ?? throw new InvalidOperationException("Port série de l'écran introuvable.");
        // L'ESP redémarre pendant l'ouverture : Windows lève souvent « device not functioning », c'est le signe attendu.
        try { using var touch = new SerialPort(port, 1200); touch.Open(); }
        catch (IOException) { }
        port = await WaitBootloaderAsync(port);

        progress.Report($"Écriture de {name} ({port}), ne débranchez pas l'écran…");
        // Seulement l'application (partition app0) : bootloader et table de partitions restent ceux du premier flash.
        var psi = new ProcessStartInfo(esptool,
            $"--chip esp32s3 --port {port} --before no_reset --after hard_reset write_flash 0x10000 \"{bin}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"esptool a échoué : {(await stderr + await stdout).Trim().Split('\n').Last()}");
    }

    static async Task<string> EsptoolAsync()
    {
        var dir = Path.Combine(Dir, "esptool");
        string? Find() => Directory.Exists(dir) ? Directory.GetFiles(dir, "esptool.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (Find() is { } exe) return exe;
        ZipFile.ExtractToDirectory(await Http.GetStreamAsync(EsptoolZip), dir, overwriteFiles: true);
        return Find() ?? throw new InvalidOperationException("esptool.exe absent de l'archive.");
    }

    // Le port disparaît au redémarrage puis revient (souvent sous le même nom) une fois en mode téléchargement.
    static async Task<string> WaitBootloaderAsync(string port)
    {
        for (int i = 0; i < 30 && EspPorts().Contains(port); i++) await Task.Delay(100);
        for (int i = 0; i < 150; i++)
        {
            if (EspPorts().FirstOrDefault() is { } boot)
            {
                await Task.Delay(500);  // laisse le pilote finir d'ouvrir le port
                return boot;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("L'écran n'est pas revenu en mode téléchargement. Rebranchez-le en maintenant BOOT, puis réessayez.");
    }

    // Ports COM présents des périphériques Espressif (VID 303A) : firmware Arduino comme bootloader ROM.
    static IEnumerable<string> EspPorts()
    {
        var present = SerialPort.GetPortNames();
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        foreach (var dev in usb?.GetSubKeyNames() ?? [])
        {
            if (!dev.StartsWith("VID_303A", StringComparison.OrdinalIgnoreCase)) continue;
            using var devKey = usb!.OpenSubKey(dev);
            foreach (var inst in devKey?.GetSubKeyNames() ?? [])
            {
                using var param = devKey!.OpenSubKey($@"{inst}\Device Parameters");
                if (param?.GetValue("PortName") is string name && present.Contains(name)) yield return name;
            }
        }
    }

    public static void SelfTest()
    {
        Trace.Assert(ParseTag("v1.3") == new Version(1, 3));
        Trace.Assert(ParseTag("1.2") == new Version(1, 2));
        Trace.Assert(ParseTag("v2") == null);  // Version.TryParse refuse un seul composant
        Trace.Assert(ParseTag("nightly") == null);
        Trace.Assert(CompanionLink.FromBcd(0x0120) == new Version(1, 2));
        Trace.Assert(CompanionLink.FromBcd(0x1031) == new Version(10, 3));
        Trace.Assert(ParseTag("v1.3") > CompanionLink.FromBcd(0x0120));

        var app = new byte[1024];
        app[0] = 0xE9;
        app[12] = 9;
        Trace.Assert(Invalid(app) == null);
        Trace.Assert(Invalid([0x50, 0x4B, 3, 4]) != null);  // un zip, trop court et mauvais magic
        app[12] = 0;  // ESP32 classique
        Trace.Assert(Invalid(app) != null);
        var merged = new byte[0x9000];
        merged[0] = 0xE9;
        merged[12] = 9;
        merged[0x8000] = 0xAA;
        merged[0x8001] = 0x50;
        Trace.Assert(Invalid(merged) != null);

        // Signature ECDSA P-256 produite par openssl (dgst -sha256 -sign), vérifiée par la BCL : clé de TEST, pas de prod.
        const string testPub = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMbaoHBQgFiGvwFHEefECjvNmPsIQ4ax8IpPHCZeDgWRv1Yyeemtavzel3GBmLXcdtiBL3IXLPYxM30ZEIa8PkQ==";
        byte[] msg = Encoding.ASCII.GetBytes("firmware-test");
        byte[] goodSig = Convert.FromBase64String("MEUCIQDzcDM6wmCCvgafAqviUmwnFI0L5U7yNwMkDOtPaoiQtgIgdoY1jxQMafcndO/4bo7sgQD2FaWdFbQW/DQK4qPJLCk=");
        Trace.Assert(Verify(msg, goodSig, testPub));             // signature valide -> accepté
        msg[0] ^= 1;
        Trace.Assert(!Verify(msg, goodSig, testPub));            // firmware altéré -> rejet
        Trace.Assert(!Verify(Encoding.ASCII.GetBytes("x"), goodSig, ""));  // clé non configurée -> rejet
    }
}
