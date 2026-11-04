using Avalonia.Controls;
using Avalonia.Interactivity;
using GZJam.App.ViewModels;

namespace GZJam.App.Views;

/// <summary>
/// The download queue: songs coming down from the Navidrome server, and how the last ones went.
/// Not modal, so the decks stay a click away while a playlist comes down. Escape closes it.
/// </summary>
public partial class DownloadsWindow : Window
{
    public DownloadsWindow() => InitializeComponent();

    public DownloadsWindow(DownloadsViewModel vm) : this() => DataContext = vm;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
