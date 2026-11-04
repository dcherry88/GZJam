using System.Globalization;
using Avalonia.Data.Converters;

namespace GZJam.App;

public static class Format
{
    /// <summary>"3:05", or "1:02:05" past an hour.</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Whole BPMs as "132"; half-steps keep their decimal ("98.5").</summary>
    public static string Bpm(double? bpm) => bpm switch
    {
        null => "",
        var b when Math.Abs(b.Value - Math.Round(b.Value)) < 0.05 => b.Value.ToString("0", CultureInfo.CurrentCulture),
        var b => b.Value.ToString("0.0", CultureInfo.CurrentCulture),
    };

    public static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"640 MB", "1.2 GB": a size on disk, as a file manager says it.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB",
        _ => Math.Max(1, bytes / 1024).ToString("0", CultureInfo.CurrentCulture) + " KB",
    };

    /// <summary>"1st", "2nd", "3rd", "4th", "11th", "22nd".</summary>
    public static string Ordinal(int n) => Count(n) + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });

    /// <summary>How long ago, briefly: "Today", "3 days", "2 wk", "5 mo", "2 yr"; "" for never.</summary>
    public static string Ago(DateTime? utc, DateTime? nowUtc = null)
    {
        if (utc is not { } then)
            return "";
        DateTime now = (nowUtc ?? DateTime.UtcNow).ToLocalTime();
        DateTime local = then.ToLocalTime();
        int days = (now.Date - local.Date).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "1 day",
            < 14 => $"{days} days",
            < 60 => $"{days / 7} wk",
            < 730 => $"{days / 30} mo",
            _ => $"{days / 365} yr",
        };
    }
}

public static class Converters
{
    public static readonly IValueConverter Duration = new FuncValueConverter<TimeSpan, string>(t => Format.Duration(t));
    public static readonly IValueConverter Bpm = new FuncValueConverter<double?, string>(Format.Bpm);
    public static readonly IValueConverter Count = new FuncValueConverter<int, string>(Format.Count);
    public static readonly IValueConverter Ago = new FuncValueConverter<DateTime?, string>(t => Format.Ago(t));

    /// <summary>Played tonight: dimmed, so it isn't played again by mistake.</summary>
    public static readonly IValueConverter Dimmed = new FuncValueConverter<bool, double>(played => played ? 0.4 : 1);

    private static readonly Avalonia.Media.IBrush s_plainIcon = Avalonia.Media.Brush.Parse("#8F9BB0");
    private static readonly Avalonia.Media.IBrush s_serverIcon = Avalonia.Media.Brush.Parse("#5AA9FF");

    /// <summary>A playlist's icon: the usual gray, or blue for a copy of a server's playlist (it follows the server).</summary>
    public static readonly IValueConverter ServerIcon = new FuncValueConverter<bool, Avalonia.Media.IBrush>(remote => remote ? s_serverIcon : s_plainIcon);

    /// <summary>A song from the server that is on this computer (first value: its path; the second, <see cref="OfflineIndex.Version"/>, only says when to look again).</summary>
    public static readonly IMultiValueConverter IsOffline =
        new FuncMultiValueConverter<object?, bool>(values => values.FirstOrDefault() is string path && GZJam.Audio.RemoteSongs.IsCached(path));

    /// <summary>A deck's color by its label ("1": Deck1Brush), for what stands in for a deck (#177).</summary>
    public static readonly IValueConverter DeckAccent = new FuncValueConverter<string?, Avalonia.Media.IBrush?>(label =>
        label is not null && Avalonia.Application.Current is { } app
            && Avalonia.Controls.ResourceNodeExtensions.TryFindResource(app, $"Deck{label}Brush", out object? found) && found is Avalonia.Media.IBrush brush ? brush : null);
}
