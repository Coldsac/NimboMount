using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NimboMount.Mega;

namespace NimboMount;

public sealed class AppSettings
{
    public string Email { get; set; } = "";

    public string DriveLetter { get; set; } = "M";

    public bool Remember { get; set; } = true;

    public bool MountOnLaunch { get; set; } = true;

    public bool StartWithWindows { get; set; }
}

public static class AppPaths
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NimboMount");

    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    public static string SessionPath => Path.Combine(Directory, "session.bin");

    public static string LogPath => Path.Combine(Directory, "nimbo.log");
}

public static class SessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NimboMount.session.v1");

    public static void Save(MegaSession session)
    {
        Directory.CreateDirectory(AppPaths.Directory);
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredSession
        {
            Email = session.Email,
            UserHandle = session.UserHandle,
            SessionId = session.SessionId,
            MasterKey = Convert.ToBase64String(session.MasterKey),
        });
        var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.SessionPath, protectedBytes);
    }

    public static MegaSession? TryLoad()
    {
        if (!File.Exists(AppPaths.SessionPath))
        {
            return null;
        }

        var json = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.SessionPath), Entropy, DataProtectionScope.CurrentUser);
        var stored = JsonSerializer.Deserialize<StoredSession>(json);
        if (stored == null
            || string.IsNullOrWhiteSpace(stored.Email)
            || string.IsNullOrWhiteSpace(stored.UserHandle)
            || string.IsNullOrWhiteSpace(stored.SessionId)
            || string.IsNullOrWhiteSpace(stored.MasterKey))
        {
            return null;
        }

        return new MegaSession
        {
            Email = stored.Email,
            UserHandle = stored.UserHandle,
            SessionId = stored.SessionId,
            MasterKey = Convert.FromBase64String(stored.MasterKey),
        };
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(AppPaths.SessionPath))
            {
                File.Delete(AppPaths.SessionPath);
            }
        }
        catch (IOException)
        {
        }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Directory);
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(settings));
    }

    private sealed class StoredSession
    {
        public string? Email { get; set; }

        public string? UserHandle { get; set; }

        public string? SessionId { get; set; }

        public string? MasterKey { get; set; }
    }
}
