using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GZJam.App.ViewModels;
using GZJam.Library;

namespace GZJam.App.Views;

/// <summary>
/// Playlists: listed under the folder tree; one opens in the song list in its own order (numbered,
/// with a bar above to add it to the queue). Songs are added by dragging them onto a playlist's
/// name or from the songs' menu, and reordered by dragging rows within an open playlist.
/// </summary>
public partial class MainWindow
{
    private void InitializePlaylists()
    {
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Library.PropertyChanged += OnLibraryChangedForPlaylists;
                ApplyLibraryColumn();
            }
        };
        LibraryColumn.SizeChanged += (_, _) => ApplyLibraryColumn();
        PlaylistsGrip.PointerPressed += OnPlaylistsGripPressed;
        PlaylistsGrip.PointerMoved += OnPlaylistsGripMoved;
        PlaylistsGrip.PointerReleased += OnPlaylistsGripReleased;
    }

    private void OnLibraryChangedForPlaylists(object? sender, PropertyChangedEventArgs e)
    {
        // A playlist shows in its own order: whatever column the library was sorted by doesn't apply.
        if (e.PropertyName == nameof(LibraryViewModel.IsPlaylistOpen) && Vm.Library.IsPlaylistOpen)
            TrackGrid.CollectionView?.SortDescriptions.Clear();
        else if (e.PropertyName == nameof(LibraryViewModel.AreFoldersFolded))
            ApplyLibraryColumn();
    }

    // ───────────── Sharing the column with the folders (#163) ─────────────

    /// <summary>How tall the playlist list may grow until the DJ drags its grip (about eight rows).</summary>
    internal const double DefaultPlaylistsHeight = 220;

    /// <summary>The least the grip leaves either side: about two rows of playlists, or of folders.</summary>
    private const double MinPlaylistsHeight = 60, MinFoldersHeight = 70;

    private double _playlistsGripFrom, _playlistsHeightFrom;

    /// <summary>
    /// Folded, the folder list is only as tall as its music folders and the playlists take the
    /// rest. Otherwise the folders take the room and the playlists grow up to the height dragged
    /// (220 to start with), always leaving the folders a little.
    /// </summary>
    private void ApplyLibraryColumn()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        bool folded = vm.Library.AreFoldersFolded;
        LibraryColumn.RowDefinitions[1].Height = folded ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        LibraryColumn.RowDefinitions[2].Height = folded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        PlaylistList.MaxHeight = folded ? double.PositiveInfinity : ClampPlaylistsHeight(vm.PlaylistsHeight ?? DefaultPlaylistsHeight);
    }

    private double ClampPlaylistsHeight(double height)
    {
        // What else the column holds: its header, the playlists' header and the margins around the lists.
        double others = LibraryColumn.RowDefinitions[0].ActualHeight + PlaylistsSection.Bounds.Height - PlaylistList.Bounds.Height
            + FolderTree.Margin.Top + FolderTree.Margin.Bottom;
        double most = LibraryColumn.Bounds.Height - others - MinFoldersHeight;
        if (LibraryColumn.Bounds.Height > 0 && most > MinPlaylistsHeight)
            height = Math.Min(height, most);
        return Math.Max(MinPlaylistsHeight, height);
    }

    internal double PlaylistsMaxHeight => PlaylistList.MaxHeight;

    private void OnPlaylistsGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _playlistsGripFrom = e.GetPosition(this).Y;
        _playlistsHeightFrom = PlaylistList.Bounds.Height;
        e.Pointer.Capture(PlaylistsGrip);
        e.Handled = true;
    }

    /// <summary>Up: taller playlists, down: shorter, following the pointer.</summary>
    private void OnPlaylistsGripMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured != PlaylistsGrip)
            return;
        double height = ClampPlaylistsHeight(_playlistsHeightFrom - (e.GetPosition(this).Y - _playlistsGripFrom));
        PlaylistList.MaxHeight = height;
        Vm.PlaylistsHeight = height;
    }

    private void OnPlaylistsGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != PlaylistsGrip)
            return;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnNewPlaylistClick(object? sender, RoutedEventArgs e) => _ = Vm.CreatePlaylistAsync();

    private void OnPlaylistToQueueClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist)
            Vm.AddPlaylistToQueue(playlist);
    }

    private void OnPlaylistToQueueShuffledClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist)
            Vm.AddPlaylistToQueue(playlist, shuffled: true);
    }

    private void OnPlaylistPlayClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist)
            Vm.PlayPlaylist(playlist);
    }

    private void OnPlaylistPlayShuffledClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist)
            Vm.PlayPlaylist(playlist, shuffled: true);
    }

    private void OnRenameOpenPlaylistClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist)
            _ = Vm.RenamePlaylistAsync(playlist);
    }

    private void OnDeleteOpenPlaylistClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist)
            _ = Vm.DeletePlaylistAsync(playlist);
    }

    private static PlaylistInfo? PlaylistOf(object? sender) => (sender as Control)?.DataContext as PlaylistInfo;

    private void OnPlaylistItemToQueueClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            Vm.AddPlaylistToQueue(playlist);
    }

    private void OnPlaylistItemToQueueShuffledClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            Vm.AddPlaylistToQueue(playlist, shuffled: true);
    }

    private void OnPlaylistItemPlayClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            Vm.PlayPlaylist(playlist);
    }

    private void OnPlaylistItemPlayShuffledClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            Vm.PlayPlaylist(playlist, shuffled: true);
    }

    private void OnCachePlaylistClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            Vm.CachePlaylist(playlist);
    }

    private void OnRefreshPlaylistsClick(object? sender, RoutedEventArgs e) => _ = Vm.SyncNavidromeAsync();

    /// <summary>The songs' menu item: fetch the selected songs from the server ahead of time.</summary>
    private void OnCacheSelectedClick(object? sender, RoutedEventArgs e) => Vm.CacheForOffline(SelectedTracks(), "The selected songs");

    private void OnCacheQueueSelectedClick(object? sender, RoutedEventArgs e) =>
        Vm.CacheForOffline(SelectedQueueItems().Select(i => i.Track), "The selected songs");

    /// <summary>Shows "Cache for offline play" on a songs' menu only where one of the songs comes from the server, and says how many aren't here yet.</summary>
    private void FillCacheItem(MenuItem item, IReadOnlyList<Track> tracks)
    {
        int notHere = Vm.NotHere(tracks);
        bool fromServer = tracks.Any(t => GZJam.Audio.RemoteSongs.IsRemote(t.Path));
        item.IsVisible = fromServer;
        item.IsEnabled = notHere > 0;
        item.Header = notHere > 0 ? $"Cache for offline play ({notHere} {(notHere == 1 ? "song" : "songs")})" : "On this computer already";
    }
    private void OnRenamePlaylistItemClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            _ = Vm.RenamePlaylistAsync(playlist);
    }

    private void OnDeletePlaylistItemClick(object? sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is { } playlist)
            _ = Vm.DeletePlaylistAsync(playlist);
    }

    private void OnPlaylistFromHereClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.Library.SelectedPlaylist is { } playlist && SelectedTracks().MinBy(t => t.PlaylistNumber) is { } from)
            Vm.AddPlaylistToQueue(playlist, from);
    }

    private void OnRemoveFromPlaylistClick(object? sender, RoutedEventArgs e) => Vm.RemoveFromPlaylist(SelectedTracks());

    private void OnQueueToPlaylistClick(object? sender, RoutedEventArgs e) => _ = Vm.SaveQueueAsPlaylistAsync();

    private void OnSessionToPlaylistClick(object? sender, RoutedEventArgs e) => _ = Vm.SaveSessionAsPlaylistAsync();

    /// <summary>The songs' menu: "Add to playlist" › each playlist, and "New playlist…"; the open playlist's own items.</summary>
    private void FillPlaylistItems(IReadOnlyList<Track> tracks)
    {
        bool open = Vm.Library.IsPlaylistOpen;
        TrackFromHereMenuItem.IsVisible = open;
        RemoveFromPlaylistMenuItem.IsVisible = open;
        TrackFromHereMenuItem.IsEnabled = RemoveFromPlaylistMenuItem.IsEnabled = tracks.Count > 0;
        PlaylistsMenu.Fill(TrackPlaylistsMenu, Vm, tracks, except: Vm.Library.SelectedPlaylist?.Id);
    }

    // ───────────── Dragging onto playlists, and within one ─────────────

    /// <summary>The playlist row under <paramref name="at"/> (window coordinates), if any.</summary>
    private ListBoxItem? PlaylistItemAt(Point at)
    {
        if (!IsOver(PlaylistList, at))
            return null;
        return PlaylistList.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => IsOver(item, at));
    }

    /// <summary>
    /// Where songs dropped at <paramref name="at"/> would go in the open playlist: before the
    /// entry with this index (from 0), and the line's height in the song list. Null if the
    /// playlist can't be reordered (it's filtered by a search) or the point isn't over the list.
    /// </summary>
    private (int Index, double LineY)? PlaylistDropAt(Point at)
    {
        if (!Vm.Library.CanReorderPlaylist || !IsOver(TrackGrid, at))
            return null;
        var rows = TrackGrid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is Track { PlaylistNumber: not null })
            .Select(r => (Row: r, Top: r.TranslatePoint(default, TrackGrid)?.Y ?? double.NaN))
            .Where(r => !double.IsNaN(r.Top))
            .OrderBy(r => r.Top)
            .ToList();
        double y = this.TranslatePoint(at, TrackGrid)?.Y ?? 0;
        double gridTop = TrackGrid.TranslatePoint(default, (Visual)PlaylistDropLine.Parent!)?.Y ?? 0;
        foreach (var (row, top) in rows)
        {
            if (y < top + row.Bounds.Height / 2)
                return (((Track)row.DataContext!).PlaylistNumber!.Value - 1, gridTop + top);
        }
        if (rows.Count == 0)
            return (0, gridTop + (TrackGrid.Bounds.Height > 40 ? 34 : 0));
        var (last, lastTop) = rows[^1];
        return (((Track)last.DataContext!).PlaylistNumber!.Value, gridTop + lastTop + last.Bounds.Height);
    }

    /// <summary>Shows where a drag over the playlists or an open playlist would put the songs.</summary>
    private void UpdatePlaylistDrop(Point at)
    {
        ListBoxItem? over = PlaylistItemAt(at);
        foreach (ListBoxItem item in PlaylistList.GetVisualDescendants().OfType<ListBoxItem>())
            item.Classes.Set("drop-target", item == over);
        if (PlaylistDropAt(at) is { } drop)
        {
            PlaylistDropLine.Margin = new Thickness(8, drop.LineY - 1, 8, 0);
            PlaylistDropLine.IsVisible = true;
        }
        else
        {
            PlaylistDropLine.IsVisible = false;
        }
    }

    private void EndPlaylistDrop()
    {
        foreach (ListBoxItem item in PlaylistList.GetVisualDescendants().OfType<ListBoxItem>())
            item.Classes.Set("drop-target", false);
        PlaylistDropLine.IsVisible = false;
    }

    /// <summary>
    /// The playlist songs dropped at <paramref name="at"/> would go into: one whose name is there
    /// (at its end, Index null), or the open playlist, before entry Index. Null for neither.
    /// </summary>
    private (PlaylistInfo Playlist, int? Index)? PlaylistTargetAt(Point at)
    {
        if (PlaylistItemAt(at)?.DataContext is PlaylistInfo playlist)
            return (playlist, null);
        if (PlaylistDropAt(at) is { } drop && Vm.Library.SelectedPlaylist is { } open)
            return (open, drop.Index);
        return null;
    }

    private static string PlaylistPlace((PlaylistInfo Playlist, int? Index) target) => target.Index is { } index
        ? $"position {index + 1} in playlist \"{target.Playlist.Name}\""
        : $"playlist \"{target.Playlist.Name}\"";

    /// <summary>
    /// Drops songs on a playlist's name (added at its end) or into the open playlist (moved, if
    /// they're its own rows; else added there). True if it was a playlist drop.
    /// </summary>
    private bool DropOnPlaylist(TrackDrag drag, Point at, out string where)
    {
        if (PlaylistTargetAt(at) is not { } target)
        {
            where = "";
            return false;
        }
        where = PlaylistPlace(target);
        if (target.Index is { } index && drag.Tracks.All(t => t.PlaylistEntryId is not null) && drag.FromQueue is null)
            Vm.MoveInPlaylist(drag.Tracks, index);
        else
            Vm.AddToPlaylist(target.Playlist, drag.Tracks, target.Index);
        return true;
    }
}
