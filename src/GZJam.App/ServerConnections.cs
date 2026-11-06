using GZJam.Audio;
using GZJam.Library;
using GZJam.Subsonic;

namespace GZJam.App;

/// <summary>
/// The music servers GZJam gets songs from, any number of them: the DJ's own and a friend's, say.
/// Each is a Subsonic-compatible server (Navidrome, Gonic, Airsonic…) with its own address and login,
/// kept in Settings › Servers (see <see cref="ServerCredentials"/>); with none saved, the environment
/// variables <c>GZJAM_SUBSONIC_URL</c>, <c>GZJAM_SUBSONIC_USER</c> and <c>GZJAM_SUBSONIC_PASSWORD</c>
/// give one (for trying GZJam out without saving a login).
/// </summary>
internal static class ServerConnections
{
    private static readonly Lock s_lock = new();
    private static Dictionary<string, SubsonicClient> s_clients = new(StringComparer.Ordinal);

    /// <summary>The servers set up, with the logins in use (salt and token: the password is forgotten once saved).</summary>
    public static IReadOnlyList<ServerAccount> Accounts { get; private set; } = [];

    /// <summary>Servers were added, changed or removed (the windows follow: the Refresh button, the library's folders).</summary>
    public static event Action? Changed;

    /// <summary>The servers' logins came from the environment, not from Settings.</summary>
    public static bool FromEnvironment { get; private set; }

    /// <summary>At least one server can be asked for songs.</summary>
    public static bool Any => Clients.Count > 0;

    /// <summary>A client for each server whose login is complete.</summary>
    public static IReadOnlyList<SubsonicClient> Clients
    {
        get
        {
            lock (s_lock)
                return [.. s_clients.Values];
        }
    }

    /// <summary>The client for the server GZJam calls <paramref name="serverId"/>, if it is set up.</summary>
    public static SubsonicClient? ClientFor(string serverId)
    {
        lock (s_lock)
            return s_clients.GetValueOrDefault(serverId);
    }

    public static ServerAccount? AccountFor(string serverId) => Accounts.FirstOrDefault(a => a.Id == serverId);

    /// <summary>The name the DJ gave a server, for its folder in the library; null for a server that isn't set up (any more).</summary>
    public static string? NameOf(string serverId) => AccountFor(serverId) is { Name.Length: > 0 } account ? account.Name : null;

    /// <summary>Reads the saved servers (or the environment's) and lets the decks use them.</summary>
    public static void Configure()
    {
        // These hold whether or not a server is set up now: songs fetched earlier still play.
        RemoteSongs.CacheDirectory = ServerCacheFolder.Current();
        if (ServerCacheFolder.Problem(RemoteSongs.CacheDirectory) is { } problem)
            Log.Error($"The folder for songs from servers ({RemoteSongs.CacheDirectory}) can't be used: {problem}");
        RemoteSongs.ClearTemporary();   // what was fetched only to play last time
        FileVersion.Remote = path => SubsonicClient.IsSubsonicPath(path) ? new FileVersion(0, 0) : null;
        LibraryRoot.RemoteNames = root => SubsonicClient.IsSubsonicPath(root) ? NameOf(root[SubsonicClient.Scheme.Length..].TrimEnd('/')) : null;
        RemoteSongs.Resolver = Resolve;

        var saved = ServerCredentials.Load();
        if (saved.Count == 0 && FromEnvironmentVariables() is { } fromEnvironment)
        {
            Set([fromEnvironment], fromEnvironment: true);
            return;
        }
        Set(saved, fromEnvironment: false);
        if (saved.Count == 0)
            Log.Info("Servers: none set up (Settings › Servers)");
    }

    private static ServerAccount? FromEnvironmentVariables()
    {
        var account = new ServerAccount("env", "Server",
            Environment.GetEnvironmentVariable("GZJAM_SUBSONIC_URL") ?? "",
            Environment.GetEnvironmentVariable("GZJAM_SUBSONIC_USER") ?? "",
            Environment.GetEnvironmentVariable("GZJAM_SUBSONIC_PASSWORD") ?? "");
        return account.IsComplete && TryParseUrl(account.Url, out _) ? account : null;
    }

    /// <summary>Uses exactly these servers from now on (a list just tested and saved).</summary>
    public static void Set(IEnumerable<ServerAccount> accounts, bool fromEnvironment = false)
    {
        var list = accounts.ToList();
        var clients = new Dictionary<string, SubsonicClient>(StringComparer.Ordinal);
        foreach (ServerAccount account in list.Where(a => a.IsComplete && TryParseUrl(a.Url, out _)))
            clients[account.Id] = CreateClient(account);
        Dictionary<string, SubsonicClient> old;
        lock (s_lock)
        {
            old = s_clients;
            s_clients = clients;
            Accounts = list;
        }
        FromEnvironment = fromEnvironment;
        RemoteSongs.Resolver = Resolve;
        foreach (SubsonicClient client in old.Values)
            client.Dispose();
        Log.Info($"Servers: {clients.Count} set up" + (clients.Count == 0 ? "" : ": " + string.Join(", ", list.Where(a => clients.ContainsKey(a.Id)).Select(a => TryParseUrl(a.Url, out Uri? u) ? u.Host : "?")))
            + (fromEnvironment ? " (login from the environment)" : ""));
        Changed?.Invoke();
    }

    /// <summary>Stops using every server. Songs already fetched stay on disk, but can't be opened without their server.</summary>
    public static void DisconnectAll() => Set([]);

    /// <summary>The address to download a song from, or null if it isn't a song of a server that is set up.</summary>
    private static string? Resolve(string path) =>
        SubsonicClient.TryParsePath(path, out string serverId, out _) ? ClientFor(serverId)?.DownloadUrl(path) : null;

    /// <summary>A client for this server; the caller disposes it. Throws <see cref="ArgumentException"/> for an address that isn't one.</summary>
    public static SubsonicClient CreateClient(ServerAccount account) =>
        TryParseUrl(account.Url, out Uri? server)
            ? account.Password.Length > 0 ? new SubsonicClient(account.Id, server, account.User, account.Password)
                : new SubsonicClient(account.Id, server, account.User, account.Salt, account.Token)
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
