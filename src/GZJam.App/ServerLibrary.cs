using GZJam.App.ViewModels;
using GZJam.Library;
using GZJam.Subsonic;

namespace GZJam.App;

/// <summary>
/// What a sync with the servers changed: the songs in the library and the playlists made from the
/// servers'. <paramref name="Problems"/> says which servers couldn't be reached; the others were synced.
/// </summary>
internal sealed record ServerSyncResult(RemoteSyncResult Songs, RemotePlaylistSyncResult Playlists, IReadOnlyList<string> Problems);

/// <summary>
/// Keeping the library's songs and playlists from the servers in step with them: each server is asked
/// for every song and every playlist, that server's root in the library (<c>subsonic://&lt;server&gt;</c>)
/// is made to match (see <see cref="LibraryDatabase.SyncRemoteTracks"/>), and each server playlist is
/// mirrored as a GZJam playlist (see <see cref="LibraryDatabase.SyncRemotePlaylists"/>). Only
/// descriptions come over: the music itself is downloaded when the DJ asks for it.
/// </summary>
internal static class ServerLibrary
{
    /// <summary>The library root of a server's songs.</summary>
    public static string RootPath(string serverId) => SubsonicClient.Scheme + serverId;

    /// <summary>
    /// Asks every server for its songs and playlists and brings the library up to date. A server
    /// that can't be reached is left as it was and named in <see cref="ServerSyncResult.Problems"/>.
    /// Null if no server is set up.
    /// </summary>
    public static async Task<ServerSyncResult?> SyncAsync(MainWindowViewModel main, IProgress<int>? progress = null)
    {
        var clients = ServerConnections.Clients;
        if (clients.Count == 0)
            return null;
        RemoteSyncResult songs = new(0, 0, 0);
        RemotePlaylistSyncResult playlists = new(0, 0, 0, 0);
        var problems = new List<string>();
        foreach (SubsonicClient client in clients)
        {
            try
            {
                var one = await SyncOneAsync(main, client, progress);
                songs = new RemoteSyncResult(songs.Added + one.Songs.Added, songs.Kept + one.Songs.Kept, songs.Removed + one.Songs.Removed);
                playlists = new RemotePlaylistSyncResult(playlists.Added + one.Playlists.Added, playlists.Updated + one.Playlists.Updated,
                    playlists.Unchanged + one.Playlists.Unchanged, playlists.Removed + one.Playlists.Removed);
            }
            catch (Exception ex) when (ex is SubsonicException or TaskCanceledException)
            {
                string name = ServerConnections.NameOf(client.ServerId) ?? "A server";
                string why = ex is SubsonicException ? ex.Message : "it didn't answer in time";
                Log.Error($"Sync with server {client.ServerId} failed: {why}");
                problems.Add($"{name}: {why}");
            }
        }
        if (problems.Count == clients.Count)
            throw new SubsonicException(string.Join(" ", problems));
        main.Library.RefreshPlaylists();
        main.Library.ReloadAfterRemoteSync();
        return new ServerSyncResult(songs, playlists, problems);
    }

    /// <summary>Syncs one server (what its folder's menu does).</summary>
    public static async Task<ServerSyncResult> SyncOneAsync(MainWindowViewModel main, SubsonicClient client, IProgress<int>? progress = null)
    {
        // Everything is read before anything is changed, so a server that fails halfway changes nothing.
        var songs = new List<SubsonicSong>(await client.AllSongsAsync(progress));
        var known = songs.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var mirrored = new List<RemotePlaylist>();
        foreach (SubsonicPlaylist playlist in await client.PlaylistsAsync())
        {
            var entries = await client.PlaylistSongsAsync(playlist.Id);
            foreach (SubsonicSong song in entries.Where(s => known.Add(s.Id)))
                songs.Add(song);   // in a playlist, though the song list didn't have it (changed meanwhile)
            mirrored.Add(new RemotePlaylist(playlist.Id, playlist.Name, [.. entries.Select(s => client.PathFor(s.Id))]));
        }

        var tracks = songs.Select(s => ServerBrowserViewModel.ToTrack(client.ServerId, s)).ToList();
        string name = ServerConnections.NameOf(client.ServerId) ?? client.ServerId;
        var result = await Task.Run(() =>
        {
            var root = main.Database.AddRemoteRoot(RootPath(client.ServerId));
            return new ServerSyncResult(
                main.Database.SyncRemoteTracks(root, tracks),
                main.Database.SyncRemotePlaylists(client.ServerId, name, mirrored), []);
        });
        Log.Info($"Server {client.ServerId} synced: songs {result.Songs.Added:N0} added, {result.Songs.Kept:N0} kept, {result.Songs.Removed:N0} gone; " +
                 $"playlists {result.Playlists.Added} added, {result.Playlists.Updated} updated, {result.Playlists.Unchanged} unchanged, {result.Playlists.Removed} gone");
        return result;
    }

    /// <summary>The sync at startup: nothing is said if a server can't be reached (a gig with no network); what's there from last time stays.</summary>
    public static async Task SyncQuietlyAsync(MainWindowViewModel main)
    {
        try
        {
            await SyncAsync(main);
        }
        catch (Exception ex) when (ex is SubsonicException or TaskCanceledException)
        {
            Log.Info("Servers not synced: " + ex.Message);
        }
    }
}
