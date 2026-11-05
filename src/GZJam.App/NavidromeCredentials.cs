using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GZJam.Navidrome;

namespace GZJam.App;

/// <summary>
/// The Navidrome server and login GZJam uses. The login is the <see cref="Password"/> (just typed,
/// or from the environment) or a <see cref="Salt"/> and <see cref="Token"/> made from it, which is
/// all that is kept (<see cref="NavidromeCredentials"/>). Both are empty when the login isn't known
/// (never saved, or it can't be read on this computer).
/// </summary>
internal sealed record NavidromeAccount(string Url, string User, string Password, string Salt = "", string Token = "")
{
    /// <summary>There is something to log in with: a password, or a salt and token made from one.</summary>
    public bool HasLogin => Password.Length > 0 || (Salt.Length > 0 && Token.Length > 0);

    public bool IsComplete => Url.Length > 0 && User.Length > 0 && HasLogin;
}

/// <summary>
/// Where the Navidrome login is kept: a file of its own in the settings folder, so it never goes
/// into <c>settings.json</c> (which library backups carry to other computers). The password itself
/// isn't kept: a Subsonic login is a salt and a token (md5 of the password and the salt), and the
/// server takes the same pair every time, so that pair is what is saved. It can't give anyone the
/// password (people reuse theirs) or open the server's web page; it can still be used for the
/// server's API as that user, so it is protected like a password: on Windows it is encrypted for
/// the Windows user (DPAPI, no use on another computer or user), and elsewhere the file is
/// readable only by its owner (mode 600). Files saved by earlier versions, with the password in
/// them, are read as before and rewritten this way.
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

        /// <summary>The salt of the saved login (not secret).</summary>
        public string? Salt { get; set; }

        /// <summary>The token: base64 of its DPAPI-protected bytes (<see cref="Protection"/> "dpapi") or of the plain text ("none").</summary>
        public string? Token { get; set; }

        /// <summary>Files from before tokens: the password, protected the same way. Read, never written.</summary>
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
            string url = stored.Url ?? "", user = stored.User ?? "";
            if (!string.IsNullOrEmpty(stored.Token))
                return new NavidromeAccount(url, user, "", stored.Salt ?? "", Unprotect(stored.Token, stored.Protection));

            // Saved with the password in it: the same login, kept the new way from now on.
            var account = new NavidromeAccount(url, user, Unprotect(stored.Password, stored.Protection));
            if (account.Password.Length > 0 && account.Url.Length > 0 && account.User.Length > 0)
            {
                try
                {
                    return Save(account, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Error("Couldn't keep the saved Navidrome login without its password", ex);
                }
            }
            return account;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("Couldn't read the saved Navidrome login", ex);
            return null;
        }
    }

    /// <summary>
    /// Saves the login: from its password, or (when it has none) the salt and token it already
    /// has. Returns the login as saved, without the password.
    /// </summary>
    public static NavidromeAccount Save(NavidromeAccount account, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (salt, token) = account.Password.Length > 0 ? NavidromeClient.MakeLogin(account.Password) : (account.Salt, account.Token);
        var stored = new Stored
        {
            Url = account.Url,
            User = account.User,
            Salt = salt,
            Protection = OperatingSystem.IsWindows() ? "dpapi" : "none",
        };
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        stored.Token = Convert.ToBase64String(OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes);

        // Written to a temporary file and swapped in, so a crash never leaves half a login. Where
        // files have owners, it is made readable by its owner alone from the start.
        string temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(new FileStream(temp, options)))
            file.Write(JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        File.Move(temp, path, overwrite: true);
        return new NavidromeAccount(account.Url, account.User, "", salt, token);
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
            Log.Error("The saved Navidrome login can't be read on this computer (it was saved by another Windows user or computer)");
            return "";
        }
    }
}
