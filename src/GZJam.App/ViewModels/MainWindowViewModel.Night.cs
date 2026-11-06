using System.Collections.Specialized;
using System.ComponentModel;
using GZJam.Library;

namespace GZJam.App.ViewModels;

/// <summary>
/// Bringing the night back (#96): the queue and the decks are kept in night.json as they change.
/// After a crash they come back as they were, paused; after a normal close too, with each deck at
/// the start of its song (#117).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>How often a playing deck's position is kept.</summary>
    private const long PositionEveryMs = 5000;

    /// <summary>The soonest a change is written after the last write, so a burst of changes is one write.</summary>
    private const long WriteAtMostEveryMs = 1000;

    /// <summary>The restore is done (or there was nothing): only then is the night kept, so a crash's night isn't overwritten first.</summary>
    private bool _keepingNight;

    private bool _nightChanged;
    private string? _nightDecks;
    private long _nightWrittenAt;
    private Task _nightWrite = Task.CompletedTask;
    private readonly Lock _nightLock = new();

    /// <summary>Where the night is kept: beside the library.</summary>
    internal string NightPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_database.DatabasePath))!, NightState.FileName);

    /// <summary>
    /// At startup: brings back what was saved (the queue, and the decks, paused: where they were
    /// after a crash, at the start of their songs after a normal close),
    /// then keeps the night from here on.
    /// </summary>
    internal async Task RestoreNightAsync()
    {
        try
        {
            if (NightState.Load(NightPath) is { } saved)
                await RestoreAsync(saved);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't bring the night back", ex);   // a bad file never stops GZJam starting
        }
        _keepingNight = true;
        Queue.Items.CollectionChanged += OnNightChanged;
        if (AutoPlay is not null)
            AutoPlay.PropertyChanged += OnAutoPlayChangedForNight;
        WriteNight(closedNormally: false);   // running now: a crash from here leaves this
    }

    private async Task RestoreAsync(NightState saved)
    {
        bool crashed = !saved.ClosedNormally;
        var decks = saved.Decks.Where(d => d.Deck >= 0 && d.Deck < Decks.Count).ToList();
        var paths = saved.Queue.Concat(decks.Select(d => d.Path)).Distinct().ToList();
        if (paths.Count == 0)
            return;

        // The tags are read off the UI thread, as for files dragged in.
        var found = await Task.Run(() => paths.Select(p => (Path: p, Track: Restorable(p))).Where(x => x.Track is not null)
            .ToDictionary(x => x.Path, x => x.Track!));
        int missing = paths.Count - found.Count;

        var queued = saved.Queue.Select(p => found.GetValueOrDefault(p)).OfType<Track>().ToList();
        Queue.Add(queued, 0);   // before anything queued while this was reading

        int onDecks = 0;
        foreach (var state in decks)
        {
            DeckViewModel deck = Decks[state.Deck];
            if (deck.HasTrack || found.GetValueOrDefault(state.Path) is not { } track)
                continue;
            try
            {
                // After a normal close, the start: a song played to its end (or halfway) isn't
                // left there for the next press of play.
                var position = crashed ? TimeSpan.FromSeconds(state.PositionSeconds) : TimeSpan.Zero;
                deck.Restore(track, position, state.TempoPercent, state.Cue);
                onDecks++;
            }
            catch (GZJam.Audio.AudioException ex)
            {
                Log.Error($"Couldn't bring back Deck {deck.Label}'s {track.Path}", ex);
                missing++;
            }
        }

        string songs = queued.Count == 1 ? "1 song" : $"{queued.Count} songs";
        string what = crashed ? "GZJam didn't close normally: the queue and decks are back as they were, paused."
            : onDecks == 0 ? $"The queue from last time is back ({songs})."
            : queued.Count == 0 ? "The decks from last time are back."
            : $"The decks and queue from last time are back ({songs} queued).";
        if (crashed && saved.AutoPlay)
            what += " Auto-play is off until you turn it on.";
        if (missing > 0)
            what += missing == 1 ? " 1 song couldn't be found (moved, or on a drive that isn't plugged in)."
                : $" {missing} songs couldn't be found (moved, or on a drive that isn't plugged in).";
        Log.Info($"Night back ({(crashed ? "after a crash" : "after a normal close")}, saved {saved.SavedUtc:u}): " +
                 $"{queued.Count} queued, {onDecks} on decks, {missing} not found" +
                 string.Concat(paths.Where(p => !found.ContainsKey(p)).Select(p => $"; missing: {p}")));
        if (crashed || queued.Count > 0 || onDecks > 0 || missing > 0)
            ShowNotice(what, seconds: crashed ? 12 : 6);
    }

    /// <summary>
    /// A saved song as a track: a file that is still there, or a song from a server (<c>subsonic://</c>)
    /// the library knows from its last sync. Null if it can't be found.
    /// </summary>
    private Track? Restorable(string path) =>
        File.Exists(path) ? _database.TrackForFile(path)
        : path.Contains("://", StringComparison.Ordinal) ? _database.FindTrack(path)
        : null;

    private void OnNightChanged(object? sender, NotifyCollectionChangedEventArgs e) => _nightChanged = true;

    private void OnAutoPlayChangedForNight(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoPlayViewModel.IsOn))
            _nightChanged = true;
    }

    /// <summary>From the playback tick: writes the night after a change, and a playing deck's position every few seconds.</summary>
    private void KeepNight()
    {
        if (!_keepingNight)
            return;
        // The decks say they've changed on every tick, so what's kept of them is compared instead.
        string decks = string.Join('|', Decks.Select(d => $"{d.Track?.Path}*{d.IsCueOn}*{d.TempoPercent}*{d.IsPlaying}"));
        if (decks != _nightDecks)
        {
            _nightDecks = decks;
            _nightChanged = true;
        }
        long now = Environment.TickCount64;
        bool positionDue = now - _nightWrittenAt >= PositionEveryMs && Decks.Any(d => d.IsPlaying);
        if ((_nightChanged && now - _nightWrittenAt >= WriteAtMostEveryMs) || positionDue)
            WriteNight(closedNormally: false);
    }

    /// <summary>The night as it stands, written in the background (each write after the last).</summary>
    private void WriteNight(bool closedNormally)
    {
        _nightChanged = false;
        _nightWrittenAt = Environment.TickCount64;
        string json = CaptureNight(closedNormally).ToJson();
        string path = NightPath;
        lock (_nightLock)
            _nightWrite = _nightWrite.ContinueWith(_ => NightState.Write(path, json), TaskScheduler.Default);
    }

    internal NightState CaptureNight(bool closedNormally) => new()
    {
        ClosedNormally = closedNormally,
        SavedUtc = DateTime.UtcNow,
        Queue = Queue.Items.Select(i => i.Track.Path).ToList(),
        Decks = Decks.Where(d => d.Track is not null).Select(d => new NightState.DeckState
        {
            Deck = d.Deck.Index,
            Path = d.Track!.Path,
            PositionSeconds = Math.Round(d.Deck.Position.TotalSeconds, 2),
            TempoPercent = d.TempoPercent,
            Cue = d.IsCueOn,
        }).ToList(),
        AutoPlay = AutoPlay?.IsOn == true,
    };

    /// <summary>
    /// GZJam is closing as it should: the night is written once more, marked so, and waited for.
    /// The next start brings back the queue, and the decks at the start of their songs.
    /// </summary>
    public void CloseNight()
    {
        if (!_keepingNight)
            return;
        _keepingNight = false;
        WriteNight(closedNormally: true);
        Task pending;
        lock (_nightLock)
            pending = _nightWrite;
        pending.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>For tests: writes the night now (a crash would leave this), and waits for it.</summary>
    internal void WriteNightNow()
    {
        WriteNight(closedNormally: false);
        Task pending;
        lock (_nightLock)
            pending = _nightWrite;
        pending.Wait(TimeSpan.FromSeconds(5));
    }
}
