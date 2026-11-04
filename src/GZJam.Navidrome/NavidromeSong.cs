namespace GZJam.Navidrome;

/// <summary>A song on a Navidrome server, as its Subsonic API describes it (no file to read tags from).</summary>
public sealed record NavidromeSong(
    string Id, string Title, string? Artist, string? Album, string? AlbumArtist, string? Genre,
    int? Year, int? TrackNumber, TimeSpan Duration, double? Bpm, string? Suffix, long Size,
    string? CoverArt, string? Comment);
