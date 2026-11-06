namespace GZJam.Subsonic;

/// <summary>A playlist on a Subsonic-compatible server (the songs in it come from <see cref="SubsonicClient.PlaylistSongsAsync"/>).</summary>
public sealed record SubsonicPlaylist(string Id, string Name, int SongCount);
