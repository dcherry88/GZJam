using GZJam.Audio;
using GZJam.Library;
using GZJam.Navidrome;

namespace GZJam.App.ViewModels;

/// <summary>Songs from a server (Navidrome): syncing with it, and keeping songs on this computer ahead of when they're needed.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The download queue: what is coming down from the server, and how the last ones went.</summary>
    public DownloadsViewModel Downloads { get; }

    /// <summary>Asks for the Downloads window (the top bar's status, the Navidrome window).</summary>
    public event Action? ShowDownloadsRequested;

    public void ShowDownloads() => ShowDownloadsRequested?.Invoke();

    /// <summary>A Navidrome server is set up (the playlists' Refresh button, the menus' sync items).</summary>
    public bool HasNavidrome => NavidromeConnection.Client is not null;

    private void OnNavidromeChanged() => OnPropertyChanged(nameof(HasNavidrome));

    /// <summary>The server is being read now.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsSyncingNavidrome { get; private set; }

    /// <summary>How many of these songs come from a server and aren't on this computer yet.</summary>
    public int NotHere(IEnumerable<Track> tracks) =>
        tracks.Select(t => t.Path).Distinct().Count(p => RemoteSongs.IsRemote(p) && !RemoteSongs.IsCached(p));

    /// <summary>
    /// Puts the songs from a server that aren't on this computer into the download queue, so they
    /// play with the server away (a venue with no network). <paramref name="what"/> names where
    /// they came from, for the notice. Songs already here, and files on this computer, are skipped.
    /// </summary>
    public void CacheForOffline(IEnumerable<string> paths, string what)
    {
        var fromServer = paths.Distinct().Where(RemoteSongs.IsRemote).ToList();
        if (fromServer.Count == 0)
        {
            ShowNotice($"{what}: nothing from the server to fetch");
            return;
        }
        int added = RemoteSongs.Enqueue(fromServer);
        Log.Info($"Caching {what}: {added} of {fromServer.Count} songs to fetch");
        ShowNotice(added == 0
            ? $"{what}: all {fromServer.Count} songs are on this computer already, or on their way"
            : $"{what}: {added} {(added == 1 ? "song" : "songs")} added to the downloads (see the Downloads button, top right)", seconds: 6);
    }

    public void CacheForOffline(IEnumerable<Track> tracks, string what) => CacheForOffline(tracks.Select(t => t.Path), what);

    public void CachePlaylist(PlaylistInfo playlist) =>
        CacheForOffline(_database.GetPlaylistEntries(playlist.Id).Select(e => e.Path), $"\"{playlist.Name}\"");

    /// <summary>Everything under a folder of the library (an artist or album of the server, say).</summary>
    public void CacheFolder(long rootId, string relativeFolder, string name) =>
        CacheForOffline(_database.Search(null, new FolderScope(rootId, relativeFolder)), $"\"{name}\"");

    /// <summary>Reads the server's songs and playlists again and says what changed (the Navidrome folder's menu, the playlists' Refresh, Settings).</summary>
    public async Task SyncNavidromeAsync()
    {
        if (NavidromeConnection.Client is null)
        {
            ShowNotice("No Navidrome server is set up: Settings › Navidrome", seconds: 6);
            return;
        }
        if (IsSyncingNavidrome)
            return;
        IsSyncingNavidrome = true;
        ShowNotice("Syncing with Navidrome…", seconds: 120);
        try
        {
            if (await NavidromeLibrary.SyncAsync(this) is { } result)
            {
                bool changed = result.Songs.Added + result.Songs.Removed + result.Playlists.Added + result.Playlists.Updated + result.Playlists.Removed > 0;
                string playlists = $"{result.Playlists.Added + result.Playlists.Updated + result.Playlists.Unchanged} playlists";
                ShowNotice(changed
                    ? $"Navidrome: {result.Songs.Added + result.Songs.Kept:N0} songs and {playlists}. New: {result.Songs.Added} songs, {result.Playlists.Added} playlists. Gone: {result.Songs.Removed} songs, {result.Playlists.Removed} playlists."
                    : $"Navidrome: {result.Songs.Added + result.Songs.Kept:N0} songs and {playlists}, no changes", seconds: 8);
            }
        }
        catch (Exception ex) when (ex is NavidromeException or TaskCanceledException)
        {
            Log.Error("Navidrome sync failed: " + ex.Message);
            ShowNotice(ex is NavidromeException ? ex.Message : "The Navidrome server didn't answer in time", seconds: 8);
        }
        finally
        {
            IsSyncingNavidrome = false;
        }
    }
}
