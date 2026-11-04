namespace GZJam.Library;

/// <summary>What a sync with a server changed in the library.</summary>
public sealed record RemoteSyncResult(int Added, int Kept, int Removed);

/// <summary>
/// Songs on a server (Navidrome) in the library: a root of their own, <c>navidrome://</c>, whose
/// songs come from asking the server rather than from reading a folder. They are kept by path like
/// any other song (<c>navidrome://&lt;id&gt;</c>), so search, playlists, History and the DJ's marks
/// treat them just the same.
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The root for songs from a server, e.g. <c>navidrome://</c>; made if it isn't there yet.</summary>
    public LibraryRoot AddRemoteRoot(string scheme)
    {
        string path = scheme.EndsWith("://", StringComparison.Ordinal) ? scheme : scheme + "://";
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO roots(path) VALUES ($path) ON CONFLICT(path) DO NOTHING;
            SELECT id FROM roots WHERE path = $path;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        return new LibraryRoot((long)cmd.ExecuteScalar()!, path);
    }

    /// <summary>
    /// Makes the songs of <paramref name="root"/> the ones in <paramref name="songs"/> (each with
    /// its path, e.g. <c>navidrome://&lt;id&gt;</c>, and the server's description of it): new ones are
    /// added, changed ones updated, and ones the server no longer has taken out. What the DJ gave
    /// a song that has gone (marks, labels, playlist places, plays) is kept, as for a deleted file.
    /// An empty answer changes nothing: a server that failed to list its songs must not empty the library.
    /// </summary>
    public RemoteSyncResult SyncRemoteTracks(LibraryRoot root, IReadOnlyCollection<Track> songs)
    {
        var known = GetFileStamps(root.Id);
        if (songs.Count == 0)
            return new RemoteSyncResult(0, known.Count, 0);
        var wanted = songs.Select(s => s.InRemoteRoot(root.Id)).ToList();
        int added = wanted.Count(s => !known.ContainsKey(s.Path));
        UpsertTracks(wanted);
        var present = wanted.Select(w => w.Path).ToHashSet(StringComparer.Ordinal);
        var gone = known.Where(k => !present.Contains(k.Key)).Select(k => k.Value.Id).ToList();
        if (gone.Count > 0)
            RemoveTracks(gone, []);
        return new RemoteSyncResult(added, wanted.Count - added, gone.Count);
    }
}
