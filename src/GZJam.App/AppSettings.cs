using System.Text.Json;
using System.Text.Json.Serialization;
using GZJam.Audio;

namespace GZJam.App;

/// <summary>What happens when a track is loaded onto the deck the room hears (ON AIR).</summary>
public enum PlayingDeckLoad
{
    /// <summary>Refuse; the deck must be paused, or crossfaded out of the room, first. The safe default.</summary>
    Block,
    /// <summary>Ask for confirmation.</summary>
    Ask,
    /// <summary>Load anyway.</summary>
    Allow,
}

/// <summary>How the play and FADE OUT keys stop a deck the room is hearing.</summary>
public enum OnAirPause
{
    /// <summary>A tap pauses or fades, like any other deck.</summary>
    Tap,
    /// <summary>
    /// Tap twice (or press and hold), so a stray touch can't stop the music. Works with trackpad
    /// tap-to-click, where holding is awkward. The default since 0.9.1: the first tap says "Tap
    /// again to pause" (or "to fade out") and lights the key red, so nobody is left wondering why
    /// the music didn't stop (what made hold-to-pause a poor default, 2026-10-20).
    /// </summary>
    TapTwice,
}

/// <summary>What a deck's big readout shows; tapping it cycles through these.</summary>
public enum DeckReadout
{
    Remaining,
    Elapsed,
    Bpm,
}

/// <summary>Everything GZJam remembers between sessions. Saved as JSON; unknown or bad files fall back to defaults.</summary>
public sealed class AppSettings
{
    public WindowPlacement? Window { get; set; }

    /// <summary>The folder selected in the library tree, as root path + relative folder.</summary>
    public string? SelectedRootPath { get; set; }
    public string? SelectedFolder { get; set; }

    /// <summary>The playlist open in the song list, if one was (instead of a folder).</summary>
    public long? OpenPlaylist { get; set; }

    /// <summary>Speakers. Matched by name and driver ID, since device numbers change between sessions.</summary>
    public AudioDevicePreference? MainDevice { get; set; }

    /// <summary>Headphones.</summary>
    public AudioDevicePreference? CueDevice { get; set; }

    /// <summary>
    /// A monitor speaker at the DJ table: what the room hears, at its own volume. None by default.
    /// Like the headphones, it never falls back to another output.
    /// </summary>
    public AudioDevicePreference? BoothDevice { get; set; }

    /// <summary>
    /// Speakers, booth and headphones play mono, so the whole floor hears instruments recorded on
    /// one side, and the DJ hears what the room hears, with either ear. On by default.
    /// </summary>
    public bool Mono { get; set; } = true;

    /// <summary>
    /// Windows: the speakers' sound card is GZJam's alone while it's open, so no other program's
    /// sound (a notification, a browser) can reach the room. Only for an output chosen by name,
    /// not "Default"; the headphones stay shared. Off for new installs since 0.9.8 (#209): on a
    /// DJ's everyday computer it stopped their other music software (an editor, a player), with
    /// nothing there to say why. Settings files already hold a choice, which stays. For a laptop
    /// kept for gigs, the guide recommends it.
    /// </summary>
    public bool ExclusiveSpeakers { get; set; }

    /// <summary>
    /// Longer buffers on every output, for a computer where the sound crackles or drops out. Play
    /// and the crossfader respond a little later. Off by default.
    /// </summary>
    public bool ExtraBuffering { get; set; }

    /// <summary>
    /// What happens to a song from a server that is queued or loaded but isn't on this computer yet and
    /// wasn't asked for by name (see <see cref="RemoteFetchMode"/>). By default nothing is downloaded by
    /// itself: only songs the DJ asks for (right-click > Cache for offline play, or Play and Add to queue
    /// in the Servers window) are, and a song that isn't here can't be loaded or queued.
    /// </summary>
    public RemoteFetchMode RemoteFetch { get; set; } = RemoteFetchMode.Never;

    /// <summary>
    /// How many decks are on screen: 2, or 4 (half height, 1 and 3 on the left of the mixer, 2 and
    /// 4 on the right).
    /// </summary>
    public int DeckCount { get; set; } = 2;

    /// <summary>
    /// Finger-sized keys, rows, menus and crossfader, for a touchscreen. Off by default; touch
    /// gestures (dragging, long-press for the menu) work either way.
    /// </summary>
    public bool TouchMode { get; set; }

    /// <summary>
    /// Smaller decks, so the library and queue show more songs (a laptop's screen). Switched from
    /// the top bar, with Ctrl/Cmd+D, or the grip under the decks; DJs flip it back and forth.
    /// </summary>
    public bool CompactDecks { get; set; }

    /// <summary>
    /// The decks and mixer hidden, with one line under the top bar for what the room hears and
    /// what's next (#164): one step past <see cref="CompactDecks"/>, which they come back as.
    /// </summary>
    public bool DecksHidden { get; set; }

    /// <summary>
    /// The folder list folded down to the music folders themselves, for more playlists (#163): for
    /// a DJ who plays from the whole library or a playlist, never one album's folder.
    /// </summary>
    public bool FoldersFolded { get; set; }

    /// <summary>How tall the playlist list may grow, in pixels, dragged by the DJ (#163); null: 220.</summary>
    public double? PlaylistsHeight { get; set; }

    /// <summary>The library and queue panels' widths, as their dividers were last dragged; null: as they start.</summary>
    public double? LibraryPanelWidth { get; set; }
    public double? QueuePanelWidth { get; set; }

    /// <summary>
    /// The library or queue panel folded into a slim strip at its side, opened again by a tap on
    /// the strip: easier by touch than finding a divider at the screen's edge.
    /// </summary>
    public bool LibraryPanelFolded { get; set; }
    public bool QueuePanelFolded { get; set; }

    /// <summary>
    /// The queue given the room in the middle, the song list narrowed beside it (#178): for nights
    /// spent reordering a long queue. The same key swaps back.
    /// </summary>
    public bool QueueWide { get; set; }

    /// <summary>The song list's width while the queue is wide, as last dragged; null: as it starts.</summary>
    public double? WideSongListWidth { get; set; }

    /// <summary>
    /// Turn each track up or down toward the same loudness (measured when it's analyzed), so a
    /// quiet 1938 transfer and a loud remaster play at about the same level. On by default.
    /// </summary>
    public bool MatchLoudness { get; set; } = true;

    /// <summary>
    /// What loudness matching aims at, in LUFS: one of <see cref="LoudnessMatch.Targets"/>. Lower
    /// leaves more headroom, for a venue's mixer or PA that clips easily (#122).
    /// </summary>
    public double LoudnessTarget { get; set; } = LoudnessMatch.TargetLufs;

    /// <summary>Speaker, headphone and booth volume, 0-100.</summary>
    public double MainVolume { get; set; } = 100;
    public double CueVolume { get; set; } = 100;
    public double BoothVolume { get; set; } = 100;

    /// <summary>What the headphones hear: 0 = what's cued, 1 = what the room hears, between = both.</summary>
    public double PhonesMix { get; set; }

    /// <summary>How many seconds of the song the decks' close-up waveforms show.</summary>
    public double CloseUpSeconds { get; set; } = 8;

    public PlayingDeckLoad LoadOntoPlayingDeck { get; set; } = PlayingDeckLoad.Block;
    public OnAirPause OnAirPause { get; set; } = OnAirPause.TapTwice;
    public double FadeOutSeconds { get; set; } = 5;

    /// <summary>How far <c>~bpm:</c> searches reach either side, in percent (the tempo slider's ±8% to start with).</summary>
    public double NearBpmPercent { get; set; } = 8;

    /// <summary>Seconds of silence auto-play leaves between songs.</summary>
    public double GapSeconds { get; set; } = 3;

    /// <summary>
    /// Auto-play fades each song out after this many seconds of it, for practice nights and
    /// classes (#98); 0 = off, songs play to their end.
    /// </summary>
    public double AutoFadeAfterSeconds { get; set; }

    /// <summary>
    /// Library columns the DJ has hidden, by header. Genre starts hidden: the folder tree already
    /// sorts music by genre and style, and a laptop can't fit every column. Art too: an album's
    /// songs all share one picture, so in a long list it doesn't tell them apart. Grouping and
    /// Comment too: only some DJs fill them in (#161).
    /// </summary>
    public List<string> HiddenColumns { get; set; } = ["Genre", "Art", "Grouping", "Comment"];

    /// <summary>
    /// What the queue doesn't show of each song, chosen from its menu (#131), by
    /// <see cref="Views.MainWindow.QueueColumns"/> name. Album, genre, year and when it was last
    /// played start hidden: they're for a wide queue (#178).
    /// </summary>
    public List<string> HiddenQueueColumns { get; set; } = ["Album", "Genre", "Year", "Played"];

    /// <summary>
    /// The song files' other tags shown as library columns (#119), by <see cref="Library.SongTag.Key"/>,
    /// in the order the DJ chose them. None at first: which tags a library has depends on who tagged it.
    /// </summary>
    public List<string> TagColumns { get; set; } = [];

    /// <summary>A song's info also shows the tags only other programs use (#129): MusicBrainz IDs, ReplayGain, the encoder.</summary>
    public bool ShowAllTags { get; set; }

    /// <summary>
    /// Tag edits in a song's info (#129) are written into the song files too, so other players see
    /// them. Off at first: GZJam keeps them in its library only, and the files aren't changed.
    /// </summary>
    public bool WriteTagsToFiles { get; set; }

    /// <summary>The queue's parts in the order the DJ dragged them into (#188), by name; null: as they come.</summary>
    public List<string>? QueueColumnOrder { get; set; }

    /// <summary>Widths the DJ gave the queue's text parts by dragging (#188), by name.</summary>
    public Dictionary<string, double> QueueColumnWidths { get; set; } = [];

    /// <summary>The column names over the queue (#188), from its menu › Show.</summary>
    public bool ShowQueueColumnNames { get; set; } = true;

    /// <summary>
    /// Columns added since the column chooser that start hidden, which these settings have seen
    /// (so <see cref="HiddenColumns"/> already says whether the DJ wants them). Null: settings
    /// new this session, which started with them hidden, or saved before this was kept.
    /// </summary>
    public List<string>? SeenColumns { get; set; }

    /// <summary>Columns added later that start hidden; settings saved before one was added get it hidden.</summary>
    private static readonly string[] NewHiddenColumns = ["Art", "Grouping", "Comment"];

    /// <summary>
    /// At startup, ask GitHub whether there's a newer GZJam, and say so (see <see cref="UpdateCheck"/>).
    /// On by default; off, GZJam never contacts the internet by itself.
    /// </summary>
    public bool CheckForNewVersion { get; set; } = true;

    /// <summary>The newer version the DJ dismissed ("0.8.0"); not mentioned again, only a later one.</summary>
    public string? DismissedVersion { get; set; }

    /// <summary>When the DJ last backed up the library (Settings › Library), for saying so there.</summary>
    public DateTime? LastBackupUtc { get; set; }

    /// <summary>Each deck's readout mode, by deck number ("1", "2").</summary>
    public Dictionary<string, DeckReadout> DeckReadouts { get; set; } = [];

    public sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return Migrate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Ignoring unreadable settings: {ex.Message}");
        }
        return new();
    }

    /// <summary>Upgrades settings saved by older versions.</summary>
    internal static AppSettings Migrate(AppSettings settings)
    {
        // Decks were named A/B before they became 1/2.
        foreach (var (old, renamed) in new[] { ("A", "1"), ("B", "2") })
        {
            if (settings.DeckReadouts.Remove(old, out DeckReadout readout))
                settings.DeckReadouts.TryAdd(renamed, readout);
        }
        // A hand-edited target that isn't one of the choices: the nearest one.
        settings.LoudnessTarget = LoudnessMatch.Targets.MinBy(t => Math.Abs(t - settings.LoudnessTarget));
        settings.SeenColumns ??= [];
        foreach (string column in NewHiddenColumns.Except(settings.SeenColumns).ToList())
        {
            if (!settings.HiddenColumns.Contains(column))
                settings.HiddenColumns.Add(column);
            settings.SeenColumns.Add(column);
        }
        return settings;
    }

    /// <summary>These settings as they'd be saved, for a backup.</summary>
    public string ToJson()
    {
        SeenColumns ??= [.. NewHiddenColumns];
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public void Save(string path)
    {
        try
        {
            SeenColumns ??= [.. NewHiddenColumns];   // new settings: they started hidden
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write then swap, so a crash mid-save never leaves a truncated file.
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}
