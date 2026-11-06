using GZJam.Audio;
using GZJam.Library;
using GZJam.Subsonic;

namespace GZJam.App.ViewModels;

/// <summary>Songs from music servers: syncing with them, and keeping songs on this computer ahead of when they're needed.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The download queue: what is coming down from the server, and how the last ones went.</summary>
    public DownloadsViewModel Downloads { get; }

    /// <summary>Asks for the Downloads window (the top bar's status, the Servers window).</summary>
    public event Action? ShowDownloadsRequested;

    public void ShowDownloads() => ShowDownloadsRequested?.Invoke();

    /// <summary>A server is set up (the playlists' Refresh button, the menus' sync items).</summary>
    public bool HasServers => ServerConnections.Any;

    private void OnServersChanged() => OnPropertyChanged(nameof(HasServers));

    /// <summary>The servers are being read now.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool IsSyncingServers { get; private set; }

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

    /// <summary>Reads the servers' songs and playlists again and says what changed (a server folder's menu, the playlists' Refresh, Settings).</summary>
    public async Task SyncServersAsync()
    {
        if (!ServerConnections.Any)
        {
            ShowNotice("No server is set up: Settings > Servers", seconds: 6);
            return;
        }
        if (IsSyncingServers)
            return;
        IsSyncingServers = true;
        ShowNotice("Syncing with the servers.", seconds: 120);
        try
        {
            if (await ServerLibrary.SyncAsync(this) is { } result)
            {
                bool changed = result.Songs.Added + result.Songs.Removed + result.Playlists.Added + result.Playlists.Updated + result.Playlists.Removed > 0;
                string playlists = $"{result.Playlists.Added + result.Playlists.Updated + result.Playlists.Unchanged} playlists";
                string problems = result.Problems.Count > 0 ? " Not reached: " + string.Join("; ", result.Problems) + "." : "";
                ShowNotice((changed
                    ? $"Servers: {result.Songs.Added + result.Songs.Kept:N0} songs and {playlists}. New: {result.Songs.Added} songs, {result.Playlists.Added} playlists. Gone: {result.Songs.Removed} songs, {result.Playlists.Removed} playlists."
                    : $"Servers: {result.Songs.Added + result.Songs.Kept:N0} songs and {playlists}, no changes") + problems, seconds: 8);
            }
        }
        catch (Exception ex) when (ex is SubsonicException or TaskCanceledException)
        {
            Log.Error("Server sync failed: " + ex.Message);
            ShowNotice(ex is SubsonicException ? ex.Message : "The server didn't answer in time", seconds: 8);
        }
        finally
        {
            IsSyncingServers = false;
        }
    }

    /// <summary>A server was removed in Settings: its songs and playlists leave the library (hearts and labels are kept by song, the downloaded files stay).</summary>
    internal void ForgetServer(string serverId)
    {
        _database.ForgetRemoteServer(serverId, ServerLibrary.RootPath(serverId));
        Library.RefreshPlaylists();
        Library.ReloadAfterRemoteSync();
    }
}