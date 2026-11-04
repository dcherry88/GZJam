using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace GZJam.App.Views;

/// <summary>The sections down the side of Settings.</summary>
public enum SettingsSection
{
    Sound,
    Decks,
    Screen,
    Library,
    Navidrome,
    About,
}

/// <summary>
/// Settings, shown over the dimmed main screen (see <see cref="MainWindow.OpenSettings"/>). Music
/// keeps playing while it's open.
/// </summary>
public partial class SettingsPanel : UserControl
{
    private bool _aboutFilled;

    public SettingsPanel()
    {
        InitializeComponent();
        NavVersionText.Text = $"GZJam {About.Version}";
        foreach ((RadioButton nav, _) in Sections)
            nav.IsCheckedChanged += (_, _) => ShowCheckedSection();
    }

    /// <summary>DONE was pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>QUIT GZJAM was pressed.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Library › IMPORT… was pressed.</summary>
    public event EventHandler? MixxxImportRequested;

    /// <summary>Library › Import playlists was pressed.</summary>
    public event EventHandler? PlaylistImportRequested;

    /// <summary>Library › Ratings in the song files was pressed.</summary>
    public event EventHandler? FileRatingsRequested;

    private (RadioButton Nav, Control Page)[] Sections =>
        [(NavSound, SoundPage), (NavDecks, DecksPage), (NavScreen, ScreenPage), (NavLibrary, LibraryPage), (NavNavidrome, NavidromePage), (NavAbout, AboutPage)];

    /// <summary>The section showing.</summary>
    public SettingsSection Section
    {
        get => (SettingsSection)Math.Max(0, Array.FindIndex(Sections, s => s.Nav.IsChecked == true));
        set => Sections[(int)value].Nav.IsChecked = true;
    }

    /// <summary>Puts the keyboard on DONE, so keys don't reach the decks or the library behind.</summary>
    public void FocusDone() => DoneButton.Focus();

    private void ShowCheckedSection()
    {
        foreach ((RadioButton nav, Control page) in Sections)
            page.IsVisible = nav.IsChecked == true;
        if (AboutPage.IsVisible)
            FillAbout();
    }

    /// <summary>The About section reads the license notices, so only when it's first shown.</summary>
    private void FillAbout()
    {
        if (_aboutFilled)
            return;
        _aboutFilled = true;
        VersionText.Text = About.Version;
        MakerText.Text = $"A simple DJ app for social dance nights, made by {About.Maker}.";
        BuiltWithText.Text = About.BuiltWith();
        NoticesButton.IsVisible = File.Exists(About.NoticesPath);
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuitClick(object? sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void OnMixxxImportClick(object? sender, RoutedEventArgs e) => MixxxImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnPlaylistImportClick(object? sender, RoutedEventArgs e) => PlaylistImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnFileRatingsClick(object? sender, RoutedEventArgs e) => FileRatingsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnGuideClick(object? sender, RoutedEventArgs e) =>
        await TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(new Uri(About.GuideUrl));

    private async void OnProjectClick(object? sender, RoutedEventArgs e) =>
        await TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(new Uri(About.ProjectUrl));

    private static readonly FilePickerFileType BackupFiles = new("GZJam backup") { Patterns = ["*" + LibraryBackup.Extension] };

    /// <summary>Backs up the library to a file the DJ picks.</summary>
    private async void OnBackUpClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm)
            return;
        var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Back up my library",
            SuggestedFileName = ViewModels.SettingsViewModel.SuggestedBackupName,
            DefaultExtension = LibraryBackup.Extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [BackupFiles],
        });
        if (file?.TryGetLocalPath() is { } path)
            await vm.BackUpAsync(path);
    }

    /// <summary>Restores a backup the DJ picks, after asking.</summary>
    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm)
            return;
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore a backup",
            AllowMultiple = false,
            FileTypeFilter = [BackupFiles],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
            await vm.RestoreAsync(path);
    }

    /// <summary>The release Check now found: what's new, and the downloads.</summary>
    private async void OnFoundReleaseClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel { FoundRelease: { } release })
            await TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(new Uri(release.Url));
    }

    /// <summary>Opens the notices in the computer's own text viewer: they're long.</summary>
    private async void OnNoticesClick(object? sender, RoutedEventArgs e) =>
        await TopLevel.GetTopLevel(this)!.Launcher.LaunchFileInfoAsync(new FileInfo(About.NoticesPath));
}
