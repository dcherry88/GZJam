using System.Net;
using System.Web;

namespace GZJam.Subsonic.Tests;

public sealed class SubsonicClientTests
{
    private const string SearchAnswer = """
        {"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","serverVersion":"0.64.2",
         "searchResult3":{"song":[
           {"id":"abc","title":"One O'Clock Jump","artist":"Count Basie","album":"Decca","albumArtist":"Count Basie",
            "year":1937,"track":3,"duration":186,"bpm":172,"suffix":"flac","size":23185426,"genre":"Swing"},
           {"title":"No id, so it is left out"},
           {"id":"def","title":"Plain","duration":61,"bpm":0}]}}}
        """;

    /// <summary>A server that answers every request with <paramref name="body"/>, and remembers the last address asked for.</summary>
    private sealed class FakeServer(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static SubsonicClient Client(HttpMessageHandler server) =>
        new("abc12345", new Uri("https://music.example.com/"), "danny", "s3cret pass", new HttpClient(server));

    [Fact]
    public void The_login_token_is_the_hash_of_the_password_and_salt_as_Subsonic_defines_it()
    {
        // From the Subsonic API documentation: password "sesame", salt "c19b2d".
        Assert.Equal("26719a1196d2a940705a59634eb18eab", SubsonicClient.Token("sesame", "c19b2d"));
    }

    [Fact]
    public void A_salt_and_token_made_earlier_log_in_without_the_password()
    {
        var (salt, token) = SubsonicClient.MakeLogin("s3cret pass");
        Assert.Equal(SubsonicClient.Token("s3cret pass", salt), token);

        var server = new FakeServer("");
        using var client = new SubsonicClient("abc12345", new Uri("https://music.example.com/"), "danny", salt, token, new HttpClient(server));
        var one = HttpUtility.ParseQueryString(new Uri(client.DownloadUrl(client.PathFor("abc"))!).Query);
        var two = HttpUtility.ParseQueryString(new Uri(client.DownloadUrl(client.PathFor("def"))!).Query);

        Assert.Equal((salt, token), (one["s"], one["t"]));
        Assert.Equal((salt, token), (two["s"], two["t"]));   // the same pair every time, which the server accepts
        Assert.DoesNotContain("s3cret", client.DownloadUrl(client.PathFor("abc")));
    }

    [Fact]
    public void A_songs_download_address_asks_for_the_original_file_and_never_holds_the_password()
    {
        var client = Client(new FakeServer(""));
        string url = client.DownloadUrl(client.PathFor("abc"))!;
        var query = HttpUtility.ParseQueryString(new Uri(url).Query);

        Assert.StartsWith("https://music.example.com/rest/download?", url);   // the Subsonic call for the file as it is, never converted
        Assert.Equal("abc", query["id"]);
        Assert.Equal("danny", query["u"]);
        Assert.Equal(SubsonicClient.Token("s3cret pass", query["s"]!), query["t"]);
        Assert.DoesNotContain("s3cret", url);
    }

    [Fact]
    public void A_path_names_the_server_and_the_song_and_only_that_servers_client_makes_an_address_for_it()
    {
        var client = Client(new FakeServer(""));
        Assert.Equal("subsonic://abc12345/song-7", client.PathFor("song-7"));
        Assert.True(SubsonicClient.TryParsePath("subsonic://abc12345/song-7", out string server, out string song));
        Assert.Equal(("abc12345", "song-7"), (server, song));
        Assert.True(SubsonicClient.TryParsePath("subsonic://abc12345/a/b", out _, out string deep));
        Assert.Equal("a/b", deep);   // an id may hold a slash

        Assert.NotNull(client.DownloadUrl("subsonic://abc12345/song-7"));
        Assert.Null(client.DownloadUrl("subsonic://other999/song-7"));   // another server's song
        Assert.Null(client.DownloadUrl(@"C:\music\song.mp3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"C:\music\song.mp3")]
    [InlineData("subsonic://")]
    [InlineData("subsonic://abc12345")]
    [InlineData("subsonic://abc12345/")]
    [InlineData("subsonic:///song")]
    [InlineData("navidrome://song")]
    public void Anything_else_is_not_a_servers_song_path(string path) =>
        Assert.False(SubsonicClient.TryParsePath(path, out _, out _));
    [Fact]
    public async Task Search_reads_the_songs_the_server_describes()
    {
        var server = new FakeServer(SearchAnswer);
        var songs = await Client(server).SearchAsync("basie", cancel: TestContext.Current.CancellationToken);

        Assert.Equal(2, songs.Count);
        var first = songs[0];
        Assert.Equal(("abc", "One O'Clock Jump", "Count Basie"), (first.Id, first.Title, first.Artist));
        Assert.Equal((1937, 3, "flac", 23185426L), (first.Year, first.TrackNumber, first.Suffix, first.Size));
        Assert.Equal(TimeSpan.FromSeconds(186), first.Duration);
        Assert.Equal(172, first.Bpm);
        Assert.Null(songs[1].Bpm);   // 0 means the server doesn't know
        Assert.Equal("basie", HttpUtility.ParseQueryString(server.Asked!.Query)["query"]);
    }

    [Fact]
    public async Task A_refusal_from_the_server_says_why()
    {
        var server = new FakeServer("""{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password"}}}""");

        var ex = await Assert.ThrowsAsync<SubsonicException>(() => Client(server).PingAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Wrong username or password", ex.Message);
    }

    [Fact]
    public async Task An_error_never_repeats_the_address_with_its_login()
    {
        var server = new FakeServer("", HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<SubsonicException>(() => Client(server).SearchAsync("basie", cancel: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("music.example.com", ex.Message);
        Assert.DoesNotContain("danny", ex.Message);
    }

    [Fact]
    public async Task Something_that_is_not_a_Subsonic_server_is_said_so()
    {
        await Assert.ThrowsAsync<SubsonicException>(() => Client(new FakeServer("""{"hello":"world"}""")).PingAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A server that pages through <paramref name="total"/> songs, as Navidrome does for an empty search.</summary>
    private sealed class PagedServer(int total) : HttpMessageHandler
    {
        public List<(int Count, int Offset)> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (!request.RequestUri.AbsolutePath.EndsWith("search3", StringComparison.Ordinal))   // no albums to read them by either
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"albumList2\":{}}}") });
            int count = int.Parse(query["songCount"]!), offset = int.Parse(query["songOffset"]!);
            Asked.Add((count, offset));
            var songs = Enumerable.Range(offset, Math.Max(0, Math.Min(count, total - offset)))
                .Select(i => "{\"id\":\"s" + i + "\",\"title\":\"Song " + i + "\"}");
            string body = "{\"subsonic-response\":{\"status\":\"ok\",\"searchResult3\":{\"song\":[" + string.Join(",", songs) + "]}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Theory]
    [InlineData(0, 1)]   // an empty server: one question
    [InlineData(5, 3)]   // 2 + 2 + 1
    [InlineData(4, 3)]   // 2 + 2 + an empty page that says it's the end
    public async Task All_songs_are_read_a_page_at_a_time_until_the_server_runs_out(int total, int questions)
    {
        var server = new PagedServer(total);
        var progress = new List<int>();

        var songs = await Client(server).AllSongsAsync(new Progress<int>(progress.Add), pageSize: 2, cancel: TestContext.Current.CancellationToken);

        Assert.Equal(total, songs.Count);
        Assert.Equal(Enumerable.Range(0, total).Select(i => $"s{i}"), songs.Select(s => s.Id));
        Assert.Equal(questions, server.Asked.Count);
        Assert.Equal(Enumerable.Range(0, questions).Select(i => i * 2), server.Asked.Select(a => a.Offset));
    }
    /// <summary>A server whose empty search finds nothing, but which lists albums and their songs.</summary>
    private sealed class AlbumsOnlyServer : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            string method = request.RequestUri.AbsolutePath[(request.RequestUri.AbsolutePath.LastIndexOf('/') + 1)..];
            Asked.Add(method);
            string body = method switch
            {
                "search3" => """{"subsonic-response":{"status":"ok","searchResult3":{}}}""",
                "getAlbumList2" => query["offset"] == "0"
                    ? """{"subsonic-response":{"status":"ok","albumList2":{"album":[{"id":"al1"},{"id":"al2"}]}}}"""
                    : """{"subsonic-response":{"status":"ok","albumList2":{"album":[]}}}""",
                "getAlbum" => query["id"] == "al1"
                    ? """{"subsonic-response":{"status":"ok","album":{"id":"al1","song":[{"id":"a","title":"A"},{"id":"b","title":"B"}]}}}"""
                    : """{"subsonic-response":{"status":"ok","album":{"id":"al2","song":[{"id":"b","title":"B again"},{"id":"c","title":"C"}]}}}""",
                _ => """{"subsonic-response":{"status":"failed","error":{"code":0,"message":"?"}}}""",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task A_server_that_finds_nothing_for_an_empty_search_is_read_album_by_album()
    {
        var server = new AlbumsOnlyServer();

        var songs = await Client(server).AllSongsAsync(pageSize: 2, cancel: TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c"], songs.Select(s => s.Id));   // a song on two albums is one song
        Assert.Equal(["search3", "getAlbumList2", "getAlbum", "getAlbum", "getAlbumList2"], server.Asked);
    }

    [Theory]
    [InlineData("""{"subsonic-response":{"status":"ok","user":{"username":"danny","downloadRole":true}}}""", true)]
    [InlineData("""{"subsonic-response":{"status":"ok","user":{"username":"danny","downloadRole":false}}}""", false)]
    [InlineData("""{"subsonic-response":{"status":"ok","user":{"username":"danny"}}}""", null)]
    [InlineData("""{"subsonic-response":{"status":"failed","error":{"code":50,"message":"not allowed"}}}""", null)]
    public async Task Whether_this_login_may_download_is_read_from_its_user(string answer, bool? expected)
    {
        Assert.Equal(expected, await Client(new FakeServer(answer)).CanDownloadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Playlists_and_their_songs_are_read_in_order()
    {
        var lists = await Client(new FakeServer("{\"subsonic-response\":{\"status\":\"ok\",\"playlists\":{\"playlist\":[{\"id\":\"p1\",\"name\":\"Friday Lindy\",\"songCount\":3},{\"name\":\"no id\"}]}}}"))
            .PlaylistsAsync(TestContext.Current.CancellationToken);
        var songs = await Client(new FakeServer("{\"subsonic-response\":{\"status\":\"ok\",\"playlist\":{\"id\":\"p1\",\"entry\":[{\"id\":\"b\",\"title\":\"B\"},{\"id\":\"a\",\"title\":\"A\"},{\"id\":\"b\",\"title\":\"B\"}]}}}"))
            .PlaylistSongsAsync("p1", TestContext.Current.CancellationToken);

        Assert.Equal([new SubsonicPlaylist("p1", "Friday Lindy", 3)], lists);
        Assert.Equal(["b", "a", "b"], songs.Select(s => s.Id));
    }

    [Fact]
    public async Task A_server_with_no_playlists_gives_none()
    {
        var lists = await Client(new FakeServer("{\"subsonic-response\":{\"status\":\"ok\",\"playlists\":{}}}")).PlaylistsAsync(TestContext.Current.CancellationToken);
        var songs = await Client(new FakeServer("{\"subsonic-response\":{\"status\":\"ok\",\"playlist\":{\"id\":\"p1\"}}}")).PlaylistSongsAsync("p1", TestContext.Current.CancellationToken);

        Assert.Empty(lists);
        Assert.Empty(songs);
    }}
