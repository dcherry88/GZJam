using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GZJam.Library;

namespace GZJam.App;

/// <summary>
/// Songs' album art, small. The first time a song's art is wanted, its picture is found
/// (<see cref="AlbumArtFinder"/>) on a low-priority background thread and a small copy is saved
/// in the art folder, one per picture (so one per album, usually); after that only the small copy
/// is ever read, so full-size pictures are never decoded while playing. The pictures on screen
/// are kept in memory.
/// </summary>
public sealed class AlbumArtCache : IDisposable
{
    /// <summary>The small copies' width, in pixels: a deck's picture on a 175% screen, with room to spare.</summary>
    internal const int Size = 128;

    /// <summary>Pictures kept in memory: the decks, the queue and a screenful of library rows, many times over.</summary>
    private const int MemoryLimit = 300;

    private readonly LibraryDatabase _database;
    private readonly string _folder;
    private readonly Lock _lock = new();
    private readonly LinkedList<string> _wanted = [];   // newest first: what's just come on screen
    private readonly Dictionary<string, LinkedListNode<string>> _wantedNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _keys = new(StringComparer.Ordinal);   // path → key; null: no art
    private readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bitmap)>> _bitmaps = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, Bitmap Bitmap)> _recent = [];   // most recently shown first
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;

    public AlbumArtCache(LibraryDatabase database, string folder)
    {
        _database = database;
        _folder = folder;
        // Below normal: the audio thread and the UI always come first.
        _worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Album art" };
        _worker.Start();
    }

    /// <summary>Raised on the UI thread when a song's art has been found (path); <see cref="Get"/> has it now.</summary>
    public event Action<string>? Found;

    /// <summary>
    /// A song's art if it's ready: null if it has none or isn't ready yet. When it isn't ready,
    /// it's looked for ahead of everything else, and <see cref="Found"/> says when it is.
    /// </summary>
    public Bitmap? Get(string path)
    {
        lock (_lock)
        {
            if (_keys.TryGetValue(path, out string? key))
            {
                if (key is null)
                    return null;
                if (_bitmaps.TryGetValue(key, out var node))
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    return node.Value.Bitmap;
                }
            }
            if (_wantedNodes.Remove(path, out var waiting))
                _wanted.Remove(waiting);
            _wantedNodes[path] = _wanted.AddFirst(path);
        }
        _available.Release();
        return null;
    }

    private void Work()
    {
        while (true)
        {
            try
            {
                _available.Wait(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            string path;
            lock (_lock)
            {
                if (_wanted.First is not { } first)
                    continue;
                path = first.Value;
                _wanted.RemoveFirst();
                _wantedNodes.Remove(path);
            }
            try
            {
                (string? key, Bitmap? bitmap) = Load(path);
                lock (_lock)
                {
                    _keys[path] = key;
                    if (key is not null && bitmap is not null && !_bitmaps.ContainsKey(key))
                    {
                        _bitmaps[key] = _recent.AddFirst((key, bitmap));
                        // Not disposed: one may still be on screen. They're small, and collected.
                        while (_recent.Count > MemoryLimit && _recent.Last is { } oldest)
                        {
                            _recent.RemoveLast();
                            _bitmaps.Remove(oldest.Value.Key);
                        }
                    }
                }
                Dispatcher.UIThread.Post(() => Found?.Invoke(path));
            }
            catch (Exception ex)
            {
                Log.Error($"Album art for {path}", ex);
                lock (_lock)
                    _keys[path] = null;
            }
        }
    }

    /// <summary>A song's art: its key and the small copy (from the art folder, made first if need be).</summary>
    private (string? Key, Bitmap? Bitmap) Load(string path)
    {
        if (Navidrome.NavidromeClient.IsNavidromePath(path))
            return (null, null);   // no file to take a picture from (a later step: the server's cover art)
        if (FileVersion.Of(path) is not { } version)
            return (null, null);
        long folderTicks = FolderTicks(path);
        if (_database.GetArt(path, version, folderTicks) is { } stored)
        {
            if (stored.Key is null)
                return (null, null);
            if (ReadSmallCopy(stored.Key) is { } bitmap)
                return (stored.Key, bitmap);
        }

        string? key = null;
        Bitmap? made = null;
        if (AlbumArtFinder.Find(path) is { } source)
        {
            key = Convert.ToHexStringLower(SHA256.HashData(source.Data))[..24];
            made = ReadSmallCopy(key) ?? MakeSmallCopy(key, source.Data);
            if (made is null)
                key = null;   // a picture that can't be decoded: as good as none
        }
        _database.SaveArt(path, version, folderTicks, new StoredArt(key));
        return (key, made);
    }

    private string SmallCopyPath(string key) => Path.Combine(_folder, key + ".png");

    private Bitmap? ReadSmallCopy(string key)
    {
        string file = SmallCopyPath(key);
        if (!File.Exists(file))
            return null;
        try
        {
            return new Bitmap(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private Bitmap? MakeSmallCopy(string key, byte[] picture)
    {
        Bitmap small;
        try
        {
            using var stream = new MemoryStream(picture);
            small = Bitmap.DecodeToWidth(stream, Size, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            return null;   // not a picture Skia can read: whatever it throws, the song has no art
        }
        try
        {
            Directory.CreateDirectory(_folder);
            // Write then swap, so a crash never leaves half a picture.
            string file = SmallCopyPath(key);
            string temp = file + ".tmp";
            small.Save(temp, new PngBitmapEncoderOptions());
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Shown anyway; made again next time.
        }
        return small;
    }

    /// <summary>When the song's folder last changed (e.g. a cover.jpg added), so the art is looked for again.</summary>
    private static long FolderTicks(string path) =>
        Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder).Ticks : 0;

    public void Dispose()
    {
        _stop.Cancel();
        _worker.Join(TimeSpan.FromSeconds(2));
    }
}
