using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GZJam.App.ViewModels;
using GZJam.Library;

namespace GZJam.App.Views;

public partial class MainWindow : Window
{
    private readonly DragStarter _gridDrag;
    private readonly DragStarter _queueDrag;

    private PixelPoint _normalPosition;
    private Size _normalSize;

    public MainWindow()
    {
        InitializeComponent();
        RevealMenuItem.Header = FileManager.RevealLabel;
        SetUpSongInfo();
        SetUpDownloads();

        TrackGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        _gridDrag = new DragStarter(this, TrackGrid, item => DragFromLibrary((Track)item), TrackMenu);

        QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        QueueList.AddHandler(KeyDownEvent, OnQueueKeyDown, RoutingStrategies.Tunnel);
        _queueDrag = new DragStarter(this, QueueList, item => DragFromQueue((QueueItem)item), QueueMenu);

        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        // Ahead of the lists and the search box, which would take Ctrl+arrows for themselves.
        AddHandler(KeyDownEvent, OnVolumeKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnFullScreenKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnDeckKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                ShowFullScreen();
        };

        PositionChanged += (_, _) => RememberNormalBounds();
        Resized += (_, _) => RememberNormalBounds();

        InitializeDrag();
        InitializeFileDrop();
        InitializeDecks();
        InitializeHistory();
        InitializeSearch();
        InitializeColumns();
        InitializeQueueColumns();
        InitializePlaylists();
        InitializeDoubleClick();
        InitializePanes();
        UseTopBarAsTitleBar();
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private bool _closeConfirmed;
    private bool _askingToClose;

    /// <summary>
    /// While the room is hearing a deck, closing (the close button, Alt+F4, Cmd+Q) asks first, so
    /// a stray click or tap can't stop the music. Not when the computer is shutting down: GZJam
    /// mustn't hold that up.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && e.CloseReason != WindowCloseReason.OSShutdown
            && DataContext is MainWindowViewModel vm && vm.WhatTheRoomIsHearing() is { } playing)
        {
            e.Cancel = true;
            if (!_askingToClose)
                _ = AskBeforeClosingAsync(vm, playing);
            return;
        }
        base.OnClosing(e);
    }

    private async Task AskBeforeClosingAsync(MainWindowViewModel vm, string playing)
    {
        _askingToClose = true;
        try
        {
            bool close = vm.ConfirmCloseAsync is not { } ask || await ask(playing);
            Log.Info($"Asked before closing ({playing}): {(close ? "closed" : "kept playing")}");
            if (close)
                CloseWithoutAsking();
        }
        finally
        {
            _askingToClose = false;
        }
    }

    /// <summary>Closes even while music is playing (after the DJ has said so, and in tests).</summary>
    public void CloseWithoutAsking()
    {
        _closeConfirmed = true;
        Close();
    }

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    // ───────────── Track list ─────────────

    private List<Track> SelectedTracks() => TrackGrid.SelectedItems.OfType<Track>().ToList();

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { DataContext: Track track })
            return;

        var point = e.GetCurrentPoint(TrackGrid);
        if (point.Properties.IsRightButtonPressed)
        {
            // Right-clicking outside the selection acts on the clicked row, like a file manager.
            if (!TrackGrid.SelectedItems.Contains(track))
                TrackGrid.SelectedItem = track;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _gridDrag.Pressed(e, track);
        }
    }

    /// <summary>Dragging a library row drags the selection if the row is in it, else that row alone.</summary>
    private TrackDrag DragFromLibrary(Track pressed)
    {
        var tracks = SelectedTracks();
        if (!tracks.Contains(pressed))
        {
            TrackGrid.SelectedItem = pressed;
            tracks = [pressed];
        }
        return new TrackDrag(tracks, null);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Vm.AddToQueue(SelectedTracks(), 0);
                break;
            case Key.Enter:
                Vm.AddToQueue(SelectedTracks());
                break;
            case Key.Space:
                PreviewSelected();
                break;
            case Key.Delete when Vm.Library.IsPlaylistOpen:
                Vm.RemoveFromPlaylist(SelectedTracks());
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnAddToQueueClick(object? sender, RoutedEventArgs e) => Vm.AddToQueue(SelectedTracks());

    private void OnPlayNextClick(object? sender, RoutedEventArgs e) => Vm.AddToQueue(SelectedTracks(), 0);

    /// <summary>Auto-play's options opening: its stop times start from the next quarter hour.</summary>
    private void OnAutoPlayOptionsOpening(object? sender, EventArgs e) => Vm.AutoPlay?.RefreshStopChoices();

    private void OnShowInTreeClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedTracks().FirstOrDefault() is not { } track)
            return;
        Vm.Library.RevealTrack(track);
        // Scroll once the expanded folders have been laid out.
        Dispatcher.UIThread.Post(() =>
        {
            if (Vm.Library.SelectedFolder is { } folder && FolderTree.TreeContainerFromItem(folder) is Control container)
                container.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void OnRevealFileClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedTracks().FirstOrDefault() is { } track)
            FileManager.Reveal(track.Path);
    }

    // ───────────── Search box ─────────────

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (OnSuggestionKey(e))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Vm.Library.SearchText = "";
                e.Handled = true;
                break;
            case Key.Down when Vm.Library.Results.Count > 0:
                // Arrow down from the search box into the results, keyboard-only.
                TrackGrid.SelectedIndex = 0;
                TrackGrid.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>One press of a volume key, in the sliders' percent: small, so a held key can't blast the room.</summary>
    internal const double VolumeKeyStep = 2;

    /// <summary>
    /// Ctrl+↑/↓ (Cmd on macOS) turns MAIN up or down, with Shift the PHONES (#132): from anywhere,
    /// the search box too, as they type nothing. Held, the key repeats.
    /// </summary>
    private void OnVolumeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || Vm.Mixer is not { } mixer
            || (e.KeyModifiers & ~KeyModifiers.Shift) != CommandModifier)
            return;
        double step = e.Key == Key.Up ? VolumeKeyStep : -VolumeKeyStep;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            mixer.CueVolume += step;
        else
            mixer.MainVolume += step;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+F on Windows and Linux, Cmd+F on macOS.
        if (e.Key == Key.Escape && IsDragging)
        {
            EndDrag();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && IsSettingsOpen)
        {
            CloseSettings();
            e.Handled = true;
            return;
        }
        // Ctrl+D: compact decks (or back from hidden); with Shift, hide them (#164).
        if (e.Key == Key.D && (e.KeyModifiers & ~KeyModifiers.Shift) == CommandModifier)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                Vm.ToggleHideDecksCommand.Execute(null);
            else
                Vm.ToggleCompactDecksCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl+L: the queue takes the room in the middle, or the song list does (#178).
        if (e.Key == Key.L && e.KeyModifiers == CommandModifier)
        {
            ToggleQueueWide();
            e.Handled = true;
            return;
        }
        // Ctrl+, (the Mac's own key for Settings) or Ctrl+O for Options; again to close (#166).
        if (e.Key is Key.OemComma or Key.O && e.KeyModifiers == CommandModifier)
        {
            if (IsSettingsOpen)
                CloseSettings();
            else
                OpenSettings();
            e.Handled = true;
            return;
        }
        // Ctrl+I (Cmd+I, like the Mac's Get Info): the selected song's info (#129).
        if (e.Key == Key.I && e.KeyModifiers == CommandModifier && ShowSelectedSongInfo())
        {
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+J: the downloads from the Navidrome server.
        if (e.Key == Key.J && e.KeyModifiers == (CommandModifier | KeyModifiers.Shift))
        {
            ShowDownloadsWindow();
            e.Handled = true;
            return;
        }
        // Ctrl+Shift+N: the Navidrome server's songs.
        if (e.Key == Key.N && e.KeyModifiers == (CommandModifier | KeyModifiers.Shift))
        {
            ShowNavidrome();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(CommandModifier))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ───────────── Queue ─────────────

    private List<QueueItem> SelectedQueueItems() => QueueList.SelectedItems?.OfType<QueueItem>().ToList() ?? [];

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: QueueItem item })
            return;

        var point = e.GetCurrentPoint(QueueList);
        if (point.Properties.IsRightButtonPressed)
        {
            if (QueueList.SelectedItems?.Contains(item) != true)
                QueueList.SelectedItem = item;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            bool onHandle = (e.Source as Control)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("grip");
            _queueDrag.Pressed(e, item, onHandle);
        }
    }

    /// <summary>Dragging a queue row drags the selection if the row is in it, else that row alone.</summary>
    private TrackDrag DragFromQueue(QueueItem pressed)
    {
        var items = SelectedQueueItems();
        if (!items.Contains(pressed))
        {
            QueueList.SelectedItems?.Clear();
            QueueList.SelectedItem = pressed;
            items = [pressed];
        }
        return new TrackDrag(items.Select(i => i.Track).ToList(), items);
    }

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                RemoveSelectedFromQueue();
                break;
            case Key.Space when SelectedQueueItems().FirstOrDefault() is { } item:
                Vm.Preview?.Toggle(item.Track);   // as in the library (#134)
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Takes the selected songs out of the queue and selects the one that took their place (or
    /// the last), keeping the keyboard in the queue, so ↑ ↓ and Delete go on working (#133).
    /// </summary>
    private void RemoveSelectedFromQueue()
    {
        var items = SelectedQueueItems();
        if (items.Count == 0)
            return;
        int first = items.Min(i => Vm.Queue.Items.IndexOf(i));
        Vm.Queue.Remove(items);
        if (Vm.Queue.Items.Count == 0)
            return;
        int next = Math.Min(first, Vm.Queue.Items.Count - 1);
        QueueList.SelectedIndex = next;
        Dispatcher.UIThread.Post(() =>
        {
            if (QueueList.ContainerFromIndex(next) is ListBoxItem row)
                row.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Where a drop at <paramref name="position"/> would insert, and where to draw the insertion line.</summary>
    private int DropIndex(Point position, out double lineY)
    {
        int count = Vm.Queue.Items.Count;
        lineY = 8;
        for (int i = 0; i < count; i++)
        {
            if (QueueList.ContainerFromIndex(i) is not Control container || container.TranslatePoint(default, QueueArea) is not { } top)
                continue;
            if (position.Y < top.Y + container.Bounds.Height / 2)
            {
                lineY = top.Y - 3;
                return i;
            }
            lineY = top.Y + container.Bounds.Height + 1;
        }
        return count;
    }

    private void OnQueueMoveToTopClick(object? sender, RoutedEventArgs e) => Vm.Queue.Move(SelectedQueueItems(), 0);

    private void OnQueueRemoveClick(object? sender, RoutedEventArgs e) => Vm.Queue.Remove(SelectedQueueItems());

    private void OnQueueShuffleClick(object? sender, RoutedEventArgs e) => Vm.ShuffleQueue();

    private void OnQueueClearClick(object? sender, RoutedEventArgs e) => Vm.Queue.Clear();

    // ───────────── Library folders ─────────────

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a music folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            await Vm.Library.AddRootAsync(path);
    }

    // ───────────── Window placement ─────────────

    public void RestorePlacement(AppSettings.WindowPlacement? placement) => RestorePlacement(this, placement);

    /// <summary>
    /// Puts <paramref name="window"/> where the main window was last time: the main window itself,
    /// and the startup screen, which opens in the same place so the one becomes the other.
    /// </summary>
    internal static void RestorePlacement(Window window, AppSettings.WindowPlacement? placement)
    {
        if (placement is null || placement.Width < window.MinWidth || placement.Height < window.MinHeight)
            return;

        window.Width = placement.Width;
        window.Height = placement.Height;
        // Only restore the position if it's still on a connected screen (e.g. after undocking).
        var bounds = new PixelRect(placement.X, placement.Y, (int)placement.Width, (int)placement.Height);
        if (window.Screens.All.Any(s => s.WorkingArea.Intersects(bounds)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(placement.X, placement.Y);
        }
        if (placement.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    public AppSettings.WindowPlacement CapturePlacement()
    {
        RememberNormalBounds();
        return new AppSettings.WindowPlacement
        {
            X = _normalPosition.X,
            Y = _normalPosition.Y,
            Width = _normalSize.Width,
            Height = _normalSize.Height,
            // Full screen comes back maximized: a booth laptop never opens to a screen with no way out in sight.
            Maximized = WindowState is WindowState.Maximized or WindowState.FullScreen,
        };
    }

    /// <summary>Tracks the un-maximized size and position, so restoring a maximized window un-maximizes sensibly.</summary>
    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal)
            return;
        _normalPosition = Position;
        _normalSize = new Size(Width, Height);
    }
}
