using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using GZJam.App.ViewModels;
using GZJam.App.Views;
using GZJam.Audio;

namespace GZJam.App.Tests;

/// <summary>Where the songs fetched from Navidrome are kept: the choice in Settings, moving what is there, and the Downloads button.</summary>
[Collection("Navidrome globals")]   // sets RemoteSongs.CacheDirectory and the one Navidrome login
public sealed class NavidromeCacheFolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gzjam-cache-folder-").FullName;
    private readonly string? _cacheBefore = RemoteSongs.CacheDirectory;

    public NavidromeCacheFolderTests()
    {
        NavidromeCacheFolder.SettingFileOverride = Path.Combine(_dir, "choice.txt");
        NavidromeCacheFolder.DefaultOverride = Path.Combine(_dir, "default");
        NavidromeCredentials.PathOverride = Path.Combine(_dir, "navidrome.json");
    }

    public void Dispose()
    {
        NavidromeConnection.Disconnect();
        NavidromeCredentials.PathOverride = null;
        NavidromeCacheFolder.SettingFileOverride = null;
        NavidromeCacheFolder.DefaultOverride = null;
        RemoteSongs.CacheDirectory = _cacheBefore;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string Folder(string name) => Path.Combine(_dir, name);

    private string Songs(string folder, params string[] names)
    {
        Directory.CreateDirectory(folder);
        foreach (string name in names)
            File.WriteAllBytes(Path.Combine(folder, name), new byte[2048]);
        return folder;
    }

    // ───────────── The helper ─────────────

    [Fact]
    public void With_no_choice_the_default_is_used_and_a_choice_is_remembered_until_forgotten()
    {
        Assert.Equal(NavidromeCacheFolder.Default, NavidromeCacheFolder.Current());

        NavidromeCacheFolder.Save(Folder("big drive"));
        Assert.Equal(Folder("big drive"), NavidromeCacheFolder.Current());

        NavidromeCacheFolder.Save(NavidromeCacheFolder.Default);   // choosing the default is no choice
        Assert.Equal(NavidromeCacheFolder.Default, NavidromeCacheFolder.Current());
        Assert.False(File.Exists(Path.Combine(_dir, "choice.txt")));
    }

    [Fact]
    public void Measuring_counts_finished_songs_only()
    {
        Songs(Folder("c"), "a.flac", "b.mp3");
        File.WriteAllBytes(Path.Combine(Folder("c"), "c.part"), new byte[999]);

        Assert.Equal((2, 4096), NavidromeCacheFolder.Measure(Folder("c")));
        Assert.Equal((0, 0), NavidromeCacheFolder.Measure(Folder("nothing here")));
        Assert.Equal("No songs fetched yet.", NavidromeCacheFolder.Describe(0, 0));
        Assert.Equal("2 songs, 3 MB", NavidromeCacheFolder.Describe(2, 3 * 1048576));
        Assert.Equal("1 song, 2.0 GB", NavidromeCacheFolder.Describe(1, 2L * 1073741824));
    }

    [Fact]
    public void Moving_takes_the_songs_across_and_tidies_the_old_folder_away()
    {
        Songs(Folder("old"), "a.flac", "b.mp3");

        var result = NavidromeCacheFolder.Move(Folder("old"), Folder("new"));

        Assert.Equal(new CacheMoveResult(2, 0, 4096), result);
        Assert.Equal(["a.flac", "b.mp3"], Directory.GetFiles(Folder("new")).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Folder("old")));
    }

    [Fact]
    public void A_song_already_there_stays_behind_and_so_does_the_old_folder()
    {
        Songs(Folder("old"), "a.flac", "c.ogg");
        Songs(Folder("new"), "a.flac");

        var result = NavidromeCacheFolder.Move(Folder("old"), Folder("new"));

        Assert.Equal(new CacheMoveResult(1, 1, 2048), result);   // c.ogg moved; a.flac was there
        Assert.True(Directory.Exists(Folder("old")));
        Assert.Equal(["a.flac"], Directory.GetFiles(Folder("old")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_song_a_deck_has_open_stays_behind()
    {
        // Only Windows stops a file that is open from being moved; elsewhere it moves and plays on.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a file that is open");
        Songs(Folder("old"), "b.mp3", "c.ogg");
        using var inUse = new FileStream(Path.Combine(Folder("old"), "b.mp3"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = NavidromeCacheFolder.Move(Folder("old"), Folder("new"));

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Stayed);
        Assert.True(Directory.Exists(Folder("old")));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_to_is_refused_with_a_reason()
    {
        string file = Path.Combine(_dir, "a file");
        File.WriteAllText(file, "");

        Assert.Null(NavidromeCacheFolder.Problem(Folder("fine")));
        Assert.NotNull(NavidromeCacheFolder.Problem(Path.Combine(file, "inside a file")));
    }

    // ───────────── Settings › Navidrome ─────────────

    private static NavidromeSettingsViewModel Settings(AppHarness app) => app.ViewModel.Settings.Navidrome;

    [AvaloniaFact]
    public void Choosing_another_folder_can_move_the_songs_already_fetched()
    {
        using var app = new AppHarness();
        var vm = Settings(app);
        RemoteSongs.CacheDirectory = Songs(Folder("old"), "a.flac", "b.mp3");
        vm.ShowCache();
        string? asked = null;
        app.ViewModel.AskAsync = (heading, message, _, _) =>
        {
            asked = message;
            return Task.FromResult(true);   // move them
        };

        AppHarness.Await(vm.ChangeCacheFolderAsync(Folder("new")));

        Assert.Contains("2 songs", asked);
        Assert.Equal(Folder("new"), RemoteSongs.CacheDirectory);
        Assert.Equal(Folder("new"), NavidromeCacheFolder.Current());
        Assert.Equal(2, Directory.GetFiles(Folder("new")).Length);
        Assert.Equal(Folder("new"), vm.CacheFolder);
        Assert.Equal("2 songs, 0 MB", vm.CacheSummary);
        Assert.False(vm.UsesDefaultCacheFolder);
        Assert.StartsWith("Moved 2 songs to", vm.Status);
        Assert.True(vm.CanAct);
    }

    [AvaloniaFact]
    public void Or_leave_them_where_they_are()
    {
        using var app = new AppHarness();
        var vm = Settings(app);
        RemoteSongs.CacheDirectory = Songs(Folder("old"), "a.flac");
        vm.ShowCache();
        app.ViewModel.AskAsync = (_, _, _, _) => Task.FromResult(false);

        AppHarness.Await(vm.ChangeCacheFolderAsync(Folder("new")));

        Assert.Equal(Folder("new"), RemoteSongs.CacheDirectory);
        Assert.Empty(Directory.GetFiles(Folder("new")));
        Assert.Single(Directory.GetFiles(Folder("old")));
        Assert.Equal($"Songs from Navidrome are kept in {Folder("new")} from now on.", vm.Status);
    }

    [AvaloniaFact]
    public void An_empty_old_folder_is_not_asked_about()
    {
        using var app = new AppHarness();
        var vm = Settings(app);
        RemoteSongs.CacheDirectory = Folder("old");
        vm.ShowCache();
        app.ViewModel.AskAsync = (_, _, _, _) => throw new InvalidOperationException("nothing to move: no question");

        AppHarness.Await(vm.ChangeCacheFolderAsync(Folder("new")));

        Assert.Equal(Folder("new"), RemoteSongs.CacheDirectory);
    }

    [AvaloniaFact]
    public void The_same_folder_and_a_folder_that_cannot_be_used_change_nothing()
    {
        using var app = new AppHarness();
        var vm = Settings(app);
        RemoteSongs.CacheDirectory = Songs(Folder("old"), "a.flac");
        vm.ShowCache();
        string file = Path.Combine(_dir, "a file");
        File.WriteAllText(file, "");

        AppHarness.Await(vm.ChangeCacheFolderAsync(Folder("old")));
        Assert.Equal("Songs are kept there already.", vm.Status);

        AppHarness.Await(vm.ChangeCacheFolderAsync(Path.Combine(file, "inside a file")));
        Assert.StartsWith("GZJam can't keep songs in that folder:", vm.Status);
        Assert.Equal(Folder("old"), RemoteSongs.CacheDirectory);
        Assert.False(File.Exists(Path.Combine(_dir, "choice.txt")));
    }

    [AvaloniaFact]
    public void Back_to_the_default_forgets_the_choice()
    {
        using var app = new AppHarness();
        var vm = Settings(app);
        RemoteSongs.CacheDirectory = Folder("old");
        vm.ShowCache();
        app.ViewModel.AskAsync = (_, _, _, _) => Task.FromResult(true);
        AppHarness.Await(vm.ChangeCacheFolderAsync(Folder("elsewhere")));
        Assert.False(vm.UsesDefaultCacheFolder);

        AppHarness.Await(vm.UseDefaultCacheFolderCommand.ExecuteAsync(null));

        Assert.True(vm.UsesDefaultCacheFolder);
        Assert.Equal(NavidromeCacheFolder.Default, RemoteSongs.CacheDirectory);
        Assert.False(File.Exists(Path.Combine(_dir, "choice.txt")));
    }

    // ───────────── The Downloads button in the top bar ─────────────

    [AvaloniaFact]
    public void The_downloads_button_is_in_the_top_bar_once_a_server_is_set_up_and_opens_the_list()
    {
        using var app = new AppHarness();
        var button = app.Window.FindControl<Button>("DownloadsButton")!;
        Assert.False(button.IsVisible);

        NavidromeConnection.Apply(new NavidromeAccount("https://music.example.com", "danny", "pw"));
        AppHarness.Settle(100);
        Assert.True(button.IsVisible);
        Assert.False(app.Window.FindControl<TextBlock>("DownloadsBadge")!.IsVisible);   // nothing coming down

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(app.Window.Downloads);
        app.Window.Downloads!.Close();
    }

    [AvaloniaFact]
    public void The_button_counts_what_is_left()
    {
        using var app = new AppHarness();
        NavidromeConnection.Apply(new NavidromeAccount("https://music.example.com", "danny", "pw"));
        var badge = app.Window.FindControl<TextBlock>("DownloadsBadge")!;

        app.ViewModel.Downloads.Apply(new RemoteTransfer("navidrome://a", RemoteTransferState.Waiting));
        app.ViewModel.Downloads.Apply(new RemoteTransfer("navidrome://b", RemoteTransferState.Downloading, 100, 1000));
        AppHarness.Settle(100);

        Assert.True(badge.IsVisible);
        Assert.Equal("2 left", badge.Text);

        app.ViewModel.Downloads.Apply(new RemoteTransfer("navidrome://a", RemoteTransferState.Done, 5, 5));
        app.ViewModel.Downloads.Apply(new RemoteTransfer("navidrome://b", RemoteTransferState.Done, 5, 5));
        AppHarness.Settle(100);
        Assert.False(badge.IsVisible);
    }
}
