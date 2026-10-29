using System.Diagnostics;
using System.IO;
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
        Trace.Assert(Decide(true, false, false, "BeRightBack", local) == "AWAY");
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
            "Away" or "BeRightBack" => "AWAY",
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
// ponytail: miroir de esp32/src/lang/*.h (mêmes textes ASCII calibrés pour la police 4 de l'écran) ;
//           garder les deux en phase, comme la fonction draw() (voir README).
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
        ["fr"] = new() { ["OFFLINE"] = "Hors ligne", ["AVAILABLE"] = "Disponible", ["AWAY"] = "Absent", ["CALL"] = "En appel", ["VIDEO"] = "En visio", ["BUSY"] = "Occupe", ["DND"] = "Ne pas deranger" },
        ["en"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Available", ["AWAY"] = "Away", ["CALL"] = "In a call", ["VIDEO"] = "Video call", ["BUSY"] = "Busy", ["DND"] = "Do not disturb" },
        ["de"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Verfuegbar", ["AWAY"] = "Abwesend", ["CALL"] = "Im Anruf", ["VIDEO"] = "Videoanruf", ["BUSY"] = "Beschaeftigt", ["DND"] = "Nicht stoeren" },
        ["es"] = new() { ["OFFLINE"] = "Sin conexion", ["AVAILABLE"] = "Disponible", ["AWAY"] = "Ausente", ["CALL"] = "En llamada", ["VIDEO"] = "Videollamada", ["BUSY"] = "Ocupado", ["DND"] = "No molestar" },
        ["it"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Disponibile", ["AWAY"] = "Assente", ["CALL"] = "In chiamata", ["VIDEO"] = "Videochiamata", ["BUSY"] = "Occupato", ["DND"] = "Non disturbare" },
        ["pt"] = new() { ["OFFLINE"] = "Offline", ["AVAILABLE"] = "Disponivel", ["AWAY"] = "Ausente", ["CALL"] = "Em chamada", ["VIDEO"] = "Videochamada", ["BUSY"] = "Ocupado", ["DND"] = "Nao incomodar" },
    };

    public static string Label(string lang, string status) =>
        (Labels.TryGetValue(lang, out var m) ? m : Labels["fr"]).GetValueOrDefault(status, status);

    public static void SelfTest()
    {
        string[] codes = { "OFFLINE", "AVAILABLE", "AWAY", "CALL", "VIDEO", "BUSY", "DND" };
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
    // 1.2 = premier firmware qui affiche le libellé localisé envoyé par l'appli (sinon libellé compilé).
    public static readonly Version ExpectedFirmware = new(1, 2);

    HidStream? hid;

    public bool Connected => hid != null;
    public Version? Firmware => hid?.Device.ReleaseNumber;

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
                if (d.GetMaxOutputReportLength() > 0 && d.GetProductName() == "OpenCompanion") return d.Open();
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
