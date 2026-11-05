using Microsoft.Data.Sqlite;

namespace GZJam.Library.Tests;

public sealed class HistoryTests : IDisposable
{
    private static readonly TimeSpan Gig = TimeSpan.FromHours(3);
    private static readonly DateTime Evening = new(2026, 10, 18, 19, 30, 0, DateTimeKind.Utc);

    private readonly TempLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    private static Track Song(string title, string path = "") => new()
    {
        Path = path.Length > 0 ? path : $@"C:\music\{title}.mp3",
        Folder = "",
        FileName = title + ".mp3",
        Title = title,
        Artist = "Count Basie",
        TagBpm = 150,
        Duration = TimeSpan.FromMinutes(3),
    };

    [Fact]
    public void Plays_are_saved_in_order_with_their_details()
    {
        var db = _lib.Database;
        PlaySession session = db.StartOrContinueSession(Evening, Gig);
        db.AddPlay(session.Id, Song("Jumpin' at the Woodside"), Evening, "1", 0);
        db.AddPlay(session.Id, Song("Splanky"), Evening.AddMinutes(4), "2", -2.5);

        var plays = db.GetPlays(session.Id);

        Assert.Equal(["Jumpin' at the Woodside", "Splanky"], plays.Select(p => p.DisplayTitle));
        Assert.Equal("2", plays[1].Deck);
        Assert.Equal(-2.5, plays[1].TempoPercent);
        Assert.Equal(Evening.AddMinutes(4), plays[1].PlayedUtc);
        Assert.Equal(150, plays[1].Bpm);
    }

    [Fact]
    public void A_restart_mid_gig_continues_the_same_session()
    {
        var db = _lib.Database;
        PlaySession first = db.StartOrContinueSession(Evening, Gig);
        db.AddPlay(first.Id, Song("One"), Evening.AddHours(1), "1", 0);

        PlaySession afterRestart = db.StartOrContinueSession(Evening.AddHours(3), Gig);   // 2 h after the last play

        Assert.Equal(first.Id, afterRestart.Id);
    }

    [Fact]
    public void The_next_gig_starts_a_new_session()
    {
        var db = _lib.Database;
        PlaySession saturday = db.StartOrContinueSession(Evening, Gig);
        db.AddPlay(saturday.Id, Song("One"), Evening, "1", 0);

        PlaySession nextWeek = db.StartOrContinueSession(Evening.AddDays(7), Gig);
        db.AddPlay(nextWeek.Id, Song("Two"), Evening.AddDays(7), "1", 0);
        db.AddPlay(nextWeek.Id, Song("Three"), Evening.AddDays(7).AddMinutes(3), "2", 0);

        Assert.NotEqual(saturday.Id, nextWeek.Id);
        var sessions = db.GetSessions();
        Assert.Equal([nextWeek.Id, saturday.Id], sessions.Select(s => s.Id));   // newest first
        Assert.Equal(2, sessions[0].PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(6), sessions[0].TotalDuration);
        Assert.Equal(Evening.AddDays(7).AddMinutes(3), sessions[0].LastPlayUtc);
    }

    [Fact]
    public void Empty_old_sessions_are_not_listed()
    {
        var db = _lib.Database;
        db.StartOrContinueSession(Evening, Gig);                   // opened, nothing played
        PlaySession later = db.StartOrContinueSession(Evening.AddDays(1), Gig);

        Assert.Equal([later.Id], db.GetSessions().Select(s => s.Id));
    }

    [Fact]
    public void History_survives_the_track_leaving_the_library()
    {
        string path = _lib.AddMp3("Swing/gone.mp3", new GZJam.Testing.TestMp3.Tags(Title: "Gone Song", Artist: "Somebody"));
        LibraryRoot root = _lib.AddRootAndScan();
        Track track = Assert.Single(_lib.Database.Search(null));
        PlaySession session = _lib.Database.StartOrContinueSession(Evening, Gig);
        _lib.Database.AddPlay(session.Id, track, Evening, "1", 0);

        File.Delete(path);
        _lib.Scanner.Scan(root);

        Assert.Empty(_lib.Database.Search(null));
        PlayRecord play = Assert.Single(_lib.Database.GetPlays(session.Id));
        Assert.Equal("Gone Song", play.DisplayTitle);
        Assert.Equal("Somebody", play.Artist);
    }

    [Fact]
    public void Bpms_stored_ten_or_a_hundred_times_over_are_scaled_back_once()
    {
        // A library scanned before tags storing BPM x10 were understood (schema 12).
        string path = Path.Combine(_lib.Directory, "x10.db");
        new LibraryDatabase(path).AddRoot(@"C:\music");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tracks(root_id, path, folder, file_name, file_size, modified_utc, title, bpm) VALUES
                    (1, 'a.mp3', '', 'a.mp3', 1, 0, 'Pink Panther Theme', 1180),
                    (1, 'b.mp3', '', 'b.mp3', 1, 0, 'Times a hundred', 13200),
                    (1, 'c.mp3', '', 'c.mp3', 1, 0, 'Fine as it is', 236),
                    (1, 'd.mp3', '', 'd.mp3', 1, 0, 'No tag', NULL);
                INSERT INTO sessions(id, started_utc) VALUES (1, 0);
                INSERT INTO plays(session_id, played_utc, path, title, bpm) VALUES (1, 0, 'a.mp3', 'Pink Panther Theme', 1180);
                ALTER TABLE tracks DROP COLUMN tag_version;   -- added by upgrade 14
                DROP TABLE cues;                              -- added by upgrade 15
                DROP TABLE art;                               -- added by upgrade 16
                ALTER TABLE eq DROP COLUMN mid_hz;            -- added by upgrade 17
                ALTER TABLE eq DROP COLUMN even;
                DROP TABLE remote_playlists;                  -- added by upgrade 25 (it points at playlists, so it goes first)
                DROP TABLE playlist_entries;                  -- added by upgrade 18
                DROP TABLE playlists;
                DROP TABLE schema_compat;                     -- added by upgrade 19
                DROP INDEX ix_tracks_order;                   -- added by upgrade 20
                DROP TRIGGER tracks_ai;                       -- upgrades 21 and 22 remade the search index
                DROP TRIGGER tracks_ad;                       -- with grouping (and makes it again)
                DROP TRIGGER tracks_au;
                DROP TABLE tracks_fts;
                ALTER TABLE tracks DROP COLUMN grouping;
                ALTER TABLE tracks DROP COLUMN tags;          -- added by upgrade 22
                ALTER TABLE tracks DROP COLUMN tag_text;
                DROP TABLE tag_edits;                         -- added by upgrade 23
                ALTER TABLE trims DROP COLUMN fade_out;       -- added by upgrade 24
                PRAGMA user_version = 12;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new LibraryDatabase(path);

        double? Bpm(string title) => db.Search(null).Single(t => t.Title == title).TagBpm;
        Assert.Equal(118, Bpm("Pink Panther Theme"));
        Assert.Equal(132, Bpm("Times a hundred"));
        Assert.Equal(236, Bpm("Fine as it is"));
        Assert.Null(Bpm("No tag"));
        Assert.Equal(118, Assert.Single(db.GetPlays(1)).Bpm);
    }

    [Fact]
    public void An_existing_library_is_upgraded_in_place()
    {
        // A database made by the first version of GZJam (schema 1, no history tables).
        string path = Path.Combine(_lib.Directory, "old.db");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE roots (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE);
                CREATE TABLE tracks (id INTEGER PRIMARY KEY, root_id INTEGER, path TEXT NOT NULL UNIQUE, folder TEXT,
                                     file_name TEXT, title TEXT, artist TEXT, album TEXT, album_artist TEXT, genre TEXT,
                                     comment TEXT, track_number INTEGER, bpm REAL);
                INSERT INTO roots(path) VALUES ('C:\music');
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new LibraryDatabase(path);

        Assert.Equal(@"C:\music", Assert.Single(db.GetRoots()).Path);   // kept
        PlaySession session = db.StartOrContinueSession(Evening, Gig);      // and history works
        db.AddPlay(session.Id, Song("Works"), Evening, "1", 0);
        Assert.Single(db.GetPlays(session.Id));
    }
}
