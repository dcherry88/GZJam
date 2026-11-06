using Microsoft.Data.Sqlite;

namespace GZJam.Library;

/// <summary>A playlist on a server: its id there, its name, and its songs' paths (<c>subsonic://&lt;server&gt;/&lt;id&gt;</c>) in order.</summary>
public sealed record RemotePlaylist(string Id, string Name, IReadOnlyList<string> Paths);

/// <summary>What a sync of a server's playlists changed.</summary>
public sealed record RemotePlaylistSyncResult(int Added, int Updated, int Unchanged, int Removed);

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Makes GZJam's copies of a server's playlists match the server's: a one-way mirror. A
    /// playlist new on the server is made here; one that changed (its songs, their order, its
    /// name on the server) is updated; one gone from the server is deleted here. Anything the DJ
    /// changed in a copy is put back as the server has it at the next sync, except its name: a
    /// copy renamed here keeps the new name. The DJ's own playlists are never touched, and a copy
    /// whose name a playlist of theirs has already is called "Name (<paramref name="label"/>)".
    /// A copy the DJ deleted comes back at the next sync.
    /// </summary>
    /// <param name="source">Which server: its id in GZJam.</param>
    /// <param name="label">How the server is named in a playlist name that needs telling apart.</param>
    public RemotePlaylistSyncResult SyncRemotePlaylists(string source, string label, IReadOnlyCollection<RemotePlaylist> playlists)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var mapped = ReadRemotePlaylists(c, source);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, updated = 0, unchanged = 0;

        foreach (RemotePlaylist remote in playlists)
        {
            seen.Add(remote.Id);
            string name = remote.Name.Trim().Length > 0 ? remote.Name.Trim() : "Playlist";
            if (mapped.TryGetValue(remote.Id, out var copy))
            {
                bool changed = false;
                if (copy.RemoteName != name)
                {
                    SetPlaylistName(c, copy.PlaylistId, FreeName(c, name, label, except: copy.PlaylistId));
                    SetRemoteName(c, copy.PlaylistId, name);
                    changed = true;
                }
                var entries = ReadEntries(c, copy.PlaylistId);
                if (!entries.Select(e => e.Path).SequenceEqual(remote.Paths, StringComparer.Ordinal))
                {
                    WriteEntries(c, copy.PlaylistId, [.. remote.Paths.Select(p => (EntryId: 0L, Path: p))]);
                    changed = true;
                }
                if (changed)
                    updated++;
                else
                    unchanged++;
                continue;
            }

            long id = InsertPlaylist(c, FreeName(c, name, label, except: null));
            using (var link = c.CreateCommand())
            {
                link.CommandText = "INSERT INTO remote_playlists (playlist_id, source, remote_id, remote_name) VALUES ($id, $source, $remote, $name);";
                link.Parameters.AddWithValue("$id", id);
                link.Parameters.AddWithValue("$source", source);
                link.Parameters.AddWithValue("$remote", remote.Id);
                link.Parameters.AddWithValue("$name", name);
                link.ExecuteNonQuery();
            }
            WriteEntries(c, id, [.. remote.Paths.Select(p => (EntryId: 0L, Path: p))]);
            added++;
        }

        int removed = 0;
        foreach (var (_, copy) in mapped.Where(m => !seen.Contains(m.Key)))
        {
            using var delete = c.CreateCommand();
            delete.CommandText = "DELETE FROM playlists WHERE id = $id;";   // its link and entries go with it
            delete.Parameters.AddWithValue("$id", copy.PlaylistId);
            delete.ExecuteNonQuery();
            removed++;
        }

        tx.Commit();
        return new RemotePlaylistSyncResult(added, updated, unchanged, removed);
    }

    /// <summary>Whether a playlist is a copy of a server's (so a sync would overwrite changes to it).</summary>
    public bool IsRemotePlaylist(long playlistId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM remote_playlists WHERE playlist_id = $id;";
        cmd.Parameters.AddWithValue("$id", playlistId);
        return cmd.ExecuteScalar() is not null;
    }

    private static Dictionary<string, (long PlaylistId, string RemoteName)> ReadRemotePlaylists(SqliteConnection c, string source)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT remote_id, playlist_id, remote_name FROM remote_playlists WHERE source = $source;";
        cmd.Parameters.AddWithValue("$source", source);
        using var r = cmd.ExecuteReader();
        var copies = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        while (r.Read())
            copies[r.GetString(0)] = (r.GetInt64(1), r.GetString(2));
        return copies;
    }

    private static long InsertPlaylist(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists (name) VALUES ($name); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", name);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void SetPlaylistName(SqliteConnection c, long id, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void SetRemoteName(SqliteConnection c, long id, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE remote_playlists SET remote_name = $name WHERE playlist_id = $id;";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary><paramref name="name"/> if no other playlist has it, else "name (label)", numbered if that is taken too.</summary>
    private static string FreeName(SqliteConnection c, string name, string label, long? except)
    {
        string unique = UniqueName(c, name, except);
        return unique == name ? name : UniqueName(c, $"{name} ({label})", except);
    }
}
