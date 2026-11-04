using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;
using GZJam.Navidrome;

namespace GZJam.App.ViewModels;

/// <summary>
/// Settings › Navidrome: the server's address, the user name and password, a button to test them
/// and one to save them. A login is only saved once it has been tested and works. The password is
/// never shown again once saved: leaving the box empty keeps it.
/// </summary>
public sealed partial class NavidromeSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    /// <summary>Makes the client a test is run with; tests give it a fake server.</summary>
    internal Func<NavidromeAccount, NavidromeClient> CreateClient { get; set; } = NavidromeConnection.CreateClient;

    /// <summary>Reads the server's songs and playlists once a login is saved; tests don't.</summary>
    internal Func<Task> SyncAfterSave { get; set; } = null!;

    internal NavidromeSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        SyncAfterSave = owner.SyncNavidromeAsync;
        ShowCurrent();
        ShowCache();
    }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "";

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    /// <summary>What is typed in the password box; empty keeps the saved password.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHelp))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanDisconnect))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHelp), nameof(CanDisconnect))]
    public partial bool HasSavedPassword { get; private set; }

    public bool CanAct => !IsBusy;

    public bool CanDisconnect => !IsBusy && NavidromeConnection.Account is not null;

    public string PasswordHelp => Password.Length > 0 ? "" : HasSavedPassword ? "A password is saved. Leave this empty to keep it." : "";

    public string WhereItIsKept => OperatingSystem.IsWindows()
        ? "The password is kept encrypted for your Windows user on this computer. It is not in GZJam's settings or in library backups."
        : "The password is kept in a file only you can read. It is not in GZJam's settings or in library backups.";

    /// <summary>Shows the login in use (or the one saved, if its password can't be read here).</summary>
    internal void ShowCurrent()
    {
        NavidromeAccount? account = NavidromeConnection.Account;
        ServerUrl = account?.Url ?? "";
        UserName = account?.User ?? "";
        Password = "";
        HasSavedPassword = account is { Password.Length: > 0 };
        Status = account is null ? "No server is set up."
            : NavidromeConnection.FromEnvironment ? "Using the login in GZJam's environment variables. Save one here to use it instead."
            : account.Password.Length == 0 ? "The saved password can't be read on this computer: type it again."
            : "";
        OnPropertyChanged(nameof(CanDisconnect));
    }

    /// <summary>The login as typed, with the saved password where none was typed; or why it can't be used.</summary>
    internal (NavidromeAccount? Account, string? Problem) Typed()
    {
        if (!NavidromeConnection.TryParseUrl(ServerUrl, out Uri server))
            return (null, "The server address isn't a web address (like https://music.example.com).");
        if (string.IsNullOrWhiteSpace(UserName))
            return (null, "Type the user name.");
        string password = Password.Length > 0 ? Password : NavidromeConnection.Account?.Password ?? "";
        if (password.Length == 0)
            return (null, "Type the password.");
        return (new NavidromeAccount(server.ToString().TrimEnd('/'), UserName.Trim(), password), null);
    }

    /// <summary>Asks the server who it is with the login as typed. Returns what it said, or null (and says why in <see cref="Status"/>).</summary>
    private async Task<string?> PingAsync(NavidromeAccount account)
    {
        try
        {
            using NavidromeClient client = CreateClient(account);
            return await client.PingAsync();
        }
        catch (Exception ex) when (ex is NavidromeException or TaskCanceledException or ArgumentException)
        {
            Log.Info("Navidrome test failed: " + ex.Message);
            Status = "Couldn't connect. " + (ex is TaskCanceledException ? "The server didn't answer in time." : ex.Message);
            return null;
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var (account, problem) = Typed();
        if (account is null)
        {
            Status = problem!;
            return;
        }
        IsBusy = true;
        Status = "Testing…";
        try
        {
            if (await PingAsync(account) is { } server)
                Status = $"Connected: {server}. The login works; press SAVE to use it.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var (account, problem) = Typed();
        if (account is null)
        {
            Status = problem!;
            return;
        }
        IsBusy = true;
        Status = "Testing before saving…";
        try
        {
            if (await PingAsync(account) is not { } server)
            {
                Status += " Not saved.";
                return;
            }
            NavidromeCredentials.Save(account);
            NavidromeConnection.Apply(account);
            Log.Info($"Navidrome login saved for {account.User}");
            Password = "";
            HasSavedPassword = true;
            Status = $"Saved. Connected: {server}. Reading its songs and playlists…";
            OnPropertyChanged(nameof(CanDisconnect));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save the Navidrome login", ex);
            Status = "Couldn't save the login: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await SyncAfterSave();
        Status = "Saved and in use. The server's songs are in the Navidrome folder, its playlists in the playlist list.";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_owner.ConfirmAsync is { } confirm && !await confirm("Stop using this Navidrome server?",
                "GZJam forgets the address and login. The songs and playlists already in GZJam stay, and songs already fetched still play once a server is set up again.",
                "Disconnect"))
            return;
        NavidromeCredentials.Clear();
        NavidromeConnection.Disconnect();
        ShowCurrent();
        Status = "Disconnected. No server is set up.";
    }

    // ───────────── Where the fetched songs are kept ─────────────

    /// <summary>The folder the songs fetched from the server are kept in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesDefaultCacheFolder))]
    public partial string CacheFolder { get; private set; } = "";

    /// <summary>"12 songs, 340 MB" in it.</summary>
    [ObservableProperty]
    public partial string CacheSummary { get; private set; } = "";

    public bool UsesDefaultCacheFolder => NavidromeCacheFolder.Same(CacheFolder, NavidromeCacheFolder.Default);

    /// <summary>Looks at the folder in use and what is in it.</summary>
    internal void ShowCache()
    {
        CacheFolder = RemoteSongs.CacheDirectory ?? NavidromeCacheFolder.Default;
        var (songs, bytes) = NavidromeCacheFolder.Measure(CacheFolder);
        CacheSummary = NavidromeCacheFolder.Describe(songs, bytes);
    }

    [RelayCommand]
    private async Task ChooseCacheFolderAsync()
    {
        if (_owner.PickFolderAsync is not { } pick || await pick("Where should songs from Navidrome be kept on this computer?") is not { Length: > 0 } folder)
            return;
        await ChangeCacheFolderAsync(folder);
    }

    [RelayCommand]
    private Task UseDefaultCacheFolderAsync() => ChangeCacheFolderAsync(NavidromeCacheFolder.Default);

    [RelayCommand]
    private void OpenCacheFolder() => FileManager.Open(CacheFolder);

    /// <summary>
    /// Keeps the fetched songs in <paramref name="folder"/> from now on. If there are songs in the old
    /// folder, asks whether to move them: they take a while to move and are fetched again if left.
    /// Downloads going on are stopped first (they were headed for the old folder).
    /// </summary>
    internal async Task ChangeCacheFolderAsync(string folder)
    {
        string current = CacheFolder;
        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Status = "That isn't a folder GZJam can use: " + ex.Message;
            return;
        }
        if (NavidromeCacheFolder.Same(folder, current))
        {
            Status = "Songs are kept there already.";
            return;
        }
        if (NavidromeCacheFolder.Problem(folder) is { } problem)
        {
            Status = "GZJam can't keep songs in that folder: " + problem;
            return;
        }

        var (songs, bytes) = NavidromeCacheFolder.Measure(current);
        bool move = false;
        if (songs > 0 && _owner.AskAsync is { } ask)
            move = await ask("Move the songs already fetched?",
                $"{NavidromeCacheFolder.Describe(songs, bytes)} {(songs == 1 ? "is" : "are")} in the old folder. Moving takes a while for a big collection. " +
                "Songs left behind are fetched again when they are needed.", "Move them", "Leave them there");

        IsBusy = true;
        try
        {
            RemoteSongs.StopAll();
            CacheMoveResult? moved = null;
            if (move)
            {
                Status = "Moving the songs…";
                string from = current;
                moved = await Task.Run(() => NavidromeCacheFolder.Move(from, folder));
            }
            try
            {
                NavidromeCacheFolder.Save(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Couldn't remember the folder for Navidrome songs", ex);
                Status = "Couldn't save the choice: " + ex.Message;
                return;
            }
            RemoteSongs.CacheDirectory = folder;
            OfflineIndex.Instance.Refresh();   // the marks look at the new folder
            ShowCache();
            Log.Info($"Navidrome songs are kept in {folder}" + (moved is null ? "" : $"; moved {moved.Moved}, left {moved.Stayed}"));
            Status = moved is null ? $"Songs from Navidrome are kept in {folder} from now on."
                : $"Moved {moved.Moved:N0} {(moved.Moved == 1 ? "song" : "songs")} to {folder}."
                  + (moved.Stayed > 0 ? $" {moved.Stayed} stayed behind (in use, or already there)." : "");
        }
        finally
        {
            IsBusy = false;
        }
    }
}