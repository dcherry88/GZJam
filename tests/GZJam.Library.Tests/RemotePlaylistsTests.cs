namespace GZJam.Library.Tests;

/// <summary>A server's playlists as GZJam's own, kept as the server has them.</summary>
public sealed class RemotePlaylistsTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    private LibraryDatabase Db => _lib.Database;

    private static RemotePlaylist Remote(string id, string name, params string[] songs) =>
        new(id, name, [.. songs.Select(s => "navidrome://" + s)]);

    private RemotePlaylistSyncResult Sync(params RemotePlaylist[] playlists) => Db.SyncRemotePlaylists("navidrome", "Navidrome", playlists);

    private IReadOnlyList<string> Songs(string name) =>
        Db.GetPlaylistEntries(Db.GetPlaylists().Single(p => p.Name == name).Id).Select(e => e.Path.Replace("navidrome://", "")).ToList();

    [Fact]
    public void A_playlist_on_the_server_becomes_one_here_in_its_order_with_repeats()
    {
        var result = Sync(Remote("p1", "Friday Lindy", "b", "a", "b"));

        Assert.Equal(new RemotePlaylistSyncResult(1, 0, 0, 0), result);
        Assert.Equal(["b", "a", "b"], Songs("Friday Lindy"));
        Assert.True(Db.IsRemotePlaylist(Db.GetPlaylists().Single().Id));
    }

    [Fact]
    public void Syncing_again_with_nothing_changed_leaves_the_playlist_as_it_was()
    {
        Sync(Remote("p1", "Friday Lindy", "a", "b"));
        var before = Db.GetPlaylistEntries(Db.GetPlaylists().Single().Id);

        var result = Sync(Remote("p1", "Friday Lindy", "a", "b"));

        Assert.Equal(new RemotePlaylistSyncResult(0, 0, 1, 0), result);
        Assert.Equal(before, Db.GetPlaylistEntries(Db.GetPlaylists().Single().Id));   // the same entries, not made again
    }

    [Fact]
    public void A_playlist_changed_on_the_server_is_changed_here()
    {
        Sync(Remote("p1", "Friday Lindy", "a", "b", "c"));

        var result = Sync(Remote("p1", "Friday Lindy", "c", "a"));

        Assert.Equal(new RemotePlaylistSyncResult(0, 1, 0, 0), result);
        Assert.Equal(["c", "a"], Songs("Friday Lindy"));
        Assert.Single(Db.GetPlaylists());
    }

    [Fact]
    public void A_playlist_the_server_renamed_is_renamed_here_but_a_rename_here_sticks()
    {
        Sync(Remote("p1", "Friday", "a"), Remote("p2", "Saturday", "a"));
        Db.RenamePlaylist(Db.GetPlaylists().Single(p => p.Name == "Saturday").Id, "Sat night");

        var result = Sync(Remote("p1", "Friday Lindy", "a"), Remote("p2", "Saturday", "a"));

        Assert.Equal(new RemotePlaylistSyncResult(0, 1, 1, 0), result);
        Assert.Equal(["Friday Lindy", "Sat night"], Db.GetPlaylists().Select(p => p.Name).Order());
    }

    [Fact]
    public void A_playlist_gone_from_the_server_is_gone_here_and_the_others_stay()
    {
        Sync(Remote("p1", "Friday", "a"), Remote("p2", "Saturday", "b"));

        var result = Sync(Remote("p2", "Saturday", "b"));

        Assert.Equal(new RemotePlaylistSyncResult(0, 0, 1, 1), result);
        Assert.Equal(["Saturday"], Db.GetPlaylists().Select(p => p.Name));
    }

    [Fact]
    public void A_server_with_no_playlists_clears_the_copies_but_never_the_dj_own()
    {
        Db.CreatePlaylist("Mine");
        Sync(Remote("p1", "Friday", "a"));

        Sync();

        Assert.Equal(["Mine"], Db.GetPlaylists().Select(p => p.Name));
    }

    [Fact]
    public void A_playlist_with_a_name_the_dj_has_already_is_told_apart_and_theirs_is_untouched()
    {
        var mine = Db.CreatePlaylist("Friday");
        Db.AddToPlaylist(mine.Id, ["navidrome://x"]);

        Sync(Remote("p1", "Friday", "a"));

        Assert.Equal(["Friday", "Friday (Navidrome)"], Db.GetPlaylists().Select(p => p.Name).Order());
        Assert.Equal(["navidrome://x"], Db.GetPlaylistEntries(mine.Id).Select(e => e.Path));
        Assert.False(Db.IsRemotePlaylist(mine.Id));
    }

    [Fact]
    public void Edits_to_a_copy_are_put_back_and_a_deleted_copy_returns()
    {
        Sync(Remote("p1", "Friday", "a", "b"));
        long id = Db.GetPlaylists().Single().Id;
        Db.AddToPlaylist(id, ["navidrome://extra"]);

        Sync(Remote("p1", "Friday", "a", "b"));
        Assert.Equal(["a", "b"], Songs("Friday"));

        Db.DeletePlaylist(id);
        var result = Sync(Remote("p1", "Friday", "a", "b"));

        Assert.Equal(1, result.Added);
        Assert.Equal(["a", "b"], Songs("Friday"));
    }

    [Fact]
    public void A_copys_songs_are_the_librarys_when_it_has_them()
    {
        var root = Db.AddRemoteRoot("navidrome");
        Db.SyncRemoteTracks(root, [new Track
        {
            Path = "navidrome://a", Folder = "Basie/Decca", FileName = "Jump.flac", Title = "Jump", Artist = "Basie", Duration = TimeSpan.FromSeconds(100),
        }]);

        Sync(Remote("p1", "Friday", "a"));

        var playlist = Db.GetPlaylists().Single();
        Assert.Equal(1, playlist.Count);
        Assert.Equal(TimeSpan.FromSeconds(100), playlist.Length);
        Assert.Equal("Jump", Db.Search(null, playlist: playlist.Id).Single().Title);
    }
}
