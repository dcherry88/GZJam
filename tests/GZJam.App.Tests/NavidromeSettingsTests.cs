using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Avalonia.Headless.XUnit;
using GZJam.App.ViewModels;
using GZJam.Audio;
using GZJam.Navidrome;

namespace GZJam.App.Tests;

/// <summary>The saved Navidrome login, and Settings › Navidrome's Test and Save: never touching the real login.</summary>
[Collection("Navidrome globals")]   // they set the app's one Navidrome login and RemoteSongs.Resolver: one test class at a time
public sealed class NavidromeSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gzjam-navidrome-settings-").FullName;

    public NavidromeSettingsTests() => NavidromeCredentials.PathOverride = Path.Combine(_dir, "navidrome.json");

    public void Dispose()
    {
        NavidromeConnection.Disconnect();
        NavidromeCredentials.PathOverride = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string SavedFile => NavidromeCredentials.DefaultPath;

    // ───────────── The saved login ─────────────

    [Fact]
    public void A_saved_login_comes_back_and_the_password_is_not_readable_in_the_file()
    {
        NavidromeCredentials.Save(new NavidromeAccount("https://music.example.com", "danny", "s3cret pass"));

        var back = NavidromeCredentials.Load();

        Assert.Equal(new NavidromeAccount("https://music.example.com", "danny", "s3cret pass"), back);
        Assert.DoesNotContain("s3cret", File.ReadAllText(SavedFile));
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("s3cret pass")), File.ReadAllText(SavedFile));   // encrypted, not just encoded
    }

    [Fact]
    public void Nothing_saved_or_a_damaged_file_is_no_login()
    {
        Assert.Null(NavidromeCredentials.Load());
        File.WriteAllText(SavedFile, "{ not json");
        Assert.Null(NavidromeCredentials.Load());
    }

    [Fact]
    public void A_password_that_cant_be_read_leaves_the_address_and_user_for_Settings_to_show()
    {
        File.WriteAllText(SavedFile, """{"Url":"https://m.example.com","User":"danny","Password":"AAAA","Protection":"dpapi"}""");

        var back = NavidromeCredentials.Load();

        Assert.Equal(new NavidromeAccount("https://m.example.com", "danny", ""), back);
        Assert.False(back!.IsComplete);
    }

    [Fact]
    public void A_cleared_login_is_gone()
    {
        NavidromeCredentials.Save(new NavidromeAccount("https://m.example.com", "danny", "x"));
        NavidromeCredentials.Clear();
        Assert.Null(NavidromeCredentials.Load());
    }

    [Theory]
    [InlineData("https://music.example.com", "https://music.example.com/")]
    [InlineData("music.example.com", "https://music.example.com/")]
    [InlineData("  http://192.168.1.5:4533/  ", "http://192.168.1.5:4533/")]
    [InlineData("https://example.com/navidrome/", "https://example.com/navidrome")]
    [InlineData("https://example.com/app?x=1#top", "https://example.com/app")]
    public void A_server_address_is_taken_as_typed_where_it_makes_sense(string typed, string expected)
    {
        Assert.True(NavidromeConnection.TryParseUrl(typed, out Uri uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://music.example.com")]
    [InlineData("https://")]
    [InlineData("not a url at all")]
    public void Something_that_is_not_a_web_address_is_refused(string typed) =>
        Assert.False(NavidromeConnection.TryParseUrl(typed, out _));

    // ───────────── Settings › Navidrome ─────────────

    /// <summary>A Navidrome that knows one login and answers pings, as Subsonic does: a token from the password and salt.</summary>
    private sealed class FakeNavidrome(string user, string password) : HttpMessageHandler
    {
        public int Pings { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Pings++;
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            string expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password + query["s"]))).ToLowerInvariant();
            string body = query["u"] == user && query["t"] == expected
                ? """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","serverVersion":"0.64.2"}}"""
                : """{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password"}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static NavidromeSettingsViewModel Settings(AppHarness app, FakeNavidrome server, out List<string> syncs)
    {
        var syncsMade = new List<string>();
        syncs = syncsMade;
        var vm = app.ViewModel.Settings.Navidrome;
        vm.CreateClient = account => new NavidromeClient(new Uri(account.Url), account.User, account.Password, new HttpClient(server));
        vm.SyncAfterSave = () =>
        {
            syncsMade.Add("synced");
            return Task.CompletedTask;
        };
        return vm;
    }

    [AvaloniaFact]
    public void Test_says_what_is_wrong_with_what_was_typed_before_asking_the_server()
    {
        using var app = new AppHarness();
        var server = new FakeNavidrome("danny", "pw");
        var vm = Settings(app, server, out _);

        vm.ServerUrl = "not a url at all";
        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));
        Assert.Contains("isn't a web address", vm.Status);

        vm.ServerUrl = "https://music.example.com";
        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));
        Assert.Equal("Type the user name.", vm.Status);

        vm.UserName = "danny";
        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));
        Assert.Equal("Type the password.", vm.Status);
        Assert.Equal(0, server.Pings);
    }

    [AvaloniaFact]
    public void Test_connects_with_the_login_as_typed_and_saves_nothing()
    {
        using var app = new AppHarness();
        var server = new FakeNavidrome("danny", "pw");
        var vm = Settings(app, server, out _);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("music.example.com", "danny", "pw");

        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));

        Assert.Equal("Connected: navidrome 0.64.2. The login works; press SAVE to use it.", vm.Status);
        Assert.False(File.Exists(SavedFile));
        Assert.Null(NavidromeConnection.Account);
        Assert.True(vm.CanAct);
    }

    [AvaloniaFact]
    public void Test_with_a_wrong_password_says_so_and_never_shows_the_login()
    {
        using var app = new AppHarness();
        var vm = Settings(app, new FakeNavidrome("danny", "pw"), out _);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("https://music.example.com", "danny", "wrong");

        AppHarness.Await(vm.TestCommand.ExecuteAsync(null));

        Assert.Equal("Couldn't connect. Navidrome said: Wrong username or password", vm.Status);
    }

    [AvaloniaFact]
    public void Save_keeps_a_login_that_works_uses_it_and_reads_the_server()
    {
        using var app = new AppHarness();
        var vm = Settings(app, new FakeNavidrome("danny", "pw"), out var syncs);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("https://music.example.com", "danny", "pw");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.Equal(new NavidromeAccount("https://music.example.com", "danny", "pw"), NavidromeCredentials.Load());
        Assert.Equal("pw", NavidromeConnection.Account!.Password);
        Assert.NotNull(NavidromeConnection.Client);
        Assert.NotNull(RemoteSongs.Resolver);
        Assert.Single(syncs);
        Assert.Equal("", vm.Password);   // never left in the box
        Assert.True(vm.HasSavedPassword);
        Assert.Contains("leave this empty to keep it", vm.PasswordHelp, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Saved and in use.", vm.Status);
    }

    [AvaloniaFact]
    public void Save_does_not_keep_a_login_that_fails()
    {
        using var app = new AppHarness();
        var vm = Settings(app, new FakeNavidrome("danny", "pw"), out var syncs);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("https://music.example.com", "danny", "nope");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.False(File.Exists(SavedFile));
        Assert.Null(NavidromeConnection.Account);
        Assert.Empty(syncs);
        Assert.EndsWith("Not saved.", vm.Status);
    }

    [AvaloniaFact]
    public void An_empty_password_box_keeps_the_saved_password_when_the_address_changes()
    {
        using var app = new AppHarness();
        var vm = Settings(app, new FakeNavidrome("danny", "pw"), out _);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("https://music.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        vm.ServerUrl = "https://music.example.com/navidrome";
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.Equal(new NavidromeAccount("https://music.example.com/navidrome", "danny", "pw"), NavidromeCredentials.Load());
    }

    [AvaloniaFact]
    public void Disconnect_is_offered_once_a_login_is_saved_as_the_button_sees_it()
    {
        using var app = new AppHarness();
        var vm = Settings(app, new FakeNavidrome("danny", "pw"), out _);
        bool enabled = vm.CanDisconnect;   // what the button last heard
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavidromeSettingsViewModel.CanDisconnect))
                enabled = vm.CanDisconnect;
        };
        Assert.False(enabled);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("https://music.example.com", "danny", "pw");

        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));

        Assert.True(enabled);
    }

    [AvaloniaFact]
    public void Disconnect_forgets_the_login()
    {
        using var app = new AppHarness();
        var vm = Settings(app, new FakeNavidrome("danny", "pw"), out _);
        app.ViewModel.ConfirmAsync = (_, _, _) => Task.FromResult(true);
        (vm.ServerUrl, vm.UserName, vm.Password) = ("https://music.example.com", "danny", "pw");
        AppHarness.Await(vm.SaveCommand.ExecuteAsync(null));
        Assert.True(vm.CanDisconnect);

        AppHarness.Await(vm.DisconnectCommand.ExecuteAsync(null));

        Assert.False(File.Exists(SavedFile));
        Assert.Null(NavidromeConnection.Account);
        Assert.Null(RemoteSongs.Resolver);
        Assert.Equal("", vm.ServerUrl);
        Assert.False(vm.HasSavedPassword);
        Assert.False(vm.CanDisconnect);
    }
}
