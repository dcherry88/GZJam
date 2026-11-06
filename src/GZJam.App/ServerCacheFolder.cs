namespace GZJam.App;

/// <summary>What moving the fetched songs to another folder did.</summary>
internal sealed record CacheMoveResult(int Moved, int Stayed, long Bytes);

/// <summary>
/// Where the songs fetched from the music servers are kept (Settings › Servers). The choice
/// is kept in a file of its own beside the settings, not in <c>settings.json</c>: a path like
/// <c>D:\Music cache</c> belongs to this computer, and library backups carry the settings to others.
/// With no choice, it is a folder beside the library.
/// </summary>
internal static class ServerCacheFolder
{
    /// <summary>Tests keep the choice somewhere else, so they never touch the real one.</summary>
    internal static string? SettingFileOverride { get; set; }

    private static string SettingFile => SettingFileOverride ?? Path.Combine(AppPaths.SettingsDirectory, "server-cache-folder.txt");

    /// <summary>Tests use a folder of their own as the default.</summary>
    internal static string? DefaultOverride { get; set; }

    public static string Default => DefaultOverride ?? Path.Combine(AppPaths.DataDirectory, "remote");

    /// <summary>The folder in use: the one chosen, else <see cref="Default"/>.</summary>
    public static string Current()
    {
        try
        {
            if (File.Exists(SettingFile) && File.ReadAllText(SettingFile).Trim() is { Length: > 0 } chosen)
                return chosen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't read where songs from servers are kept", ex);
        }
        return Default;
    }

    /// <summary>Remembers the folder; null, or the default, forgets the choice.</summary>
    public static void Save(string? folder)
    {
        if (folder is null || Same(folder, Default))
        {
            File.Delete(SettingFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(SettingFile)!);
        File.WriteAllText(SettingFile, folder);
    }

    public static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>How many songs, and how many bytes, are in a folder (complete ones only).</summary>
    public static (int Songs, long Bytes) Measure(string folder)
    {
        if (!Directory.Exists(folder))
            return (0, 0);
        var files = Directory.EnumerateFiles(folder).Where(f => !f.EndsWith(".part", StringComparison.Ordinal)).Select(f => new FileInfo(f)).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>Whether songs can be kept in this folder: made if it isn't there, and written to. Null if so, else why not.</summary>
    public static string? Problem(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".gzjam-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Moves the fetched songs from one folder to another. A song that can't move (a deck has it
    /// open) or that is already there stays where it was. The old folder goes if it ends up empty.
    /// </summary>
    public static CacheMoveResult Move(string from, string to)
    {
        if (Same(from, to) || !Directory.Exists(from))
            return new CacheMoveResult(0, 0, 0);
        Directory.CreateDirectory(to);
        int moved = 0, stayed = 0;
        long bytes = 0;
        foreach (string file in Directory.EnumerateFiles(from).Where(f => !f.EndsWith(".part", StringComparison.Ordinal)).ToList())
        {
            string target = Path.Combine(to, Path.GetFileName(file));
            try
            {
                if (File.Exists(target))
                {
                    stayed++;
                    continue;
                }
                long size = new FileInfo(file).Length;
                File.Move(file, target);
                moved++;
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Couldn't move {Path.GetFileName(file)} to the new download folder", ex);
                stayed++;
            }
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(from).Any())
                Directory.Delete(from);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind, empty or not: harmless.
        }
        return new CacheMoveResult(moved, stayed, bytes);
    }

    /// <summary>"12 songs, 340 MB".</summary>
    public static string Describe(int songs, long bytes)
    {
        if (songs == 0)
            return "No songs fetched yet.";
        double mb = bytes / 1048576.0;
        string size = mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
        return $"{songs:N0} {(songs == 1 ? "song" : "songs")}, {size}";
    }
}
