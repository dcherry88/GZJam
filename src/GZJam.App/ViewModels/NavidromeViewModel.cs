using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;
using GZJam.Library;
using GZJam.Navidrome;

namespace GZJam.App.ViewModels;

/// <summary>A found song: what to show, and the <see cref="Track"/> the decks and queue take.</summary>
public sealed record NavidromeRow(NavidromeSong Song, Track Track)
{
    public string Title => Song.Title;
    public string Detail => string.Join("  ·  ", new[] { Song.Artist, Song.Album, Song.Suffix?.ToUpperInvariant() }.Where(s => !string.IsNullOrEmpty(s)));
    public string Length => Format.Duration(Song.Duration);
}

/// <summary>
/// Searching the Navidrome server and playing from it: a song is downloaded whole, in the
/// background (see <see cref="RemoteSongs"/>), then loaded onto a deck or added to the queue.
/// </summary>
public sealed partial class NavidromeViewModel(MainWindowViewModel main, NavidromeClient? client) : ObservableObject
{
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = client is null
        ? "No server set up. Start GZJam with GZJAM_NAVIDROME_URL, GZJAM_NAVIDROME_USER and GZJAM_NAVIDROME_PASSWORD."
        : "Type a song, artist or album, then press Enter.";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial NavidromeRow? Selected { get; set; }

    public ObservableCollection<NavidromeRow> Results { get; } = [];

    public bool IsSetUp => client is not null;

    /// <summary>A song as GZJam holds one: no file to read tags from, so the server's description stands in for them.</summary>
    internal static Track ToTrack(NavidromeSong song) => new()
    {
        Path = NavidromeClient.PathFor(song.Id),
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
        if (client is null || string.IsNullOrWhiteSpace(Query))
            return;
        IsBusy = true;
        Status = "Searching…";
        try
        {
            var songs = await client.SearchAsync(Query.Trim());
            Results.Clear();
            foreach (NavidromeSong song in songs)
                Results.Add(new NavidromeRow(song, ToTrack(song)));
            Status = songs.Count == 0 ? "Nothing found." : $"{songs.Count} songs. Double-click one to load it on a deck.";
        }
        catch (Exception ex) when (ex is NavidromeException or TaskCanceledException)
        {
            Log.Error("Navidrome search failed: " + ex.Message);
            Status = ex is NavidromeException ? ex.Message : "The server didn't answer in time.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowDownloads() => main.ShowDownloads();

    /// <summary>Brings the library's Navidrome songs up to date with the server (they then show in the main song list and its search).</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (client is null)
            return;
        IsBusy = true;
        Status = "Reading the server's songs…";
        try
        {
            var result = await NavidromeLibrary.SyncAsync(main, new Progress<int>(n => Status = $"Reading the server's songs… {n:N0}"));
            Status = result is null ? "No server set up."
                : $"The library has {result.Songs.Added + result.Songs.Kept:N0} songs from the server ({result.Songs.Added:N0} new, {result.Songs.Removed:N0} gone), " +
                  $"and {result.Playlists.Added + result.Playlists.Updated + result.Playlists.Unchanged} of its playlists. " +
                  "Those playlists follow the server: changes made to them here are undone at the next sync.";
        }
        catch (Exception ex) when (ex is NavidromeException or TaskCanceledException)
        {
            Log.Error("Navidrome sync failed: " + ex.Message);
            Status = ex is NavidromeException ? ex.Message : "The server didn't answer in time.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The library's copy of a found song if it has one, with the DJ's marks on it; else the server's description.</summary>
    private Track Known(NavidromeRow row) => main.Database.FindTrack(row.Track.Path) ?? row.Track;

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
    private async Task<bool> DownloadAsync(NavidromeRow row)
    {
        IsBusy = true;
        Status = $"Downloading \"{row.Title}\"…";
        try
        {
            var started = DateTime.UtcNow;
            await Task.Run(() => RemoteSongs.Prepare(row.Track.Path));
            Log.Info($"Navidrome: \"{row.Title}\" downloaded in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
            return true;
        }
        catch (AudioException ex)
        {
            Log.Error($"Navidrome: couldn't get \"{row.Title}\": {ex.Message}");
            Status = $"Couldn't get \"{row.Title}\": {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
