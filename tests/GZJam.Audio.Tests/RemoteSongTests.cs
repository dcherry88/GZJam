using System.Net;
using System.Net.Sockets;
using GZJam.Testing;

namespace GZJam.Audio.Tests;

/// <summary>
/// Songs that live on a server (a Navidrome library): a local web server stands in for it, serving
/// a tone with byte ranges as Navidrome does, and the real decks play it.
/// </summary>
[Collection("BASS")]   // BASS is process-global; keep these tests sequential
public sealed class RemoteSongTests : IDisposable
{
    private const string Song = "remote://tone";

    private readonly string _dir = Directory.CreateTempSubdirectory("gzjam-remote-").FullName;
    private readonly AudioEngine _engine = new();
    private readonly MixEngine _mix;
    private readonly HttpListener _server = new();
    private readonly string _url;
    private readonly byte[] _file;
    private int _requests;

    public RemoteSongTests()
    {
        _file = File.ReadAllBytes(TestWav.WriteTone(Path.Combine(_dir, "tone.wav"), TimeSpan.FromSeconds(10)));
        int port = FreePort();
        _url = $"http://127.0.0.1:{port}/song";
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ = Task.Run(Serve);
        _mix = new MixEngine(_engine, offline: true);
        RemoteSongs.Resolver = path => path == Song ? _url : null;
        RemoteSongs.CacheDirectory = Path.Combine(_dir, "cache");
    }

    public void Dispose()
    {
        RemoteSongs.Resolver = null;
        RemoteSongs.CacheDirectory = null;
        _server.Close();
        _mix.Dispose();
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Answers each request with the file, or with the part of it asked for.</summary>
    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext context;
            try { context = await _server.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            Interlocked.Increment(ref _requests);
            try
            {
                var response = context.Response;
                long from = 0, to = _file.Length - 1;
                response.AddHeader("Accept-Ranges", "bytes");
                if (context.Request.Headers["Range"] is { } range && range.StartsWith("bytes=") && range[6..].Split('-') is var parts)
                {
                    from = long.Parse(parts[0]);
                    if (parts[1].Length > 0)
                        to = Math.Min(long.Parse(parts[1]), _file.Length - 1);
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", $"bytes {from}-{to}/{_file.Length}");
                }
                response.ContentType = "audio/wav";
                response.ContentLength64 = to - from + 1;
                await response.OutputStream.WriteAsync(_file.AsMemory((int)from, (int)(to - from + 1)));
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // BASS closes a connection once it has what it wants.
            }
        }
    }

    [Fact]
    public void A_song_on_a_server_plays_in_the_room_like_one_on_disk()
    {
        RemoteSongs.Prepare(Song);
        var deck = _mix.Decks[0];
        deck.Load(Song);
        deck.Play();

        var (main, _) = _mix.Render(TimeSpan.FromSeconds(0.3));

        Assert.Equal(0.5f, main, 0.02f);
        Assert.InRange(deck.Length.TotalSeconds, 9.9, 10.1);
    }

    [Fact]
    public void A_song_from_a_server_can_be_jumped_around_in_and_shifted_in_tempo()
    {
        RemoteSongs.Prepare(Song);
        var deck = _mix.Decks[0];
        deck.Load(Song);
        deck.TempoPercent = 8;
        deck.Seek(TimeSpan.FromSeconds(7));
        deck.Play();

        var (main, _) = _mix.Render(TimeSpan.FromSeconds(0.3));

        Assert.Equal(0.5f, main, 0.02f);
        Assert.InRange(deck.Position, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void A_song_that_was_not_prepared_is_downloaded_when_the_deck_loads_it()
    {
        var deck = _mix.Decks[0];
        deck.Load(Song);
        deck.Play();

        Assert.Equal(0.5f, _mix.Render(TimeSpan.FromSeconds(0.3)).Main, 0.02f);
    }

    [Fact]
    public void A_deck_the_analysis_and_the_close_up_share_one_download()
    {
        var deck = _mix.Decks[0];
        deck.Load(Song);
        var analyzer = new TrackAnalyzer(_engine);

        TrackAnalysis analysis = analyzer.Analyze(Song, detectBpm: false, cancellationToken: TestContext.Current.CancellationToken);
        Waveform detail = analyzer.Detail(Song, TestContext.Current.CancellationToken);

        Assert.InRange(analysis.Length.TotalSeconds, 9.9, 10.1);
        Assert.NotNull(detail);
        Assert.Equal(1, _requests);
    }

    [Fact]
    public void A_song_played_before_plays_again_with_the_server_gone()
    {
        RemoteSongs.Prepare(Song);
        _server.Close();
        _mix.Decks[0].Unload();

        _mix.Decks[0].Load(Song);
        _mix.Decks[0].Play();

        Assert.Equal(0.5f, _mix.Render(TimeSpan.FromSeconds(0.3)).Main, 0.02f);
    }

    [Fact]
    public void A_song_the_server_refuses_says_so_without_the_address()
    {
        RemoteSongs.Resolver = _ => "http://127.0.0.1:1/song?t=secret";   // nothing listens on port 1

        var ex = Assert.Throws<AudioException>(() => RemoteSongs.Prepare("remote://other"));

        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
    }

    [Fact]
    public void A_path_that_is_not_a_server_song_is_read_as_a_file()
    {
        Assert.False(RemoteSongs.IsRemote(Path.Combine(_dir, "tone.wav")));
        Assert.Throws<AudioException>(() => _mix.Decks[0].Load(Path.Combine(_dir, "missing.wav")));
    }
}
