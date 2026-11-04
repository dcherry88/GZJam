using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using GZJam.App.ViewModels;
using GZJam.Library;

namespace GZJam.App.Views;

/// <summary>Decks, headphone preview and settings: loading tracks onto decks (drag or menu), with deck protection.</summary>
public partial class MainWindow
{
    private void InitializeDecks()
    {
        PreviewSeek.SeekRequested += (_, fraction) => Vm.Preview?.SeekTo(fraction);
        FlyoutMenus.FillAsItOpens(TrackMenu, _ => OnTrackMenuOpening());
        FlyoutMenus.FillAsItOpens(QueueMenu, _ => OnQueueMenuOpening());
        SettingsPanel.CloseRequested += (_, _) => CloseSettings();
        SettingsPanel.QuitRequested += (_, _) =>
        {
            CloseSettings();
            Close();   // asks first while the room hears a deck
        };
        DeckGrip.PointerPressed += OnDeckGripPressed;
        DeckGrip.PointerMoved += OnDeckGripMoved;
        DeckGrip.PointerReleased += OnDeckGripReleased;
        SettingsPanel.MixxxImportRequested += (_, _) => ImportFromMixxx();
        SettingsPanel.PlaylistImportRequested += (_, _) => ImportPlaylists();
        SettingsPanel.FileRatingsRequested += (_, _) => ImportFileRatings();
        // Touch mode's sizes are app-wide; take them away with the window (tests open many windows).
        Closed += (_, _) => GZJam.App.Styles.TouchStyles.Apply(false);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm)
                return;
            vm.ConfirmAsync = (heading, message, confirm) => new ConfirmDialog(heading, message, confirm).ShowDialog<bool>(this);
            vm.ConfirmCloseAsync = playing => new ConfirmDialog("Stop the music and close GZJam?", playing, "Close GZJam",
                cancelText: "Keep playing", cancelIsDefault: true).ShowDialog<bool>(this);
            vm.ConfirmImportAsync = (heading, message, confirm) =>
            {
                var dialog = new ConfirmDialog(heading, message, confirm);
                dialog.ConfirmIsHarmless();
                return dialog.ShowDialog<bool>(this);
            };
            vm.PromptAsync = (heading, message, ok, initial) => new PromptDialog(heading, message, ok, initial).ShowDialog<string?>(this);
            vm.AskAsync = (heading, message, yes, no) =>
            {
                var dialog = new ConfirmDialog(heading, message, yes, cancelText: no);
                dialog.ConfirmIsHarmless();
                return dialog.ShowDialog<bool>(this);
            };
            vm.PickFolderAsync = async title =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
                return folders is [var folder] ? folder.TryGetLocalPath() : null;
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainWindowViewModel.FourDecks) or nameof(MainWindowViewModel.CompactDecks)
                    or nameof(MainWindowViewModel.DecksHidden))
                    ApplyDeckLayout();
            };
            vm.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.TouchMode))
                {
                    ApplyTouchMode();
                    ApplyDeckLayout();   // the compact mixer's crossfader is finger-sized too
                }
            };
            if (vm.AirLine is { } airLine)
            {
                airLine.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AirLineViewModel.Now))
                        ColorAirLine(airLine.Now);
                };
                ColorAirLine(airLine.Now);
            }
            ApplyDeckLayout();
            ApplyTouchMode();
        };
    }

    /// <summary>The hidden decks' line (#164) in the colors of the deck it shows, as the deck itself is.</summary>
    private void ColorAirLine(DeckViewModel? deck)
    {
        IBrush accent = deck is not null && this.TryFindResource($"Deck{deck.Label}Brush", out object? found) && found is IBrush brush
            ? brush
            : (IBrush)this.FindResource("MutedBrush")!;
        AirDeckName.Foreground = AirTimeLeft.Foreground = AirPlayButton.Foreground = AirPlayButton.BorderBrush = accent;
    }

    private DeckView[] DeckViews => [Deck1View, Deck2View, Deck3View, Deck4View];

    /// <summary>Finger-sized controls everywhere, or the usual sizes (Settings › Touch mode).</summary>
    private void ApplyTouchMode()
    {
        bool on = Vm.Settings.TouchMode;
        GZJam.App.Styles.TouchStyles.Apply(on);
        foreach (DeckView view in DeckViews)
            view.IsTouch = on;
    }

    /// <summary>
    /// Two full-height decks, or four at half height in the same space. Four share it equally,
    /// so the upper and lower decks line up whatever the mixer beside them needs.
    /// Compact decks (#128): two decks at half height, or four without their waveforms, and the
    /// mixer beside them no taller: its faders shorten, PHONES MIX goes (it still applies), and the
    /// side keys move either side of the crossfader.
    /// </summary>
    private void ApplyDeckLayout()
    {
        bool four = Vm.FourDecks, compact = Vm.CompactDecks;
        foreach (DeckView view in DeckViews)
        {
            view.IsCompact = four || compact;
            view.IsMini = four && compact;
        }
        Deck3View.IsVisible = Deck4View.IsVisible = four;
        DecksRow.IsVisible = !Vm.DecksHidden;   // the air line stands in (#164)
        foreach (Grid side in new[] { LeftDecks, RightDecks })
        {
            side.RowDefinitions[1].Height = four ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            side.RowSpacing = four ? DeckView.CompactGap : 0;
        }

        MixerPanel.MaxHeight = compact ? DeckView.CompactHeight : double.PositiveInfinity;
        if (compact)
        {
            // No taller than the decks need (four without waveforms, or touch mode's bigger keys,
            // need more than a compact deck's height): known once they've been measured.
            Dispatcher.UIThread.Post(() =>
            {
                if (Vm.CompactDecks && !Vm.DecksHidden)
                    MixerPanel.MaxHeight = Math.Max(LeftDecks.DesiredSize.Height, RightDecks.DesiredSize.Height);
            }, DispatcherPriority.Loaded);
        }
        MixerPanel.Padding = compact ? new Thickness(12, 8) : new Thickness(14, 12);
        PhonesMixRow.IsVisible = !compact;
        LiveLabel.IsVisible = !compact;
        Grid.SetRow(Crossfader, compact ? 1 : 0);
        Grid.SetColumn(Crossfader, compact ? 1 : 0);
        Grid.SetColumnSpan(Crossfader, compact ? 1 : 3);
        Crossfader.Margin = compact ? new Thickness(6, 0) : default;
        CrossfaderRows.RowSpacing = compact ? 0 : 6;
        CrossfaderRows.Classes.Set("compact", compact);
        if (compact)
            MixerPanel.Resources["CrossfaderHeight"] = Vm.Settings.TouchMode ? 40.0 : 30.0;
        else
            MixerPanel.Resources.Remove("CrossfaderHeight");
    }

    // ───────────── The grip under the decks ─────────────

    /// <summary>How far the grip must be dragged to switch; less, and letting go counts as a tap.</summary>
    private const double DeckGripSwitchDistance = 16;

    /// <summary>How far for a second step in the same drag: full to hidden, or hidden to full (#171).</summary>
    private const double DeckGripSecondStep = 76;

    private double _deckGripFrom;
    private int _deckSizeFrom, _deckGripSize;
    private bool _deckGripMoved;

    private void OnDeckGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _deckGripFrom = e.GetPosition(this).Y;
        _deckSizeFrom = _deckGripSize = DeckSize;
        _deckGripMoved = false;
        e.Pointer.Capture(DeckGrip);
        e.Handled = true;
    }

    /// <summary>
    /// The size follows the finger from where the grip was pressed (full, compact, hidden; #164,
    /// #171): up past the switching distance, one step smaller, and further up a second; down the
    /// same for bigger; back again, as it was.
    /// </summary>
    private void OnDeckGripMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured != DeckGrip)
            return;
        double moved = e.GetPosition(this).Y - _deckGripFrom;
        if (Math.Abs(moved) > 4)
            _deckGripMoved = true;
        double distance = Math.Abs(moved);
        int steps = distance >= DeckGripSecondStep ? 2 : distance >= DeckGripSwitchDistance ? 1 : 0;
        int step = moved < 0 ? steps : -steps;
        int size = Math.Clamp(_deckSizeFrom + step, 0, 2);
        if (size == _deckGripSize)
            return;
        _deckGripSize = size;
        if (size == 2)
            Vm.DecksHidden = true;
        else if (_deckSizeFrom == 2 && size == 1)
            Vm.DecksHidden = false;   // one step down: back as they were, compact or not
        else
        {
            Vm.DecksHidden = false;
            Vm.CompactDecks = size == 1;
        }
    }

    /// <summary>The decks' size as a step: 0 full, 1 compact, 2 hidden.</summary>
    private int DeckSize => Vm.DecksHidden ? 2 : Vm.CompactDecks ? 1 : 0;

    private void OnDeckGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured != DeckGrip)
            return;
        e.Pointer.Capture(null);
        if (!_deckGripMoved)
            Vm.ToggleCompactDecksCommand.Execute(null);   // a tap: hidden decks come back
        e.Handled = true;
    }

    private DeckViewModel? DeckAt(int index) => Vm.Decks.ElementAtOrDefault(index);

    // ───────────── Import from Mixxx ─────────────

    private async void ImportFromMixxx()
    {
        var result = await new MixxxImportDialog(Vm.CreateMixxxImport()).ShowDialog<GZJam.Library.MixxxImportResult?>(this);
        if (result is not null)
            Vm.ShowNotice(MixxxImportViewModel.Summary(result));
    }

    // ───────────── Import from other players ─────────────

    private async void ImportPlaylists()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import playlists",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Playlists") { Patterns = PlaylistFile.Patterns }],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        await Vm.ImportPlaylistsAsync(paths);
    }

    private async void ImportFileRatings()
    {
        if (await new FileRatingsDialog(Vm.CreateFileRatingsImport()).ShowDialog<int?>(this) is int rated)
            Vm.ShowNotice(FileRatingsImportViewModel.Summary(rated));
    }

    // ───────────── Menus ─────────────

    /// <summary>Grays out "Load to Deck X" when protection would refuse it, and says why.</summary>
    private void OnTrackMenuOpening()
    {
        UpdateLoadMenuItems([TrackLoad1, TrackLoad2, TrackLoad3, TrackLoad4], SelectedTracks().Count == 1);
        var tracks = SelectedTracks();
        TrackEqItem.IsEnabled = tracks.Count == 1;
        TrackInfoItem.IsEnabled = tracks.Count > 0;
        TrackShowInTreeItem.IsEnabled = tracks.FirstOrDefault() is { IsOutsideLibrary: false };   // one outside the music folders isn't in the tree
        MarksMenu.FillRating(TrackRatingMenu, Vm, tracks);
        MarksMenu.FillEnergy(TrackEnergyMenu, Vm, tracks);
        LabelsMenu.Fill(TrackLabelsMenu, Vm, tracks);
        BpmMenu.Fill(TrackBpmMenu, Vm, tracks);
        FillPlaylistItems(tracks);
        FillCacheItem(TrackCacheItem, tracks);
    }

    private void OnQueueMenuOpening()
    {
        UpdateLoadMenuItems([QueueLoad1, QueueLoad2, QueueLoad3, QueueLoad4], SelectedQueueItems().Count == 1);
        var tracks = SelectedQueueItems().Select(i => i.Track).ToList();
        QueueEqItem.IsEnabled = tracks.Count == 1;
        QueueInfoItem.IsEnabled = tracks.Count > 0;
        QueueShuffleItem.IsEnabled = Vm.Queue.Items.Count > 1;
        MarksMenu.FillRating(QueueRatingMenu, Vm, tracks);
        MarksMenu.FillEnergy(QueueEnergyMenu, Vm, tracks);
        LabelsMenu.Fill(QueueLabelsMenu, Vm, tracks);
        BpmMenu.Fill(QueueBpmMenu, Vm, tracks);
        PlaylistsMenu.Fill(QueuePlaylistsMenu, Vm, tracks);
        FillCacheItem(QueueCacheItem, tracks);
        FillQueueColumnsMenu();
    }

    private void UpdateLoadMenuItems(MenuItem[] items, bool oneTrackSelected)
    {
        for (int index = 0; index < items.Length; index++)
        {
            MenuItem item = items[index];
            DeckViewModel? deck = DeckAt(index);
            item.IsVisible = index < Vm.ActiveDecks.Count;
            bool canLoad = deck is not null && Vm.CanLoad(deck);
            item.IsEnabled = oneTrackSelected && canLoad;
            item.Header = deck is not null && !canLoad
                ? $"Load to Deck {deck.Label}  (on air)"
                : $"Load to Deck {index + 1}";
        }
    }

    private async void OnLoadSelectedToDeckClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedTracks() is [var track] && DeckAt(int.Parse((string)((MenuItem)sender!).Tag!)) is { } deck)
            await Vm.LoadToDeckAsync(deck, track);
    }

    private async void OnLoadQueueToDeckClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedQueueItems() is [var item] && DeckAt(int.Parse((string)((MenuItem)sender!).Tag!)) is { } deck
            && await Vm.LoadToDeckAsync(deck, item.Track))
            Vm.Queue.Loaded([item]);   // loaded: it's no longer waiting in the queue (with Repeat, it goes to the end)
    }

    // ───────────── Headphone preview ─────────────

    private void OnPreviewTrackClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Track track)
            Vm.Preview?.Toggle(track);
    }

    private void OnRatingCellClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Track track)
            Vm.CycleRating(track);
    }

    private void OnEnergyCellClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Track track)
            Vm.CycleEnergy(track);
    }

    private void OnPreviewQueueItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is QueueItem item)
            Vm.Preview?.Toggle(item.Track);
    }

    private void OnPreviewSelectedClick(object? sender, RoutedEventArgs e) => PreviewSelected();

    private void OnPreviewQueueSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedQueueItems().FirstOrDefault() is { } item)
            Vm.Preview?.Toggle(item.Track);
    }

    private void PreviewSelected()
    {
        if (SelectedTracks().FirstOrDefault() is { } track)
            Vm.Preview?.Toggle(track);
    }

    // ───────────── Settings ─────────────

    /// <summary>Settings is open over the main screen.</summary>
    public bool IsSettingsOpen => SettingsOverlay.IsVisible;

    /// <summary>
    /// Opens Settings over the dimmed main screen, on <paramref name="section"/> or where it was
    /// last. The device list is read again, in case something was plugged in.
    /// </summary>
    public void OpenSettings(SettingsSection? section = null)
    {
        Vm.Settings.RefreshDevices();
        if (section is { } wanted)
            SettingsPanel.Section = wanted;
        SettingsOverlay.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(SettingsPanel.FocusDone, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    public void CloseSettings()
    {
        SettingsOverlay.IsVisible = false;
        SettingsButton.Focus();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>The new version's release page: what's new, and the downloads.</summary>
    private async void OnNewVersionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm.NewVersion is { } release)
            await Launcher.LaunchUriAsync(new Uri(release.Url));
    }

    private void OnNewVersionDismissClick(object? sender, RoutedEventArgs e) => Vm.DismissNewVersion();

    private void OnNoticeUndoClick(object? sender, RoutedEventArgs e) => Vm.UndoNotice();

    /// <summary>MAIN and CUE at the top open Settings on the outputs.</summary>
    private void OnOutputChipClick(object? sender, RoutedEventArgs e) => OpenSettings(SettingsSection.Sound);

    private void OnSettingsDimmerPressed(object? sender, PointerPressedEventArgs e)
    {
        CloseSettings();
        e.Handled = true;
    }
}
