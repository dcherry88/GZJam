using GZJam.Audio;
using GZJam.Library;
using GZJam.Navidrome;

namespace GZJam.App;

/// <summary>
/// The Navidrome server GZJam streams songs from, if one is set up. Its address and login are what
/// Settings › Navidrome saved (see <see cref="NavidromeCredentials"/>); if nothing is saved, the
/// environment variables <c>GZJAM_NAVIDROME_URL</c>, <c>GZJAM_NAVIDROME_USER</c> and
/// <c>GZJAM_NAVIDROME_PASSWORD</c> are used (for trying GZJam out without saving a login).
/// </summary>
internal static class NavidromeConnection
{
    public static NavidromeClient? Client { get; private set; }

    /// <summary>A server was set up or disconnected (the window follows: its Refresh button).</summary>
    public static event Action? Changed;

    /// <summary>The login in use, with its password; null when no server is set up.</summary>
    public static NavidromeAccount? Account { get; private set; }

    /// <summary>Where the login in use came from: saved in Settings, or the environment variables.</summary>
    public static bool FromEnvironment { get; private set; }

    /// <summary>Reads the saved login (or the environment's) and lets the decks use the server.</summary>
    public static void Configure()
    {
        // These hold whether or not a server is set up now: songs fetched earlier still play.
        RemoteSongs.CacheDirectory = NavidromeCacheFolder.Current();
        if (NavidromeCacheFolder.Problem(RemoteSongs.CacheDirectory) is { } problem)
            Log.Error($"The folder for Navidrome songs ({RemoteSongs.CacheDirectory}) can't be used: {problem}");
        FileVersion.Remote = path => NavidromeClient.IsNavidromePath(path) ? new FileVersion(0, 0) : null;

        NavidromeAccount? saved = NavidromeCredentials.Load();
        if (saved is { IsComplete: true } && TryParseUrl(saved.Url, out _))
        {
            Apply(saved, fromEnvironment: false);
            return;
        }
        var fromEnvironment = new NavidromeAccount(
            Environment.GetEnvironmentVariable("GZJAM_NAVIDROME_URL") ?? "",
            Environment.GetEnvironmentVariable("GZJAM_NAVIDROME_USER") ?? "",
            Environment.GetEnvironmentVariable("GZJAM_NAVIDROME_PASSWORD") ?? "");
        if (fromEnvironment.IsComplete && TryParseUrl(fromEnvironment.Url, out _))
        {
            Apply(fromEnvironment, fromEnvironment: true);
            return;
        }
        // Saved but without a password that can be read here: kept for Settings to show, not used.
        Account = saved;
        Log.Info("Navidrome: not set up (Settings › Navidrome)");
    }

    /// <summary>Uses this server from now on (a login just tested and saved).</summary>
    public static void Apply(NavidromeAccount account, bool fromEnvironment = false)
    {
        if (!TryParseUrl(account.Url, out Uri? server))
            throw new ArgumentException("Not a web address", nameof(account));
        Client?.Dispose();
        Client = CreateClient(account);
        Account = account;
        FromEnvironment = fromEnvironment;
        RemoteSongs.Resolver = Client.StreamUrl;
        Log.Info($"Navidrome: songs come from {server.Host}{(fromEnvironment ? " (login from the environment)" : "")}");
        Changed?.Invoke();
    }

    /// <summary>Stops using the server. Songs already fetched stay on disk, but can't be opened without it.</summary>
    public static void Disconnect()
    {
        Client?.Dispose();
        Client = null;
        Account = null;
        FromEnvironment = false;
        RemoteSongs.Resolver = null;
        Log.Info("Navidrome: disconnected");
        Changed?.Invoke();
    }

    /// <summary>A client for this login; the caller disposes it. Throws <see cref="ArgumentException"/> for an address that isn't one.</summary>
    public static NavidromeClient CreateClient(NavidromeAccount account) =>
        TryParseUrl(account.Url, out Uri? server)
            ? account.Password.Length > 0 ? new NavidromeClient(server, account.User, account.Password)
                : new NavidromeClient(server, account.User, account.Salt, account.Token)
            : throw new ArgumentException("Not a web address", nameof(account));

    /// <summary>
    /// A server address as typed: "music.example.com" is taken as https; only http and https are
    /// accepted. Anything after the host and path (a query, a fragment) is dropped.
    /// </summary>
    public static bool TryParseUrl(string? text, out Uri uri)
    {
        uri = null!;
        string typed = text?.Trim() ?? "";
        if (typed.Length == 0)
            return false;
        if (!typed.Contains("://", StringComparison.Ordinal))
            typed = "https://" + typed;
        if (!Uri.TryCreate(typed, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host))
            return false;
        uri = new UriBuilder(parsed.Scheme, parsed.Host, parsed.IsDefaultPort ? -1 : parsed.Port, parsed.AbsolutePath.TrimEnd('/')).Uri;
        return true;
    }
}
