using System.ComponentModel;

namespace GZJam.Library;

/// <summary>One audio file in the library, with the tag data read from it (and the DJ's edits to it, #129).</summary>
public sealed class Track : INotifyPropertyChanged
{
    public long Id { get; init; }
    public long RootId { get; init; }

    /// <summary>Full path on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Folder relative to its library root, '/'-separated; empty for files directly in the root.</summary>
    public required string Folder { get; init; }

    public required string FileName { get; init; }
    public long FileSize { get; init; }
    public DateTime ModifiedUtc { get; init; }

    // The tags: read from the file, and changed in place when the DJ edits them (#129), so every
    // copy of the song on screen shows the edit.

    public string? Title { get => _title; set => Set(ref _title, value, nameof(Title), nameof(DisplayTitle)); }
    public string? Artist { get => _artist; set => Set(ref _artist, value, nameof(Artist)); }
    public string? Album { get => _album; set => Set(ref _album, value, nameof(Album)); }
    public string? AlbumArtist { get => _albumArtist; set => Set(ref _albumArtist, value, nameof(AlbumArtist)); }
    public string? Genre { get => _genre; set => Set(ref _genre, value, nameof(Genre)); }
    public string? Comment { get => _comment; set => Set(ref _comment, value, nameof(Comment)); }

    /// <summary>
    /// The Grouping tag, which iTunes and Music users often sort by: the dance, the feel, the era
    /// ("Balboa, fast, oldie"). Read from the file, like the comment (#161).
    /// </summary>
    public string? Grouping { get => _grouping; set => Set(ref _grouping, value, nameof(Grouping)); }
    public int? Year { get => _year; set => Set(ref _year, value, nameof(Year)); }
    public int? TrackNumber { get => _trackNumber; set => Set(ref _trackNumber, value, nameof(TrackNumber)); }

    /// <summary>BPM from the file's tag. Always preferred to a detected one.</summary>
    public double? TagBpm { get => _tagBpm; set => Set(ref _tagBpm, value, nameof(TagBpm), nameof(Bpm)); }

    /// <summary>
    /// The file's other tags (#119), sorted by name: the composer, a mood, the dance in a custom
    /// tag. Shown as columns the DJ chooses, and searched.
    /// </summary>
    public IReadOnlyList<SongTag> Tags { get => _tags; set => Set(ref _tags, value, nameof(Tags)); }

    private string? _title, _artist, _album, _albumArtist, _genre, _comment, _grouping;
    private int? _year, _trackNumber;
    private double? _tagBpm;
    private IReadOnlyList<SongTag> _tags = [];

    private void Set<T>(ref T field, T value, string name, string? also = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (also is not null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(also));
    }

    /// <summary>Takes <paramref name="other"/>'s tags, as read again or edited: the song's marks stay.</summary>
    public void CopyTagsFrom(Track other)
    {
        Title = other.Title;
        Artist = other.Artist;
        Album = other.Album;
        AlbumArtist = other.AlbumArtist;
        Genre = other.Genre;
        Comment = other.Comment;
        Grouping = other.Grouping;
        Year = other.Year;
        TrackNumber = other.TrackNumber;
        TagBpm = other.TagBpm;
        if (!Tags.SequenceEqual(other.Tags))
            Tags = other.Tags;
    }

    /// <summary>The DJ's edits (#129), by <see cref="SongFields"/> field, over the tags read from the file.</summary>
    internal void ApplyEdits(IReadOnlyDictionary<string, string?> edits)
    {
        if (edits.Count == 0)
            return;
        var tags = Tags.ToList();
        foreach (var (field, value) in edits)
        {
            switch (field)
            {
                case SongFields.Title: Title = value; break;
                case SongFields.Artist: Artist = value; break;
                case SongFields.Album: Album = value; break;
                case SongFields.AlbumArtist: AlbumArtist = value; break;
                case SongFields.Genre: Genre = value; break;
                case SongFields.Comment: Comment = value; break;
                case SongFields.Grouping: Grouping = value; break;
                case SongFields.Year: Year = SongFields.ParseWhole(value); break;
                case SongFields.Track: TrackNumber = SongFields.ParseWhole(value); break;
                case SongFields.Bpm: TagBpm = SongFields.ParseBpm(value); break;
                default:
                    if (SongFields.TagName(field) is { } name)
                    {
                        string key = SongTags.KeyOf(name);
                        tags.RemoveAll(t => t.Key == key);
                        if (value is not null)
                            tags.Add(new SongTag(name, value));
                    }
                    break;
            }
        }
        tags.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        if (!Tags.SequenceEqual(tags))
            Tags = tags;
    }

    /// <summary>The value of the tag with this <see cref="SongTag.Key"/>, if the song has it.</summary>
    public string? Tag(string key)
    {
        foreach (SongTag tag in Tags)
        {
            if (tag.Key == key)
                return tag.Value;
        }
        return null;
    }

    /// <summary>
    /// BPM found by analysis, as detected. Set when analysis finishes, which may be after the
    /// track is on screen.
    /// </summary>
    public double? DetectedBpm
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetectedBpm)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bpm)));
        }
    }

    /// <summary>
    /// The tempo range the DJ chose for this track because detection got it wrong; null for
    /// automatic. Choosing one also means the detected BPM is preferred to the tag's.
    /// </summary>
    public BpmRange? BpmRange
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BpmRange)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bpm)));
        }
    }

    /// <summary>A BPM the DJ typed (or locked in Mixxx); it wins over the tag and detection.</summary>
    public double? ManualBpm
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ManualBpm)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bpm)));
        }
    }

    /// <summary>The BPM to show: one typed by the DJ, else the tag's, unless the DJ asked for detection in a range.</summary>
    public double? Bpm => ManualBpm ?? (BpmRange is not null ? DetectedBpm ?? TagBpm : TagBpm ?? DetectedBpm);

    /// <summary>
    /// How long the song is: from its tags, or, when they give none (its audio behind a damaged
    /// tag, #220), as long as the analysis found, set when it finishes.
    /// </summary>
    public TimeSpan Duration
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        }
    }

    /// <summary>The DJ's rating. Kept in the library database, never written to the file.</summary>
    public Rating Rating
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rating)));
        }
    }

    /// <summary>The DJ's energy mark. Kept in the library database, never written to the file.</summary>
    public Energy Energy
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Energy)));
        }
    }

    /// <summary>The DJ's labels on this song ("Lindy", "Female vocals"), alphabetically. Kept in the library database.</summary>
    public IReadOnlyList<string> Labels
    {
        get;
        set
        {
            if (field.SequenceEqual(value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Labels)));
        }
    } = [];

    /// <summary>When the room last heard this file (from History), or null if never.</summary>
    public DateTime? LastPlayedUtc
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastPlayedUtc)));
        }
    }

    /// <summary>How many times the room has heard it, across all sessions.</summary>
    public int PlayCount
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayCount)));
        }
    }

    /// <summary>
    /// The file isn't there any more: a song in a playlist that was moved or deleted outside GZJam's
    /// music folders. Shown grayed out, as players show a missing file.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>Not in the library: a song from outside the music folders, in a playlist or the queue.</summary>
    public bool IsOutsideLibrary => Id == 0;

    /// <summary>Played in the session going on now (tonight's gig): shown with a check so it isn't repeated (#238).</summary>
    public bool IsPlayedTonight
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlayedTonight)));
        }
    }

    /// <summary>
    /// Shown as part of a playlist: which entry this row is (a song can be in a playlist twice,
    /// each its own row), and its number in the playlist, from 1. Null in the rest of the library.
    /// </summary>
    public long? PlaylistEntryId { get; private set; }

    public int? PlaylistNumber { get; private set; }

    /// <summary>A new track with this one's file and tags, without the DJ's marks or a playlist place.</summary>
    internal Track CopyOfTags() => new()
    {
        Id = Id, RootId = RootId, Path = Path, Folder = Folder, FileName = FileName, FileSize = FileSize, ModifiedUtc = ModifiedUtc,
        Title = Title, Artist = Artist, Album = Album, AlbumArtist = AlbumArtist, Genre = Genre, Comment = Comment, Grouping = Grouping,
        Year = Year, TrackNumber = TrackNumber, TagBpm = TagBpm, Tags = Tags, Duration = Duration, IsMissing = IsMissing,
    };

    /// <summary>
    /// This song as a row of a remote root (<see cref="LibraryRoot.IsRemote"/>). It has no file to
    /// stamp, so its size and date are zero: the same "version" every time, so its analysis is made once.
    /// </summary>
    internal Track InRemoteRoot(long rootId) => new()
    {
        Id = Id, RootId = rootId, Path = Path, Folder = Folder, FileName = FileName,
        Title = Title, Artist = Artist, Album = Album, AlbumArtist = AlbumArtist, Genre = Genre, Comment = Comment, Grouping = Grouping,
        Year = Year, TrackNumber = TrackNumber, TagBpm = TagBpm, Tags = Tags, Duration = Duration,
    };

    internal Track InPlaylist(long entryId, int number)
    {
        (PlaylistEntryId, PlaylistNumber) = (entryId, number);
        return this;
    }

    /// <summary>Title tag, or the file name when the tag is missing.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(FileName) : Title;

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => $"{DisplayTitle} - {Artist}";
}

/// <summary>A folder added to the library by the user.</summary>
public sealed record LibraryRoot(long Id, string Path)
{
    /// <summary>
    /// The songs here live on a server, not in a folder (<c>subsonic://</c>): they are filled in by
    /// a sync with it rather than a scan, and are never watched or analyzed in the background.
    /// </summary>
    public bool IsRemote => Path.Contains("://", StringComparison.Ordinal);

    public string Name => IsRemote ? RemoteNames?.Invoke(Path) ?? RemoteSchemeName(Path)
        : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : Path;

    /// <summary>
    /// The name of a server's folder, from what the app knows of its servers (a root is
    /// <c>subsonic://&lt;server&gt;</c>); null for one it doesn't know.
    /// </summary>
    public static Func<string, string?>? RemoteNames { get; set; }

    /// <summary>"Subsonic" for <c>subsonic://abc</c>: the scheme, for a server that isn't known.</summary>
    private static string RemoteSchemeName(string path)
    {
        string scheme = path[..path.IndexOf("://", StringComparison.Ordinal)];
        return scheme.Length == 0 ? path : char.ToUpperInvariant(scheme[0]) + scheme[1..];
    }
}

/// <summary>Limits a search to a folder and everything beneath it.</summary>
/// <param name="Folder">Relative to the root, '/'-separated; empty means the whole root.</param>
public sealed record FolderScope(long RootId, string Folder);
