using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;

namespace GZJam.App.ViewModels;

/// <summary>One song coming down from the server, for the downloads list.</summary>
public sealed partial class DownloadItem(string path, string title, string detail) : ObservableObject
{
    public string Path { get; } = path;
    public string Title { get; } = title;
    public string Detail { get; } = detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsDownloading), nameof(IsFinished), nameof(Percent))]
    public partial RemoteTransferState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Percent))]
    public partial long Received { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(Percent))]
    public partial long Total { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial string? Error { get; set; }

    public bool IsDownloading => State == RemoteTransferState.Downloading;

    public bool IsFinished => State is RemoteTransferState.Done or RemoteTransferState.Failed or RemoteTransferState.Cancelled;

    public double Percent => State == RemoteTransferState.Done ? 100 : Total > 0 ? Math.Clamp(100.0 * Received / Total, 0, 100) : 0;

    public string StateText => State switch
    {
        RemoteTransferState.Waiting => "Waiting",
        RemoteTransferState.Downloading => Total > 0 ? $"{Megabytes(Received)} of {Megabytes(Total)}" : Megabytes(Received),
        RemoteTransferState.Done => $"On this computer ({Megabytes(Total)})",
        RemoteTransferState.Failed => "Failed: " + Error,
        _ => "Stopped",
    };

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}

/// <summary>
/// The download queue, for the DJ to watch (the Downloads window, and the top bar while it is
/// busy): every song coming down from the server, the ones waiting, and how the last ones went.
/// Songs get here from caching a playlist, folder or songs, from queueing a song, and from loading
/// one on a deck; the work itself is <see cref="RemoteSongs"/>'s.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    /// <summary>Finished downloads kept in the list; older ones go, so a long night doesn't grow it for ever.</summary>
    private const int FinishedKept = 100;

    private readonly MainWindowViewModel _owner;
    private readonly Dictionary<string, DownloadItem> _byPath = new(StringComparer.Ordinal);
    private int _sessionTotal;
    private int _sessionDone;

    internal DownloadsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        RemoteSongs.TransferChanged += OnTransfer;
    }

    public void Dispose() => RemoteSongs.TransferChanged -= OnTransfer;

    public ObservableCollection<DownloadItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(HasItems))]
    public partial int Waiting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int Active { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(HasFinished))]
    public partial int Done { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(HasFinished), nameof(HasFailed))]
    public partial int Failed { get; private set; }

    /// <summary>For the top bar while songs are coming down; null when nothing is.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>How far through what has been asked for since the queue was last empty, 0 to 100.</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    public bool HasItems => Items.Count > 0;

    public bool HasFinished => Done + Failed > 0 || Items.Any(i => i.IsFinished);

    public bool HasFailed => Failed > 0;

    public bool IsBusy => Waiting + Active > 0;

    /// <summary>"12 left", for the top bar's Downloads button while songs are coming down.</summary>
    public string Badge => $"{Waiting + Active} left";

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Active > 0) parts.Add($"{Active} downloading");
            if (Waiting > 0) parts.Add($"{Waiting} waiting");
            if (Done > 0) parts.Add($"{Done} done");
            if (Failed > 0) parts.Add($"{Failed} failed");
            return parts.Count == 0 ? "Nothing is downloading." : string.Join(", ", parts);
        }
    }

    private void OnTransfer(RemoteTransfer transfer) => Dispatcher.UIThread.Post(() => Apply(transfer));

    internal void Apply(RemoteTransfer transfer)
    {
        bool isNew = !_byPath.TryGetValue(transfer.Path, out DownloadItem? item);
        if (isNew)
        {
            var track = _owner.Database.FindTrack(transfer.Path);
            item = new DownloadItem(transfer.Path, track?.DisplayTitle ?? transfer.Path, track is null ? "" : $"{track.Artist}  ·  {track.Album}".Trim(' ', '·'));
            _byPath[transfer.Path] = item;
            Items.Add(item);
        }

        // "How far" is counted as songs come in and finish, until the queue is empty again.
        bool wasWork = !isNew && !item!.IsFinished;
        bool nowFinished = transfer.State is RemoteTransferState.Done or RemoteTransferState.Failed or RemoteTransferState.Cancelled;
        if (!wasWork && !nowFinished)
            _sessionTotal++;   // a song coming in, or asked for again after it ended
        else if (!wasWork && nowFinished && isNew)
            (_sessionTotal, _sessionDone) = (_sessionTotal + 1, _sessionDone + 1);
        else if (wasWork && nowFinished)
            _sessionDone++;

        item!.State = transfer.State;
        item.Received = transfer.Received;
        item.Total = transfer.Total;
        item.Error = transfer.Error;

        Recount();
        TrimFinished();
    }

    private void Recount()
    {
        Waiting = Items.Count(i => i.State == RemoteTransferState.Waiting);
        Active = Items.Count(i => i.State == RemoteTransferState.Downloading);
        Done = Items.Count(i => i.State == RemoteTransferState.Done);
        Failed = Items.Count(i => i.State == RemoteTransferState.Failed);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(Badge));

        if (Waiting + Active == 0)
        {
            _sessionTotal = _sessionDone = 0;
            Status = null;
            Percent = 0;
            return;
        }
        double partial = Items.Where(i => i.IsDownloading).Sum(i => i.Percent / 100);
        Status = $"Downloading songs: {_sessionDone} of {_sessionTotal}";
        Percent = Math.Clamp(100 * (_sessionDone + partial) / Math.Max(1, _sessionTotal), 0, 100);
    }
    private void TrimFinished()
    {
        foreach (DownloadItem old in Items.Where(i => i.IsFinished).SkipLast(FinishedKept).ToList())
        {
            Items.Remove(old);
            _byPath.Remove(old.Path);
        }
    }

    /// <summary>Takes everything out of the queue and stops what it started. A song a deck is waiting for finishes.</summary>
    [RelayCommand]
    private void StopAll() => RemoteSongs.StopAll();

    /// <summary>Asks again for the songs that failed.</summary>
    [RelayCommand]
    private void RetryFailed() =>
        RemoteSongs.Enqueue(Items.Where(i => i.State == RemoteTransferState.Failed).Select(i => i.Path).ToList());

    /// <summary>Takes the finished ones (done, failed, stopped) off the list.</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (DownloadItem finished in Items.Where(i => i.IsFinished).ToList())
        {
            Items.Remove(finished);
            _byPath.Remove(finished.Path);
        }
        Recount();
    }
}
