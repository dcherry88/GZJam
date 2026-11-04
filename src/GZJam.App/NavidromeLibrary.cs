using GZJam.App.ViewModels;
using GZJam.Library;
using GZJam.Navidrome;

namespace GZJam.App;

/// <summary>What a sync with the Navidrome server changed: the songs in the library and the playlists made from the server's.</summary>
internal sealed record NavidromeSyncResult(RemoteSyncResult Songs, RemotePlaylistSyncResult Playlists);

/// <summary>
/// Keeping the library's Navidrome songs and playlists in step with the server: it lists every song
/// and every playlist, the library's <c>navidrome://</c> root is made to match (see
/// <see cref="LibraryDatabase.SyncRemoteTracks"/>), and each server playlist is mirrored as a GZJam
/// playlist (see <see cref="LibraryDatabase.SyncRemotePlaylists"/>). Only descriptions come over: the
/// music itself is fetched when a song is queued or loaded.
/// </summary>
internal static class NavidromeLibrary
{
    /// <summary>Asks the server for its songs and playlists and brings the library up to date. Null if no server is set up.</summary>
    public static async Task<NavidromeSyncResult?> SyncAsync(MainWindowViewModel main, IProgress<int>? progress = null)
    {
        if (NavidromeConnection.Client is not { } client)
            return null;

        // Everything is read before anything is changed, so a server that fails halfway changes nothing.
        var songs = new List<NavidromeSong>(await client.AllSongsAsync(progress));
        var known = songs.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var mirrored = new List<RemotePlaylist>();
        foreach (NavidromePlaylist playlist in await client.PlaylistsAsync())
        {
            var entries = await client.PlaylistSongsAsync(playlist.Id);
            foreach (NavidromeSong song in entries.Where(s => known.Add(s.Id)))
                songs.Add(song);   // in a playlist, though the song list didn't have it (changed meanwhile)
            mirrored.Add(new RemotePlaylist(playlist.Id, playlist.Name, [.. entries.Select(s => NavidromeClient.PathFor(s.Id))]));
        }

        var tracks = songs.Select(NavidromeViewModel.ToTrack).ToList();
        var result = await Task.Run(() =>
        {
            var root = main.Database.AddRemoteRoot("navidrome");
            return new NavidromeSyncResult(
                main.Database.SyncRemoteTracks(root, tracks),
                main.Database.SyncRemotePlaylists("navidrome", "Navidrome", mirrored));
        });
        main.Library.RefreshPlaylists();
        main.Library.ReloadAfterRemoteSync();
        Log.Info($"Navidrome library synced: songs {result.Songs.Added:N0} added, {result.Songs.Kept:N0} kept, {result.Songs.Removed:N0} gone; " +
                 $"playlists {result.Playlists.Added} added, {result.Playlists.Updated} updated, {result.Playlists.Unchanged} unchanged, {result.Playlists.Removed} gone");
        return result;
    }

    /// <summary>The sync at startup: nothing is said if the server can't be reached (a gig with no network); what's there from last time stays.</summary>
    public static async Task SyncQuietlyAsync(MainWindowViewModel main)
    {
        try
        {
            await SyncAsync(main);
        }
        catch (Exception ex) when (ex is NavidromeException or TaskCanceledException)
        {
            Log.Info("Navidrome library not synced: " + ex.Message);
        }
    }
}
