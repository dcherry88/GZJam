using Microsoft.Data.Sqlite;

namespace GZJam.Library;

/// <summary>A playlist: <paramref name="Count"/> entries (songs, a song perhaps more than once), <paramref name="Length"/> in all.</summary>
/// <param name="IsRemote">A copy of a server's playlist: it follows the server, and changes made to it are undone at the next sync.</param>
public sealed record PlaylistInfo(long Id, string Name, int Count, TimeSpan Length, bool IsRemote = false)
{
    /// <summary>The playlist's name for a tooltip, saying where it comes from if it's a server's.</summary>
    public string Tip => IsRemote ? $"{Name}\nFrom a music server: it follows the server" : Name;
}

/// <summary>
/// Playlists: a set planned before the night, in order, saved by name. Unlike labels (what a song
/// is, in no order), a playlist is a plan: ordered, and a song can be in it twice. Entries are kept
/// by path, never in the music files; a song missing from the library is kept but not shown.
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>Every playlist, alphabetically, with how many songs and how long.</summary>
    public IReadOnlyList<PlaylistInfo> GetPlaylists()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, COUNT(e.id), COALESCE(SUM(t.duration_ms), 0), rp.playlist_id IS NOT NULL
            FROM playlists p LEFT JOIN playlist_entries e ON e.playlist_id = p.id LEFT JOIN tracks t ON t.path = e.path
                 LEFT JOIN remote_playlists rp ON rp.playlist_id = p.id
            GROUP BY p.id ORDER BY p.name COLLATE NOCASE;
            """;
        using var r = cmd.ExecuteReader();
        var playlists = new List<PlaylistInfo>();
        while (r.Read())
            playlists.Add(new PlaylistInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2), TimeSpan.FromMilliseconds(r.GetInt64(3)), r.GetBoolean(4)));
        return playlists;
    }

    public PlaylistInfo? GetPlaylist(long id) => GetPlaylists().FirstOrDefault(p => p.Id == id);

    /// <summary>The names of the playlists a file is in, alphabetically: for its song's info (#129).</summary>
    public IReadOnlyList<string> GetPlaylistsWith(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT p.name FROM playlists p JOIN playlist_entries e ON e.playlist_id = p.id
            WHERE e.path = $path ORDER BY p.name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read())
            names.Add(r.GetString(0));
        return names;
    }

    /// <summary>
    /// Makes an empty playlist. A name already taken gets a number ("Saturday 2"), so a playlist
    /// is never silently merged into another. Returns it.
    /// </summary>
    public PlaylistInfo CreatePlaylist(string name)
    {
        name = CleanName(name);
        using var c = Open();
        string unique = UniqueName(c, name, except: null);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists (name) VALUES ($name); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", unique);
        return new PlaylistInfo((long)cmd.ExecuteScalar()!, unique, 0, TimeSpan.Zero);
    }

    /// <summary>Renames a playlist; a name another one has gets a number. Returns the name as stored.</summary>
    public string RenamePlaylist(long id, string name)
    {
        name = CleanName(name);
        using var c = Open();
        string unique = UniqueName(c, name, except: id);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$name", unique);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return unique;
    }

    /// <summary>Deletes a playlist. Its songs stay in the library, untouched.</summary>
    public void DeletePlaylist(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM playlists WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A playlist's entries in order: each entry's id and its file.</summary>
    public IReadOnlyList<(long EntryId, string Path)> GetPlaylistEntries(long id)
    {
        using var c = Open();
        return ReadEntries(c, id);
    }

    /// <summary>Adds files to a playlist at <paramref name="index"/> (0: the top; null or past the end: the end).</summary>
    public void AddToPlaylist(long id, IEnumerable<string> paths, int? index = null)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        var entries = ReadEntries(c, id).ToList();
        int at = Math.Clamp(index ?? entries.Count, 0, entries.Count);
        entries.InsertRange(at, paths.Select(p => (EntryId: 0L, Path: p)));
        WriteEntries(c, id, entries);
        transaction.Commit();
    }

    /// <summary>Takes entries out of a playlist (the files are untouched).</summary>
    public void RemoveFromPlaylist(long id, IEnumerable<long> entryIds)
    {
        var remove = entryIds.ToHashSet();
        using var c = Open();
        using var transaction = c.BeginTransaction();
        WriteEntries(c, id, ReadEntries(c, id).Where(e => !remove.Contains(e.EntryId)).ToList());
        transaction.Commit();
    }

    /// <summary>
    /// Moves entries, keeping their order among themselves, to before the entry that was at
    /// <paramref name="index"/> (counting before the move; past the end: to the end).
    /// </summary>
    public void MovePlaylistEntries(long id, IEnumerable<long> entryIds, int index)
    {
        var moving = entryIds.ToHashSet();
        using var c = Open();
        using var transaction = c.BeginTransaction();
        var entries = ReadEntries(c, id).ToList();
        int at = Math.Clamp(index, 0, entries.Count);
        int before = entries.Take(at).Count(e => moving.Contains(e.EntryId));
        var moved = entries.Where(e => moving.Contains(e.EntryId)).ToList();
        entries.RemoveAll(e => moving.Contains(e.EntryId));
        entries.InsertRange(Math.Clamp(at - before, 0, entries.Count), moved);
        WriteEntries(c, id, entries);
        transaction.Commit();
    }

    private static List<(long EntryId, string Path)> ReadEntries(SqliteConnection c, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM playlist_entries WHERE playlist_id = $id ORDER BY position;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var entries = new List<(long, string)>();
        while (r.Read())
            entries.Add((r.GetInt64(0), r.GetString(1)));
        return entries;
    }

    /// <summary>Numbers the entries 0, 1, 2… in this order; new ones (id 0) are added. Entries not listed are removed.</summary>
    private static void WriteEntries(SqliteConnection c, long id, IReadOnlyList<(long EntryId, string Path)> entries)
    {
        var keep = entries.Where(e => e.EntryId != 0).Select(e => e.EntryId).ToList();
        using (var delete = c.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM playlist_entries WHERE playlist_id = $id AND id NOT IN ({string.Join(',', keep.DefaultIfEmpty(-1))});";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }
        using var update = c.CreateCommand();
        update.CommandText = "UPDATE playlist_entries SET position = $position WHERE id = $entry;";
        var updatePosition = update.Parameters.Add("$position", SqliteType.Integer);
        var updateEntry = update.Parameters.Add("$entry", SqliteType.Integer);
        using var insert = c.CreateCommand();
        insert.CommandText = "INSERT INTO playlist_entries (playlist_id, position, path) VALUES ($id, $position, $path);";
        insert.Parameters.AddWithValue("$id", id);
        var insertPosition = insert.Parameters.Add("$position", SqliteType.Integer);
        var insertPath = insert.Parameters.Add("$path", SqliteType.Text);
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].EntryId != 0)
            {
                (updatePosition.Value, updateEntry.Value) = (i, entries[i].EntryId);
                update.ExecuteNonQuery();
            }
            else
            {
                (insertPosition.Value, insertPath.Value) = (i, entries[i].Path);
                insert.ExecuteNonQuery();
            }
        }
    }

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("A playlist needs a name.", nameof(name));
        return name;
    }

    /// <summary><paramref name="name"/>, or with a number after it if another playlist has it already.</summary>
    private static string UniqueName(SqliteConnection c, string name, long? except)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM playlists WHERE name = $name AND id IS NOT $except;";
        var nameParameter = cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$except", (object?)except ?? DBNull.Value);
        string candidate = name;
        for (int n = 2; cmd.ExecuteScalar() is not null; n++)
            nameParameter.Value = candidate = $"{name} {n}";
        return candidate;
    }
}
