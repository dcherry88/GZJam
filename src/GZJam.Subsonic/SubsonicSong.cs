namespace GZJam.Subsonic;

/// <summary>A song on a Subsonic-compatible server, as its API describes it (no file to read tags from).</summary>
public sealed record SubsonicSong(
    string Id, string Title, string? Artist, string? Album, string? AlbumArtist, string? Genre,
    int? Year, int? TrackNumber, TimeSpan Duration, double? Bpm, string? Suffix, long Size,
    string? CoverArt, string? Comment);
