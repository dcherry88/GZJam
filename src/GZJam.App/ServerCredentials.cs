using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GZJam.Subsonic;

namespace GZJam.App;

/// <summary>
/// A music server (anything that speaks the Subsonic API: Navidrome, Gonic, Airsonic…) and the login
/// GZJam uses for it. <see cref="Id"/> is GZJam's own, made once and never changed: it is part of
/// every song's path, so renaming a server or moving it to a new address keeps the songs' marks.
/// The login is the <see cref="Password"/> (just typed, or from the environment) or a
/// <see cref="Salt"/> and <see cref="Token"/> made from it, which is all that is kept
/// (<see cref="ServerCredentials"/>). Both are empty when the login isn't known (never saved, or it
/// can't be read on this computer).
/// </summary>
internal sealed record ServerAccount(string Id, string Name, string Url, string User, string Password = "", string Salt = "", string Token = "")
{
    /// <summary>There is something to log in with: a password, or a salt and token made from one.</summary>
    public bool HasLogin => Password.Length > 0 || (Salt.Length > 0 && Token.Length > 0);

    public bool IsComplete => Id.Length > 0 && Url.Length > 0 && User.Length > 0 && HasLogin;

    /// <summary>A short random id for a new server.</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

/// <summary>
/// Where the servers and their logins are kept: a file of their own in the settings folder, so they
/// never go into <c>settings.json</c> (which library backups carry to other computers). The password
/// itself isn't kept: a Subsonic login is a salt and a token (md5 of the password and the salt), and
/// the server takes the same pair every time, so that pair is what is saved. It can't give anyone the
/// password (people reuse theirs) or open the server's web page; it can still be used for the
/// server's API as that user, so it is protected like a password: on Windows it is encrypted for the
/// Windows user (DPAPI, no use on another computer or user), and elsewhere the file is readable only
/// by its owner (mode 600).
/// </summary>
internal static class ServerCredentials
{
    private static readonly byte[] Entropy = "GZJam servers"u8.ToArray();

    /// <summary>Tests keep the logins somewhere else, so they never touch the real ones.</summary>
    internal static string? PathOverride { get; set; }

    public static string DefaultPath => PathOverride ?? Path.Combine(AppPaths.SettingsDirectory, "servers.json");

    private sealed class Stored
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? User { get; set; }

        /// <summary>The salt of the saved login (not secret).</summary>
        public string? Salt { get; set; }

        /// <summary>The token: base64 of its DPAPI-protected bytes (<see cref="Protection"/> "dpapi") or of the plain text ("none").</summary>
        public string? Token { get; set; }

        public string? Protection { get; set; }
    }

    private sealed class Saved
    {
        public List<Stored> Servers { get; set; } = [];
    }

    /// <summary>The saved servers, in the order they were added; empty if there are none or the file can't be read.</summary>
    public static List<ServerAccount> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is not { } file)
                return [];
            return [.. file.Servers.Where(s => !string.IsNullOrEmpty(s.Id)).Select(s => new ServerAccount(
                s.Id!, s.Name ?? "", s.Url ?? "", s.User ?? "", "", s.Salt ?? "", Unprotect(s.Token, s.Protection)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Couldn't read the saved servers", ex);
            return [];
        }
    }

    /// <summary>
    /// Saves these servers (replacing what was saved). A login with a password is kept as the salt
    /// and token made from it; one without keeps the salt and token it has. Returns the servers as
    /// saved, without passwords.
    /// </summary>
    public static List<ServerAccount> Save(IEnumerable<ServerAccount> accounts, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var saved = new List<ServerAccount>();
        var file = new Saved();
        foreach (ServerAccount account in accounts)
        {
            var (salt, token) = account.Password.Length > 0 ? SubsonicClient.MakeLogin(account.Password) : (account.Salt, account.Token);
            byte[] bytes = Encoding.UTF8.GetBytes(token);
            file.Servers.Add(new Stored
            {
                Id = account.Id, Name = account.Name, Url = account.Url, User = account.User, Salt = salt,
                Protection = OperatingSystem.IsWindows() ? "dpapi" : "none",
                Token = Convert.ToBase64String(OperatingSystem.IsWindows()
                    ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes),
            });
            saved.Add(account with { Password = "", Salt = salt, Token = token });
        }

        // Written to a temporary file and swapped in, so a crash never leaves half a login. Where
        // files have owners, it is made readable by its owner alone from the start.
        string temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new StreamWriter(new FileStream(temp, options)))
            stream.Write(JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
        return saved;
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
            Log.Error("Couldn't remove the saved servers", ex);
        }
    }

    private static string Unprotect(string? saved, string? protection)
    {
        if (string.IsNullOrEmpty(saved))
            return "";
        try
        {
            byte[] bytes = Convert.FromBase64String(saved);
            return protection == "dpapi" && OperatingSystem.IsWindows()
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser))
                : protection == "none" ? Encoding.UTF8.GetString(bytes) : "";
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Error("A saved server login can't be read on this computer (it was saved by another Windows user or computer)");
            return "";
        }
    }
}
