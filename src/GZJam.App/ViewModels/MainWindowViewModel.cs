using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;
using GZJam.Library;

namespace GZJam.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly LibraryDatabase _database;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _playbackTimer;

    /// <param name="audio">BASS, or null if it failed to load (the library still works).</param>
    /// <param name="mix">The mixer; tests pass an offline one.</param>
    /// <param name="audioError">Why audio isn't available, shown in the window.</param>
    public MainWindowViewModel(LibraryDatabase database, AppSettings settings,
        AudioEngine? audio = null, MixEngine? mix = null, string? audioError = null)
    {
        _settings = settings;
        _database = database;
        Audio = audio;
        Mix = mix;
        AudioError = audioError;
        Library = new LibraryViewModel(database) { NearBpm = settings.NearBpmPercent / 100, AreFoldersFolded = settings.FoldersFolded };
        Art = new AlbumArtCache(database, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database.DatabasePath))!, "art"));
        Queue = new QueueViewModel();
        History = new HistoryViewModel(database);
        Library.TonightSince = () => History.CurrentSessionStartUtc;
        History.SessionDeleted += wasTonight =>
        {
            // Played dates and counts come from History: show them without the deleted session.
            Library.RefreshResults();
            if (wasTonight)
            {
                foreach (Track track in Queue.Items.Select(i => i.Track).Concat(Decks.Select(d => d.Track)).OfType<Track>())
                    track.IsPlayedTonight = false;
            }
        };
        if (audio is not null)
        {
            audio.ExtraBuffering = settings.ExtraBuffering;
            Analysis = new TrackAnalysisService(database, audio);
            Analysis.Analyzed += OnAnalyzed;
            Analysis.PropertyChanged += (_, _) => BackgroundChanged();
            Library.Scanned += Analysis.QueueLibrary;
            Queue.Items.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is not null)
                {
                    Analysis.Prioritize(e.NewItems.Cast<QueueItem>().Select(i => i.Track));
                    // Unless Settings › Servers says songs are only downloaded when asked for, songs on a server
                    // download as they are queued, ahead of any playlist being cached, so a deck finds them here.
                    RemoteSongs.Enqueue(e.NewItems.Cast<QueueItem>().Select(i => i.Track.Path), first: true, requested: false);
                }
            };
        }
        Library.SongsMoved += OnSongsMoved;
        Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LibraryViewModel.ScanStatus) or nameof(LibraryViewModel.ScanPercent))
                BackgroundChanged();
            else if (e.PropertyName == nameof(LibraryViewModel.AreFoldersFolded))
                settings.FoldersFolded = Library.AreFoldersFolded;
        };

        if (mix is not null)
        {
            mix.ExclusiveSpeakers = settings.ExclusiveSpeakers;
            mix.MainOutputLost += (_, _) => OnMainOutputLost();
            mix.OutputUnplugged += (_, output) => OnTableOutputChanged(output, unplugged: true);
            mix.OutputBack += (_, output) => OnTableOutputChanged(output, unplugged: false);
            mix.MainDropout += (_, _) => Log.Info(
                $"Speakers: the sound buffer ran dry (a dropout may have been heard){(settings.ExtraBuffering ? "" : "; Settings › Sound › Extra protection against crackles may help")}");
            Decks = mix.Decks.Select(d => new DeckViewModel(this, d, settings)).ToList();
            _deckCount = settings.DeckCount == 4 && Decks.Count >= 4 ? 4 : Math.Min(2, Decks.Count);
            Mixer = new MixerViewModel(this, mix, settings);
            AirLine = new AirLineViewModel(this, mix, Queue);
            Preview = new PreviewViewModel(this, mix.Preview);
            AutoPlay = new AutoPlayViewModel(this, mix, Queue, settings);
            foreach (DeckViewModel deck in Decks)
            {
                deck.PlayedToRoom += (d, firstOnAir) =>
                {
                    History.Record(d.Track!, firstOnAir, d.Label, d.TempoPercent, d.Deck.Length);
                    MarkPlayed(d.Track!.Path, firstOnAir);
                };
                deck.Deck.Finished += (_, _) =>
                {
                    if (deck.Deck.IsInRoom)
                    {
                        Log.Info($"Deck {deck.Label}: finished \"{deck.Title}\"");
                        deck.MarkFinished();
                        AutoPlay.OnDeckFinished(deck);
                    }
                    else
                    {
                        // Ran out in the headphones only: the room hasn't heard it, so it's still
                        // the next song. Rewind it, ready to play.
                        Log.Info($"Deck {deck.Label}: \"{deck.Title}\" ended in the headphones; rewound");
                        deck.Rewind();
                    }
                };
                deck.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DeckViewModel.Track))
                        AutoPlay.Refresh();
                };
            }
        }
        Settings = new SettingsViewModel(this, settings);
        Room = new RoomControls(this);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.FadeOutSeconds))
                foreach (DeckViewModel deck in Decks)
                    deck.ApplyEndFade();
        };
        RemoteSongs.AutoFetch = settings.RemoteFetch;
        Library.SyncRemote = SyncServersAsync;
        Library.CacheFolder = CacheFolder;
        Downloads = new DownloadsViewModel(this);
        Downloads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DownloadsViewModel.Status) or nameof(DownloadsViewModel.Percent))
                BackgroundChanged();
        };
        ServerConnections.Changed += OnServersChanged;

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock());
        _playbackTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        UpdateClock();
    }

    public LibraryViewModel Library { get; }
    public QueueViewModel Queue { get; }
    public HistoryViewModel History { get; }

    /// <summary>Waveforms, silence trimming and BPM detection; null without audio.</summary>
    public TrackAnalysisService? Analysis { get; }

    /// <summary>Songs' album art, for the decks, queue and library.</summary>
    public AlbumArtCache Art { get; }

    /// <summary>What's happening in the background (copying songs out, scanning, then analyzing), for the top bar.</summary>
    public string? BackgroundStatus => ExportStatus ?? Library.ScanStatus ?? Downloads?.Status ?? Analysis?.Status;
    public double BackgroundPercent => ExportStatus is not null ? ExportPercent
        : Library.ScanStatus is not null ? Library.ScanPercent
        : Downloads?.Status is not null ? Downloads.Percent : Analysis?.Percent ?? 0;

    /// <summary>What the background work is and why, for the top bar status's tooltip (#139).</summary>
    public string? BackgroundTip => _export is { } export
        ? $"Copying the songs to {export.Folder}. Your own files aren't changed."
        : Library.ScanStatus is not null
        ? "Reading the tags (title, artist, BPM…) of the songs in your music folders. Your files aren't changed."
        : Downloads?.Status is not null
        ? "Songs coming down from the music servers, to play with no network. The Downloads button shows the list."
        : Analysis?.StatusTip;

    private void BackgroundChanged()
    {
        OnPropertyChanged(nameof(BackgroundStatus));
        OnPropertyChanged(nameof(BackgroundPercent));
        OnPropertyChanged(nameof(BackgroundTip));
    }

    /// <summary>
    /// Songs renamed or moved on disk (#87): the queue and the decks follow them to their new
    /// paths, so they load (or load again) from there. A deck that's playing plays on.
    /// </summary>
    private void OnSongsMoved(IReadOnlyList<(string From, string To)> moves)
    {
        var to = moves.GroupBy(m => m.From, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().To, StringComparer.Ordinal);
        var moved = new Dictionary<string, Track>(StringComparer.Ordinal);
        Track Moved(Track old)
        {
            if (!moved.TryGetValue(old.Path, out Track? track))
            {
                track = _database.TrackForFile(to[old.Path]);
                track.IsPlayedTonight = old.IsPlayedTonight;
                moved[old.Path] = track;
            }
            return track;
        }

        for (int i = 0; i < Queue.Items.Count; i++)
        {
            if (to.ContainsKey(Queue.Items[i].Track.Path))
                Queue.Items[i] = new QueueItem(Moved(Queue.Items[i].Track));
        }
        foreach (DeckViewModel deck in Decks)
        {
            if (deck.Track is { } track && to.ContainsKey(track.Path))
                deck.FollowMove(Moved(track));
        }
        foreach (var (from, track) in moved)
            Log.Info($"Moved on disk, followed in the queue and decks: {from} -> {track.Path}");
    }

    /// <summary>Every copy of a file's track on screen: in the library results, the queue and on the decks.</summary>
    private IEnumerable<Track> TracksFor(string path) =>
        Library.ResultsFor(path)
            .Concat(Queue.Items.Select(i => i.Track))
            .Concat(Decks.Select(d => d.Track))
            .OfType<Track>()
            .Where(t => t.Path == path)
            .Distinct();

    /// <summary>The room heard a song: wherever it's on screen, it's now "played tonight".</summary>
    private void MarkPlayed(string path, DateTime playedUtc)
    {
        foreach (Track track in TracksFor(path))
        {
            track.LastPlayedUtc = playedUtc;
            track.PlayCount++;
            track.IsPlayedTonight = true;
        }
    }

    /// <summary>
    /// A track's analysis is in: show its detected BPM wherever the track is on screen, and its
    /// length where the tags gave none (#220).
    /// </summary>
    private void OnAnalyzed(string path, TrackAnalysis analysis)
    {
        foreach (Track track in TracksFor(path))
        {
            track.DetectedBpm = analysis.Bpm;
            if (track.Duration == TimeSpan.Zero)
                track.Duration = analysis.Length;
        }
    }

    /// <summary>
    /// The ranges offered when a tempo was detected wrongly. Each is under double, so there's
    /// only one choice between half and double time; neighbors overlap.
    /// </summary>
    public static IReadOnlyList<(BpmRange Range, string Label)> BpmRanges { get; } =
    [
        (new BpmRange(60, 110), "Slow (60–110)"),
        (new BpmRange(100, 160), "Medium (100–160)"),
        (new BpmRange(140, 220), "Up-tempo (140–220)"),
        (new BpmRange(200, 320), "Fast (200–320)"),
    ];

    /// <summary>
    /// Detects tracks' BPMs again within a range the DJ chose (null: back to automatic), and
    /// remembers the choice. With a range, the detected BPM is shown even if the tag has one.
    /// </summary>
    public async Task DetectBpmAsync(IReadOnlyList<Track> tracks, BpmRange? range)
    {
        if (Analysis is null || tracks.Count == 0)
            return;
        if (tracks.Any(t => t.ManualBpm is not null))
            SetManualBpm(tracks, null);   // detection again: forget a typed BPM
        var paths = tracks.Select(t => t.Path).Distinct().ToList();
        foreach (string path in paths)
        {
            _database.SetBpmRange(path, range);
            foreach (Track track in TracksFor(path).Concat(tracks.Where(t => t.Path == path)))
                track.BpmRange = range;
        }
        string what = paths.Count == 1 ? $"\"{tracks[0].DisplayTitle}\"" : $"{paths.Count} tracks";
        Log.Info($"BPM range for {what}: {range?.ToString() ?? "automatic"}");
        if (paths.Count > 1)
            ShowNotice($"Detecting the BPM of {what}…");

        var results = await Task.WhenAll(paths.Select(Analysis.RedoAsync));
        if (paths.Count == 1 && results[0]?.Bpm is { } bpm && tracks[0].BpmRange == range)
            ShowNotice($"{what}: {Format.Bpm(tracks[0].Bpm ?? bpm)} BPM");
        else if (paths.Count > 1)
            ShowNotice($"BPM detected again for {what}.");
    }

    /// <summary>
    /// Sets tracks' BPM by hand (when detection and the tag are both wrong), or with null goes
    /// back to the tag and detection. Saved in GZJam's database, never in the files.
    /// </summary>
    public void SetManualBpm(IReadOnlyList<Track> tracks, double? bpm)
    {
        var paths = tracks.Select(t => t.Path).Distinct().ToList();
        if (paths.Count == 0)
            return;
        _database.SetManualBpm(paths, bpm);
        foreach (string path in paths)
        {
            foreach (Track track in TracksFor(path).Concat(tracks.Where(t => t.Path == path)))
                track.ManualBpm = bpm;
            foreach (DeckViewModel deck in Decks.Where(d => d.Track?.Path == path))
                deck.TrackBpmChanged();
        }
        string what = paths.Count == 1 ? $"\"{tracks[0].DisplayTitle}\"" : $"{paths.Count} tracks";
        Log.Info($"BPM for {what}: {(bpm is { } b ? Format.Bpm(b) + " (typed)" : "automatic")}");
    }

    /// <summary>Settings › Import from Mixxx; afterward, the library, labels and History show what came over.</summary>
    public MixxxImportViewModel CreateMixxxImport() => new(_database, _ =>
    {
        Library.LabelsChanged();
        Library.RefreshPlaylists();
        Library.RefreshResults();
        History.Reload();
    });

    /// <summary>Where the DJ set a file to start and end.</summary>
    internal ManualTrim GetTrim(string path) => _database.GetTrim(path);

    internal void SaveTrim(string path, ManualTrim trim) => _database.SetTrim(path, trim);

    /// <summary>The jumps the DJ set in a file.</summary>
    internal IReadOnlyList<SongJump> GetJumps(string path) => _database.GetJumps(path);

    internal void SaveJumps(string path, IReadOnlyList<SongJump> jumps) => _database.SetJumps(path, jumps);

    /// <summary>The cues the DJ marked in a file.</summary>
    internal IReadOnlyList<SongCue> GetCues(string path) => _database.GetCues(path);

    internal void SaveCues(string path, IReadOnlyList<SongCue> cues) => _database.SetCues(path, cues);

    /// <summary>The EQ the DJ set for a file.</summary>
    internal TrackEq GetEq(string path) => _database.GetEq(path);

    /// <summary>
    /// Saves a song's EQ, and has everywhere playing it follow (#137): a deck holding it (other than
    /// <paramref name="from"/>, where it was changed) and the headphone preview.
    /// </summary>
    internal void SaveEq(string path, TrackEq eq, object? from = null)
    {
        _database.SetEq(path, eq);
        foreach (DeckViewModel deck in Decks)
        {
            if (deck != from && deck.Track?.Path == path)
                deck.EqChangedElsewhere(eq);
        }
        Preview?.EqChanged(path, eq);
    }

    /// <summary>
    /// The EQ panel for a song that isn't on a deck (#137): picked in the library, the queue, a
    /// playlist or History, heard in the headphone preview, and used when the song is loaded.
    /// </summary>
    public EqEditor EqFor(Track track)
    {
        var (lufs, peakDb) = GetLoudness(track.Path);
        EqEditor? editor = null;
        editor = new EqEditor(() => "EQ", () => track.DisplayTitle, () => LevelText(lufs, peakDb), _ => { },
            eq => SaveEq(track.Path, eq, editor))
        {
            PreviewCommand = new RelayCommand(() => Preview?.Toggle(track)),
        };
        editor.Set(GetEq(track.Path), save: false);
        return editor;
    }

    // ───────────── Loudness matching ─────────────

    /// <summary>A song's measured loudness and peak, from its analysis (null until it's measured).</summary>
    internal (double? Lufs, double? PeakDb) GetLoudness(string path)
    {
        if (FileVersion.Of(path) is not { } version || _database.GetAnalysis(path, version) is not { } analysis)
            return (null, null);
        return (analysis.Lufs, analysis.PeakDb);
    }

    /// <summary>What loudness matching does to a song: 0 if it's off or the song isn't measured yet.</summary>
    internal double MatchDb(double? lufs, double? peakDb) =>
        _settings.MatchLoudness && lufs is { } measured ? LoudnessMatch.GainDb(measured, peakDb, _settings.LoudnessTarget) : 0;

    /// <summary>"−14.2 LUFS · matching −2 dB", for the EQ panel.</summary>
    internal string LevelText(double? lufs, double? peakDb, bool measuring = false)
    {
        if (lufs is not { } measured)
            return measuring ? "Measuring loudness…" : "Loudness not measured";
        string text = string.Create(CultureInfo.CurrentCulture, $"{measured:0.0} LUFS").Replace('-', '−');
        return _settings.MatchLoudness
            ? $"{text} · matching {EqEditor.Db(MatchDb(lufs, peakDb))}"
            : $"{text} · loudness matching off";
    }

    // ───────────── Ratings and energy ─────────────

    /// <summary>Rates tracks (saved in the library, never in the files) wherever they are on screen.</summary>
    public void SetRating(IReadOnlyList<Track> tracks, Rating rating)
    {
        var paths = tracks.Select(t => t.Path).Distinct().ToList();
        _database.SetRating(paths, rating);
        foreach (string path in paths)
            foreach (Track track in TracksFor(path).Concat(tracks.Where(t => t.Path == path)))
                track.Rating = rating;
        Log.Info($"Rating for {Describe(tracks, paths)}: {rating}");
    }

    /// <summary>Marks tracks' energy wherever they are on screen.</summary>
    public void SetEnergy(IReadOnlyList<Track> tracks, Energy energy)
    {
        var paths = tracks.Select(t => t.Path).Distinct().ToList();
        _database.SetEnergy(paths, energy);
        foreach (string path in paths)
            foreach (Track track in TracksFor(path).Concat(tracks.Where(t => t.Path == path)))
                track.Energy = energy;
        Log.Info($"Energy for {Describe(tracks, paths)}: {energy}");
    }

    // ───────────── Labels ─────────────

    /// <summary>Every label, with how many songs carry it.</summary>
    public IReadOnlyList<LabelInfo> GetLabels() => _database.GetLabels();

    /// <summary>Puts a label on tracks (creating it if new) or takes it off, wherever they are on screen.</summary>
    public void SetLabel(IReadOnlyList<Track> tracks, string name, bool on)
    {
        var paths = tracks.Select(t => t.Path).Distinct().ToList();
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(name))
            return;
        if (on)
            name = _database.AddLabel(paths, name);
        else
            _database.RemoveLabel(paths, name);
        foreach (string path in paths)
        {
            var labels = _database.GetLabels(path);
            foreach (Track track in TracksFor(path).Concat(tracks.Where(t => t.Path == path)))
                track.Labels = labels;
        }
        Log.Info($"Label \"{name}\" {(on ? "on" : "off")} {Describe(tracks, paths)}");
        Library.LabelsChanged();
    }

    /// <summary>
    /// Renames a label on every song (or, if another label already has the name, makes the two
    /// one), wherever the songs are on screen. A search for the label follows it.
    /// </summary>
    public void RenameLabel(string name, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == name)
            return;
        bool merging = _database.GetLabels().Any(l => l.Name.Equals(newName.Trim(), StringComparison.OrdinalIgnoreCase)
            && !l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (_database.RenameLabel(name, newName) is not { } stored)
            return;
        var onScreen = Library.Results.Concat(Queue.Items.Select(i => i.Track)).Concat(Decks.Select(d => d.Track)).OfType<Track>()
            .Where(t => t.Labels.Contains(name, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
        foreach (var group in onScreen.GroupBy(t => t.Path))
        {
            var labels = _database.GetLabels(group.Key);
            foreach (Track track in group)
                track.Labels = labels;
        }
        Log.Info($"Label \"{name}\" renamed \"{stored}\"{(merging ? ", joining the label already called that" : "")}");
        if (merging)
            ShowNotice($"\"{name}\" and \"{stored}\" are one label now.");
        Library.LabelRenamed(name, stored);
    }

    // ───────────── Playlists ─────────────

    /// <summary>A new, empty playlist (a taken name gets a number). Null if the name is empty.</summary>
    public PlaylistInfo? CreatePlaylist(string name, IReadOnlyList<Track>? songs = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        PlaylistInfo playlist = _database.CreatePlaylist(name);
        if (songs is { Count: > 0 })
            _database.AddToPlaylist(playlist.Id, songs.Select(t => t.Path));
        Log.Info($"Playlist \"{playlist.Name}\" made{(songs is { Count: > 0 } ? $" with {songs.Count} songs" : "")}");
        Library.RefreshPlaylists();
        return playlist;
    }

    /// <summary>Asks for a name, then makes a playlist (of <paramref name="songs"/>, if any) and opens it.</summary>
    public async Task<PlaylistInfo?> CreatePlaylistAsync(IReadOnlyList<Track>? songs = null, string suggestion = "", bool open = true)
    {
        if (PromptAsync is null)
            return null;
        string message = songs is not { Count: > 0 } ? "A set to plan before the night, in order."
            : songs.Count == 1 ? $"With \"{songs[0].DisplayTitle}\"" : $"With {songs.Count} songs";
        if (await PromptAsync("New playlist", message, "Make it", suggestion) is not { } name || CreatePlaylist(name, songs) is not { } playlist)
            return null;
        if (open)
            Library.OpenPlaylist(playlist.Id);
        return playlist;
    }

    /// <summary>Adds songs to a playlist: at the end, or before its entry number <paramref name="index"/> (from 0).</summary>
    public void AddToPlaylist(PlaylistInfo playlist, IReadOnlyList<Track> songs, int? index = null)
    {
        if (songs.Count == 0)
            return;
        var before = _database.GetPlaylistEntries(playlist.Id);
        _database.AddToPlaylist(playlist.Id, songs.Select(t => t.Path), index);
        Log.Info($"Added {(songs.Count == 1 ? $"\"{songs[0].DisplayTitle}\"" : $"{songs.Count} songs")} to playlist \"{playlist.Name}\"");
        Library.RefreshPlaylists(playlist.Id);

        // Already in it (#136): still added, but said, and Undo takes the new copies out again.
        var earlier = FirstPositions(before.Select(e => e.Path));
        var oldIds = before.Select(e => e.EntryId).ToHashSet();
        var repeats = _database.GetPlaylistEntries(playlist.Id).Where(e => !oldIds.Contains(e.EntryId) && earlier.ContainsKey(e.Path)).ToList();
        if (repeats.Count > 0)
        {
            Track first = songs.First(t => t.Path == repeats[0].Path);
            ShowNotice(AlreadyIn(playlist.Name, repeats.Count, songs.Count, first, earlier[first.Path]), 6, undo: () =>
            {
                _database.RemoveFromPlaylist(playlist.Id, repeats.Select(e => e.EntryId));
                Log.Info($"Took {repeats.Count} repeated songs back out of playlist \"{playlist.Name}\"");
                Library.RefreshPlaylists(playlist.Id);
            });
        }
        else if (Library.SelectedPlaylist?.Id != playlist.Id)
        {
            ShowNotice(songs.Count == 1 ? $"Added to {playlist.Name}" : $"{songs.Count} songs added to {playlist.Name}");
        }
    }

    /// <summary>
    /// Adds songs to the queue (before row <paramref name="index"/>, or at the end). Songs in it
    /// already are still added, but a quiet notice says so, with an Undo key that takes the new
    /// copies out again (#136): it's easy to add one twice while building a set.
    /// </summary>
    public void AddToQueue(IReadOnlyList<Track> tracks, int? index = null)
    {
        if (tracks.Count == 0)
            return;
        // Nothing is downloaded by itself: a song from a server that is not on this computer yet
        // can't be queued, so the room never waits on the network.
        if (_settings.RemoteFetch == RemoteFetchMode.Never && tracks.Where(t => RemoteSongs.IsRemote(t.Path) && !RemoteSongs.IsCached(t.Path)).ToList() is { Count: > 0 } missing)
        {
            tracks = [.. tracks.Except(missing)];
            ShowNotice(missing.Count == 1
                ? $"\"{missing[0].DisplayTitle}\" is not on this computer yet. Right-click it > Cache for offline play first."
                : $"{missing.Count} of these songs are not on this computer yet. Right-click them > Cache for offline play first.", seconds: 8);
            if (tracks.Count == 0)
                return;
        }
        var earlier = FirstPositions(Queue.Items.Select(i => i.Track.Path));
        var repeats = Queue.Add(tracks, index).Where(i => earlier.ContainsKey(i.Track.Path)).ToList();
        if (repeats.Count > 0)
            NoticeRepeatsInQueue(repeats, tracks.Count, earlier);
    }

    private void NoticeRepeatsInQueue(IReadOnlyList<QueueItem> repeats, int added, Dictionary<string, int> earlier)
    {
        Track first = repeats[0].Track;
        ShowNotice(AlreadyIn("the queue", repeats.Count, added, first, earlier[first.Path]), 6, undo: () =>
        {
            Queue.Remove(repeats);
            Log.Info($"Took {repeats.Count} repeated songs back out of the queue");
        });
    }

    /// <summary>Each file's first place in a list, from 1.</summary>
    private static Dictionary<string, int> FirstPositions(IEnumerable<string> paths)
    {
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        int n = 0;
        foreach (string path in paths)
            first.TryAdd(path, ++n);
        return first;
    }

    /// <summary>"\"Shiny Stockings\" is in the queue already (5th)", or "3 of these songs are in Tuesday already".</summary>
    internal static string AlreadyIn(string where, int repeats, int added, Track first, int position) =>
        repeats == 1 ? $"\"{first.DisplayTitle}\" is in {where} already ({Format.Ordinal(position)})"
        : repeats == added ? $"These {Format.Count(repeats)} songs are in {where} already"
        : $"{Format.Count(repeats)} of these songs are in {where} already";

    /// <summary>Takes rows of the open playlist out of it. The files are untouched.</summary>
    public void RemoveFromPlaylist(IReadOnlyList<Track> rows)
    {
        if (Library.SelectedPlaylist is not { } playlist || rows.Select(t => t.PlaylistEntryId).OfType<long>().ToList() is not { Count: > 0 } entries)
            return;
        _database.RemoveFromPlaylist(playlist.Id, entries);
        Log.Info($"Removed {entries.Count} from playlist \"{playlist.Name}\"");
        Library.RefreshPlaylists(playlist.Id);
    }

    /// <summary>Moves rows of the open playlist to before its entry number <paramref name="index"/> (from 0).</summary>
    public void MoveInPlaylist(IReadOnlyList<Track> rows, int index)
    {
        if (Library.SelectedPlaylist is not { } playlist || rows.Select(t => t.PlaylistEntryId).OfType<long>().ToList() is not { Count: > 0 } entries)
            return;
        _database.MovePlaylistEntries(playlist.Id, entries, index);
        Library.RefreshPlaylists(playlist.Id);
    }

    public async Task RenamePlaylistAsync(PlaylistInfo playlist)
    {
        if (PromptAsync is null || await PromptAsync($"Rename \"{playlist.Name}\"", $"{playlist.Count} {(playlist.Count == 1 ? "song" : "songs")}",
                "Rename", playlist.Name) is not { } name || string.IsNullOrWhiteSpace(name))
            return;
        string stored = _database.RenamePlaylist(playlist.Id, name);
        Log.Info($"Playlist \"{playlist.Name}\" renamed \"{stored}\"");
        Library.RefreshPlaylists();
    }

    /// <summary>Deletes a playlist, after asking. Its songs stay in the library.</summary>
    public async Task DeletePlaylistAsync(PlaylistInfo playlist)
    {
        if (playlist.Count > 0 && (ConfirmAsync is null || !await ConfirmAsync($"Delete the playlist \"{playlist.Name}\"?",
                $"Its {playlist.Count} {(playlist.Count == 1 ? "song stays" : "songs stay")} in your library; only the list goes.", "Delete")))
            return;
        if (Library.SelectedPlaylist?.Id == playlist.Id)
            Library.OpenPlaylist(null);
        _database.DeletePlaylist(playlist.Id);
        Log.Info($"Playlist \"{playlist.Name}\" deleted");
        Library.RefreshPlaylists();
    }

    /// <summary>
    /// Adds a playlist's songs to the end of the queue (from row <paramref name="from"/> on, if
    /// given), never replacing what's there; in a random order if <paramref name="shuffled"/>
    /// (#254). Songs no longer in the library are skipped.
    /// </summary>
    /// <summary>A playlist's songs for the queue, from <paramref name="from"/> on, shuffled if asked.</summary>
    private List<Track> PlaylistSongs(PlaylistInfo playlist, Track? from, bool shuffled)
    {
        // A song whose file is missing stays behind: it couldn't play (#237).
        var songs = _database.Search(null, playlist: playlist.Id).Where(t => !t.IsMissing).ToList();
        if (from?.PlaylistNumber is { } number)
            songs = songs.Where(t => t.PlaylistNumber >= number).ToList();
        // The library's rows where they're showing, so marks and "played tonight" stay in step.
        var shown = songs.Select(t => Library.ResultsFor(t.Path).FirstOrDefault(r => r.PlaylistEntryId == t.PlaylistEntryId) ?? t).ToList();
        if (TonightSince() is { } since)
        {
            foreach (Track track in shown)
                track.IsPlayedTonight = track.LastPlayedUtc >= since;
        }
        if (shuffled)
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shown));
        return shown;
    }

    public void AddPlaylistToQueue(PlaylistInfo playlist, Track? from = null, bool shuffled = false)
    {
        var shown = PlaylistSongs(playlist, from, shuffled);
        if (shown.Count == 0)
            return;
        var earlier = FirstPositions(Queue.Items.Select(i => i.Track.Path));
        var repeats = Queue.Add(shown).Where(i => earlier.ContainsKey(i.Track.Path)).ToList();
        Log.Info($"Playlist \"{playlist.Name}\" added to the queue{(shuffled ? ", shuffled" : "")}: {shown.Count} songs{(from is null ? "" : $" from number {from.PlaylistNumber}")}");
        if (repeats.Count > 0)
            NoticeRepeatsInQueue(repeats, shown.Count, earlier);
        else
            ShowNotice((shown.Count == 1 ? "1 song added to the queue" : $"{shown.Count} songs added to the queue") + (shuffled && shown.Count > 1 ? ", shuffled" : ""));
    }

    /// <summary>
    /// Play a playlist (#259): it replaces the queue, takes the place of the songs ready on the decks
    /// auto-play starts next, and auto-play turns on. The song playing to the room is never
    /// touched: it finishes, and the playlist follows. Undo puts back the queue and those songs.
    /// Repeat this song turns off: playing a playlist is the opposite of it.
    /// </summary>
    public void PlayPlaylist(PlaylistInfo playlist, bool shuffled = false)
    {
        if (AutoPlay is not { } auto)
            return;
        var songs = PlaylistSongs(playlist, null, shuffled);
        if (songs.Count == 0)
        {
            ShowNotice($"\"{playlist.Name}\" has no songs to play");
            return;
        }
        if (auto.Repeat == RepeatMode.Song)
            auto.Repeat = RepeatMode.Off;
        var queueBefore = Queue.Items.Select(i => i.Track).ToList();
        bool wasOn = auto.IsOn;
        Queue.Clear();
        Queue.Add(songs);
        var held = auto.LineUpQueue();
        auto.IsOn = true;
        Log.Info($"Playing playlist \"{playlist.Name}\"{(shuffled ? ", shuffled" : "")}: {songs.Count} songs, in place of {queueBefore.Count} queued");
        string count = songs.Count == 1 ? "1 song" : $"{songs.Count} songs";
        ShowNotice($"Playing \"{playlist.Name}\": {count}{(shuffled && songs.Count > 1 ? ", shuffled" : "")}", 8, undo: () =>
        {
            Queue.Clear();
            Queue.Add(queueBefore);
            foreach (var (deck, track) in held)
            {
                bool restored = false;
                if (!deck.IsPlaying)
                {
                    try
                    {
                        deck.Load(track);
                        restored = true;
                    }
                    catch (AudioException ex)
                    {
                        Log.Error($"Couldn't load {track.Path} again", ex);
                    }
                }
                if (!restored)
                    Queue.PlayNext([track]);   // its deck has moved on: first in the queue instead
            }
            if (!wasOn)
                auto.IsOn = false;
            Log.Info($"Playing \"{playlist.Name}\" undone: the queue and decks are back as they were");
        });
    }

    /// <summary>Puts the queue in a random order (#254), with Undo to put it back.</summary>
    public void ShuffleQueue()
    {
        if (Queue.Items.Count < 2)
            return;
        var before = Queue.Shuffle();
        Log.Info($"Queue shuffled: {before.Count} songs");
        ShowNotice($"Queue shuffled: {before.Count} songs", 6, undo: () =>
        {
            Queue.Reorder(before);
            Log.Info("Queue put back in the order it was in before shuffling");
        });
    }

    private DateTime? TonightSince() => History.CurrentSessionStartUtc;

    /// <summary>Saves the queue, in its order, as a new playlist (asks for its name).</summary>
    public Task SaveQueueAsPlaylistAsync() =>
        CreatePlaylistAsync(Queue.Items.Select(i => i.Track).ToList(), open: false);

    /// <summary>Saves the night shown in History, in the order it was played, as a new playlist.</summary>
    public Task SaveSessionAsPlaylistAsync() =>
        CreatePlaylistAsync(History.Plays.Select(p => p.Track).ToList(), History.SelectedName ?? "", open: false);

    /// <summary>Asks the DJ for a short piece of text (heading, message, button); the window supplies the dialog.</summary>
    /// <summary>Asks for a short text (heading, message, OK button, starting text); the window supplies the dialog.</summary>
    public Func<string, string, string, string, Task<string?>>? PromptAsync { get; set; }

    /// <summary>A tap on a track's heart: unrated → ♥ → broken heart → unrated.</summary>
    public void CycleRating(Track track) => SetRating([track], track.Rating switch
    {
        Rating.Unrated => Rating.Loved,
        Rating.Loved => Rating.Rejected,
        _ => Rating.Unrated,
    });

    /// <summary>A tap on a track's energy bars: unset → low → medium → high → unset.</summary>
    public void CycleEnergy(Track track) => SetEnergy([track], track.Energy == Energy.High ? Energy.Unset : track.Energy + 1);

    private static string Describe(IReadOnlyList<Track> tracks, IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"\"{tracks[0].DisplayTitle}\"" : $"{paths.Count} tracks";

    /// <summary>The right-hand panel shows the History tab rather than the Queue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQueue), nameof(DecksInQueue), nameof(ShowAirLine))]
    public partial bool ShowHistory { get; set; }

    public bool ShowQueue => !ShowHistory;
    public AudioEngine? Audio { get; }
    public MixEngine? Mix { get; }
    /// <summary>Every deck the mixer has, shown or not.</summary>
    public IReadOnlyList<DeckViewModel> Decks { get; } = [];
    public DeckViewModel? Deck1 => Decks.ElementAtOrDefault(0);
    public DeckViewModel? Deck2 => Decks.ElementAtOrDefault(1);
    public DeckViewModel? Deck3 => Decks.ElementAtOrDefault(2);
    public DeckViewModel? Deck4 => Decks.ElementAtOrDefault(3);

    private int _deckCount;

    /// <summary>The decks on screen, in playing order: 1, 2 (, 3, 4).</summary>
    public IReadOnlyList<DeckViewModel> ActiveDecks => Decks.Take(_deckCount).ToList();

    /// <summary>Four decks on screen rather than two (a setting).</summary>
    public bool FourDecks => _deckCount == 4;

    /// <summary>Smaller decks, for more songs in the library and queue (see <see cref="AppSettings.CompactDecks"/>).</summary>
    public bool CompactDecks
    {
        get => _settings.CompactDecks;
        set
        {
            if (value == _settings.CompactDecks)
                return;
            _settings.CompactDecks = value;
            Log.Info($"Decks: {(value ? "compact" : "full size")}");
            OnPropertyChanged();
            DeckSizeChanged();
        }
    }

    /// <summary>The side panels' widths and folds, for the window to apply and keep (see <see cref="AppSettings.LibraryPanelFolded"/>).</summary>
    internal AppSettings PanelSettings => _settings;

    /// <summary>The column names over the queue (#188), switched off or on from its menu › Show.</summary>
    public bool ShowsQueueColumnNames
    {
        get => _settings.ShowQueueColumnNames;
        set
        {
            if (value == _settings.ShowQueueColumnNames)
                return;
            _settings.ShowQueueColumnNames = value;
            OnPropertyChanged();
        }
    }

    /// <summary>How tall the playlist list may grow, as its grip was last dragged (#163); null: as it starts.</summary>
    public double? PlaylistsHeight
    {
        get => _settings.PlaylistsHeight;
        set => _settings.PlaylistsHeight = value;
    }

    public string SettingsTip => $"Settings · keys: {Shortcuts.Command}+, or {Shortcuts.Command}+O";

    /// <summary>With the decks hidden, the compact key (and Ctrl/Cmd+D) brings them back as they were.</summary>
    [RelayCommand]
    private void ToggleCompactDecks()
    {
        if (DecksHidden)
            DecksHidden = false;
        else
            CompactDecks = !CompactDecks;
    }

    /// <summary>
    /// The decks and mixer folded into one line under the top bar, for the most songs on screen
    /// (#164; see <see cref="AppSettings.DecksHidden"/>). Nothing playing is affected.
    /// </summary>
    public bool DecksHidden
    {
        get => _settings.DecksHidden;
        set
        {
            if (value == _settings.DecksHidden)
                return;
            _settings.DecksHidden = value;
            Log.Info($"Decks: {(value ? "hidden" : "shown")}");
            AirLine?.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HideDecksTip));
            OnPropertyChanged(nameof(DecksInQueue));
            OnPropertyChanged(nameof(ShowAirLine));
            DeckSizeChanged();
        }
    }

    /// <summary>Whether the queue panel is open (not folded into its strip); the window says (#173).</summary>
    public bool QueuePanelOpen
    {
        get => _queuePanelOpen;
        set
        {
            if (SetProperty(ref _queuePanelOpen, value))
            {
                OnPropertyChanged(nameof(DecksInQueue));
                OnPropertyChanged(nameof(ShowAirLine));
            }
        }
    }
    private bool _queuePanelOpen = true;

    /// <summary>
    /// Hidden decks sit atop the queue, in the order the room will hear them (#177), whenever the
    /// queue is in view; otherwise the line under the top bar stands in for them. Either way the
    /// room's play key is on screen, once.
    /// </summary>
    public bool DecksInQueue => DecksHidden && ShowQueue && QueuePanelOpen;

    public bool ShowAirLine => DecksHidden && !DecksInQueue;

    public string HideDecksTip => DecksHidden
        ? $"Show the decks ({Shortcuts.Command}+Shift+D)"
        : $"Hide the decks, for the most songs ({Shortcuts.Command}+Shift+D)";

    [RelayCommand]
    private void ToggleHideDecks() => DecksHidden = !DecksHidden;

    /// <summary>
    /// The decks' size, as the top bar's three-way switch and Settings › Decks choose it (#171).
    /// Hidden keeps whether they were compact, so showing them again from the keys brings them
    /// back as they were; choosing Full or Compact sets both.
    /// </summary>
    public DeckSize DeckSize
    {
        get => DecksHidden ? DeckSize.Hidden : CompactDecks ? DeckSize.Compact : DeckSize.Full;
        set
        {
            if (value == DeckSize.Hidden)
            {
                DecksHidden = true;
                return;
            }
            CompactDecks = value == DeckSize.Compact;
            DecksHidden = false;
        }
    }

    [RelayCommand]
    private void SetDeckSize(DeckSize size) => DeckSize = size;

    public bool IsFullDecks => DeckSize == DeckSize.Full;
    public bool IsCompactSize => DeckSize == DeckSize.Compact;

    public string FullDecksTip => "Full decks";
    public string CompactDecksTip => $"Compact decks, more songs · {Shortcuts.Command}+D";
    public string HiddenDecksTip => $"Hide the decks, for the most songs · {Shortcuts.Command}+Shift+D";

    private void DeckSizeChanged()
    {
        OnPropertyChanged(nameof(DeckSize));
        OnPropertyChanged(nameof(IsFullDecks));
        OnPropertyChanged(nameof(IsCompactSize));
        Settings.DeckSizeChanged();
    }

    /// <summary>
    /// Shows 2 or 4 decks. Going back to 2 is refused while Deck 3 or 4 is playing; otherwise
    /// they're emptied. Returns whether it changed.
    /// </summary>
    public bool SetDeckCount(int count)
    {
        count = count == 4 && Decks.Count >= 4 ? 4 : Math.Min(2, Decks.Count);
        if (count == _deckCount)
            return false;
        var hidden = Decks.Skip(count).ToList();
        if (hidden.FirstOrDefault(d => d.IsPlaying) is { } playing)
        {
            ShowNotice($"Deck {playing.Label} is playing. Stop it before going back to two decks.");
            OnPropertyChanged(nameof(FourDecks));   // put the switch back
            return false;
        }
        foreach (DeckViewModel deck in hidden)
        {
            deck.IsCueOn = false;
            deck.EjectCommand.Execute(null);
        }
        _deckCount = count;
        _settings.DeckCount = count;
        Log.Info($"Decks: {count}");
        OnPropertyChanged(nameof(FourDecks));
        OnPropertyChanged(nameof(ActiveDecks));
        Mixer?.DeckCountChanged();
        AutoPlay?.Refresh();
        return true;
    }

    /// <summary>
    /// Another deck on the same side of the crossfader that the room is hearing. Only one song at
    /// a time per side: the next one waits until it's faded out.
    /// </summary>
    internal DeckViewModel? OnAirBeside(DeckViewModel deck) => ActiveDecks.FirstOrDefault(d =>
        d != deck && d.IsOnAir && Crossfade.Side(d.Deck.Index) == Crossfade.Side(deck.Deck.Index));
    public MixerViewModel? Mixer { get; }

    /// <summary>What the room hears and what's next, in place of hidden decks (#164).</summary>
    public AirLineViewModel? AirLine { get; }
    public PreviewViewModel? Preview { get; }
    public AutoPlayViewModel? AutoPlay { get; }

    /// <summary>Play/pause, fade out and auto-play from the taskbar or the Dock (#262).</summary>
    public RoomControls Room { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial string Clock { get; private set; } = "";

    /// <summary>Why there's no sound at all (e.g. BASS failed to load).</summary>
    [ObservableProperty]
    public partial string? AudioError { get; private set; }

    /// <summary>A short message in the top bar, e.g. why a load was refused. Clears itself.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>What the notice's Undo key does; null when it has none.</summary>
    [ObservableProperty]
    public partial Action? NoticeUndo { get; private set; }

    // Split, the side comes first: a long name is trimmed at its end.
    public string MainOutputName => (Mix?.IsSplit == true ? "Left · " : "") + OutputName(Mix?.Main);
    public string CueOutputName => Mix?.IsUnplugged(TableOutput.Headphones) == true ? "Unplugged"
        : (Mix?.IsSplit == true ? "Right · " : "") + OutputName(Mix?.Cue);
    public bool HasMainOutput => Mix is { } mix && (mix.IsOffline || mix.Main.Device is not null);

    /// <summary>
    /// Headphones are available (not unplugged). Offline mixes (tests) count, since nothing reaches
    /// the speakers.
    /// </summary>
    public bool HasCueOutput => Mix is { } mix && !mix.IsUnplugged(TableOutput.Headphones) && (mix.IsOffline || mix.Cue.Device is not null);

    public string BoothOutputName => Mix?.IsUnplugged(TableOutput.Booth) == true ? "Unplugged"
        : Mix?.Booth.Device?.Name ?? _settings.BoothDevice?.Name ?? "Not set";

    /// <summary>The booth is playing (not unplugged).</summary>
    public bool HasBoothOutput => Mix is { } mix && !mix.IsUnplugged(TableOutput.Booth) && mix.Booth.Device is not null;

    /// <summary>A booth output has been chosen (it may not be connected right now): the top bar shows it.</summary>
    public bool ShowsBoothOutput => HasBoothOutput || _settings.BoothDevice is not null;

    private string OutputName(OutputBus? bus) =>
        Mix is null ? "No audio" : Mix.IsOffline ? "Offline" : bus?.Device?.Name ?? "Not set";

    private void UpdateClock() => Clock = DateTime.Now.ToString("t");

    public void Start()
    {
        _clock.Start();
        if (Mix is { IsOffline: false } && Audio is not null)
            ConnectSavedOutputs();
        if (Mix is not null)
            _playbackTimer.Start();
        _ = StartLibraryAsync();
        _ = RestoreNightAsync();
    }

    private async Task StartLibraryAsync()
    {
        await Library.StartAsync(_settings.SelectedRootPath, _settings.SelectedFolder, _settings.OpenPlaylist);
        Analysis?.QueueLibrary();   // after the scan, so new files are included
        await KeepAutomaticBackupAsync();
    }

    /// <summary>
    /// Reconnects the speakers, headphones and booth chosen last time. The speakers fall back to
    /// the system default; the headphones and booth never do, so cueing can't leak into the room.
    /// </summary>
    private void ConnectSavedOutputs()
    {
        var (main, cue, notice) = ChooseOutputs(_settings.MainDevice, _settings.CueDevice,
            Audio!.FindDevice(_settings.MainDevice), Audio.FindDevice(_settings.CueDevice), Audio.GetDefaultDevice());
        AudioDevice? booth = Audio.FindDevice(_settings.BoothDevice);
        if (_settings.BoothDevice is { } savedBooth && booth is null)
            notice = $"{notice} Booth output \"{savedBooth.Name}\" isn't connected.".TrimStart();
        if (notice is not null)
        {
            Log.Info(notice);
            ShowNotice(notice);
        }
        SetOutputs(main, cue, booth, remember: false);
        Settings.RefreshDevices();
    }

    /// <summary>
    /// The outputs to open from the saved choices and what's connected now. Speakers that aren't
    /// connected fall back to the system default. If that's the very output the headphones are on,
    /// the headphones are left off, so a preview can't reach the room (after an unplug, a laptop's
    /// own output is often both).
    /// </summary>
    internal static (AudioDevice? Main, AudioDevice? Cue, string? Notice) ChooseOutputs(
        AudioDevicePreference? savedMain, AudioDevicePreference? savedCue, AudioDevice? main, AudioDevice? cue, AudioDevice? fallback)
    {
        string? notice = null;
        if (savedMain is not null && main is null)
            notice = $"Speaker output \"{savedMain.Name}\" isn't connected; using {fallback?.Name ?? "the system default"}.";
        else if (savedCue is not null && cue is null)
            notice = $"Headphone output \"{savedCue.Name}\" isn't connected.";

        if (main is null)
        {
            main = fallback;
            if (main is not null && cue?.Index == main.Index)
            {
                cue = null;
                notice = $"{notice} Headphones are off: they'd play in the room. Choose them in Settings › Sound.".TrimStart();
            }
        }
        return (main, cue, notice);
    }

    /// <summary>
    /// The speakers stopped by themselves while GZJam had them alone (unplugged, or taken by
    /// Windows): opens them again if they're still there, else the system default, as at startup.
    /// </summary>
    private void OnMainOutputLost()
    {
        AudioDevice? lost = Mix!.Main.Device;
        string name = lost?.Name ?? "The speaker output";
        Log.Error($"Speaker output stopped: {name}");
        AudioDevice? cue = Audio!.FindDevice(_settings.CueDevice);
        AudioDevice? booth = Audio.FindDevice(_settings.BoothDevice);

        // Still there (Windows took it for a moment): open it again. Windows may still list a card
        // just unplugged, so only opening it says it's back.
        if (Audio.FindDevice(_settings.MainDevice) is { } saved && SetOutputs(saved, cue, booth, remember: false, quiet: true))
        {
            ShowNotice($"{name} stopped for a moment; GZJam opened it again.");
            Settings.RefreshDevices();
            return;
        }

        // Gone: the system default, as at startup, keeping the headphones out of the room.
        AudioDevice? fallback = Audio.GetDefaultDevice() is { } byDefault && byDefault.Index != lost?.Index ? byDefault : null;
        var (main, newCue, _) = ChooseOutputs(_settings.MainDevice, _settings.CueDevice, null, cue, fallback);
        if (SetOutputs(main, newCue, booth, remember: false))
        {
            string notice = main is null
                ? $"{name} stopped (unplugged?), and no other speakers are connected. Choose them in Settings › Sound."
                : $"{name} stopped (unplugged?), so the music is playing through {main.Name}." +
                  (cue is not null && newCue is null ? " Headphones are off: they'd play in the room. Choose them in Settings › Sound." : "");
            Log.Info(notice);
            ShowNotice(notice);
        }
        Settings.RefreshDevices();
    }

    /// <summary>
    /// The headphones or booth were unplugged (they go quiet: nothing moves to another output), or
    /// came back by themselves once plugged in again.
    /// </summary>
    private void OnTableOutputChanged(TableOutput output, bool unplugged)
    {
        OutputBus bus = output == TableOutput.Headphones ? Mix!.Cue : Mix!.Booth;
        string what = output == TableOutput.Headphones ? "Headphones" : "Booth";
        string notice = unplugged
            ? $"{what} unplugged. {(output == TableOutput.Headphones ? "They'll" : "It'll")} come back by {(output == TableOutput.Headphones ? "themselves" : "itself")} when plugged in."
            : output == TableOutput.Headphones ? "Headphones are back." : "The booth is back.";
        if (unplugged)
            Log.Error($"{what} output stopped (unplugged?): {bus.Device?.Name}; waiting for it to come back");
        else
            Log.Info($"{what} output back: {bus.Device?.Name}");
        ShowNotice(notice);
        OnPropertyChanged(output == TableOutput.Headphones ? nameof(CueOutputName) : nameof(BoothOutputName));
        OnPropertyChanged(output == TableOutput.Headphones ? nameof(HasCueOutput) : nameof(HasBoothOutput));
        Settings.RefreshDevices();
    }

    /// <summary>Applies <see cref="AppSettings.ExclusiveSpeakers"/>, opening the speakers again with it.</summary>
    public void SetExclusiveSpeakers(bool value)
    {
        _settings.ExclusiveSpeakers = value;
        Log.Info($"Speakers for GZJam alone: {(value ? "on" : "off")}");
        if (Mix is null)
            return;
        Mix.ExclusiveSpeakers = value;
        if (!Mix.IsOffline && Mix.Main.Device is not null)
            SetOutputs(Mix.Main.Device, Mix.Cue.Device, Mix.Booth.Device, remember: false);
    }

    /// <summary>Applies <see cref="AppSettings.ExtraBuffering"/>, opening the outputs again with it.</summary>
    public void SetExtraBuffering(bool value)
    {
        _settings.ExtraBuffering = value;
        Log.Info($"Extra protection against crackles: {(value ? "on" : "off")}");
        if (Audio is null)
            return;
        Audio.ExtraBuffering = value;
        if (Mix is { IsOffline: false })
            SetOutputs(Mix.Main.Device, Mix.Cue.Device, Mix.Booth.Device, remember: false);
    }

    /// <summary>Opens outputs. Returns whether they opened (if not, the last ones play on and a notice says why).</summary>
    /// <param name="remember">Save them as the DJ's choice (a booth that won't open is forgotten).</param>
    /// <param name="quiet">No notice if they won't open (GZJam is only trying).</param>
    public bool SetOutputs(AudioDevice? main, AudioDevice? cue, AudioDevice? booth, bool remember = true, bool quiet = false)
    {
        if (Mix is null)
            return false;
        bool opened = false;
        try
        {
            Mix.SetOutputs(main, cue, booth);
            Log.Info($"Outputs: main = {main?.Name ?? "none"} [{main?.Driver}]{(Mix.SpeakersAreExclusive ? " (GZJam's alone)" : "")}{(Mix.IsSplit ? " (split: speakers left, headphones right)" : "")}, " +
                     $"cue = {Mix.Cue.Device?.Name ?? "none"} [{Mix.Cue.Device?.Driver}], booth = {Mix.Booth.Device?.Name ?? "none"} [{Mix.Booth.Device?.Driver}]");
            if (Mix.CueRefusal is { } cueWhy)
            {
                Log.Error($"Headphones couldn't open: {cueWhy}");
                ShowNotice($"The headphones couldn't open: {cueWhy} The speakers are playing.");
            }
            if (Mix.BoothRefusal is { } boothWhy)
            {
                Log.Error($"Booth couldn't open: {boothWhy}");
                ShowNotice($"The booth is off: {boothWhy}.".Replace("..", "."));
            }
            if (Mix.ExclusiveRefusal is { } why)
            {
                Log.Info($"Speakers shared, not GZJam's alone: {why}");
                ShowNotice($"{why}, so it's shared: other programs' sounds can still play through it.");
            }
            if (remember)
            {
                _settings.MainDevice = main?.Preference;
                _settings.CueDevice = cue?.Preference;
                _settings.BoothDevice = Mix.Booth.Device?.Preference;
            }
            opened = true;
        }
        catch (AudioException ex)
        {
            Log.Error("Couldn't open output", ex);
            if (!quiet)
            {
                ShowNotice($"Couldn't open that output: {ex.Message}");
                Settings.RefreshDevices();   // show the outputs still in use
            }
        }
        OnPropertyChanged(nameof(MainOutputName));
        OnPropertyChanged(nameof(CueOutputName));
        OnPropertyChanged(nameof(HasMainOutput));
        OnPropertyChanged(nameof(HasCueOutput));
        OnPropertyChanged(nameof(BoothOutputName));
        OnPropertyChanged(nameof(HasBoothOutput));
        OnPropertyChanged(nameof(ShowsBoothOutput));
        return opened;
    }

    // ───────────── Decks ─────────────

    /// <summary>Starting over with an empty library was confirmed: GZJam closes and opens again (see <see cref="LibraryReset"/>).</summary>
    public event Action? StartOverRequested;

    internal void RequestStartOver() => StartOverRequested?.Invoke();

    /// <summary>A backup is readied to be restored: GZJam closes and opens again with it (see <see cref="LibraryBackup"/>).</summary>
    public event Action? RestoreRequested;

    internal void RequestRestore() => RestoreRequested?.Invoke();

    /// <summary>The library, for backing it up.</summary>
    internal LibraryDatabase Database => _database;

    /// <summary>The settings as they stand, for a backup.</summary>
    internal string SettingsJson()
    {
        SaveState();
        return _settings.ToJson();
    }

    /// <summary>
    /// Keep the automatic daily copy of the library (set as GZJam starts; tests leave it off, so
    /// their throwaway libraries aren't copied).
    /// </summary>
    internal bool KeepsAutomaticBackups { get; set; }

    /// <summary>The automatic copy, once the library has started: in the background, never in the way.</summary>
    private async Task KeepAutomaticBackupAsync()
    {
        if (!KeepsAutomaticBackups)
            return;
        string settings = SettingsJson();
        try
        {
            if (await Task.Run(() => LibraryBackup.KeepAutomatic(_database, settings, DateTime.Now)) is { } path)
                Log.Info($"Automatic backup: {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't make the automatic backup", ex);
        }
    }

    /// <summary>Asks the user to confirm something (heading, message, the confirm button's text); the window supplies the dialog.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Asks a question with two harmless answers (heading, message, yes, no); the window supplies the dialog.</summary>
    public Func<string, string, string, string, Task<bool>>? AskAsync { get; set; }

    /// <summary>Asks for a folder (the picker's title); null if none was chosen. The window supplies the picker.</summary>
    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>
    /// Asks whether to close GZJam while the room is hearing music (the message says what's
    /// playing); true to close. The window supplies the dialog, with "Keep playing" as its default.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmCloseAsync { get; set; }

    /// <summary>
    /// What the room is hearing, for the question before closing: "Deck 1 is playing "Jump
    /// Steady" to the room." Null when no deck is on air; music only in the headphones (the
    /// preview, or a cued deck) doesn't count.
    /// </summary>
    public string? WhatTheRoomIsHearing()
    {
        var onAir = Decks.Where(d => d.IsOnAir).ToList();
        return onAir.Count switch
        {
            0 => null,
            1 => $"Deck {onAir[0].Label} is playing \u201c{onAir[0].Title}\u201d to the room.",
            _ => $"Decks {string.Join(" and ", onAir.Select(d => d.Label))} are playing to the room.",
        };
    }

    /// <summary>
    /// Whether a load onto this deck would go ahead without being refused outright. Only the deck
    /// the room hears is protected: one the crossfader has out of the room (OFF AIR) takes a song
    /// while it plays on quietly, as a paused one does (#226).
    /// </summary>
    public bool CanLoad(DeckViewModel deck) => !deck.IsOnAir || _settings.LoadOntoPlayingDeck != PlayingDeckLoad.Block;

    /// <summary>
    /// Songs for files and folders dragged in from the file manager, in the order given (a
    /// folder's songs in order of their paths). A song in the library comes with its marks; one
    /// outside it plays as it is, without joining the library. Also the dropped files GZJam can't
    /// play (a folder's other files, its pictures say, are left out quietly).
    /// </summary>
    public (IReadOnlyList<Track> Tracks, IReadOnlyList<string> Skipped) TracksFromFiles(IEnumerable<string> paths)
    {
        var tracks = new List<Track>();
        var skipped = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (string file in Directory.EnumerateFiles(path, "*", options).Where(LibraryScanner.IsAudioFile)
                             .Order(StringComparer.OrdinalIgnoreCase))
                    tracks.Add(_database.TrackForFile(file));
            }
            else if (File.Exists(path) && LibraryScanner.IsAudioFile(path))
            {
                tracks.Add(_database.TrackForFile(path));
            }
            else
            {
                skipped.Add(path);
            }
        }
        return (tracks, skipped);
    }

    /// <summary>
    /// Loads a track onto a deck, protecting the deck the room hears as configured in Settings.
    /// Returns whether the track was loaded.
    /// </summary>
    public async Task<bool> LoadToDeckAsync(DeckViewModel deck, Track track)
    {
        if (deck.IsOnAir)
        {
            switch (_settings.LoadOntoPlayingDeck)
            {
                case PlayingDeckLoad.Block:
                    Log.Info($"Deck {deck.Label}: refused load of \"{track.DisplayTitle}\" (deck is on air)");
                    ShowNotice($"Deck {deck.Label} is on air. Pause it, or crossfade away from it, to load a new track.");
                    return false;
                case PlayingDeckLoad.Ask:
                    if (ConfirmAsync is null || !await ConfirmAsync(
                            $"Replace the track on Deck {deck.Label}?",
                            $"\"{deck.Title}\" is still playing to the room. Loading \"{track.DisplayTitle}\" will stop it.",
                            "Load anyway"))
                        return false;
                    break;
            }
        }

        // A song on a server is fetched first, off the UI thread, so the window doesn't wait on it.
        if (RemoteSongs.IsRemote(track.Path) && !RemoteSongs.IsCached(track.Path))
        {
            if (_settings.RemoteFetch == RemoteFetchMode.Never)
            {
                // Songs are downloaded when the DJ asks (the playlist's menu, the Servers window), not by playing them.
                Log.Info($"Deck {deck.Label}: \"{track.DisplayTitle}\" isn't on this computer and songs aren't fetched by themselves");
                ShowNotice($"\"{track.DisplayTitle}\" isn't on this computer yet. Right-click it > Cache for offline play, or change Settings > Servers > Songs not on this computer.", seconds: 8);
                return false;
            }
            string getting = $"Getting \"{track.DisplayTitle}\" from the server…";
            ShowNotice(getting, seconds: 130);
            try
            {
                await Task.Run(() => RemoteSongs.Prepare(track.Path));
            }
            catch (AudioException ex)
            {
                Log.Error($"Couldn't get {track.Path}", ex);
                ShowNotice($"Couldn't load {track.DisplayTitle}: {ex.Message}");
                return false;
            }
            if (Notice == getting)
                Notice = null;   // it's here: nothing more to say
        }

        // With auto-play on, the deck waiting to play is the front of the queue (#261).
        Track? bumped = AutoPlay?.IsWaiting(deck) == true ? deck.Track : null;
        try
        {
            deck.Load(track);
        }
        catch (AudioException ex)
        {
            Log.Error($"Couldn't load {track.Path}", ex);
            ShowNotice($"Couldn't load {track.DisplayTitle}: {ex.Message}");
            return false;
        }
        if (bumped is not null && bumped.Path != track.Path)
            BackToTopOfQueue(bumped);
        return true;
    }

    /// <summary>
    /// A song replaced on the waiting deck goes back to the top of the queue (#261), so changing the
    /// very next song doesn't lose the one that was ready. With Repeat the queue it's at the end
    /// already (it went there as it was loaded): it moves up rather than being queued twice.
    /// </summary>
    private void BackToTopOfQueue(Track track)
    {
        QueueItem? moved = Queue.Repeat ? Queue.Items.LastOrDefault(i => i.Track.Path == track.Path) : null;
        QueueItem item;
        if (moved is not null)
        {
            Queue.Move([moved], 0);
            item = moved;
        }
        else
        {
            item = Queue.Add([track], 0)[0];
        }
        Log.Info($"\"{track.DisplayTitle}\" went back to the top of the queue");
        ShowNotice($"\"{track.DisplayTitle}\" went back to the top of the queue", 6, undo: () =>
        {
            if (moved is not null)
                Queue.Move([moved], Queue.Items.Count);
            else
                Queue.Remove([item]);
            Log.Info($"\"{track.DisplayTitle}\" taken back out of the top of the queue");
        });
    }

    /// <summary>
    /// A song double-clicked (#260) goes onto the deck that plays next, never the one on air. With
    /// auto-play on, that's the deck it starts next: the song that was ready there goes back to
    /// the top of the queue (#261). If that deck is busy (playing in the headphones, say), the song
    /// goes to the top of the queue instead. With auto-play off, it's the deck the room isn't
    /// hearing (with nothing playing, the one it will hear). A song from the queue leaves it, as
    /// dragging it would. Returns whether it was loaded onto a deck.
    /// </summary>
    public async Task<bool> PlaySongAsync(Track track, QueueItem? queued = null)
    {
        DeckViewModel? deck;
        if (AutoPlay is { IsOn: true } auto)
        {
            deck = auto.FreeWaitingDeck();
            if (deck is null || !CanLoad(deck))
            {
                if (queued is not null)
                    Queue.Move([queued], 0);
                else
                    Queue.PlayNext([track]);
                Log.Info($"\"{track.DisplayTitle}\" first in the queue (double-click; the next deck is busy)");
                ShowNotice($"\"{track.DisplayTitle}\" is first in the queue");
                return false;
            }
        }
        else if (AutoPlay?.DeckForNextSong() is { } next)
        {
            deck = next;
        }
        else
        {
            return false;
        }
        if (!CanLoad(deck))
        {
            ShowNotice($"Deck {deck.Label} is on air. Pause it, or crossfade away from it, to load a new track.");
            return false;
        }
        if (!await LoadToDeckAsync(deck, track))
            return false;
        if (queued is not null)
            Queue.Loaded([queued]);
        return true;
    }

    public void RefreshDecks()
    {
        foreach (DeckViewModel deck in Decks)
            deck.Refresh();
    }

    /// <summary>One playback tick: fades, end-of-track, snaps, and all the moving readouts.</summary>
    public void Tick()
    {
        Mix?.Update();
        Mixer?.Update();
        AutoPlay?.Tick();
        foreach (DeckViewModel deck in Decks)
            deck.UpdateMeter();
        RefreshDecks();
        AirLine?.Refresh();
        Preview?.Refresh();
        KeepNight();
        Room.Refresh();
        if (!_roomHasHeardMusic && Decks.Any(d => d.IsOnAir))
        {
            _roomHasHeardMusic = true;
            NewVersion = null;   // the night has started: not during a set
        }
    }

    /// <summary>A deck has played to the room this session.</summary>
    private bool _roomHasHeardMusic;

    /// <summary>
    /// A newer GZJam than this one, said quietly in the top bar (see <see cref="UpdateCheck"/>).
    /// Only before the room has heard anything: once a deck plays to the room it goes until the
    /// next start. Null when there's none, it was dismissed, or the check is off.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNewVersion), nameof(NewVersionText))]
    public partial Release? NewVersion { get; private set; }

    public string NewVersionText => NewVersion is { } release ? $"GZJam {release.Version} is out." : "";

    /// <summary>The new version's line, except while a short notice has the top bar's middle.</summary>
    public bool ShowsNewVersion => NewVersion is not null && Notice is null;

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(ShowsNewVersion));

    /// <summary>
    /// Where the newest release comes from: GitHub (<see cref="UpdateCheck.LatestAsync"/>, set at
    /// startup), or a test's stand-in. Until then, no answer, so tests never go online.
    /// </summary>
    internal Func<Task<Release?>> LatestRelease { get; set; } = () => Task.FromResult<Release?>(null);

    /// <summary>This GZJam's version, compared with the newest release; tests pretend to be older.</summary>
    internal string CurrentVersion { get; set; } = About.Version;

    /// <summary>
    /// At startup, asks for the newest release, unless the check is turned off, and offers it if
    /// it's newer than this GZJam and not one the DJ dismissed. Says nothing if it can't be reached.
    /// </summary>
    public async Task CheckForNewVersionAsync()
    {
        if (!_settings.CheckForNewVersion)
            return;
        Release? release = await LatestRelease();
        if (release is null)
            return;
        if (!UpdateCheck.IsNewer(release.Version, CurrentVersion))
        {
            Log.Info($"GZJam {CurrentVersion} is up to date (newest release {release.Version})");
            return;
        }
        Log.Info($"GZJam {release.Version} is out (this is {CurrentVersion})");
        if (release.Version == _settings.DismissedVersion || _roomHasHeardMusic || !_settings.CheckForNewVersion)
            return;
        NewVersion = release;
    }

    /// <summary>The DJ closed the line: not mentioned again until a later version.</summary>
    public void DismissNewVersion()
    {
        if (NewVersion is null)
            return;
        _settings.DismissedVersion = NewVersion.Version;
        Log.Info($"Dismissed the news of GZJam {NewVersion.Version}");
        NewVersion = null;
    }

    /// <summary>The check turned off in Settings: the line goes too.</summary>
    internal void NewVersionCheckTurnedOff() => NewVersion = null;

    /// <param name="seconds">How long it stays: longer for one the DJ mustn't miss.</param>
    /// <param name="undo">Takes back what the notice reports, from an Undo key beside it.</param>
    public async void ShowNotice(string text, double seconds = 4, Action? undo = null)
    {
        Notice = text;
        NoticeUndo = undo;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        if (Notice == text)
        {
            Notice = null;
            NoticeUndo = null;
        }
    }

    /// <summary>The notice's Undo key was pressed.</summary>
    public void UndoNotice()
    {
        Action? undo = NoticeUndo;
        Notice = null;
        NoticeUndo = null;
        undo?.Invoke();
    }

    /// <summary>Library columns the DJ has hidden (by header); changed by the column menu, saved with the settings.</summary>
    public List<string> HiddenColumns => _settings.HiddenColumns;

    /// <summary>The tags shown as library columns, by key (#119).</summary>
    public List<string> TagColumns => _settings.TagColumns;

    /// <summary>A song's info shows the tags only other programs use too (#129).</summary>
    public bool ShowAllTags
    {
        get => _settings.ShowAllTags;
        set => _settings.ShowAllTags = value;
    }
    public List<string> HiddenQueueColumns => _settings.HiddenQueueColumns;

    /// <summary>Copies state worth remembering into the settings before they're saved.</summary>
    public void SaveState()
    {
        (_settings.SelectedRootPath, _settings.SelectedFolder) = Library.GetSelection();
        _settings.OpenPlaylist = Library.SelectedPlaylist?.Id;
    }

    public void Dispose()
    {
        _clock.Stop();
        _playbackTimer.Stop();
        ServerConnections.Changed -= OnServersChanged;
        Downloads.Dispose();
        Library.Dispose();
        Art.Dispose();
        Analysis?.Dispose();   // stops decoding before BASS goes away
        Mix?.Dispose();
        Audio?.Dispose();
    }
}

/// <summary>How much room the decks take (#171): full, compact (#128), or hidden behind one line (#164).</summary>
public enum DeckSize
{
    Full,
    Compact,
    Hidden,
}
