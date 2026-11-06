using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Avalonia.Headless.XUnit;
using GZJam.App.ViewModels;
using GZJam.Audio;
using GZJam.Subsonic;

namespace GZJam.App.Tests;

/// <summary>The saved servers, and Settings › Servers: Test, Save, several servers, never touching the real logins.</summary>
[Collection("Server globals")]   // they set the app's servers and RemoteSongs.Resolver: one test class at a time
public sealed class ServerSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gzjam-server-settings-").FullName;

    public ServerSettingsTests() => ServerCredentials.PathOverride = Path.Combine(_dir, "servers.json");

    public void Dispose()
    {
        ServerConnections.DisconnectAll();
        ServerCredentials.PathOverride = null;
        RemoteSongs.AutoFetch = RemoteFetchMode.Never;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string SavedFile => ServerCredentials.DefaultPath;

    private static string Token(string password, string salt) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + salt))).ToLowerInvariant();

    // ───────────── The saved servers ─────────────

    [Fact]
    public void Saved_servers_are_a_salt_and_token_each_and_no_password_is_in_the_file()
    {
        var saved = ServerCredentials.Save(
        [
            new ServerAccount("aaaa0001", "Mine", "https://music.example.com", "danny", "s3cret pass"),
            new ServerAccount("bbbb0002", "Dave's", "https://dave.example.com", "guest", "other pass"),
        ]);

        var back = ServerCredentials.Load();

        Assert.Equal(saved, back);
        Assert.Equal(["Mine", "Dave's"], back.Select(a => a.Name));
        Assert.All(back, a => { Assert.Equal("", a.Password); Assert.True(a.IsComplete); });
        Assert.Equal(Token("s3cret pass", back[0].Salt), back[0].Token);   // what the server expects for that salt
        Assert.Equal(Token("other pass", back[1].Salt), back[1].Token);
        string file = File.ReadAllText(SavedFile);
        Assert.DoesNotContain("s3cret", file);
        Assert.DoesNotContain("other pass", file);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("s3cret pass")), file);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(back[0].Token, file);   // the token is encrypted, not just encoded
    }

    [Fact]
    public void A_server_saved_again_without_a_password_keeps_its_login()
    {
        var first = ServerCredentials.Save([new ServerAccount("aaaa0001", "Mine", "https://music.example.com", "danny", "pw")]);

        var again = ServerCredentials.Save([first[0] with { Url = "https://new.example.com" }]);

        Assert.Equal(first[0].Token, again[0].Token);
        Assert.Equal(first[0].Salt, again[0].Salt);
        Assert.Equal("https://new.example.com", ServerCredentials.Load()[0].Url);
    }

    [Fact]
    public void Nothing_saved_or_a_damaged_file_is_no_servers()
    {
        Assert.Empty(ServerCredentials.Load());
        File.WriteAllText(SavedFile, "{ not json");
        Assert.Empty(ServerCredentials.Load());
    }

    [Fact]
    public void A_login_that_cant_be_read_leaves_the_address_and_user_for_Settings_to_show()
    {
        File.WriteAllText(SavedFile, """{"Servers":[{"Id":"aaaa0001","Name":"Mine","Url":"https://m.example.com","User":"danny","Salt":"ab","Token":"AAAA","Protection":"dpapi"}]}""");

        var back = ServerCredentials.Load();

        var server = Assert.Single(back);
        Assert.Equal(("aaaa0001", "Mine", "https://m.example.com", "danny"), (server.Id, server.Name, server.Url, server.User));
        Assert.False(server.IsComplete);
        Assert.False(server.HasLogin);
    }

    [Fact]
    public void Cleared_servers_are_gone()
    {
        ServerCredentials.Save([new ServerAccount("aaaa0001", "Mine", "https://m.example.com", "danny", "x")]);
        ServerCredentials.Clear();
        Assert.Empty(ServerCredentials.Load());
    }

    [Theory]
    [InlineData("https://music.example.com", "https://music.example.com/")]
    [InlineData("music.example.com", "https://music.example.com/")]
    [InlineData("  http://192.168.1.5:4533/  ", "http://192.168.1.5:4533/")]
    [InlineData("https://example.com/navidrome/", "https://example.com/navidrome")]
    [InlineData("https://example.com/app?x=1#top", "https://example.com/app")]
    public void A_server_address_is_taken_as_typed_where_it_makes_sense(string typed, string expected)
    {
        Assert.True(ServerConnections.TryParseUrl(typed, out Uri uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://music.example.com")]
    [InlineData("https://")]
    [InlineData("not a url at all")]
    public void Something_that_is_not_a_web_address_is_refused(string typed) =>
        Assert.False(ServerConnections.TryParseUrl(typed, out _));

    // ───────────── Settings › Servers ─────────────

    /// <summary>A server that knows one login and answers pings, as Subsonic does: a token from the password and salt.</summary>
    private sealed class FakeSubsonic(string user, string password, bool downloads = true) : HttpMessageHandler
    {
        public int Pings { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            string expected = Token(password, query["s"]!);
            bool known = query["u"] == user && query["t"] == expected;
            string body;
            if (!known)
                body = """{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password"}}}""";
            else if (request.RequestUri.AbsolutePath.EndsWith("getUser", StringComparison.Ordinal))
                body = "{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\",\"user\":{\"username\":\"" + user + "\",\"downloadRole\":" + (downloads ? "true" : "false") + "}}}";
            else
            {
                Pings++;
                body = """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","serverVersion":"0.64.2"}}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>The servers' settings, with fake servers by address: each address names the stand-in that answers.</summary>
    private static ServerSettingsViewModel Settings(AppHarness app, Dictionary<string, FakeSubsonic> servers, out List<string> syncs)
    {
        var syncsMade = new List<string>();
        syncs = syncsMade;
        var vm = app.ViewModel.Settings.Servers;
        vm.CreateClient = account =>
        {
            var http = new HttpClient(servers[new Uri(account.Url).Host]);
            return account.Password.Length > 0
                ? new SubsonicClient(account.Id, new Uri(account.Url), account.User, account.Password, http)
                : new SubsonicClient(account.Id, new Uri(account.Url), account.User, account.Salt, account.Token, http);
        };
        vm.SyncAfterSave = () =>
        {
            syncsMade.Add("synced");
            return Task.CompletedTask;
        };
        return vm;
    }

    private static Dictionary<string, FakeSubsonic> Servers(params (string Host, FakeSubsonic Server)[] servers) =>
        servers.ToDictionary(s => s.Host, s => s.Server);

    private static ServerEntry Fill(ServerSettingsViewModel vm, string name, string url, string user, string password)
    {
        vm.AddServerCommand.Execute(null);
        ServerEntry entry = vm.Selected!;
        (entry.Name, entry.Url, entry.UserName, entry.Password) = (name, url, user, password);
        return entry;
    }

    [AvaloniaFact]
    public void Test_says_what_is_wrong_with_what_was_typed_before_asking_the_server()
    {
        using var app = new AppHarness();
        var server = new FakeSubsonic("danny", "pw");
        var vm = Settings(app, Servers(("music.example.com", server)), out _);
        vm.AddServerCommand.Execute(null);

        vm.Selected!.Url = "not a url at all";
        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));
        Assert.Contains("isn't a web address", vm.Status);

        vm.Selected.Url = "https://music.example.com";
        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));
        Assert.Equal("Type the user name.", vm.Status);

        vm.Selected.UserName = "danny";
        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));
        Assert.Equal("Type the password.", vm.Status);
        Assert.Equal(0, server.Pings);
    }

    [AvaloniaFact]
    public void Test_connects_with_the_login_as_typed_and_saves_nothing()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out _);
        Fill(vm, "", "music.example.com", "danny", "pw");

        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));

        Assert.Equal("Connected: navidrome 0.64.2. The login works; press SAVE to use it.", vm.Status);
        Assert.False(File.Exists(SavedFile));
        Assert.Empty(ServerConnections.Accounts);
        Assert.True(vm.CanAct);
    }

    [AvaloniaFact]
    public void Test_with_a_wrong_password_says_so_and_never_shows_the_login()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out _);
        Fill(vm, "", "https://music.example.com", "danny", "wrong");

        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));

        Assert.Equal("Couldn't connect. The server said: Wrong username or password", vm.Status);
    }

    [AvaloniaFact]
    public void A_server_that_does_not_let_this_login_download_is_said_so_when_connecting()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw", downloads: false))), out _);
        Fill(vm, "", "music.example.com", "danny", "pw");

        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));

        Assert.Contains("may not download songs", vm.Status);
    }

    [AvaloniaFact]
    public void Save_keeps_a_login_that_works_uses_it_and_reads_the_server()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out var syncs);
        ServerEntry entry = Fill(vm, "Mine", "https://music.example.com", "danny", "pw");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        var kept = Assert.Single(ServerCredentials.Load());
        Assert.Equal(("Mine", "https://music.example.com", "danny", ""), (kept.Name, kept.Url, kept.User, kept.Password));
        Assert.Equal(Token("pw", kept.Salt), kept.Token);
        Assert.Equal(kept, Assert.Single(ServerConnections.Accounts));   // in use as saved: the password is forgotten
        Assert.NotNull(ServerConnections.ClientFor(entry.Id));
        Assert.NotNull(RemoteSongs.Resolver);
        Assert.Single(syncs);
        Assert.Equal("", entry.Password);   // never left in the box
        Assert.True(entry.HasSavedLogin);
        Assert.Contains("leave this empty to keep it", entry.PasswordHelp, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Saved and in use.", vm.Status);
    }

    [AvaloniaFact]
    public void A_server_with_no_name_is_called_by_its_address()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out _);
        Fill(vm, "", "https://music.example.com", "danny", "pw");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.Equal("music.example.com", Assert.Single(ServerCredentials.Load()).Name);
    }

    [AvaloniaFact]
    public void Save_does_not_keep_a_login_that_fails()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out var syncs);
        Fill(vm, "Mine", "https://music.example.com", "danny", "nope");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.False(File.Exists(SavedFile));
        Assert.Empty(ServerConnections.Accounts);
        Assert.Empty(syncs);
        Assert.EndsWith("Not saved.", vm.Status);
    }

    [AvaloniaFact]
    public void An_empty_password_box_keeps_the_saved_login_when_the_address_changes()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out _);
        ServerEntry entry = Fill(vm, "Mine", "https://music.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        entry.Url = "https://music.example.com/navidrome";
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        var kept = Assert.Single(ServerCredentials.Load());
        Assert.Equal("https://music.example.com/navidrome", kept.Url);
        Assert.Equal(Token("pw", kept.Salt), kept.Token);   // the same login, kept without being typed again
        Assert.Equal(entry.Id, kept.Id);   // and the same server: its songs keep their paths
    }

    // ───────────── Several servers ─────────────

    [AvaloniaFact]
    public void Several_servers_are_kept_each_with_its_own_id_and_login_and_each_is_asked_for_its_own_songs()
    {
        using var app = new AppHarness();
        var servers = Servers(("mine.example.com", new FakeSubsonic("danny", "pw")), ("dave.example.com", new FakeSubsonic("guest", "friend")));
        var vm = Settings(app, servers, out var syncs);
        ServerEntry mine = Fill(vm, "Mine", "https://mine.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));
        ServerEntry daves = Fill(vm, "Dave's", "https://dave.example.com", "guest", "friend");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        var saved = ServerCredentials.Load();
        Assert.Equal(["Mine", "Dave's"], saved.Select(s => s.Name));
        Assert.NotEqual(mine.Id, daves.Id);
        Assert.Equal(2, ServerConnections.Clients.Count);
        Assert.Equal(2, syncs.Count);

        // A song's path says which server it is on, so each is downloaded from the right place.
        string? fromMine = RemoteSongs.Resolver!(SubsonicClient.PathFor(mine.Id, "song1"));
        string? fromDaves = RemoteSongs.Resolver!(SubsonicClient.PathFor(daves.Id, "song1"));
        Assert.StartsWith("https://mine.example.com/rest/download?", fromMine);
        Assert.StartsWith("https://dave.example.com/rest/download?", fromDaves);
        Assert.Contains("id=song1", fromMine);
        Assert.Null(RemoteSongs.Resolver(SubsonicClient.PathFor("unknown1", "song1")));   // a server that isn't set up
        Assert.Null(RemoteSongs.Resolver("/music/local.mp3"));
    }

    [AvaloniaFact]
    public void Two_servers_cannot_share_a_name()
    {
        using var app = new AppHarness();
        var servers = Servers(("mine.example.com", new FakeSubsonic("danny", "pw")), ("dave.example.com", new FakeSubsonic("guest", "friend")));
        var vm = Settings(app, servers, out _);
        Fill(vm, "Music", "https://mine.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));
        Fill(vm, "music", "https://dave.example.com", "guest", "friend");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.Contains("Another server is called", vm.Status);
        Assert.Single(ServerCredentials.Load());
    }

    [AvaloniaFact]
    public void A_server_added_but_not_saved_is_not_kept_with_the_others()
    {
        using var app = new AppHarness();
        var servers = Servers(("mine.example.com", new FakeSubsonic("danny", "pw")), ("dave.example.com", new FakeSubsonic("guest", "friend")));
        var vm = Settings(app, servers, out _);
        Fill(vm, "Mine", "https://mine.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));
        Fill(vm, "Dave's", "https://dave.example.com", "guest", "typed but not saved");   // wrong, and the half-typed one stays out
        vm.Selected = vm.Servers[0];

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));   // saving the first again

        Assert.Equal(["Mine"], ServerCredentials.Load().Select(s => s.Name));
    }

    [AvaloniaFact]
    public void Removing_a_server_forgets_it_and_leaves_the_others()
    {
        using var app = new AppHarness();
        var servers = Servers(("mine.example.com", new FakeSubsonic("danny", "pw")), ("dave.example.com", new FakeSubsonic("guest", "friend")));
        var vm = Settings(app, servers, out _);
        app.ViewModel.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        ServerEntry mine = Fill(vm, "Mine", "https://mine.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));
        ServerEntry daves = Fill(vm, "Dave's", "https://dave.example.com", "guest", "friend");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));
        vm.Selected = daves;
        Assert.True(vm.CanDisconnect);

        AppHarness.Await(vm.DisconnectCommand.ExecuteAsync(null));

        Assert.Equal(["Mine"], ServerCredentials.Load().Select(s => s.Name));
        Assert.Equal([mine.Id], ServerConnections.Accounts.Select(a => a.Id));
        Assert.Null(ServerConnections.ClientFor(daves.Id));
        Assert.Equal([mine], vm.Servers);
        Assert.Equal(mine, vm.Selected);
    }

    [AvaloniaFact]
    public void Removing_the_last_server_leaves_none()
    {
        using var app = new AppHarness();
        var vm = Settings(app, Servers(("music.example.com", new FakeSubsonic("danny", "pw"))), out _);
        app.ViewModel.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        Fill(vm, "Mine", "https://music.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        AppHarness.Await(vm.DisconnectCommand.ExecuteAsync(null));

        Assert.False(File.Exists(SavedFile));
        Assert.Empty(ServerConnections.Accounts);
        Assert.False(ServerConnections.Any);
        Assert.Empty(vm.Servers);
        Assert.False(vm.CanDisconnect);
    }

    // ───────────── Songs not on this computer ─────────────

    [AvaloniaFact]
    public void The_choice_for_songs_not_on_this_computer_starts_at_only_songs_I_download_and_is_remembered()
    {
        using var app = new AppHarness();
        var vm = app.ViewModel.Settings.Servers;
        Assert.Equal(RemoteFetchMode.Never, vm.FetchChoice.Value);
        Assert.Equal(RemoteFetchMode.Never, RemoteSongs.AutoFetch);

        vm.FetchChoice = vm.FetchChoices.Single(c => c.Value == RemoteFetchMode.Temporary);

        Assert.Equal(RemoteFetchMode.Temporary, app.Settings.RemoteFetch);   // kept with the settings
        Assert.Equal(RemoteFetchMode.Temporary, RemoteSongs.AutoFetch);
        Assert.Contains("removed when GZJam next starts or closes", vm.FetchHelp);
    }
}
