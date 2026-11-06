using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using GZJam.Audio;
using GZJam.Library;
using GZJam.Testing;

namespace GZJam.App.Tests;

/// <summary>
/// Caching songs from a server for offline play (a playlist, a folder, some songs), through the
/// download queue and its Downloads list, with a stand-in server.
/// </summary>
[Collection("Server globals")]   // sets RemoteSongs.Resolver, which Settings tests change too
public sealed class CachePlaylistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gzjam-cache-playlist-").FullName;
    private readonly HttpListener _server = new();
    private const string Prefix = "subsonic://test0001/";
    private readonly byte[] _tone;
    private readonly string _base;
    private int _requests;

    public CachePlaylistTests()
    {
        _tone = File.ReadAllBytes(TestWav.WriteTone(Path.Combine(_dir, "tone.wav"), TimeSpan.FromSeconds(3)));
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _base = $"http://127.0.0.1:{port}/";
        _server.Prefixes.Add(_base);
        _server.Start();
        _ = Task.Run(Serve);
        RemoteSongs.CacheDirectory = Path.Combine(_dir, "cache");
        RemoteSongs.Resolver = path => path.StartsWith(Prefix, StringComparison.Ordinal) ? _base + path[Prefix.Length..] : null;
    }

    public void Dispose()
    {
        RemoteSongs.StopAll();
        RemoteSongs.Resolver = null;
        RemoteSongs.CacheDirectory = null;
        RemoteSongs.AutoFetch = RemoteFetchMode.Never;
        _server.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    /// <summary>Answers each request with a short tone; "missing" is a 404, and "slow" takes its time (to be stopped).</summary>
    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try { context = await _server.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            Interlocked.Increment(ref _requests);
            _ = Task.Run(async () =>
            {
                try
                {
                    string name = context.Request.Url!.AbsolutePath;
                    if (name.EndsWith("slow", StringComparison.Ordinal))
                        await Task.Delay(TimeSpan.FromSeconds(4));
                    if (name.EndsWith("missing", StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                    context.Response.ContentType = "audio/wav";
                    context.Response.ContentLength64 = _tone.Length;
                    await context.Response.OutputStream.WriteAsync(_tone);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                }
            });
        }
    }

    private static Track Song(string id) => new()
    {
        Path = Prefix + id, Folder = "Band/Album", FileName = id + ".wav", Title = id, Artist = "Band", Album = "Album", Duration = TimeSpan.FromSeconds(3),
    };

    private static PlaylistInfo PlaylistOf(AppHarness app, params string[] ids)
    {
        var db = app.Database;
        db.SyncRemoteTracks(db.AddRemoteRoot("subsonic://test0001"), [.. ids.Distinct().Select(Song)]);
        db.SyncRemotePlaylists("test0001", "Test", [new RemotePlaylist("p1", "Friday", [.. ids.Select(i => Prefix + i)])]);
        app.Library.RefreshPlaylists();
        return db.GetPlaylists().Single();
    }

    private static void WaitUntilCached(params string[] ids) =>
        AppHarness.WaitFor(() => ids.All(i => RemoteSongs.IsCached(Prefix + i)), TimeSpan.FromSeconds(20));

    /// <summary>Loads a song on a deck, letting the window's thread work while it waits for the download.</summary>
    private static bool Load(AppHarness app, ViewModels.DeckViewModel deck, Track song)
    {
        var task = app.ViewModel.LoadToDeckAsync(deck, song);
        AppHarness.Await(task);
        return task.Result;
    }
    [AvaloniaFact]
    public void Nothing_is_fetched_by_itself_unless_Settings_say_so()
    {
        using var app = new AppHarness();
        Assert.Equal(RemoteFetchMode.Never, app.Settings.RemoteFetch);   // off to start with
        Assert.Equal(RemoteFetchMode.Never, app.ViewModel.Settings.Servers.FetchChoice.Value);
        Track song = Song("a");
        app.Database.SyncRemoteTracks(app.Database.AddRemoteRoot("subsonic://test0001"), [song]);

        // Queued: refused, with the way to get it, and nothing is downloaded.
        app.ViewModel.AddToQueue([song]);
        AppHarness.Settle(300);
        Assert.Empty(app.ViewModel.Queue.Items);
        Assert.Contains("not on this computer yet", app.ViewModel.Notice);
        Assert.Equal(0, _requests);

        // Put on a deck: the same.
        var deck = app.ViewModel.Deck1!;
        Assert.False(Load(app, deck, song));
        Assert.Contains("isn't on this computer yet", app.ViewModel.Notice);
        Assert.Equal(0, _requests);
        Assert.False(deck.HasTrack);

        // Asked for by hand (the playlist's menu), the song is fetched, and then it queues and loads.
        app.ViewModel.CacheForOffline([song], "songs");
        WaitUntilCached("a");
        app.ViewModel.AddToQueue([song]);
        Assert.Single(app.ViewModel.Queue.Items);
        Assert.True(Load(app, deck, song));
        Assert.True(deck.HasTrack);
    }

    [AvaloniaFact]
    public void With_temporary_fetching_a_song_is_downloaded_to_play_and_removed_when_GZJam_starts_again()
    {
        using var app = new AppHarness();
        app.ViewModel.Settings.Servers.FetchChoice = app.ViewModel.Settings.Servers.FetchChoices.Single(c => c.Value == RemoteFetchMode.Temporary);
        Assert.Equal(RemoteFetchMode.Temporary, app.Settings.RemoteFetch);   // kept with the settings
        Track queued = Song("q"), loaded = Song("l");
        app.Database.SyncRemoteTracks(app.Database.AddRemoteRoot("subsonic://test0001"), [queued, loaded]);

        app.ViewModel.AddToQueue([queued]);   // fetched as it is queued
        WaitUntilCached("q");
        var deck = app.ViewModel.Deck1!;
        Assert.True(Load(app, deck, loaded));   // and as it is loaded
        Assert.True(RemoteSongs.IsCached(loaded.Path));
        Assert.True(deck.HasTrack);

        // Both are in the temporary folder, not among the kept songs.
        string cache = RemoteSongs.CacheDirectory!;
        Assert.Empty(Directory.EnumerateFiles(cache));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(cache, "temporary")).Count());

        // The next start removes them (a song a deck still has open stays until then).
        app.ViewModel.Decks.ToList().ForEach(d => d.EjectCommand.Execute(null));
        RemoteSongs.ClearTemporary();
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(cache, "temporary")));
        Assert.False(RemoteSongs.IsCached(queued.Path));
    }

    [AvaloniaFact]
    public void A_song_fetched_only_to_play_is_kept_when_the_DJ_asks_for_it()
    {
        using var app = new AppHarness();
        app.ViewModel.Settings.Servers.FetchChoice = app.ViewModel.Settings.Servers.FetchChoices.Single(c => c.Value == RemoteFetchMode.Temporary);
        Track song = Song("k");
        app.Database.SyncRemoteTracks(app.Database.AddRemoteRoot("subsonic://test0001"), [song]);
        app.ViewModel.AddToQueue([song]);
        WaitUntilCached("k");
        string cache = RemoteSongs.CacheDirectory!;
        Assert.Single(Directory.EnumerateFiles(Path.Combine(cache, "temporary")));
        int fetched = _requests;

        app.ViewModel.CacheForOffline([song], "songs");   // asked for by name
        AppHarness.WaitFor(() => Directory.EnumerateFiles(cache).Any(), TimeSpan.FromSeconds(10));

        Assert.Single(Directory.EnumerateFiles(cache));   // moved, not downloaded again
        Assert.Equal(fetched, _requests);
        RemoteSongs.ClearTemporary();
        Assert.True(RemoteSongs.IsCached(song.Path));
    }

    [AvaloniaFact]
    public void With_keeping_a_song_is_downloaded_and_stays()
    {
        using var app = new AppHarness();
        app.ViewModel.Settings.Servers.FetchChoice = app.ViewModel.Settings.Servers.FetchChoices.Single(c => c.Value == RemoteFetchMode.Keep);
        Track song = Song("s");
        app.Database.SyncRemoteTracks(app.Database.AddRemoteRoot("subsonic://test0001"), [song]);

        app.ViewModel.AddToQueue([song]);
        WaitUntilCached("s");

        Assert.Single(Directory.EnumerateFiles(RemoteSongs.CacheDirectory!));
        RemoteSongs.ClearTemporary();
        Assert.True(RemoteSongs.IsCached(song.Path));
    }
    [AvaloniaFact]
    public void A_playlist_from_the_server_is_marked_and_a_menu_can_say_how_many_songs_are_not_here_yet()
    {
        using var app = new AppHarness();
        var playlist = PlaylistOf(app, "a", "b", "c");

        Assert.True(playlist.IsRemote);
        Assert.Contains("From a music server", playlist.Tip);
        Assert.Equal(3, app.ViewModel.NotHere(app.Database.Search(null, playlist: playlist.Id)));
    }

    [AvaloniaFact]
    public void Caching_a_playlist_queues_each_song_once_and_the_downloads_list_shows_them_arrive()
    {
        using var app = new AppHarness();
        var playlist = PlaylistOf(app, "a", "b", "c", "a");   // a song twice is fetched once

        app.ViewModel.CachePlaylist(playlist);

        Assert.Contains("\"Friday\": 3 songs added to the downloads", app.ViewModel.Notice);
        WaitUntilCached("a", "b", "c");
        AppHarness.WaitFor(() => app.ViewModel.Downloads.Done == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(3, _requests);
        Assert.Equal(["a", "b", "c"], app.ViewModel.Downloads.Items.Select(i => i.Title).Order());
        Assert.All(app.ViewModel.Downloads.Items, i => Assert.Equal(RemoteTransferState.Done, i.State));
        Assert.Null(app.ViewModel.Downloads.Status);   // nothing left: the top bar goes quiet
        Assert.Equal(0, app.ViewModel.NotHere(app.Database.Search(null, playlist: playlist.Id)));
    }

    [AvaloniaFact]
    public void The_cached_mark_follows_a_song_arriving()
    {
        using var app = new AppHarness();
        var playlist = PlaylistOf(app, "a");
        int before = OfflineIndex.Instance.Version;
        Assert.False(RemoteSongs.IsCached(Prefix + "a"));

        app.ViewModel.CachePlaylist(playlist);
        WaitUntilCached("a");
        AppHarness.WaitFor(() => OfflineIndex.Instance.Version > before, TimeSpan.FromSeconds(10));

        Assert.True(RemoteSongs.IsCached(Prefix + "a"));
    }

    [AvaloniaFact]
    public void Caching_again_has_nothing_to_fetch()
    {
        using var app = new AppHarness();
        var playlist = PlaylistOf(app, "a", "b");
        app.ViewModel.CachePlaylist(playlist);
        WaitUntilCached("a", "b");
        int before = _requests;

        app.ViewModel.CachePlaylist(playlist);

        Assert.Equal(before, _requests);
        Assert.Contains("all 2 songs are on this computer already", app.ViewModel.Notice);
    }

    [AvaloniaFact]
    public void A_song_the_server_will_not_send_is_listed_as_failed_and_the_others_still_come()
    {
        using var app = new AppHarness();
        var playlist = PlaylistOf(app, "a", "missing", "c");

        app.ViewModel.CachePlaylist(playlist);

        WaitUntilCached("a", "c");
        AppHarness.WaitFor(() => app.ViewModel.Downloads is { Done: 2, Failed: 1 }, TimeSpan.FromSeconds(10));
        var failed = Assert.Single(app.ViewModel.Downloads.Items, i => i.State == RemoteTransferState.Failed);
        Assert.Equal("missing", failed.Title);
        Assert.StartsWith("Failed:", failed.StateText);
        Assert.True(app.ViewModel.Downloads.HasFailed);
    }

    [AvaloniaFact]
    public void Stop_all_empties_the_queue_and_stops_what_it_started()
    {
        using var app = new AppHarness();
        var playlist = PlaylistOf(app, "s1slow", "s2slow", "s3slow", "s4slow", "s5slow");
        app.ViewModel.CachePlaylist(playlist);
        AppHarness.WaitFor(() => app.ViewModel.Downloads.Waiting + app.ViewModel.Downloads.Active == 5, TimeSpan.FromSeconds(10));
        AppHarness.WaitFor(() => _requests >= 2, TimeSpan.FromSeconds(10));   // two are on their way, the rest wait
        Assert.True(app.ViewModel.Downloads.IsBusy);
        Assert.NotNull(app.ViewModel.Downloads.Status);

        app.ViewModel.Downloads.StopAllCommand.Execute(null);

        AppHarness.WaitFor(() => !app.ViewModel.Downloads.IsBusy, TimeSpan.FromSeconds(10));
        Assert.All(app.ViewModel.Downloads.Items, i => Assert.Equal(RemoteTransferState.Cancelled, i.State));
        Assert.Equal(5, app.ViewModel.Downloads.Items.Count);
        Assert.Null(app.ViewModel.Downloads.Status);
        Assert.False(RemoteSongs.IsCached(Prefix + "s1slow"));

        app.ViewModel.Downloads.ClearFinishedCommand.Execute(null);
        Assert.Empty(app.ViewModel.Downloads.Items);
    }

    [AvaloniaFact]
    public void A_folder_of_the_server_can_be_cached()
    {
        using var app = new AppHarness();
        PlaylistOf(app, "a", "b");
        var root = app.Database.AddRemoteRoot("subsonic://test0001");

        app.ViewModel.CacheFolder(root.Id, "Band/Album", "Album");

        WaitUntilCached("a", "b");
        Assert.Equal(2, _requests);
    }

    [AvaloniaFact]
    public void Selected_songs_can_be_cached_and_files_on_this_computer_are_skipped()
    {
        using var app = new AppHarness();
        PlaylistOf(app, "a", "b");
        var mine = app.Database.Search(null).First(t => !t.Path.Contains("://"));
        var theirs = app.Database.Search(null).Where(t => t.Path.Contains("://")).ToList();

        app.ViewModel.CacheForOffline([mine, .. theirs], "The selected songs");

        WaitUntilCached("a", "b");
        Assert.Equal(2, _requests);
        Assert.Equal(0, app.ViewModel.NotHere([mine, .. theirs]));
    }

    [AvaloniaFact]
    public void A_playlist_of_files_on_this_computer_has_nothing_to_cache()
    {
        using var app = new AppHarness();
        var mine = app.Database.CreatePlaylist("Mine");
        app.Database.AddToPlaylist(mine.Id, [Path.Combine(app.MusicPath, "x.mp3")]);

        app.ViewModel.CachePlaylist(mine);

        Assert.Equal("\"Mine\": nothing from the server to fetch", app.ViewModel.Notice);
        Assert.False(mine.IsRemote);
        Assert.Equal(0, _requests);
    }
}
