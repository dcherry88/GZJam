using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GZJam.Navidrome;

/// <summary>
/// Talks to a Navidrome server through its Subsonic API (v1.16.1): searching, and the address a
/// song streams from. Logs in the way Subsonic does, with a salted hash, so the password itself
/// never goes over the wire. A song's <see cref="PathFor">path</see> in GZJam is
/// <c>navidrome://&lt;id&gt;</c>; <see cref="StreamUrl"/> turns it back into an address.
/// </summary>
public sealed class NavidromeClient : IDisposable
{
    /// <summary>What a Navidrome song's path in GZJam starts with.</summary>
    public const string Scheme = "navidrome://";

    private const string ApiVersion = "1.16.1";
    private const string ClientName = "gzjam";

    private readonly Uri _server;
    private readonly string _user;
    private readonly string _password;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public NavidromeClient(Uri server, string user, string password, HttpClient? http = null)
    {
        _server = new Uri(server.ToString().TrimEnd('/') + "/");
        _user = user;
        _password = password;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public static string PathFor(string songId) => Scheme + songId;

    public static bool IsNavidromePath(string path) => path.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>
    /// The address to stream a song's original file from (<c>format=raw</c>: no transcoding, and it
    /// can be asked for any part of the file). It holds the login, so it is never logged. Null if
    /// <paramref name="path"/> isn't a Navidrome song.
    /// </summary>
    public string? StreamUrl(string path) => IsNavidromePath(path) ? Url("stream", ("id", path[Scheme.Length..]), ("format", "raw")) : null;

    /// <summary>Asks the server who it is; throws if it can't be reached or the login is wrong.</summary>
    public async Task<string> PingAsync(CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("ping", cancel);
        var response = doc.RootElement;
        return $"{Text(response, "type") ?? "server"} {Text(response, "serverVersion") ?? Text(response, "version")}";
    }

    /// <summary>Songs matching <paramref name="query"/> (title, artist or album), best first.</summary>
    public async Task<IReadOnlyList<NavidromeSong>> SearchAsync(string query, int count = 50, int offset = 0, CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("search3", cancel, ("query", query), ("songCount", count.ToString()),
            ("songOffset", offset.ToString()), ("artistCount", "0"), ("albumCount", "0"));
        return ReadSongs(doc.RootElement, "searchResult3");
    }

    /// <summary>
    /// Every song on the server, a page at a time (Navidrome lists them all for an empty search).
    /// <paramref name="progress"/> gets the number of songs so far after each page.
    /// </summary>
    public async Task<IReadOnlyList<NavidromeSong>> AllSongsAsync(IProgress<int>? progress = null, int pageSize = 500, CancellationToken cancel = default)
    {
        var all = new List<NavidromeSong>();
        while (true)
        {
            IReadOnlyList<NavidromeSong> page = await SearchAsync("", pageSize, all.Count, cancel).ConfigureAwait(false);
            all.AddRange(page);
            progress?.Report(all.Count);
            if (page.Count < pageSize)
                return all;
        }
    }

    /// <summary>Every playlist the server shows this login (its own, and public ones).</summary>
    public async Task<IReadOnlyList<NavidromePlaylist>> PlaylistsAsync(CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("getPlaylists", cancel).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("playlists", out JsonElement container) || !container.TryGetProperty("playlist", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
            return [];
        var playlists = new List<NavidromePlaylist>();
        foreach (JsonElement playlist in list.EnumerateArray())
        {
            if (Text(playlist, "id") is { Length: > 0 } id)
                playlists.Add(new NavidromePlaylist(id, Text(playlist, "name") ?? id, Whole(playlist, "songCount") ?? 0));
        }
        return playlists;
    }

    /// <summary>The songs of a playlist, in its order (a song listed twice comes twice).</summary>
    public async Task<IReadOnlyList<NavidromeSong>> PlaylistSongsAsync(string playlistId, CancellationToken cancel = default)
    {
        using JsonDocument doc = await GetAsync("getPlaylist", cancel, ("id", playlistId)).ConfigureAwait(false);
        return ReadSongs(doc.RootElement, "playlist", "entry");
    }

    /// <summary>The songs in <paramref name="response"/>'s <paramref name="container"/> object, under <paramref name="array"/>, if it has any.</summary>
    internal static IReadOnlyList<NavidromeSong> ReadSongs(JsonElement response, string container, string array = "song")
    {
        if (!response.TryGetProperty(container, out JsonElement result) || !result.TryGetProperty(array, out JsonElement songs)
            || songs.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<NavidromeSong>();
        foreach (JsonElement song in songs.EnumerateArray())
        {
            if (Text(song, "id") is not { Length: > 0 } id)
                continue;
            list.Add(new NavidromeSong(
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
            throw new NavidromeException($"Couldn't reach the Navidrome server ({ex.StatusCode?.ToString() ?? ex.HttpRequestError.ToString()})");
        }
        JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("subsonic-response", out JsonElement response))
        {
            doc.Dispose();
            throw new NavidromeException("That address didn't answer like a Navidrome server");
        }
        if (Text(response, "status") != "ok")
        {
            string why = response.TryGetProperty("error", out JsonElement error) ? Text(error, "message") ?? "unknown error" : "unknown error";
            doc.Dispose();
            throw new NavidromeException($"Navidrome said: {why}");
        }
        // Hand back the response object itself.
        var copy = JsonDocument.Parse(response.GetRawText());
        doc.Dispose();
        return copy;
    }

    private string Url(string method, params (string Key, string Value)[] parameters)
    {
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string token = Token(_password, salt);
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
public sealed class NavidromeException(string message) : Exception(message);
