using Avalonia.Headless.XUnit;
using GZJam.Library;

namespace GZJam.App.Tests;

/// <summary>Songs from a server (Navidrome) beside the ones in folders, in the song list, the queue and the night.</summary>
public sealed class RemoteSongsAppTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("gzjam-remote-app-test-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static Track Song(string id, string title, string artist) => new()
    {
        Path = "navidrome://" + id,
        Folder = artist + "/Some Album",
        FileName = title + ".flac",
        Title = title,
        Artist = artist,
        Album = "Some Album",
        Duration = TimeSpan.FromSeconds(200),
    };

    private static void Sync(AppHarness app, params Track[] songs)
    {
        app.Database.SyncRemoteTracks(app.Database.AddRemoteRoot("navidrome"), songs);
        app.Library.ReloadAfterRemoteSync();
        AppHarness.Settle();
    }

    [AvaloniaFact]
    public void Synced_songs_show_in_the_song_list_and_search_beside_the_folders()
    {
        using var app = new AppHarness();
        int inFolders = app.Library.Results.Count;

        Sync(app, Song("n1", "Zoot Suit Riot", "Zoot Band"), Song("n2", "Another One", "Zoot Band"));

        Assert.Equal(inFolders + 2, app.Library.Results.Count);
        Assert.Contains(app.Library.Roots, r => r.Node.Name == "Navidrome" && r.Node.TrackCount == 2);

        app.Library.SearchText = "zoot";
        AppHarness.Settle();
        Assert.Equal(["navidrome://n1", "navidrome://n2"], app.Library.Results.Select(t => t.Path).Order());
    }

    [AvaloniaFact]
    public void Songs_from_a_server_are_not_queued_for_analysis_with_the_library()
    {
        using var app = new AppHarness();
        Sync(app, Song("n1", "Zoot Suit Riot", "Zoot Band"));

        app.ViewModel.Analysis!.QueueLibrary();
        AppHarness.Settle();

        Assert.DoesNotContain(app.Database.GetPendingAnalysis(TrackAnalysisVersion), p => p.Path.Contains("://"));
    }

    private const int TrackAnalysisVersion = Audio.TrackAnalyzer.Version;

    [AvaloniaFact]
    public void A_queued_song_from_a_server_comes_back_after_a_restart()
    {
        using (var first = new AppHarness(workDirectory: _work))
        {
            Sync(first, Song("n1", "Zoot Suit Riot", "Zoot Band"));
            AppHarness.WaitFor(() => File.Exists(first.ViewModel.NightPath));
            first.ViewModel.Queue.Add([first.Database.FindTrack("navidrome://n1")!]);
            first.ViewModel.WriteNightNow();
            first.ViewModel.CloseNight();
        }

        using var app = new AppHarness(workDirectory: _work);
        AppHarness.WaitFor(() => app.ViewModel.Queue.Items.Count > 0);

        Assert.Equal(["navidrome://n1"], app.ViewModel.Queue.Items.Select(i => i.Track.Path));
    }
}
