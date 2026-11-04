using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GZJam.App;

/// <summary>The Navidrome server and login GZJam uses. <see cref="Password"/> is empty when it isn't known (never saved, or it can't be read on this computer).</summary>
internal sealed record NavidromeAccount(string Url, string User, string Password)
{
    public bool IsComplete => Url.Length > 0 && User.Length > 0 && Password.Length > 0;
}

/// <summary>
/// Where the Navidrome login is kept: a file of its own in the settings folder, so it never goes
/// into <c>settings.json</c> (which library backups carry to other computers). The password is
/// encrypted with Windows' own protection (DPAPI, for this Windows user on this computer), so the
/// file is no use anywhere else. On a Mac or Linux there is no such thing without more
/// dependencies: the file is then only readable by its owner (mode 600), and the password is not
/// encrypted in it.
/// </summary>
internal static class NavidromeCredentials
{
    private static readonly byte[] Entropy = "GZJam Navidrome"u8.ToArray();

    /// <summary>Tests keep the login somewhere else, so they never touch the real one.</summary>
    internal static string? PathOverride { get; set; }

    public static string DefaultPath => PathOverride ?? Path.Combine(AppPaths.SettingsDirectory, "navidrome.json");

    private sealed class Stored
    {
        public string? Url { get; set; }
        public string? User { get; set; }

        /// <summary>The password: base64 of its DPAPI-protected bytes (<see cref="Protection"/> "dpapi") or of the plain text ("none").</summary>
        public string? Password { get; set; }

        public string? Protection { get; set; }
    }

    /// <summary>The saved login, or null if there is none or the file can't be read.</summary>
    public static NavidromeAccount? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) is not { } stored)
                return null;
            return new NavidromeAccount(stored.Url ?? "", stored.User ?? "", Unprotect(stored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Couldn't read the saved Navidrome login", ex);
            return null;
        }
    }

    public static void Save(NavidromeAccount account, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = new Stored { Url = account.Url, User = account.User };
        byte[] password = Encoding.UTF8.GetBytes(account.Password);
        if (OperatingSystem.IsWindows())
        {
            stored.Protection = "dpapi";
            stored.Password = Convert.ToBase64String(ProtectedData.Protect(password, Entropy, DataProtectionScope.CurrentUser));
        }
        else
        {
            stored.Protection = "none";
            stored.Password = Convert.ToBase64String(password);
        }

        // Written to a temporary file and swapped in, so a crash never leaves half a login. Where
        // files have owners, it is made readable by its owner alone from the start.
        string temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(new FileStream(temp, options)))
            file.Write(JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    public static void Clear(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't remove the saved Navidrome login", ex);
        }
    }

    private static string Unprotect(Stored stored)
    {
        if (string.IsNullOrEmpty(stored.Password))
            return "";
        try
        {
            byte[] bytes = Convert.FromBase64String(stored.Password);
            return stored.Protection == "dpapi" && OperatingSystem.IsWindows()
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser))
                : stored.Protection == "none" ? Encoding.UTF8.GetString(bytes) : "";
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Error("The saved Navidrome password can't be read on this computer (it was saved by another Windows user or computer)");
            return "";
        }
    }
}
