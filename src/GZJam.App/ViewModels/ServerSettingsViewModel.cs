using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;
using GZJam.Subsonic;

namespace GZJam.App.ViewModels;

/// <summary>One server in Settings › Servers, as it is being edited: what is typed, and the login already saved for it.</summary>
public sealed partial class ServerEntry : ObservableObject
{
    internal ServerEntry(ServerAccount account, bool isSaved)
    {
        Id = account.Id;
        Name = account.Name;
        Url = account.Url;
        UserName = account.User;
        Salt = account.Salt;
        Token = account.Token;
        IsSaved = isSaved;
    }

    /// <summary>GZJam's own id for the server, made when it is added; part of every song's path.</summary>
    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; }

    /// <summary>What is typed in the password box; empty keeps the saved login.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHelp))]
    public partial string Password { get; set; } = "";

    /// <summary>The saved login (a salt and token); empty if it was never saved or can't be read here.</summary>
    internal string Salt { get; set; }

    internal string Token { get; set; }

    /// <summary>It is in the saved list, not just added in Settings.</summary>
    public bool IsSaved { get; internal set; }

    public bool HasSavedLogin => Salt.Length > 0 && Token.Length > 0;

    public string Title => Name.Trim().Length > 0 ? Name.Trim()
        : ServerConnections.TryParseUrl(Url, out Uri? uri) ? uri.Host : "New server";

    public string PasswordHelp => Password.Length > 0 ? "" : HasSavedLogin ? "A password is saved. Leave this empty to keep it." : "";

    internal void Saved(ServerAccount account)
    {
        (Salt, Token, IsSaved) = (account.Salt, account.Token, true);
        Password = "";
        OnPropertyChanged(nameof(HasSavedLogin));
        OnPropertyChanged(nameof(PasswordHelp));
        OnPropertyChanged(nameof(Title));
    }
}

/// <summary>
/// Settings › Servers: the music servers (anything that speaks the Subsonic API, Navidrome
/// recommended), each with its address, user name and password, a button to test them and one to
/// save them; whether songs are downloaded by themselves; and where downloaded songs are kept. A
/// login is only saved once it has been tested and works. A password is never shown again once
/// saved: leaving the box empty keeps it.
/// </summary>
public sealed partial class ServerSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    /// <summary>Makes the client a test is run with; tests give it a fake server.</summary>
    internal Func<ServerAccount, SubsonicClient> CreateClient { get; set; } = ServerConnections.CreateClient;

    /// <summary>Reads the servers' songs and playlists once a login is saved; tests don't.</summary>
    internal Func<Task> SyncAfterSave { get; set; } = null!;

    internal ServerSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        SyncAfterSave = owner.SyncServersAsync;
        ShowCurrent();
        ShowCache();
    }

    public ObservableCollection<ServerEntry> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(CanDisconnect))]
    public partial ServerEntry? Selected { get; set; }

    public bool HasSelected => Selected is not null;

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanDisconnect))]
    public partial bool IsBusy { get; private set; }

    public bool CanAct => !IsBusy;

    public bool CanDisconnect => !IsBusy && Selected is not null;

    public string WhereItIsKept => "GZJam doesn't keep your passwords: it keeps a code made from each, which only works for logging in to its server's music app. "
        + (OperatingSystem.IsWindows()
            ? "They are encrypted for your Windows user on this computer."
            : "They are in a file only you can read.")
        + " They are not in GZJam's settings or in library backups.";

    // ───────────── Fetching songs ─────────────

    public IReadOnlyList<Choice<RemoteFetchMode>> FetchChoices { get; } =
    [
        new(RemoteFetchMode.Never, "Only songs I download"),
        new(RemoteFetchMode.Temporary, "Download to play, then remove"),
        new(RemoteFetchMode.Keep, "Download and keep"),
    ];

    /// <summary>What happens to a song that is queued or loaded but isn't on this computer yet (see <see cref="RemoteFetchMode"/>).</summary>
    public Choice<RemoteFetchMode> FetchChoice
    {
        get => FetchChoices.First(c => c.Value == _owner.PanelSettings.RemoteFetch);
        set
        {
            if (value is null || value.Value == _owner.PanelSettings.RemoteFetch)
                return;
            _owner.PanelSettings.RemoteFetch = value.Value;
            RemoteSongs.AutoFetch = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FetchHelp));
            Log.Info($"Servers: songs not on this computer are handled as '{value.Value}'");
        }
    }

    public string FetchHelp => _owner.PanelSettings.RemoteFetch switch
    {
        RemoteFetchMode.Temporary => "A song that is queued or loaded and isn't here yet is downloaded in full, then plays like a file. It is removed when GZJam next starts or closes, unless you download it (right-click > Cache for offline play). The room waits for a download, so a venue's network matters.",
        RemoteFetchMode.Keep => "A song that is queued or loaded and isn't here yet is downloaded in full, and kept on this computer. The room waits for a download, so a venue's network matters.",
        _ => "Only the songs you ask for are downloaded (right-click a playlist, folder or songs > Cache for offline play, or Play and Add to queue in the Servers window). A song that isn't on this computer yet can't be loaded or queued, so the room never waits on the network.",
    };

    // ───────────── The servers ─────────────

    /// <summary>Shows the servers in use, and any saved whose login can't be read here (to type the password again).</summary>
    internal void ShowCurrent()
    {
        Servers.Clear();
        foreach (ServerAccount account in ServerConnections.Accounts)
            Servers.Add(new ServerEntry(account, isSaved: true));
        Selected = Servers.FirstOrDefault();
        Status = Servers.Count == 0 ? "No server is set up."
            : ServerConnections.FromEnvironment ? "Using the login in GZJam's environment variables. Save one here to use it instead."
            : Servers.Any(s => !s.HasSavedLogin) ? "A saved login can't be read on this computer: type that server's password again."
            : "";
        OnPropertyChanged(nameof(CanDisconnect));
    }

    [RelayCommand]
    private void AddServer()
    {
        var entry = new ServerEntry(new ServerAccount(ServerAccount.NewId(), "", "", ""), isSaved: false);
        Servers.Add(entry);
        Selected = entry;
        Status = "Type the server's address, your user name and password, then TEST and SAVE.";
    }

    /// <summary>The server as typed, with the saved login where no password was typed; or why it can't be used.</summary>
    internal (ServerAccount? Account, string? Problem) Typed(ServerEntry entry)
    {
        if (!ServerConnections.TryParseUrl(entry.Url, out Uri server))
            return (null, "The server address isn't a web address (like https://music.example.com).");
        if (string.IsNullOrWhiteSpace(entry.UserName))
            return (null, "Type the user name.");
        string url = server.ToString().TrimEnd('/'), user = entry.UserName.Trim();
        string name = entry.Name.Trim().Length > 0 ? entry.Name.Trim() : server.Host;
        if (Servers.Any(s => s != entry && string.Equals(s.Title, name, StringComparison.OrdinalIgnoreCase)))
            return (null, $"Another server is called \"{name}\": give this one a name of its own.");
        if (entry.Password.Length > 0)
            return (new ServerAccount(entry.Id, name, url, user, entry.Password), null);
        // Nothing typed: the saved login stays (it can't be told the password again).
        return entry.HasSavedLogin
            ? (new ServerAccount(entry.Id, name, url, user, "", entry.Salt, entry.Token), null)
            : (null, "Type the password.");
    }

    /// <summary>Asks the server who it is with the login as typed. Returns what it said, or null (and says why in <see cref="Status"/>).</summary>
    private async Task<string?> PingAsync(ServerAccount account)
    {
        try
        {
            using SubsonicClient client = CreateClient(account);
            string who = await client.PingAsync();
            if (await client.CanDownloadAsync() == false)
                who += ". This login may not download songs, and GZJam plays only songs it has downloaded, so it can't play from this server. The server's owner can allow downloads for your user";
            return who;
        }
        catch (Exception ex) when (ex is SubsonicException or TaskCanceledException or ArgumentException)
        {
            Log.Info("Server test failed: " + ex.Message);
            Status = "Couldn't connect. " + (ex is TaskCanceledException ? "The server didn't answer in time." : ex.Message);
            return null;
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Selected is not { } entry)
            return;
        var (account, problem) = Typed(entry);
        if (account is null)
        {
            Status = problem!;
            return;
        }
        IsBusy = true;
        Status = "Testing.";
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
        if (Selected is not { } entry)
            return;
        var (account, problem) = Typed(entry);
        if (account is null)
        {
            Status = problem!;
            return;
        }
        IsBusy = true;
        Status = "Testing before saving.";
        try
        {
            if (await PingAsync(account) is not { } server)
            {
                Status += " Not saved.";
                return;
            }
            // What is kept (and used from now on) is each server's salt and token: passwords are forgotten.
            var list = new List<ServerAccount>();
            foreach (ServerEntry other in Servers.Where(s => s.IsSaved || s == entry))
                list.Add(other == entry ? account : new ServerAccount(other.Id, other.Title, other.Url.Trim(), other.UserName.Trim(), "", other.Salt, other.Token));
            var saved = ServerCredentials.Save(list);
            entry.Name = account.Name;
            entry.Saved(saved.First(s => s.Id == entry.Id));
            ServerConnections.Set(saved);
            Log.Info($"Server login saved for {account.User}");
            Status = $"Saved. Connected: {server}. Reading its songs and playlists.";
            OnPropertyChanged(nameof(CanDisconnect));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save the server", ex);
            Status = "Couldn't save the login: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await SyncAfterSave();
        Status = "Saved and in use. The server's songs are in a folder of its name in the library, its playlists in the playlist list.";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (Selected is not { } entry)
            return;
        if (entry.IsSaved && _owner.ConfirmAsync is { } confirm && !await confirm($"Stop using \"{entry.Title}\"?",
                "GZJam forgets its address and login, and its songs and playlists leave GZJam's library. Songs already downloaded stay on this computer.",
                "Remove"))
            return;
        bool wasSaved = entry.IsSaved;
        Servers.Remove(entry);
        Selected = Servers.FirstOrDefault();
        if (wasSaved)
        {
            try
            {
                var saved = Servers.Where(s => s.IsSaved).Select(s => new ServerAccount(s.Id, s.Title, s.Url.Trim(), s.UserName.Trim(), "", s.Salt, s.Token)).ToList();
                if (saved.Count == 0)
                    ServerCredentials.Clear();
                else
                    ServerCredentials.Save(saved);
                ServerConnections.Set(saved);
                _owner.ForgetServer(entry.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Couldn't save the servers", ex);
                Status = "Couldn't save: " + ex.Message;
                return;
            }
        }
        Status = Servers.Count == 0 ? "Removed. No server is set up." : $"\"{entry.Title}\" removed.";
        OnPropertyChanged(nameof(CanDisconnect));
    }

    // ───────────── Where the fetched songs are kept ─────────────

    /// <summary>The folder the songs fetched from the server are kept in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesDefaultCacheFolder))]
    public partial string CacheFolder { get; private set; } = "";

    /// <summary>"12 songs, 340 MB" in it.</summary>
    [ObservableProperty]
    public partial string CacheSummary { get; private set; } = "";

    public bool UsesDefaultCacheFolder => ServerCacheFolder.Same(CacheFolder, ServerCacheFolder.Default);

    /// <summary>Looks at the folder in use and what is in it.</summary>
    internal void ShowCache()
    {
        CacheFolder = RemoteSongs.CacheDirectory ?? ServerCacheFolder.Default;
        var (songs, bytes) = ServerCacheFolder.Measure(CacheFolder);
        CacheSummary = ServerCacheFolder.Describe(songs, bytes);
    }

    [RelayCommand]
    private async Task ChooseCacheFolderAsync()
    {
        if (_owner.PickFolderAsync is not { } pick || await pick("Where should songs from servers be kept on this computer?") is not { Length: > 0 } folder)
            return;
        await ChangeCacheFolderAsync(folder);
    }

    [RelayCommand]
    private Task UseDefaultCacheFolderAsync() => ChangeCacheFolderAsync(ServerCacheFolder.Default);

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
        if (ServerCacheFolder.Same(folder, current))
        {
            Status = "Songs are kept there already.";
            return;
        }
        if (ServerCacheFolder.Problem(folder) is { } problem)
        {
            Status = "GZJam can't keep songs in that folder: " + problem;
            return;
        }

        var (songs, bytes) = ServerCacheFolder.Measure(current);
        bool move = false;
        if (songs > 0 && _owner.AskAsync is { } ask)
            move = await ask("Move the songs already fetched?",
                $"{ServerCacheFolder.Describe(songs, bytes)} {(songs == 1 ? "is" : "are")} in the old folder. Moving takes a while for a big collection. " +
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
                moved = await Task.Run(() => ServerCacheFolder.Move(from, folder));
            }
            try
            {
                ServerCacheFolder.Save(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Couldn't remember the folder for songs from servers", ex);
                Status = "Couldn't save the choice: " + ex.Message;
                return;
            }
            RemoteSongs.CacheDirectory = folder;
            OfflineIndex.Instance.Refresh();   // the marks look at the new folder
            ShowCache();
            Log.Info($"Songs from servers are kept in {folder}" + (moved is null ? "" : $"; moved {moved.Moved}, left {moved.Stayed}"));
            Status = moved is null ? $"Songs from servers are kept in {folder} from now on."
                : $"Moved {moved.Moved:N0} {(moved.Moved == 1 ? "song" : "songs")} to {folder}."
                  + (moved.Stayed > 0 ? $" {moved.Stayed} stayed behind (in use, or already there)." : "");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
