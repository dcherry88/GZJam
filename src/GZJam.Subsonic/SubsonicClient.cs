using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GZJam.Subsonic;

/// <summary>
/// Talks to a Subsonic-compatible server through the Subsonic API (v1.16.1, with what OpenSubsonic
/// adds): searching, playlists, and the address a song's original file downloads from. Anything
/// that speaks it will do (Navidrome, Gonic, Airsonic-Advanced, Ampache, Nextcloud Music…); nothing
/// here is particular to one server. Logs in the way Subsonic does, with a salted hash, so the
/// password itself never goes over the wire. A song's path in GZJam is
/// <c>subsonic://&lt;server&gt;/&lt;id&gt;</c> (<see cref="PathFor(string,string)"/>), the server being
/// the id GZJam gave it, so several servers never mix up their songs; <see cref="DownloadUrl"/>
/// turns a path back into an address.
/// </summary>
public sealed class SubsonicClient : IDisposable
{
    /// <summary>What a server song's path in GZJam starts with.</summary>
    public const string Scheme = "subsonic://";

    private const string ApiVersion = "1.16.1";
    private const string ClientName = "gzjam";

    private readonly Uri _server;
    private readonly string _user;
    private readonly string _password;
    private readonly string? _salt;
    private readonly string? _token;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>The id GZJam gave this server: the part of a song's path between the scheme and the song's id.</summary>
    public string ServerId { get; }

    public SubsonicClient(string serverId, Uri server, string user, string password, HttpClient? http = null)
        : this(serverId, server, user, password, null, null, http)
    {
    }

    /// <summary>
    /// Logs in with a <paramref name="salt"/> and <paramref name="token"/> made earlier by
    /// <see cref="MakeLogin"/>, so the password itself needn't be kept anywhere.
    /// </summary>
    public SubsonicClient(string serverId, Uri server, string user, string salt, string token, HttpClient? http = null)
        : this(serverId, server, user, "", salt, token, http)
    {
    }

    private SubsonicClient(string serverId, Uri server, string user, string password, string? salt, string? token, HttpClient? http)
    {
        ServerId = serverId;
        _server = new Uri(server.ToString().TrimEnd('/') + "/");
        _user = user;
        _password = password;
        _salt = salt;
        _token = token;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// A salt and the Subsonic token for <paramref name="password"/> with it. The server accepts the
    /// same pair for every request, so a login can be kept as this instead of the password: it is
    /// no use for anything but this server's API (it doesn't show the password, and can't open the
    /// server's web page), though anyone who has it can use the API as the user.
    /// </summary>
    public static (string Salt, string Token) MakeLogin(string password)
    {
        string salt = NewSalt();
        return (salt, Token(password, salt));
    }

    private static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static string PathFor(string serverId, string songId) => Scheme + serverId + "/" + songId;

    /// <summary>The path of the song <paramref name="songId"/> on this server.</summary>
    public string PathFor(string songId) => PathFor(ServerId, songId);

    public static bool IsSubsonicPath(string path) => path.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>Splits a song's path into its server's id and the song's id on that server; false if it isn't a server song's path.</summary>
    public static bool TryParsePath(string path, out string serverId, out string songId)
    {
        (serverId, songId) = ("", "");
        if (!IsSubsonicPath(path))
            return false;
        int slash = path.IndexOf('/', Scheme.Length);
        if (slash <= Scheme.Length || slash == path.Length - 1)
            return false;
        (serverId, songId) = (path[Scheme.Length..slash], path[(slash + 1)..]);
        return true;
    }

    /// <summary>
    /// The address to download a song's original file from (the <c>download</c> call: never
    /// converted, and the server can turn it off for a user). It holds the login, so it is never
    /// logged. Null if <paramref name="path"/> isn't a song on this server.
    /// </summary>
    public string? DownloadUrl(string path) =>
        TryParsePath(path, out string server, out string id) && server == ServerId ? Url("download", ("id", id)) : null;

    /// <summary>Asks the server who it is; throws if it can't be reached or the login is wrong.</summary>
    public async Task<string> PingAsync(CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("ping", cancel);
        var response = doc.RootElement;
        string name = Text(response, "type") is { Length: > 0 } type ? type : "Subsonic server";
        return $"{name} {Text(response, "serverVersion") ?? Text(response, "version")}".Trim();
    }

    /// <summary>
    /// Whether this login may download songs (<c>getUser</c>'s <c>downloadRole</c>). The room only
    /// plays files on the laptop, so a server that doesn't allow it is of no use for playing. Null
    /// if the server doesn't say.
    /// </summary>
    public async Task<bool?> CanDownloadAsync(CancellationToken cancel = default)
    {
        try
        {
            using JsonDocument doc = await GetAsync("getUser", cancel, ("username", _user)).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("user", out JsonElement user) && user.TryGetProperty("downloadRole", out JsonElement role)
                && role.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return role.GetBoolean();
        }
        catch (SubsonicException)
        {
            // A server that won't say (or a login that can't ask) is not a refusal.
        }
        return null;
    }

    /// <summary>Songs matching <paramref name="query"/> (title, artist or album), best first.</summary>
    public async Task<IReadOnlyList<SubsonicSong>> SearchAsync(string query, int count = 50, int offset = 0, CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("search3", cancel, ("query", query), ("songCount", count.ToString()),
            ("songOffset", offset.ToString()), ("artistCount", "0"), ("albumCount", "0"));
        return ReadSongs(doc.RootElement, "searchResult3");
    }

    /// <summary>
    /// Every song on the server, a page at a time. Most servers list them all for an empty search;
    /// one that answers nothing to it is read album by album instead, which takes more requests.
    /// <paramref name="progress"/> gets the number of songs so far after each page.
    /// </summary>
    public async Task<IReadOnlyList<SubsonicSong>> AllSongsAsync(IProgress<int>? progress = null, int pageSize = 500, CancellationToken cancel = default)
    {
        var all = new List<SubsonicSong>();
        while (true)
        {
            IReadOnlyList<SubsonicSong> page = await SearchAsync("", pageSize, all.Count, cancel).ConfigureAwait(false);
            all.AddRange(page);
            progress?.Report(all.Count);
            if (page.Count < pageSize)
                break;
        }
        return all.Count > 0 ? all : await AllSongsByAlbumAsync(progress, pageSize, cancel).ConfigureAwait(false);
    }

    /// <summary>Every song, found through the albums (<c>getAlbumList2</c>, then <c>getAlbum</c> for each).</summary>
    private async Task<IReadOnlyList<SubsonicSong>> AllSongsByAlbumAsync(IProgress<int>? progress, int pageSize, CancellationToken cancel)
    {
        var all = new List<SubsonicSong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; ; offset += pageSize)
        {
            using JsonDocument page = await GetAsync("getAlbumList2", cancel, ("type", "alphabeticalByName"),
                ("size", pageSize.ToString()), ("offset", offset.ToString())).ConfigureAwait(false);
            if (!page.RootElement.TryGetProperty("albumList2", out JsonElement list) || !list.TryGetProperty("album", out JsonElement albums)
                || albums.ValueKind != JsonValueKind.Array)
                return all;
            int count = 0;
            foreach (JsonElement album in albums.EnumerateArray())
            {
                count++;
                if (Text(album, "id") is not { Length: > 0 } id)
                    continue;
                using JsonDocument detail = await GetAsync("getAlbum", cancel, ("id", id)).ConfigureAwait(false);
                foreach (SubsonicSong song in ReadSongs(detail.RootElement, "album").Where(s => seen.Add(s.Id)))
                    all.Add(song);
                progress?.Report(all.Count);
            }
            if (count < pageSize)
                return all;
        }
    }

    /// <summary>Every playlist the server shows this login (its own, and public ones).</summary>
    public async Task<IReadOnlyList<SubsonicPlaylist>> PlaylistsAsync(CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("getPlaylists", cancel).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("playlists", out JsonElement container) || !container.TryGetProperty("playlist", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
            return [];
        var playlists = new List<SubsonicPlaylist>();
        foreach (JsonElement playlist in list.EnumerateArray())
        {
            if (Text(playlist, "id") is { Length: > 0 } id)
                playlists.Add(new SubsonicPlaylist(id, Text(playlist, "name") ?? id, Whole(playlist, "songCount") ?? 0));
        }
        return playlists;
    }

    /// <summary>The songs of a playlist, in its order (a song listed twice comes twice).</summary>
    public async Task<IReadOnlyList<SubsonicSong>> PlaylistSongsAsync(string playlistId, CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("getPlaylist", cancel, ("id", playlistId)).ConfigureAwait(false);
        return ReadSongs(doc.RootElement, "playlist", "entry");
    }

    /// <summary>The songs in <paramref name="response"/>'s <paramref name="container"/> object, under <paramref name="array"/>, if it has any.</summary>
    internal static IReadOnlyList<SubsonicSong> ReadSongs(JsonElement response, string container, string array = "song")
    {
        if (!response.TryGetProperty(container, out JsonElement result) || !result.TryGetProperty(array, out JsonElement songs)
            || songs.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<SubsonicSong>();
        foreach (JsonElement song in songs.EnumerateArray())
        {
            if (Text(song, "id") is not { Length: > 0 } id)
                continue;
            list.Add(new SubsonicSong(
                id, Text(song, "title") ?? id, Text(song, "artist"), Text(song, "album"), Text(song, "albumArtist"),
                Text(song, "genre"), Whole(song, "year"), Whole(song, "track"),
                TimeSpan.FromSeconds(Number(song, "duration") ?? 0), Number(song, "bpm") is > 0 and var bpm ? bpm : null,
                Text(song, "suffix"), (long)(Number(song, "size") ?? 0), Text(song, "coverArt"), Text(song, "comment")));
        }
        return list;
    }

    /// <summary>A Subsonic API call; the JSON inside <c>subsonic-response</c>. Throws if the server says it failed.</summary>
    private async Task<JsonDocument> GetAsync(string method, CancellationToken cancel, params (string Key, string Value)[] parameters)
    {
        string url = Url(method, [.. parameters, ("f", "json")]);
        string json;
        try
        {
            json = await _http.GetStringAsync(url, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The message of an HttpRequestException can hold the address, which holds the login.
            throw new SubsonicException($"Couldn't reach the server ({ex.StatusCode?.ToString() ?? ex.HttpRequestError.ToString()})");
        }
        JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("subsonic-response", out JsonElement response))
        {
            doc.Dispose();
            throw new SubsonicException("That address didn't answer like a Subsonic server");
        }
        if (Text(response, "status") != "ok")
        {
            string why = response.TryGetProperty("error", out JsonElement error) ? Text(error, "message") ?? "unknown error" : "unknown error";
            doc.Dispose();
            throw new SubsonicException($"The server said: {why}");
        }
        // Hand back the response object itself.
        var copy = JsonDocument.Parse(response.GetRawText());
        doc.Dispose();
        return copy;
    }

    private string Url(string method, params (string Key, string Value)[] parameters)
    {
        string salt = _salt ?? NewSalt();
        string token = _token ?? Token(_password, salt);
        var query = new StringBuilder($"u={Uri.EscapeDataString(_user)}&t={token}&s={salt}&v={ApiVersion}&c={ClientName}");
        foreach (var (key, value) in parameters)
            query.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        return new Uri(_server, "rest/" + method) + "?" + query;
    }

    /// <summary>Subsonic's login token: md5 of the password followed by the salt, in lower-case hex.</summary>
    internal static string Token(string password, string salt) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))).ToLowerInvariant();

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int? Whole(JsonElement element, string name) => Number(element, name) is { } n ? (int)n : null;

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

/// <summary>The server couldn't be reached, or refused the request. The message is safe to show: it never holds the login.</summary>
public sealed class SubsonicException(string message) : Exception(message);
