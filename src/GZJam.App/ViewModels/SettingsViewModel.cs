using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Audio;

namespace GZJam.App.ViewModels;

/// <summary>A choice in a settings drop-down.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The settings flyout: audio outputs and deck safety.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>Represents "no headphone output" (and no booth).</summary>
    public static readonly AudioDevice NoDevice = new(-1, "None", "none", false);   // a driver, so it isn't taken for "Default"

    private readonly MainWindowViewModel _owner;
    private readonly AppSettings _settings;
    private bool _loading;

    public SettingsViewModel(MainWindowViewModel owner, AppSettings settings)
    {
        _owner = owner;
        _settings = settings;
        Navidrome = new NavidromeSettingsViewModel(owner);
        RefreshDevices();
    }

    /// <summary>Settings › Navidrome: the server's address and login.</summary>
    public NavidromeSettingsViewModel Navidrome { get; }

    [ObservableProperty]
    public partial IReadOnlyList<AudioDevice> MainDevices { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AudioDevice> CueDevices { get; private set; } = [];

    [ObservableProperty]
    public partial AudioDevice? MainDevice { get; set; }

    [ObservableProperty]
    public partial AudioDevice? CueDevice { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AudioDevice> BoothDevices { get; private set; } = [];

    /// <summary>The booth (monitor) output, or <see cref="NoDevice"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBooth))]
    public partial AudioDevice? BoothDevice { get; set; }

    /// <summary>A booth output is chosen, so its volume shows.</summary>
    public bool HasBooth => BoothDevice is not null && BoothDevice != NoDevice;

    /// <summary>Booth volume, 0-100, in steps of 5.</summary>
    public double BoothVolume
    {
        get => _owner.Mixer?.BoothVolume ?? _settings.BoothVolume;
        set
        {
            value = Math.Clamp(Math.Round(value / 5) * 5, 0, 100);
            if (_owner.Mixer is { } mixer)
                mixer.BoothVolume = value;
            else
                _settings.BoothVolume = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoothVolumeText));
        }
    }

    public string BoothVolumeText => _owner.Mixer?.BoothVolumeText ?? $"{BoothVolume:0}%";

    [RelayCommand]
    private void BoothQuieter() => BoothVolume -= 5;

    [RelayCommand]
    private void BoothLouder() => BoothVolume += 5;

    public IReadOnlyList<Choice<PlayingDeckLoad>> LoadChoices { get; } =
    [
        new(PlayingDeckLoad.Block, "Block it (safest)"),
        new(PlayingDeckLoad.Ask, "Ask first"),
        new(PlayingDeckLoad.Allow, "Allow it"),
    ];

    public IReadOnlyList<Choice<OnAirPause>> PauseChoices { get; } =
    [
        new(OnAirPause.Tap, "Tap"),
        new(OnAirPause.TapTwice, "Tap twice (or press and hold)"),
    ];

    public Choice<PlayingDeckLoad> LoadChoice
    {
        get => LoadChoices.First(c => c.Value == _settings.LoadOntoPlayingDeck);
        set
        {
            _settings.LoadOntoPlayingDeck = value.Value;
            OnPropertyChanged();
        }
    }

    public Choice<OnAirPause> PauseChoice
    {
        get => PauseChoices.First(c => c.Value == _settings.OnAirPause);
        set
        {
            _settings.OnAirPause = value.Value;
            OnPropertyChanged();
            _owner.RefreshDecks();
        }
    }

    /// <summary>Speakers, booth and headphones play mono.</summary>
    public bool Mono
    {
        get => _owner.Mixer?.IsMono ?? _settings.Mono;
        set
        {
            if (_owner.Mixer is { } mixer)
                mixer.IsMono = value;
            else
                _settings.Mono = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Speakers for GZJam alone (see <see cref="AppSettings.ExclusiveSpeakers"/>).</summary>
    public bool ExclusiveSpeakers
    {
        get => _settings.ExclusiveSpeakers;
        set
        {
            if (value == _settings.ExclusiveSpeakers)
                return;
            _owner.SetExclusiveSpeakers(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakersAreWindowsDefault));
        }
    }

    /// <summary>
    /// The speakers GZJam has to itself are also Windows' default output, so other programs that
    /// play there (a browser) may refuse to play while GZJam is open.
    /// </summary>
    public bool SpeakersAreWindowsDefault =>
        ShowsExclusiveSpeakers && ExclusiveSpeakers && MainDevice is { IsDefault: true, FollowsSystemDefault: false };

    /// <summary>Extra protection against crackles (see <see cref="AppSettings.ExtraBuffering"/>).</summary>
    public bool ExtraBuffering
    {
        get => _settings.ExtraBuffering;
        set
        {
            if (value == _settings.ExtraBuffering)
                return;
            _owner.SetExtraBuffering(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Exclusive output is a Windows matter: Linux already opens a chosen card directly.</summary>
    public bool ShowsExclusiveSpeakers => OperatingSystem.IsWindows();

    /// <summary>Loudness matching (see <see cref="AppSettings.MatchLoudness"/>).</summary>
    public bool MatchLoudness
    {
        get => _settings.MatchLoudness;
        set
        {
            if (value == _settings.MatchLoudness)
                return;
            _settings.MatchLoudness = value;
            Log.Info($"Loudness matching {(value ? "on" : "off")}");
            foreach (DeckViewModel deck in _owner.Decks)
                deck.LoudnessSettingChanged();
            _owner.Preview?.LoudnessSettingChanged();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice<double>> LoudnessTargetChoices { get; } = LoudnessMatch.Targets
        .Select(t => new Choice<double>(t, $"−{-t:0} LUFS" + (t == LoudnessMatch.TargetLufs ? " (usual)" : t == LoudnessMatch.Targets[^1] ? " (most headroom)" : "")))
        .ToList();

    /// <summary>What matching aims at (see <see cref="AppSettings.LoudnessTarget"/>).</summary>
    public Choice<double> LoudnessTargetChoice
    {
        get => LoudnessTargetChoices.FirstOrDefault(c => c.Value == _settings.LoudnessTarget) ?? LoudnessTargetChoices[0];
        set
        {
            if (value is null || value.Value == _settings.LoudnessTarget)
                return;
            _settings.LoudnessTarget = value.Value;
            Log.Info($"Loudness target {value.Value:0} LUFS");
            foreach (DeckViewModel deck in _owner.Decks)
                deck.LoudnessSettingChanged();
            _owner.Preview?.LoudnessSettingChanged();
            OnPropertyChanged();
        }
    }

    /// <summary>Tag edits written into the song files too (see <see cref="AppSettings.WriteTagsToFiles"/>).</summary>
    public bool WriteTagsToFiles
    {
        get => _settings.WriteTagsToFiles;
        set
        {
            if (value == _settings.WriteTagsToFiles)
                return;
            _settings.WriteTagsToFiles = value;
            Log.Info($"Tag edits written to the song files {(value ? "too" : "no more: kept in the library only")}");
            OnPropertyChanged();
        }
    }

    /// <summary>Saying when a new version is out (see <see cref="AppSettings.CheckForNewVersion"/>).</summary>
    public bool CheckForNewVersion
    {
        get => _settings.CheckForNewVersion;
        set
        {
            if (value == _settings.CheckForNewVersion)
                return;
            _settings.CheckForNewVersion = value;
            Log.Info($"New version check {(value ? "on" : "off")}");
            if (!value)
                _owner.NewVersionCheckTurnedOff();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// What Check now found: "Checking…", up to date, a newer version (with <see cref="FoundRelease"/>),
    /// or that GitHub couldn't be reached. Until it's pressed, what it does.
    /// </summary>
    [ObservableProperty]
    public partial string CheckResult { get; private set; } = "Asks GitHub straight away, even with the switch above off.";

    /// <summary>The newer release Check now found, for its link; null otherwise.</summary>
    [ObservableProperty]
    public partial Release? FoundRelease { get; private set; }

    /// <summary>
    /// Settings › About › Check now: asks GitHub straight away, even with the startup check off
    /// (the DJ asked), and says what it found here, including a version the DJ dismissed before.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CheckNowAsync()
    {
        FoundRelease = null;
        CheckResult = "Checking…";
        Log.Info("Checking for a new version (asked in Settings)");
        Release? release = await _owner.LatestRelease();
        string current = _owner.CurrentVersion;
        if (release is null)
        {
            CheckResult = "Couldn't reach GitHub. Check the internet connection, and try again later.";
        }
        else if (UpdateCheck.IsNewer(release.Version, current))
        {
            Log.Info($"GZJam {release.Version} is out (this is {current})");
            FoundRelease = release;
            CheckResult = $"GZJam {release.Version} is out.";
        }
        else
        {
            Log.Info($"GZJam {current} is up to date (newest release {release.Version})");
            CheckResult = $"GZJam {current} is the newest version.";
        }
    }

    /// <summary>Touch mode (see <see cref="AppSettings.TouchMode"/>).</summary>
    public bool TouchMode
    {
        get => _settings.TouchMode;
        set
        {
            if (value == _settings.TouchMode)
                return;
            _settings.TouchMode = value;
            Log.Info($"Touch mode {(value ? "on" : "off")}");
            OnPropertyChanged();
        }
    }

    /// <summary>Smaller decks (also switched from the top bar, the key and the grip).</summary>
    public bool CompactDecks
    {
        get => _owner.CompactDecks;
        set => _owner.CompactDecks = value;
    }

    public IReadOnlyList<Choice<DeckSize>> DeckSizeChoices { get; } =
    [
        new(DeckSize.Full, "Full"),
        new(DeckSize.Compact, "Compact"),
        new(DeckSize.Hidden, "Hidden: one line"),
    ];

    /// <summary>The decks' size, as the top bar's switch sets it too (#171).</summary>
    public Choice<DeckSize> DeckSizeChoice
    {
        get => DeckSizeChoices.First(c => c.Value == _owner.DeckSize);
        set
        {
            if (value is not null)
                _owner.DeckSize = value.Value;
        }
    }

    internal void DeckSizeChanged()
    {
        OnPropertyChanged(nameof(CompactDecks));
        OnPropertyChanged(nameof(DeckSizeChoice));
    }

    /// <summary>Four decks on screen rather than two.</summary>
    public bool FourDecks
    {
        get => _owner.FourDecks;
        set
        {
            _owner.SetDeckCount(value ? 4 : 2);
            OnPropertyChanged();   // refused (a deck is playing): the switch goes back
        }
    }

    public double FadeOutSeconds
    {
        get => _settings.FadeOutSeconds;
        set
        {
            _settings.FadeOutSeconds = Math.Clamp(Math.Round(value), 1, 30);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FadeOutText));
        }
    }

    public string FadeOutText => $"{FadeOutSeconds:0} s";

    [RelayCommand]
    private void FadeOutShorter() => FadeOutSeconds -= 1;

    [RelayCommand]
    private void FadeOutLonger() => FadeOutSeconds += 1;

    /// <summary>How far <c>~bpm:</c> searches (and the BPM button's "Near Deck") reach either side, in percent.</summary>
    public double NearBpmPercent
    {
        get => _settings.NearBpmPercent;
        set
        {
            _settings.NearBpmPercent = Math.Clamp(Math.Round(value), 1, 20);
            _owner.Library.NearBpm = _settings.NearBpmPercent / 100;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NearBpmText));
        }
    }

    public string NearBpmText => $"±{NearBpmPercent:0}%";

    [RelayCommand]
    private void NearBpmCloser() => NearBpmPercent -= 1;

    [RelayCommand]
    private void NearBpmWider() => NearBpmPercent += 1;

    /// <summary>
    /// When the library was last backed up, for Settings › Library: "Last backed up 25 October
    /// 2026." or "Not backed up yet."
    /// </summary>
    public string LastBackupText => _settings.LastBackupUtc is { } at
        ? $"Last backed up {at.ToLocalTime():d MMMM yyyy}."
        : "Not backed up yet.";

    /// <summary>The backup's file name the save dialog suggests.</summary>
    public static string SuggestedBackupName => LibraryBackup.SuggestedName(DateTime.Now);

    /// <summary>
    /// Backs the library and settings up to <paramref name="destination"/> (chosen in a save
    /// dialog), in the background, and says how it went.
    /// </summary>
    public async Task BackUpAsync(string destination)
    {
        string settings = _owner.SettingsJson();
        _owner.ShowNotice("Backing up the library…", seconds: 60);
        try
        {
            BackupInfo info = await Task.Run(() => LibraryBackup.Create(_owner.Database, settings, destination));
            _settings.LastBackupUtc = DateTime.UtcNow;
            OnPropertyChanged(nameof(LastBackupText));
            Log.Info($"Backed up the library to {destination}: {info.Songs} songs, {info.Playlists} playlists, {info.Nights} nights");
            _owner.ShowNotice($"Library backed up: {Path.GetFileName(destination)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error($"Couldn't back up the library to {destination}", ex);
            _owner.ShowNotice($"Couldn't back up the library: {ex.Message}", seconds: 8);
        }
    }

    /// <summary>
    /// Restores the backup at <paramref name="path"/> (chosen in an open dialog), after saying
    /// what's in it and asking: GZJam closes and opens again with it, the library it replaces kept
    /// aside. Not while the room hears music, nor a backup this GZJam can't read.
    /// </summary>
    public async Task RestoreAsync(string path)
    {
        if (_owner.WhatTheRoomIsHearing() is not null)
        {
            _owner.ShowNotice("Pause the music first: restoring a backup closes GZJam.");
            return;
        }
        BackupInfo info;
        try
        {
            info = await Task.Run(() => LibraryBackup.Read(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't read the backup {path}", ex);
            _owner.ShowNotice($"Couldn't restore {Path.GetFileName(path)}: {ex.Message}", seconds: 8);
            return;
        }

        string what = $"{Count(info.Songs, "song")}, {Count(info.MarkedSongs, "with marks", "with marks")}, " +
                      $"{Count(info.Playlists, "playlist")} and {Count(info.Nights, "night")} of History";
        if (_owner.ConfirmAsync is null || !await _owner.ConfirmAsync(
                "Restore this backup?",
                $"From {info.Created:d MMMM yyyy, HH:mm} (GZJam {info.GZJamVersion}): {what}. Its Settings come with it.\n\n" +
                "GZJam closes and opens again with it. Your music files aren't touched. The library it replaces " +
                $"(and anything marked since the backup) isn't deleted: it's kept in the \"{LibraryReset.PreviousLibraries}\" folder beside it.",
                "Restore"))
            return;
        try
        {
            string data = Path.GetDirectoryName(Path.GetFullPath(_owner.Database.DatabasePath))!;
            await Task.Run(() => LibraryBackup.Stage(path, data));
        }
        catch (GZJam.Library.LibraryTooNewException ex)
        {
            Log.Error($"Not restoring {path}: {ex.Message}");
            _owner.ShowNotice($"That backup is from a newer GZJam ({info.GZJamVersion}). Install the newest GZJam to restore it.", seconds: 10);
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't restore {path}", ex);
            _owner.ShowNotice($"Couldn't restore {Path.GetFileName(path)}: {ex.Message}", seconds: 8);
            return;
        }
        string dataDirectory = Path.GetDirectoryName(Path.GetFullPath(_owner.Database.DatabasePath))!;
        try
        {
            await FindMusicFoldersAsync(info, dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error($"Couldn't move the backup's music folders", ex);
            LibraryBackup.Unstage(dataDirectory);
            _owner.ShowNotice($"Couldn't restore {Path.GetFileName(path)}: {ex.Message}", seconds: 8);
            return;
        }
        Log.Info($"Restoring the backup {path} (from {info.Created:u}, GZJam {info.GZJamVersion}): GZJam opens again with it");
        _owner.RequestRestore();
    }

    /// <summary>
    /// A backup made where the music lived somewhere else (#112): for each of its music folders
    /// that isn't on this computer, asks where it is now (offering the likeliest), and moves the
    /// library there, so its songs keep everything the DJ gave them. One left as it was is
    /// simply not found, as an unplugged drive isn't.
    /// </summary>
    private async Task FindMusicFoldersAsync(BackupInfo info, string dataDirectory)
    {
        var missing = LibraryBackup.MissingMusicFolders(info);
        if (missing.Count == 0)
            return;
        var here = _owner.Database.GetRoots().Select(r => r.Path).ToList();
        var moves = new List<(string From, string To)>();
        var notes = new List<string>();
        foreach (string from in missing)
        {
            var songs = await Task.Run(() => LibraryBackup.StagedSongs(dataDirectory, from));
            string? suggested = await Task.Run(() => LibraryBackup.Suggest(from, songs, here));
            string? to = null;
            string isnt = $"The backup's music folder {from} isn't on this computer.";
            if (suggested is not null && _owner.AskAsync is not null)
            {
                int found = await Task.Run(() => LibraryBackup.CountFound(songs, from, suggested));
                if (await _owner.AskAsync("Where's this music now?",
                        $"{isnt} It looks like it's {suggested} now: {found:N0} of its {songs.Count:N0} songs are there.",
                        "Use that folder", "Choose another"))
                    to = suggested;
            }
            if (to is null && _owner.PickFolderAsync is not null && (_owner.AskAsync is null || suggested is not null ||
                    await _owner.AskAsync("Where's this music now?",
                        $"{isnt} Choose where it is now, so its {songs.Count:N0} songs keep their hearts, labels, cues, EQ and History.",
                        "Choose the folder", "Leave it")))
                to = await _owner.PickFolderAsync($"Where's {from} now?");
            if (to is null)
            {
                Log.Info($"Restore: {from} isn't on this computer, left as it was");
                continue;
            }
            int there = await Task.Run(() => LibraryBackup.CountFound(songs, from, to));
            Log.Info($"Restore: {from} is {to} on this computer ({there} of {songs.Count} songs there)");
            moves.Add((from, to));
            notes.Add($"{to} ({there:N0} of its {songs.Count:N0} songs found{(there < songs.Count ? "; GZJam looks for the rest as it scans" : "")})");
        }
        await Task.Run(() => LibraryBackup.MoveStaged(dataDirectory, moves, notes));
    }

    private static string Count(int n, string one, string? many = null) =>
        many is null ? $"{n:N0} {one}{(n == 1 ? "" : "s")}" : $"{n:N0} {(n == 1 ? one : many)}";

    /// <summary>
    /// Starts over with an empty library, after asking: GZJam closes and opens again with no music
    /// folders, the old library kept aside (see <see cref="LibraryReset"/>). Not while the room
    /// hears music.
    /// </summary>
    [RelayCommand]
    private async Task StartOver()
    {
        if (_owner.WhatTheRoomIsHearing() is not null)
        {
            _owner.ShowNotice("Pause the music first: starting over closes GZJam.");
            return;
        }
        if (_owner.ConfirmAsync is null || !await _owner.ConfirmAsync(
                "Start over with an empty library?",
                "GZJam closes and opens again with no music folders, as on its first day. Your music files aren't touched, " +
                "and your Settings stay.\n\nThe old library (marks, labels, cues, playlists and History) isn't deleted: it's kept " +
                $"in the \"{LibraryReset.PreviousLibraries}\" folder beside it.",
                "Start over"))
            return;
        Log.Info("Starting over with an empty library");
        _owner.RequestStartOver();
    }

    /// <summary>Re-lists devices, e.g. after plugging in a USB interface.</summary>
    [RelayCommand]
    public void RefreshDevices()
    {
        if (_owner.Audio is not { } audio)
            return;
        _loading = true;
        var devices = audio.GetOutputDevices();
        Log.Info("Output devices: " + string.Join("; ", devices.Select(d => $"{d.Index} \"{d.Name}\"" + (d.SystemName != d.Name ? $" (\"{d.SystemName}\")" : "") + $" [{d.Driver}]")));
        MainDevices = devices;
        int? main = _owner.Mix?.Main.Device?.Index;
        // The speakers' own output is never offered whole for the headphones: they'd be in the
        // room. Its right side is the "One output for both" switch.
        CueDevices = [NoDevice, .. devices.Where(d => d.Index != main)];
        BoothDevices = devices.Prepend(NoDevice).ToList();
        MainDevice = devices.FirstOrDefault(d => d.Index == main);
        CueDevice = SplitOutput ? NoDevice : devices.FirstOrDefault(d => d.Index == _owner.Mix?.Cue.Device?.Index) ?? NoDevice;
        BoothDevice = devices.FirstOrDefault(d => d.Index == _owner.Mix?.Booth.Device?.Index) ?? NoDevice;
        _loading = false;
        OnPropertyChanged(nameof(SplitOutput));
        OnPropertyChanged(nameof(CanSplitOutput));
    }

    /// <summary>
    /// One output for both, through a splitter cable: the room on its left side, the headphones on
    /// its right (see <see cref="MixEngine.IsSplit"/>). Off, the headphones are off until chosen.
    /// </summary>
    public bool SplitOutput
    {
        get => _owner.Mix?.IsSplit == true;
        set
        {
            if (value == SplitOutput || _owner.Mix is not { IsOffline: false } || MainDevice is null)
                return;
            _owner.SetOutputs(MainDevice, value ? MainDevice : null, ChosenBooth);
            if (value && _owner.Mix.IsSplit)
                _owner.ShowNotice("Split: the room on the left plug, your headphones on the right.");
            RefreshDevices();
        }
    }

    /// <summary>Splitting needs speakers to split.</summary>
    public bool CanSplitOutput => MainDevice is not null;

    partial void OnMainDeviceChanged(AudioDevice? value)
    {
        OnPropertyChanged(nameof(SpeakersAreWindowsDefault));
        if (_loading || value is null)
            return;
        // Split, the headphones go with the speakers. Speakers moved to the headphones' own
        // output turn the headphones off: they'd be in the room.
        AudioDevice? cue = SplitOutput ? value : ChosenHeadphones;
        if (!SplitOutput && cue?.Index == value.Index)
        {
            cue = null;
            _owner.ShowNotice("Headphones are off: they were on that output.");
        }
        _owner.SetOutputs(value, cue, ChosenBooth);
        RefreshDevices();   // the headphones' list leaves out the speakers' own output
    }

    /// <summary>
    /// The headphones are on "Default", which follows the computer's own choice of output: unplug
    /// them and the computer may send the preview somewhere else, even to the room.
    /// </summary>
    public bool HeadphonesFollowDefault => !SplitOutput && CueDevice is { FollowsSystemDefault: true };

    partial void OnCueDeviceChanged(AudioDevice? value)
    {
        OnPropertyChanged(nameof(HeadphonesFollowDefault));
        if (!_loading)
            _owner.SetOutputs(MainDevice, Chosen(value), ChosenBooth);
    }

    partial void OnBoothDeviceChanged(AudioDevice? value)
    {
        if (_loading)
            return;
        _owner.SetOutputs(MainDevice, SplitOutput ? MainDevice : ChosenHeadphones, Chosen(value));
        if (Chosen(value) is not null && _owner.Mix?.Booth.Device is null)
            RefreshDevices();   // refused (the speakers' own output, say): back to None
    }

    private static AudioDevice? Chosen(AudioDevice? device) => device == NoDevice ? null : device;

    /// <summary>
    /// The headphones as chosen, for a change to another output: while they're unplugged (so not
    /// in the list, shown as None) they stay the choice, and are still waited for.
    /// </summary>
    private AudioDevice? ChosenHeadphones => Chosen(CueDevice) ?? _owner.Mix?.WaitingFor(TableOutput.Headphones);

    /// <summary>The booth as chosen, the same way.</summary>
    private AudioDevice? ChosenBooth => Chosen(BoothDevice) ?? _owner.Mix?.WaitingFor(TableOutput.Booth);
}
