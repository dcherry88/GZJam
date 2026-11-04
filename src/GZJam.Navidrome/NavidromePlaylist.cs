namespace GZJam.Navidrome;

/// <summary>A playlist on a Navidrome server (the songs in it come from <see cref="NavidromeClient.PlaylistSongsAsync"/>).</summary>
public sealed record NavidromePlaylist(string Id, string Name, int SongCount);
