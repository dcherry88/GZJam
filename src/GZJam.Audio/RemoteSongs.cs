using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ManagedBass;

namespace GZJam.Audio;

/// <summary>Where a download of a remote song is.</summary>
public enum RemoteTransferState
{
    /// <summary>In the download queue, not started.</summary>
    Waiting,
    Downloading,
    /// <summary>The song is on this computer.</summary>
    Done,
    Failed,
    /// <summary>Taken out of the queue, or stopped, by the DJ.</summary>
    Cancelled,
}

/// <summary>A change in a download. <paramref name="Total"/> is 0 until the server says how big the song is.</summary>
public readonly record struct RemoteTransfer(string Path, RemoteTransferState State, long Received = 0, long Total = 0, string? Error = null);

/// <summary>
/// Songs that live on a server rather than in a file (a Navidrome library). A song's "path" is a
/// name that <see cref="Resolver"/> turns into a URL, e.g. <c>navidrome://6AyEHLQ</c>.
///
/// A remote song is downloaded whole, once, into <see cref="CacheDirectory"/>, and from then on
/// it is a file like any other: the decks, the analysis and the close-up waveform all open that
/// copy, so they share one download, can seek and shift tempo freely, and a song played once plays
/// again with the server away. (Playing straight from the URL was tried first: BASS can't seek in
/// a URL stream beyond what it has already downloaded, and each of those three would have
/// downloaded the song again.)
///
/// A song a deck is waiting for is fetched at once (<see cref="Prepare"/>). Songs fetched ahead (a
/// song just queued, a playlist to cache) go through a download queue, two at a time
/// (<see cref="Enqueue"/>), which anything can watch through <see cref="TransferChanged"/>.
/// </summary>
public static partial class RemoteSongs
{
    /// <summary>
    /// Turns a song's path into the URL to download it from, or null if the path isn't a remote
    /// song. Set by the app once a server is configured. The URL carries the login, so it is
    /// never logged or put in an error.
    /// </summary>
    public static Func<string, string?>? Resolver { get; set; }

    /// <summary>Where downloaded songs are kept; null: a folder in the computer's temporary files.</summary>
    public static string? CacheDirectory { get; set; }

    /// <summary>How long a song may take to download before it is given up on.</summary>
    public static TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How many songs the download queue fetches at once.</summary>
    public const int ParallelDownloads = 2;

    /// <summary>
    /// A download started, moved on, finished or was given up on. Raised on whatever thread is doing
    /// the work: a listener that touches a window moves to its thread first.
    /// </summary>
    public static event Action<RemoteTransfer>? TransferChanged;

    private static readonly HttpClient s_http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly ConcurrentDictionary<string, Lazy<string>> s_downloads = new();

    private static void Raise(RemoteTransfer transfer) => TransferChanged?.Invoke(transfer);

    public static bool IsRemote(string path) => Resolver?.Invoke(path) is not null;

    // ───────────── What is here ─────────────

    private static readonly Lock s_indexLock = new();
    private static HashSet<string>? s_index;
    private static string? s_indexFolder;

    /// <summary>
    /// Whether a remote song's copy is here already, so loading it won't wait. Answered from a list
    /// kept in memory (the cache folder is read once, and the list follows the downloads), because a
    /// song list asks about every row it shows.
    /// </summary>
    public static bool IsCached(string path)
    {
        if (!IsRemote(path))
            return false;
        lock (s_indexLock)
            return Index().Contains(Stem(path));
    }

    private static HashSet<string> Index()
    {
        string folder = Folder();
        if (s_index is null || s_indexFolder != folder)
        {
            s_indexFolder = folder;
            s_index = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Where(f => !f.EndsWith(".part", StringComparison.Ordinal))
                    .Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal)
                : [];
        }
        return s_index;
    }

    private static void Remember(string stem, bool here)
    {
        lock (s_indexLock)
        {
            if (here)
                Index().Add(stem);
            else
                Index().Remove(stem);
        }
    }

    // ───────────── Fetching ─────────────

    /// <summary>Downloads the song now if it isn't here yet, ahead of everything queued. Call it off the UI thread.</summary>
    public static void Prepare(string path) => LocalFile(path, CancellationToken.None);

    /// <summary>
    /// The downloaded copy of a remote song, downloaded first if need be. Everyone asking at once
    /// shares one download; a failed one is forgotten, so the next ask tries again.
    /// </summary>
    internal static string LocalFile(string path) => LocalFile(path, CancellationToken.None);

    private static string LocalFile(string path, CancellationToken stop)
    {
        var download = s_downloads.GetOrAdd(path, p => new Lazy<string>(() => Fetch(p, stop)));
        try
        {
            if (File.Exists(download.Value))
                return download.Value;
        }
        catch
        {
            s_downloads.TryRemove(new KeyValuePair<string, Lazy<string>>(path, download));
            throw;
        }
        // The copy was deleted since (the cache folder cleared): fetch it again.
        s_downloads.TryRemove(new KeyValuePair<string, Lazy<string>>(path, download));
        Remember(Stem(path), here: false);
        return LocalFile(path, stop);
    }

    private static string Fetch(string path, CancellationToken stop)
    {
        string url = Resolver?.Invoke(path) ?? throw new AudioException($"'{path}' isn't a song on a server", Errors.Unknown);
        string folder = Folder();
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder on a drive that isn't plugged in, say.
            Raise(new RemoteTransfer(path, RemoteTransferState.Failed, Error: "The folder for downloaded songs isn't available"));
            throw new AudioException("The folder for downloaded songs isn't available (Settings › Navidrome)", Errors.Unknown);
        }
        string stem = Stem(path);

        if (Kept(folder, stem) is { } kept)
        {
            Remember(stem, here: true);
            return kept;   // from before: a new run, or another deck
        }

        using var timeout = new CancellationTokenSource(DownloadTimeout);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stop);
        string partial = Path.Combine(folder, stem + ".part");
        long received = 0, total = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = s_http.Send(request, HttpCompletionOption.ResponseHeadersRead, either.Token);
            if (!response.IsSuccessStatusCode)
                throw new AudioException($"The server answered {(int)response.StatusCode} for the song", Errors.Unknown);
            total = response.Content.Headers.ContentLength ?? 0;
            string target = Path.Combine(folder, stem + Extension(response.Content.Headers.ContentType?.MediaType));
            Raise(new RemoteTransfer(path, RemoteTransferState.Downloading, 0, total));
            var tick = Stopwatch.StartNew();
            using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (Stream body = response.Content.ReadAsStream(either.Token))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
                {
                    either.Token.ThrowIfCancellationRequested();
                    file.Write(buffer, 0, read);
                    received += read;
                    if (tick.ElapsedMilliseconds >= 200)
                    {
                        Raise(new RemoteTransfer(path, RemoteTransferState.Downloading, received, total));
                        tick.Restart();
                    }
                }
            }
            File.Move(partial, target, overwrite: true);
            Remember(stem, here: true);
            Raise(new RemoteTransfer(path, RemoteTransferState.Done, received, received));
            return target;
        }
        catch (AudioException ex)
        {
            Raise(new RemoteTransfer(path, RemoteTransferState.Failed, received, total, ex.Message));
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // The text of these can hold the address, which holds the login: say only what happened.
            try { File.Delete(partial); } catch (IOException) { }
            bool stopped = stop.IsCancellationRequested;
            string why = stopped ? "Stopped" : ex is OperationCanceledException ? "The server took too long to send the song" : "Couldn't get the song from the server";
            Raise(new RemoteTransfer(path, stopped ? RemoteTransferState.Cancelled : RemoteTransferState.Failed, received, total, why));
            throw new AudioException(why, stopped ? Errors.Ended : ex is OperationCanceledException ? Errors.Timeout : Errors.Unknown);
        }
    }

    // ───────────── The download queue ─────────────

    private static readonly Lock s_queueLock = new();
    private static readonly LinkedList<string> s_waiting = new();
    private static readonly HashSet<string> s_waitingSet = new(StringComparer.Ordinal);
    private static int s_workers;
    private static CancellationTokenSource s_stop = new();

    /// <summary>
    /// Adds songs to the download queue, which fetches <see cref="ParallelDownloads"/> at a time.
    /// Songs already here, already waiting, or not from a server are left out. <paramref name="first"/>
    /// puts them ahead of what is waiting (a song just queued for the night comes before a playlist
    /// being cached). A song that couldn't be fetched is tried again when it is loaded.
    /// </summary>
    /// <returns>How many songs were added.</returns>
    public static int Enqueue(IEnumerable<string> paths, bool first = false)
    {
        var added = new List<string>();
        lock (s_queueLock)
        {
            foreach (string path in first ? Enumerable.Reverse(paths) : paths)
            {
                if (!IsRemote(path) || IsCached(path) || !s_waitingSet.Add(path))
                    continue;
                if (first)
                    s_waiting.AddFirst(path);
                else
                    s_waiting.AddLast(path);
                added.Add(path);
            }
            while (s_workers < ParallelDownloads && s_workers < s_waiting.Count)
            {
                s_workers++;
                _ = Task.Run(Work);
            }
        }
        foreach (string path in first ? Enumerable.Reverse(added) : added)
            Raise(new RemoteTransfer(path, RemoteTransferState.Waiting));
        return added.Count;
    }

    /// <summary>Takes everything out of the queue and stops the downloads the queue started. A song a deck is waiting for finishes.</summary>
    public static void StopAll()
    {
        List<string> dropped;
        CancellationTokenSource stopped;
        lock (s_queueLock)
        {
            dropped = [.. s_waiting];
            s_waiting.Clear();
            s_waitingSet.Clear();
            stopped = s_stop;
            s_stop = new CancellationTokenSource();
        }
        stopped.Cancel();
        stopped.Dispose();
        foreach (string path in dropped)
            Raise(new RemoteTransfer(path, RemoteTransferState.Cancelled, Error: "Stopped"));
    }

    private static void Work()
    {
        while (true)
        {
            string path;
            CancellationToken stop;
            lock (s_queueLock)
            {
                if (s_waiting.First is not { } next)
                {
                    s_workers--;
                    return;
                }
                path = next.Value;
                s_waiting.RemoveFirst();
                s_waitingSet.Remove(path);
                stop = s_stop.Token;
            }
            try
            {
                LocalFile(path, stop);
            }
            catch (AudioException)
            {
                // Told to whoever is watching (TransferChanged); said again when the song is loaded.
            }
        }
    }

    private static string Folder() => CacheDirectory ?? Path.Combine(Path.GetTempPath(), "gzjam-remote");

    private static string Stem(string path) => Unsafe().Replace(path, "_");

    /// <summary>A song's complete copy in <paramref name="folder"/>, if there is one (never a .part).</summary>
    private static string? Kept(string folder, string stem) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, stem + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal))
        : null;

    /// <summary>A file extension for what the server says the song is; BASS also goes by what's inside.</summary>
    private static string Extension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" or "application/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/aiff" or "audio/x-aiff" => ".aiff",
        "audio/x-ms-wma" => ".wma",
        _ => ".audio",
    };

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();
}
