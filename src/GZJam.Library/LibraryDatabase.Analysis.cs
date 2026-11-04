using Microsoft.Data.Sqlite;

namespace GZJam.Library;

/// <summary>A saved track analysis, as the analyzer of <paramref name="Version"/> left it.</summary>
/// <param name="Waveform">The overview waveform, in the audio layer's own format.</param>
public sealed record StoredAnalysis(int Version, TimeSpan Length, TimeSpan AudibleStart, TimeSpan AudibleEnd, double? Bpm, byte[]? Waveform)
{
    /// <summary>Integrated loudness in LUFS.</summary>
    public double? Lufs { get; init; }

    /// <summary>Sample peak in dB below full scale.</summary>
    public double? PeakDb { get; init; }
}

/// <summary>Identifies one version of a file on disk: when it changes, its analysis is stale.</summary>
public readonly record struct FileVersion(long Size, long ModifiedTicks)
{
    /// <summary>
    /// The version of a song that isn't a file here (one on a server), or null for any other path.
    /// Set by the app. Such a song has nothing on disk to stamp, so it is always the same version:
    /// its analysis is made once.
    /// </summary>
    public static Func<string, FileVersion?>? Remote { get; set; }

    public static FileVersion? Of(string path)
    {
        if (Remote?.Invoke(path) is { } remote)
            return remote;
        var file = new FileInfo(path);
        return file.Exists ? new FileVersion(file.Length, file.LastWriteTimeUtc.Ticks) : null;
    }
}

/// <summary>
/// Where the DJ set a song to start and end by hand; null parts are automatic (silence trimming).
/// <paramref name="FadeOut"/>: the song starts to fade out at <paramref name="End"/> rather than
/// stopping there (#267).
/// </summary>
public readonly record struct ManualTrim(TimeSpan? Start, TimeSpan? End, bool FadeOut = false)
{
    public bool IsEmpty => Start is null && End is null;
}

/// <summary>
/// A library track still waiting for analysis. <paramref name="Update"/>: it was analyzed before,
/// by an older version of the analyzer (a new GZJam re-analyzes the library), not a new song.
/// </summary>
public sealed record PendingAnalysis(string Path, FileVersion Version, bool HasTagBpm, bool Update = false);

public sealed partial class LibraryDatabase
{
    /// <summary>The saved analysis of a file, if it still matches <paramref name="version"/>.</summary>
    public StoredAnalysis? GetAnalysis(string path, FileVersion version)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT version, length_ms, start_ms, end_ms, bpm, waveform, lufs, peak_db FROM analysis
            WHERE path = $path AND file_size = $size AND modified_utc = $mod;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$size", version.Size);
        cmd.Parameters.AddWithValue("$mod", version.ModifiedTicks);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new StoredAnalysis(
            r.GetInt32(0),
            TimeSpan.FromMilliseconds(r.GetInt64(1)),
            TimeSpan.FromMilliseconds(r.GetInt64(2)),
            TimeSpan.FromMilliseconds(r.GetInt64(3)),
            r.IsDBNull(4) ? null : r.GetDouble(4),
            r.IsDBNull(5) ? null : (byte[])r.GetValue(5))
        {
            Lufs = r.IsDBNull(6) ? null : r.GetDouble(6),
            PeakDb = r.IsDBNull(7) ? null : r.GetDouble(7),
        };
    }

    public void SaveAnalysis(string path, FileVersion version, StoredAnalysis analysis)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO analysis (path, file_size, modified_utc, version, length_ms, start_ms, end_ms, bpm, waveform, lufs, peak_db)
            VALUES ($path, $size, $mod, $version, $length, $start, $end, $bpm, $waveform, $lufs, $peak)
            ON CONFLICT(path) DO UPDATE SET
                file_size = excluded.file_size, modified_utc = excluded.modified_utc, version = excluded.version,
                length_ms = excluded.length_ms, start_ms = excluded.start_ms, end_ms = excluded.end_ms,
                bpm = excluded.bpm, waveform = excluded.waveform, lufs = excluded.lufs, peak_db = excluded.peak_db;
            """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$size", version.Size);
        cmd.Parameters.AddWithValue("$mod", version.ModifiedTicks);
        cmd.Parameters.AddWithValue("$version", analysis.Version);
        cmd.Parameters.AddWithValue("$length", (long)analysis.Length.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$start", (long)analysis.AudibleStart.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$end", (long)analysis.AudibleEnd.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$bpm", (object?)analysis.Bpm ?? DBNull.Value);
        cmd.Parameters.Add(new SqliteParameter("$waveform", SqliteType.Blob) { Value = (object?)analysis.Waveform ?? DBNull.Value });
        cmd.Parameters.AddWithValue("$lufs", (object?)analysis.Lufs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$peak", (object?)analysis.PeakDb ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The BPM range the DJ chose for a file, if any.</summary>
    public BpmRange? GetBpmRange(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT min, max FROM bpm_ranges WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new BpmRange(r.GetInt32(0), r.GetInt32(1)) : null;
    }

    /// <summary>Chooses a BPM range for a file, or (null) goes back to automatic detection.</summary>
    public void SetBpmRange(string path, BpmRange? range)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = range is null
            ? "DELETE FROM bpm_ranges WHERE path = $path;"
            : "INSERT INTO bpm_ranges (path, min, max) VALUES ($path, $min, $max) ON CONFLICT(path) DO UPDATE SET min = excluded.min, max = excluded.max;";
        cmd.Parameters.AddWithValue("$path", path);
        if (range is { } r)
        {
            cmd.Parameters.AddWithValue("$min", r.Min);
            cmd.Parameters.AddWithValue("$max", r.Max);
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>The BPM the DJ typed for a file, if any.</summary>
    public double? GetManualBpm(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT bpm FROM manual_bpm WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        return cmd.ExecuteScalar() is double bpm ? bpm : null;
    }

    /// <summary>Sets the BPM for files by hand, or (null) goes back to the tag and detection.</summary>
    public void SetManualBpm(IEnumerable<string> paths, double? bpm)
    {
        using var c = Open();
        using var transaction = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = bpm is null
            ? "DELETE FROM manual_bpm WHERE path = $path;"
            : "INSERT INTO manual_bpm (path, bpm) VALUES ($path, $bpm) ON CONFLICT(path) DO UPDATE SET bpm = excluded.bpm;";
        var path = cmd.Parameters.Add("$path", SqliteType.Text);
        if (bpm is { } value)
            cmd.Parameters.AddWithValue("$bpm", value);
        foreach (string p in paths)
        {
            path.Value = p;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Where the DJ set a file to start and end, if they did.</summary>
    public ManualTrim GetTrim(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT start_ms, end_ms, fade_out FROM trims WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return default;
        return new ManualTrim(
            r.IsDBNull(0) ? null : TimeSpan.FromMilliseconds(r.GetInt64(0)),
            r.IsDBNull(1) ? null : TimeSpan.FromMilliseconds(r.GetInt64(1)),
            !r.IsDBNull(1) && r.GetInt64(2) != 0);
    }

    /// <summary>Saves where the DJ set a file to start and end (both automatic: forgets the file).</summary>
    public void SetTrim(string path, ManualTrim trim)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = trim.IsEmpty
            ? "DELETE FROM trims WHERE path = $path;"
            : """
              INSERT INTO trims (path, start_ms, end_ms, fade_out) VALUES ($path, $start, $end, $fade)
                  ON CONFLICT(path) DO UPDATE SET start_ms = excluded.start_ms, end_ms = excluded.end_ms,
                      fade_out = excluded.fade_out;
              """;
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$start", trim.Start is { } start ? (long)start.TotalMilliseconds : DBNull.Value);
        cmd.Parameters.AddWithValue("$end", trim.End is { } end ? (long)end.TotalMilliseconds : DBNull.Value);
        cmd.Parameters.AddWithValue("$fade", trim.FadeOut && trim.End is not null ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Library tracks with no analysis from analyzer <paramref name="version"/> (or later) for the
    /// file as it is now, in folder order. Songs on a server are left out: each would be downloaded
    /// to be analyzed, so they are analyzed when they are played or queued.
    /// </summary>
    public IReadOnlyList<PendingAnalysis> GetPendingAnalysis(int version)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.path, t.file_size, t.modified_utc, t.bpm IS NOT NULL, a.path IS NOT NULL FROM tracks t {JoinAnalysis}
            WHERE (a.path IS NULL OR a.version < $version) AND t.path NOT LIKE '%://%'
            ORDER BY t.root_id, t.folder COLLATE NOCASE, t.track_number, t.file_name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$version", version);
        using var r = cmd.ExecuteReader();
        var pending = new List<PendingAnalysis>();
        while (r.Read())
            pending.Add(new PendingAnalysis(r.GetString(0), new FileVersion(r.GetInt64(1), r.GetInt64(2)), r.GetBoolean(3), r.GetBoolean(4)));
        return pending;
    }
}
