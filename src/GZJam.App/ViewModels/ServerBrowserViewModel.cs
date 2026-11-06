using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;
using GZJam.Library;
using GZJam.Subsonic;

namespace GZJam.App.ViewModels;

/// <summary>A found song: what to show, and the <see cref="Track"/> the decks and queue take.</summary>
public sealed record ServerRow(SubsonicSong Song, Track Track, string? ServerName = null)
{
    public string Title => Song.Title;
    public string Detail => string.Join("  ·  ", new[] { Song.Artist, Song.Album, Song.Suffix?.ToUpperInvariant(), ServerName }.Where(s => !string.IsNullOrEmpty(s)));
    public string Length => Format.Duration(Song.Duration);
}

/// <summary>
/// Searching the music servers and playing from them: a song is downloaded whole, in the
/// background (see <see cref="RemoteSongs"/>), then loaded onto a deck or added to the queue. With
/// more than one server set up, the search covers all of them and each result says which it is from.
/// </summary>
public sealed partial class ServerBrowserViewModel(MainWindowViewModel main) : ObservableObject
{
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = ServerConnections.Any
        ? "Type a song, artist or album, then press Enter."
        : "No server set up: Settings › Servers.";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial ServerRow? Selected { get; set; }

    public ObservableCollection<ServerRow> Results { get; } = [];

    public bool IsSetUp => ServerConnections.Any;

    /// <summary>A song as GZJam holds one: no file to read tags from, so the server's description stands in for them.</summary>
    internal static Track ToTrack(string serverId, SubsonicSong song) => new()
    {
        Path = SubsonicClient.PathFor(serverId, song.Id),
        Folder = string.Join('/', new[] { song.AlbumArtist ?? song.Artist, song.Album }.Where(s => !string.IsNullOrEmpty(s))),
        FileName = song.Title + (song.Suffix is { Length: > 0 } suffix ? "." + suffix : ""),
        FileSize = song.Size,
        Title = song.Title,
        Artist = song.Artist,
        Album = song.Album,
        AlbumArtist = song.AlbumArtist,
        Genre = song.Genre,
        Comment = song.Comment,
        Year = song.Year,
        TrackNumber = song.TrackNumber,
        TagBpm = song.Bpm,
        Duration = song.Duration,
    };

    [RelayCommand]
    private async Task SearchAsync()
    {
        var clients = ServerConnections.Clients;
        if (clients.Count == 0 || string.IsNullOrWhiteSpace(Query))
            return;
        IsBusy = true;
        Status = "Searching.";
        string query = Query.Trim();
        try
        {
            // Every server is asked at once; one that fails doesn't hide what the others found.
            var answers = await Task.WhenAll(clients.Select(async client =>
            {
                try
                {
                    return (Client: client, Songs: await client.SearchAsync(query), Problem: (string?)null);
                }
                catch (Exception ex) when (ex is SubsonicException or TaskCanceledException)
                {
                    Log.Error($"Search on server {client.ServerId} failed: " + ex.Message);
                    return (Client: client, Songs: (IReadOnlyList<SubsonicSong>)[], Problem: ex is SubsonicException ? ex.Message : "The server didn't answer in time.");
                }
            }));
            Results.Clear();
            bool several = clients.Count > 1;
            foreach (var (client, songs, _) in answers)
            {
                string? name = several ? ServerConnections.NameOf(client.ServerId) : null;
                foreach (SubsonicSong song in songs)
                    Results.Add(new ServerRow(song, ToTrack(client.ServerId, song), name));
            }
            var problems = answers.Where(a => a.Problem is not null)
                .Select(a => $"{ServerConnections.NameOf(a.Client.ServerId) ?? "A server"}: {a.Problem}").ToList();
            Status = problems.Count > 0 ? string.Join(" ", problems) + (Results.Count > 0 ? $" Found {Results.Count} songs on the others." : "")
                : Results.Count == 0 ? "Nothing found." : $"{Results.Count} songs. Double-click one to load it on a deck.";
        }
        finally
        {
            IsBusy = false;
        }
    }
    [RelayCommand]
    private void ShowDownloads() => main.ShowDownloads();

    /// <summary>Brings the library's songs from the servers up to date with them (they then show in the main song list and its search).</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (!ServerConnections.Any)
            return;
        IsBusy = true;
        Status = "Reading the server's songs…";
        try
        {
            var result = await ServerLibrary.SyncAsync(main, new Progress<int>(n => Status = $"Reading the server's songs… {n:N0}"));
            Status = result is null ? "No server set up."
                : $"The library has {result.Songs.Added + result.Songs.Kept:N0} songs from the servers ({result.Songs.Added:N0} new, {result.Songs.Removed:N0} gone), " +
                  $"and {result.Playlists.Added + result.Playlists.Updated + result.Playlists.Unchanged} of its playlists. " +
                  "Those playlists follow the server: changes made to them here are undone at the next sync.";
        }
        catch (Exception ex) when (ex is SubsonicException or TaskCanceledException)
        {
            Log.Error("Server sync failed: " + ex.Message);
            Status = ex is SubsonicException ? ex.Message : "The server didn't answer in time.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The library's copy of a found song if it has one, with the DJ's marks on it; else the server's description.</summary>
    private Track Known(ServerRow row) => main.Database.FindTrack(row.Track.Path) ?? row.Track;

    [RelayCommand]
    private async Task LoadOnDeckAsync()
    {
        if (Selected is not { } row)
            return;
        // Asking for the song here is asking for its download.
        if (!await DownloadAsync(row))
            return;
        if (await main.PlaySongAsync(Known(row)))
            Status = $"\"{row.Title}\" is on a deck.";
        else
            Status = "It didn't load: no deck is free to take it (pause one, or turn auto-play off), or the server didn't send it.";
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        if (Selected is not { } row || !await DownloadAsync(row))
            return;
        main.AddToQueue([Known(row)]);
        Status = $"\"{row.Title}\" is in the queue.";
    }

    /// <summary>Downloads the song whole before it goes anywhere, so a deck loads it at once.</summary>
    private async Task<bool> DownloadAsync(ServerRow row)
    {
        IsBusy = true;
        Status = $"Downloading \"{row.Title}\"…";
        try
        {
            var started = DateTime.UtcNow;
            await Task.Run(() => RemoteSongs.Prepare(row.Track.Path, requested: true));
            Log.Info($"Server: \"{row.Title}\" downloaded in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
            return true;
        }
        catch (AudioException ex)
        {
            Log.Error($"Server: couldn't get \"{row.Title}\": {ex.Message}");
            Status = $"Couldn't get \"{row.Title}\": {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
