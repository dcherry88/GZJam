using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Library;

namespace GZJam.App.ViewModels;

/// <summary>The folder tree, search box and search results.</summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(120);

    private readonly LibraryDatabase _database;
    private readonly LibraryScanner _scanner;
    private readonly LibraryWatcher _watcher = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private CancellationTokenSource? _typingDelay;
    private CancellationTokenSource? _searchCancel;
    private double _nearBpm = LibraryDatabase.DefaultNearBpm;
    private int _searchVersion;
    private bool _suppressSearch;
    private (string RootPath, string Folder)? _pendingSelection;
    private Dictionary<string, List<Track>> _resultsByPath = new(StringComparer.Ordinal);

    public LibraryViewModel(LibraryDatabase database)
    {
        _database = database;
        _scanner = new LibraryScanner(database);
        _watcher.RootChanged += root => Dispatcher.UIThread.Post(() => _ = ScanAsync(root));
    }

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    /// <summary>A scan added, changed or removed tracks.</summary>
    public event Action? Scanned;

    /// <summary>
    /// A scan found songs renamed or moved (old path, new path), within a music folder or to
    /// another one, so what's holding an old path (the queue, a deck) can follow.
    /// </summary>
    public event Action<IReadOnlyList<(string From, string To)>>? SongsMoved;

    [ObservableProperty]
    public partial FolderNodeViewModel? SelectedFolder { get; set; }

    /// <summary>
    /// The folder list folded down to the music folders themselves, so the playlists get the room
    /// (#163). Opening a folder in the tree (or "Show in folder") unfolds it again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FoldTip))]
    public partial bool AreFoldersFolded { get; set; }

    public string FoldTip => AreFoldersFolded ? "Show the folders" : "Fold the folders away, for more playlists";

    [RelayCommand]
    private void ToggleFoldFolders() => AreFoldersFolded = !AreFoldersFolded;

    partial void OnAreFoldersFoldedChanged(bool value)
    {
        var nodes = Roots.SelectMany(r => r.SelfAndDescendants()).ToList();
        if (value)
        {
            foreach (var node in nodes)
                node.IsExpanded = false;
        }
        else if (!nodes.Any(n => n.IsExpanded))
        {
            foreach (var root in Roots)
                root.IsExpanded = true;   // back as it opens the first time
        }
    }

    /// <summary>A folder in the tree was opened: with the folders folded, they unfold.</summary>
    internal void FolderExpanded()
    {
        if (AreFoldersFolded)
            AreFoldersFolded = false;
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Every playlist, alphabetically, under the folder tree.</summary>
    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

    /// <summary>The playlist the song list shows, in its order; null for the library (a folder, or all of it).</summary>
    [ObservableProperty]
    public partial PlaylistInfo? SelectedPlaylist { get; set; }

    public bool IsPlaylistOpen => SelectedPlaylist is not null;

    /// <summary>The open playlist's name, for the bar above the song list ("" if none is open).</summary>
    public string PlaylistName => SelectedPlaylist?.Name ?? "";

    /// <summary>"14 songs · 46:20 · 3 played tonight", or with a search, "3 of 14 songs".</summary>
    public string PlaylistSummary
    {
        get
        {
            if (SelectedPlaylist is not { } playlist)
                return "";
            int count = playlist.Count;
            string songs = count == 1 ? "1 song" : $"{count} songs";
            if (!string.IsNullOrWhiteSpace(SearchText))
                return $"{Results.Count} of {songs} match";
            if (count == 0)
                return "Empty: drag songs onto its name, or right-click songs › Add to playlist";
            int played = Results.Count(t => t.IsPlayedTonight), missing = Results.Count(t => t.IsMissing);
            return $"{songs} · {Format.Duration(playlist.Length)}" + (played > 0 ? $" · {played} played tonight" : "")
                + (missing > 0 ? $" · {missing} missing" : "");
        }
    }

    /// <summary>Rows can be dragged into a new order: a playlist, shown whole and in its own order.</summary>
    public bool CanReorderPlaylist => IsPlaylistOpen && string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// Reads the playlists again (one was made, renamed, deleted or changed), keeping the open one
    /// open. Only the rows that changed are touched, so the list stays where the DJ scrolled it
    /// (#205). The song list is read again only when <paramref name="changed"/> is the open
    /// playlist, so adding to another playlist keeps its search and place.
    /// </summary>
    public void RefreshPlaylists(long? changed = null)
    {
        long? open = SelectedPlaylist?.Id;
        var playlists = _database.GetPlaylists();
        _suppressSearch = true;
        try
        {
            var wanted = playlists.Select(p => p.Id).ToHashSet();
            for (int i = Playlists.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(Playlists[i].Id))
                    Playlists.RemoveAt(i);
            }
            for (int i = 0; i < playlists.Count; i++)
            {
                PlaylistInfo playlist = playlists[i];
                int at = IndexOfPlaylist(playlist.Id, from: i);
                if (at < 0)
                {
                    Playlists.Insert(i, playlist);
                    continue;
                }
                if (at != i)
                    Playlists.Move(at, i);   // renamed: its place in the alphabet changed
                if (Playlists[i] != playlist)
                    Playlists[i] = playlist;
            }
            // A changed row is a new one: the open playlist is selected again, as it is now.
            SelectedPlaylist = playlists.FirstOrDefault(p => p.Id == open);
        }
        finally
        {
            _suppressSearch = false;
        }
        OnPlaylistShown();
        if (open is not null && (changed == open || SelectedPlaylist is null))
            RunSearch();
    }

    private int IndexOfPlaylist(long id, int from)
    {
        for (int i = from; i < Playlists.Count; i++)
        {
            if (Playlists[i].Id == id)
                return i;
        }
        return -1;
    }

    /// <summary>Opens a playlist in the song list (null: back to the library).</summary>
    public void OpenPlaylist(long? id) =>
        SelectedPlaylist = id is null ? null : Playlists.FirstOrDefault(p => p.Id == id);

    partial void OnSelectedPlaylistChanged(PlaylistInfo? value)
    {
        if (!_suppressSearch)
        {
            if (value is not null && SelectedFolder is not null)
            {
                // One or the other: a playlist isn't within a folder.
                _suppressSearch = true;
                SelectedFolder = null;
                _suppressSearch = false;
            }
            RunSearch();
        }
        OnPlaylistShown();
    }

    private void OnPlaylistShown()
    {
        OnPropertyChanged(nameof(IsPlaylistOpen));
        OnPropertyChanged(nameof(PlaylistName));
        OnPropertyChanged(nameof(PlaylistSummary));
        OnPropertyChanged(nameof(CanReorderPlaylist));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultSummary), nameof(EmptyMessage), nameof(HasNoResults))]
    public partial IReadOnlyList<Track> Results { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRoots))]
    public partial bool HasLoaded { get; private set; }

    [ObservableProperty]
    public partial string? ScanStatus { get; private set; }

    [ObservableProperty]
    public partial double ScanPercent { get; private set; }

    public bool HasNoRoots => HasLoaded && Roots.Count == 0;
    public bool HasNoResults => HasLoaded && Roots.Count > 0 && Results.Count == 0;
    public bool HasScope => SelectedFolder is not null || SelectedPlaylist is not null;
    public string ScopeText => SelectedPlaylist is { } p ? $"in playlist {p.Name}"
        : SelectedFolder is { } f ? $"in {f.DisplayPath} · all subfolders" : "Whole library";
    public string ResultSummary => Results.Count == 1 ? "1 track" : $"{Format.Count(Results.Count)} tracks";

    public string EmptyMessage
    {
        get
        {
            if (SelectedPlaylist is { } playlist && string.IsNullOrWhiteSpace(SearchText))
                return $"\"{playlist.Name}\" is empty. Drag songs onto its name, or right-click songs › Add to playlist.";
            string where = SelectedPlaylist is { } p ? $" in {p.Name}" : SelectedFolder is { } f ? $" in {f.Name}" : "";
            return string.IsNullOrWhiteSpace(SearchText) ? $"No tracks{where}." : $"Nothing matches \"{SearchText.Trim()}\"{where}.";
        }
    }

    /// <summary>When tonight's session began, so its songs can be dimmed; null if nothing's been played yet.</summary>
    public Func<DateTime?> TonightSince { get; set; } = () => null;

    partial void OnResultsChanged(IReadOnlyList<Track> value)
    {
        if (TonightSince() is { } since)
        {
            foreach (Track track in value)
                track.IsPlayedTonight = track.LastPlayedUtc >= since;
        }
        _resultsByPath = new Dictionary<string, List<Track>>(value.Count, StringComparer.Ordinal);
        foreach (Track track in value)
        {
            if (_resultsByPath.TryGetValue(track.Path, out var rows))
                rows.Add(track);   // in a playlist twice
            else
                _resultsByPath[track.Path] = [track];
        }
        OnPropertyChanged(nameof(PlaylistSummary));
    }

    /// <summary>The track for a file in the current results, if it's there.</summary>
    public Track? ResultFor(string path) => _resultsByPath.GetValueOrDefault(path)?[0];

    /// <summary>Every row for a file in the current results: more than one in a playlist that has it twice.</summary>
    public IReadOnlyList<Track> ResultsFor(string path) => _resultsByPath.GetValueOrDefault(path) ?? [];

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        // Picking a folder by hand overrides a remembered one that hasn't appeared yet.
        if (!_suppressSearch)
            _pendingSelection = null;
        if (value is not null && SelectedPlaylist is not null)
        {
            // A folder: back from the playlist to the library.
            bool suppressed = _suppressSearch;
            _suppressSearch = true;
            SelectedPlaylist = null;
            _suppressSearch = suppressed;
            OnPlaylistShown();
        }
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(ScopeText));
        if (!_suppressSearch)
            RunSearch();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanReorderPlaylist));
        OnPropertyChanged(nameof(BpmFilter));
        OnPropertyChanged(nameof(BpmFilterLabel));
        OnPropertyChanged(nameof(HasBpmFilter));
        OnPropertyChanged(nameof(LabelFilter));
        OnPropertyChanged(nameof(LabelFilterLabel));
        OnPropertyChanged(nameof(HasLabelFilter));
        OnPropertyChanged(nameof(HasMoreFilters));
        // Debounce typing: each keystroke restarts the delay, so a fast typist triggers one search.
        _typingDelay?.Cancel();
        if (_suppressSearch)
            return;
        IsSearching = true;
        var delay = _typingDelay = new CancellationTokenSource();
        Task.Delay(SearchDelay, delay.Token).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!delay.IsCancellationRequested)
                RunSearch();
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>The BPM filter in the search text (<c>bpm:130-150</c>, <c>~bpm:140</c>), if any.</summary>
    public string? BpmFilter => FindBpmFilters(SearchText).LastOrDefault();

    /// <summary>The BPM button's label: "BPM", or the filter it has set ("BPM 130-150", "BPM ≈140").</summary>
    public string BpmFilterLabel => BpmFilter switch
    {
        null => "BPM",
        var f when f.StartsWith('~') => $"BPM ≈{f[5..]}",
        var f => $"BPM {f[4..]}",
    };

    public bool HasBpmFilter => BpmFilter is not null;

    /// <summary>
    /// Puts a BPM filter in the search text in place of any already there (null: removes it), so
    /// tapping a range and typing one are the same thing.
    /// </summary>
    public void SetBpmFilter(string? filter) => SearchText = ReplaceBpmFilter(SearchText, filter);

    internal static string ReplaceBpmFilter(string text, string? filter) => ReplaceFilter(text, IsBpmFilter, filter);

    /// <summary>Swaps the filters <paramref name="matches"/> picks out of the search text for <paramref name="filter"/> (null: just removes them).</summary>
    private static string ReplaceFilter(string text, Func<string, bool> matches, string? filter)
    {
        var tokens = WithoutFilters(LibraryDatabase.SearchTokens(text).ToList(), matches);
        if (filter is not null)
            tokens.Add(filter);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// The tokens without those <paramref name="matches"/> picks, each taking the <c>OR</c> that
    /// joined it with it, so what's left isn't joined by mistake (<c>rating:yes OR energy:high
    /// basie</c> without energy is <c>rating:yes basie</c>) and no <c>OR</c> is left behind (#245).
    /// </summary>
    private static List<string> WithoutFilters(List<string> tokens, Func<string, bool> matches)
    {
        const string or = LibraryDatabase.OrWord;
        var kept = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!matches(tokens[i]))
            {
                if (tokens[i] != or || (kept.Count > 0 && kept[^1] != or))
                    kept.Add(tokens[i]);
                continue;
            }
            if (kept.Count > 0 && kept[^1] == or)
                kept.RemoveAt(kept.Count - 1);   // "a OR this"
            else if (i + 1 < tokens.Count && tokens[i + 1] == or)
                i++;                             // "this OR b"
        }
        if (kept.Count > 0 && kept[^1] == or)
            kept.RemoveAt(kept.Count - 1);
        return kept;
    }

    // ───────────── Label filter ─────────────

    /// <summary>The label being filtered on ("Lindy"), "none" for songs without labels, or null.</summary>
    public string? LabelFilter => LibraryDatabase.SearchTokens(SearchText).Where(IsLabelFilter)
        .Select(t => t[(t.IndexOf(':') + 1)..].Trim('"')).LastOrDefault(v => v.Length > 0);

    /// <summary>The label button's text: "Label", the label it's filtering on, or "Balboa +1" for either of two (#245).</summary>
    public string LabelFilterLabel => LabelFilter switch
    {
        null => "Label",
        var l when l.Equals("none", StringComparison.OrdinalIgnoreCase) => "No label",
        var l when LibraryDatabase.SearchTokens(SearchText).Count(t => IsLabelFilter(t) && !t.EndsWith(':')) is var n and > 1 => $"{l} +{n - 1}",
        var l => l,
    };

    public bool HasLabelFilter => LabelFilter is not null;

    /// <summary>Filters on a label (quoted if it has spaces), "none", or (null) no label filter.</summary>
    public void SetLabelFilter(string? label) => SearchText = ReplaceLabelFilter(SearchText, label);

    internal static string ReplaceLabelFilter(string text, string? label) =>
        ReplaceFilter(text, IsLabelFilter, label is null ? null : label.Contains(' ') ? $"label:\"{label}\"" : $"label:{label}");

    private static bool IsLabelFilter(string token) =>
        token.StartsWith("label:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("labels:", StringComparison.OrdinalIgnoreCase);

    /// <summary>A label was renamed: a search for it follows the new name (else its results may have changed).</summary>
    public void LabelRenamed(string name, string newName)
    {
        ForgetSuggestionValues();
        if (name.Equals(LabelFilter, StringComparison.OrdinalIgnoreCase))
            SetLabelFilter(newName);
        else
            LabelsChanged();
    }

    /// <summary>How far <c>~bpm:</c> reaches either side, as a fraction (Settings › Library).</summary>
    public double NearBpm
    {
        get => _nearBpm;
        set
        {
            if (value == _nearBpm)
                return;
            _nearBpm = value;
            if (FindBpmFilters(SearchText).Any(f => f.StartsWith('~')))
                RunSearch();   // the near-BPM search reaches further or less far now
        }
    }

    /// <summary>
    /// Songs' tags were edited (#129): the songs change in place in the list, staying where they are
    /// (and selected) until the next search; the suggestions are found again.
    /// </summary>
    public void TagsEdited() => ForgetSuggestionValues();

    /// <summary>Labels were added or taken off: a label filter's results may have changed.</summary>
    public void LabelsChanged()
    {
        ForgetSuggestionValues();
        if (HasLabelFilter)
            RunSearch();
    }

    private static bool IsBpmFilter(string token) =>
        token.StartsWith("bpm:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("~bpm:", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> FindBpmFilters(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsBpmFilter).Select(t => t.ToLowerInvariant());

    [RelayCommand]
    private void ClearScope()
    {
        SelectedPlaylist = null;
        SelectedFolder = null;
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ───────────── Lifecycle ─────────────

    /// <summary>Shows what's already indexed immediately, then rescans every root in the background.</summary>
    public async Task StartAsync(string? selectedRootPath, string? selectedFolder, long? openPlaylist = null)
    {
        RefreshTree();
        RefreshPlaylists();
        if (openPlaylist is not null)
        {
            _suppressSearch = true;
            OpenPlaylist(openPlaylist);
            _suppressSearch = false;
            OnPlaylistShown();
        }
        if (selectedRootPath is not null && SelectedPlaylist is null)
        {
            _pendingSelection = (selectedRootPath, selectedFolder ?? "");
            _suppressSearch = true;
            RestorePendingSelection();
            _suppressSearch = false;
        }
        HasLoaded = true;
        RunSearch("startup");

        foreach (LibraryRoot r in _database.GetRoots())
        {
            _watcher.Watch(r);
            await ScanAsync(r);
        }
    }

    /// <summary>What to remember: the selected folder as (root path, relative folder).</summary>
    public (string? RootPath, string? Folder) GetSelection()
    {
        if (SelectedFolder is not { } f)
            return (null, null);
        string? rootPath = _database.GetRoots().FirstOrDefault(r => r.Id == f.Node.RootId)?.Path;
        return (rootPath, f.Node.RelativePath);
    }

    /// <summary>Syncs with the server whose songs are in the library (a server folder's menu); set by the main view model.</summary>
    public Func<Task>? SyncRemote { get; set; }

    /// <summary>Fetches the songs under a folder (root id, folder, its name) from the server ahead of time; set by the main view model.</summary>
    public Action<long, string, string>? CacheFolder { get; set; }

    /// <summary>The library's folders and songs have changed behind its back (a sync with a server): show them.</summary>
    public void ReloadAfterRemoteSync()
    {
        RefreshTree();
        RestorePendingSelection();
        RunSearch("remote songs synced");
    }

    public async Task AddRootAsync(string path)
    {
        LibraryRoot root = _database.AddRoot(path);
        RefreshTree();
        _watcher.Watch(root);
        await ScanAsync(root);
    }

    public void RemoveRoot(long rootId)
    {
        _watcher.Unwatch(rootId);
        _database.RemoveRoot(rootId);
        RefreshTree();
        RunSearch("folder removed");
    }

    /// <param name="readAllTags">Read every song's tags again, not only changed files (see <see cref="LibraryScanner.ScanAsync"/>).</param>
    public void RescanRoot(long rootId, bool readAllTags = false)
    {
        if (_database.GetRoots().FirstOrDefault(r => r.Id == rootId) is { } root)
            _ = ScanAsync(root, readAllTags);
    }

    private async Task ScanAsync(LibraryRoot root, bool readAllTags = false)
    {
        if (root.IsRemote)
            return;   // a server's songs are synced with it, not scanned (see LibraryDatabase.SyncRemoteTracks)
        await _scanLock.WaitAsync();
        try
        {
            bool firstImport = _database.GetFolderCounts().All(c => c.RootId != root.Id);
            Log.Info($"Scanning {root.Path}" + (firstImport ? " (first time)" : "") + (readAllTags ? ", reading every song's tags again" : ""));
            DateTime lastRefresh = DateTime.UtcNow;
            bool found = false;
            var progress = new Progress<ScanProgress>(p =>
            {
                if (!p.Enumerating && !found)
                {
                    found = true;
                    Log.Info($"Scan of {root.Path}: {Format.Count(p.FilesFound)} songs found, {Format.Count(p.FilesToRead)} to read");
                }
                if (p.Enumerating)
                {
                    ScanStatus = $"Looking for music… {Format.Count(p.FilesFound)}";
                    ScanPercent = 0;
                    return;
                }
                if (p.FilesToRead == 0)
                    return;
                ScanStatus = $"Reading tags… {Format.Count(p.FilesRead)} / {Format.Count(p.FilesToRead)}";
                ScanPercent = 100.0 * p.FilesRead / p.FilesToRead;

                // During a first import, let the library fill in as it goes.
                if (firstImport && DateTime.UtcNow - lastRefresh > TimeSpan.FromSeconds(1.5))
                {
                    lastRefresh = DateTime.UtcNow;
                    RefreshTree();
                    RunSearch("first scan, filling in");
                }
            });

            ScanResult result = await _scanner.ScanAsync(root, progress, readAllTags);
            Log.Info($"Scanned {root.Path} in {result.Elapsed.TotalSeconds:0.0} s: {Format.Count(result.Added)} added, " +
                     $"{Format.Count(result.Updated)} updated, {Format.Count(result.Removed)} removed, {Format.Count(result.Moved)} moved, " +
                     $"{Format.Count(result.Unchanged)} unchanged");
            if (result.ChangedAnything)
            {
                RefreshTree();
                // A remembered folder may only exist once its root has been scanned (e.g. a fresh database).
                RestorePendingSelection();
                RunSearch("scan finished");
                if (result.Moves.Count > 0)
                    SongsMoved?.Invoke(result.Moves);
                Scanned?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't scan {root.Path}", ex);
            ScanStatus = $"Couldn't scan {root.Path}: {ex.Message}";
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            // Never silently: the library may be left showing less than the database holds.
            Log.Error($"Scan of {root.Path} failed", ex);
            ScanStatus = $"Couldn't scan {root.Path}: {ex.Message}";
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        finally
        {
            ScanStatus = null;
            ScanPercent = 0;
            _scanLock.Release();
        }
    }

    // ───────────── Tree ─────────────

    /// <summary>Rebuilds the tree from the database, keeping expansion and selection.</summary>
    private void RefreshTree()
    {
        var expanded = Roots.SelectMany(r => r.SelfAndDescendants()).Where(n => n.IsExpanded).Select(n => n.Key).ToHashSet();
        var selected = SelectedFolder?.Key;
        bool firstBuild = Roots.Count == 0;
        ForgetSuggestionValues();   // the songs, so their artists and genres, may have changed

        var tree = FolderTree.Build(_database.GetRoots(), _database.GetFolderCounts());
        Log.Info($"Library tree: {string.Join("; ", tree.Select(r => $"{r.FullPath}, {Format.Count(r.TrackCount)} songs in {r.Children.Count} folders"))}");
        _suppressSearch = true;
        Roots.Clear();
        foreach (FolderNode node in tree)
        {
            var vm = new FolderNodeViewModel(this, node, null);
            foreach (var n in vm.SelfAndDescendants())
                n.IsExpanded = expanded.Contains(n.Key) || (firstBuild && n.IsRoot && !AreFoldersFolded);
            Roots.Add(vm);
        }
        SelectedFolder = selected is { } key ? Find(key.RootId, key.Folder) : null;
        _suppressSearch = false;
        OnPropertyChanged(nameof(HasNoRoots));
        OnPropertyChanged(nameof(ScopeText));
    }

    private FolderNodeViewModel? Find(long rootId, string folder) =>
        Roots.Where(r => r.Node.RootId == rootId).SelectMany(r => r.SelfAndDescendants()).FirstOrDefault(n => n.Node.RelativePath == folder);

    private void RestorePendingSelection()
    {
        if (_pendingSelection is not { } pending ||
            _database.GetRoots().FirstOrDefault(r => string.Equals(r.Path, pending.RootPath, StringComparison.OrdinalIgnoreCase)) is not { } root ||
            Find(root.Id, pending.Folder) is null)
            return;

        _pendingSelection = null;
        Reveal(root.Id, pending.Folder);
    }

    /// <summary>Selects a folder in the tree, expanding its parents so it's visible.</summary>
    public void Reveal(long rootId, string folder)
    {
        if (Find(rootId, folder) is not { } node)
            return;
        for (var p = node.Parent; p is not null; p = p.Parent)
            p.IsExpanded = true;
        SelectedFolder = node;
    }

    /// <summary>Jumps the tree to a track's own folder and clears the search, showing its album.</summary>
    public void RevealTrack(Track track)
    {
        _suppressSearch = true;
        SearchText = "";
        Reveal(track.RootId, track.Folder);
        _suppressSearch = false;
        RunSearch();
    }

    // ───────────── Search ─────────────

    /// <summary>Searches again, e.g. after History changed what's been played.</summary>
    public void RefreshResults() => RunSearch();

    /// <summary>A search is waiting out the typing delay or running: <see cref="Results"/> is about to change.</summary>
    internal bool IsSearching { get; private set; }

    /// <param name="why">For the log: searches made by GZJam itself (startup, a scan) are logged, typing isn't.</param>
    private void RunSearch(string? why = null)
    {
        IsSearching = true;
        _searchCancel?.Cancel();
        var cancel = _searchCancel = new CancellationTokenSource();
        int version = ++_searchVersion;
        string text = SearchText;
        FolderScope? scope = SelectedPlaylist is null ? SelectedFolder?.Node.Scope : null;
        long? playlist = SelectedPlaylist?.Id;
        double near = NearBpm;

        var started = System.Diagnostics.Stopwatch.StartNew();
        (string Text, int Count)? didYouMean = null;
        Task.Run(() =>
        {
            var found = _database.Search(text, scope, cancel.Token, near, playlist);
            if (found.Count == 0)
                didYouMean = DidYouMeanFor(text, scope, cancel.Token, near, playlist);
            return found;
        }, cancel.Token).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception?.InnerException is not OperationCanceledException)
                Log.Error($"Search \"{text}\" failed", t.Exception);
            if (why is not null)
                Log.Info(t.IsCompletedSuccessfully
                    ? $"Library list ({why}): {Format.Count(t.Result.Count)} songs in {started.ElapsedMilliseconds} ms" +
                      (version == _searchVersion ? "" : ", replaced by a newer search")
                    : $"Library list ({why}): {(t.IsCanceled ? "canceled by a newer search" : "failed")} after {started.ElapsedMilliseconds} ms");
            if (version == _searchVersion)
                Dispatcher.UIThread.Post(() =>
                {
                    if (version != _searchVersion)
                        return;
                    if (t.IsCompletedSuccessfully)
                    {
                        DidYouMean = didYouMean?.Text;
                        DidYouMeanText = didYouMean is var (better, count)
                            ? $"Did you mean {better}?  {(count == 1 ? "1 song" : $"{Format.Count(count)} songs")}"
                            : null;
                        Results = t.Result;
                    }
                    IsSearching = false;
                });
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _typingDelay?.Cancel();
        _searchCancel?.Cancel();
        _watcher.Dispose();
    }
}
