namespace GZJam.Library.Tests;

/// <summary>Songs from a server (Navidrome) in the library, beside the ones in folders.</summary>
public sealed class RemoteTracksTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    private static Track Song(string id, string title, string artist, string album, double? bpm = null) => new()
    {
        Path = "navidrome://" + id,
        Folder = $"{artist}/{album}",
        FileName = title + ".flac",
        Title = title,
        Artist = artist,
        Album = album,
        TagBpm = bpm,
        Duration = TimeSpan.FromSeconds(180),
    };

    private LibraryRoot Root() => _lib.Database.AddRemoteRoot("navidrome");

    [Fact]
    public void A_root_for_a_server_is_remote_and_called_by_its_name()
    {
        LibraryRoot root = Root();

        Assert.True(root.IsRemote);
        Assert.Equal("navidrome://", root.Path);
        Assert.Equal("Navidrome", root.Name);
        Assert.False(_lib.Database.AddRoot(_lib.MusicPath) is { IsRemote: true });
        Assert.Equal(root.Id, Root().Id);   // asking again finds it
    }

    [Fact]
    public void Synced_songs_are_searched_like_any_other()
    {
        LibraryRoot root = Root();
        var result = _lib.Database.SyncRemoteTracks(root,
            [Song("a1", "One O'Clock Jump", "Count Basie", "Decca", 172), Song("a2", "Topsy", "Count Basie", "Decca")]);

        Assert.Equal(new RemoteSyncResult(2, 0, 0), result);
        var found = _lib.Database.Search("basie");
        Assert.Equal(["navidrome://a1", "navidrome://a2"], found.Select(t => t.Path).Order());
        Assert.Equal(172, found.Single(t => t.Title == "One O'Clock Jump").Bpm);
        Assert.Equal(["Count Basie/Decca"], _lib.Database.GetFolderCounts().Select(c => c.Folder).Distinct());
        Assert.Single(_lib.Database.Search("topsy"));
    }

    [Fact]
    public void Syncing_again_updates_what_changed_and_takes_out_what_the_server_dropped()
    {
        LibraryRoot root = Root();
        _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jump", "Basie", "Decca"), Song("a2", "Topsy", "Basie", "Decca")]);

        var result = _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jumpin'", "Basie", "Decca"), Song("a3", "Corner Pocket", "Basie", "April")]);

        Assert.Equal(new RemoteSyncResult(1, 1, 1), result);
        Assert.Equal(["Corner Pocket", "Jumpin'"], _lib.Database.Search(null).Select(t => t.Title).Order());
    }

    [Fact]
    public void A_song_the_server_dropped_keeps_what_the_dj_gave_it()
    {
        LibraryRoot root = Root();
        _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jump", "Basie", "Decca")]);
        _lib.Database.SetRating(["navidrome://a1"], Rating.Loved);
        _lib.Database.SyncRemoteTracks(root, [Song("a2", "Topsy", "Basie", "Decca")]);

        Assert.Null(_lib.Database.FindTrack("navidrome://a1"));
        Assert.Equal(Rating.Loved, _lib.Database.GetMarks("navidrome://a1").Rating);

        // It comes back, marks and all.
        _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jump", "Basie", "Decca"), Song("a2", "Topsy", "Basie", "Decca")]);
        Assert.Equal(Rating.Loved, _lib.Database.FindTrack("navidrome://a1")!.Rating);
    }

    [Fact]
    public void An_empty_answer_from_the_server_changes_nothing()
    {
        LibraryRoot root = Root();
        _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jump", "Basie", "Decca")]);

        var result = _lib.Database.SyncRemoteTracks(root, []);

        Assert.Equal(new RemoteSyncResult(0, 1, 0), result);
        Assert.Single(_lib.Database.Search(null));
    }

    [Fact]
    public void Songs_on_a_server_are_not_analyzed_in_the_background_but_folder_songs_still_are()
    {
        _lib.AddMp3("Swing/a.mp3");
        _lib.AddRootAndScan();
        _lib.Database.SyncRemoteTracks(Root(), [Song("a1", "Jump", "Basie", "Decca")]);

        var pending = _lib.Database.GetPendingAnalysis(1);

        Assert.Single(pending);
        Assert.DoesNotContain(pending, p => p.Path.Contains("://"));
    }

    [Fact]
    public void Scanning_leaves_a_servers_songs_alone()
    {
        LibraryRoot root = Root();
        _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jump", "Basie", "Decca")]);

        var scan = _lib.Scanner.Scan(root);

        Assert.Equal(0, scan.Removed);
        Assert.Single(_lib.Database.Search(null));
    }

    [Fact]
    public void Removing_the_root_forgets_its_songs_but_not_what_the_dj_gave_them()
    {
        LibraryRoot root = Root();
        _lib.Database.SyncRemoteTracks(root, [Song("a1", "Jump", "Basie", "Decca")]);
        _lib.Database.SetRating(["navidrome://a1"], Rating.Loved);

        _lib.Database.RemoveRoot(root.Id);

        Assert.Empty(_lib.Database.Search(null));
        Assert.Equal(Rating.Loved, _lib.Database.GetMarks("navidrome://a1").Rating);
    }
}
