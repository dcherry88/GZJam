using GZJam.App.ViewModels;

namespace GZJam.App.Views;

/// <summary>The Navidrome window (Ctrl+Shift+N): search the server, load a song on a deck. One at a time.</summary>
public partial class MainWindow
{
    private NavidromeWindow? _navidrome;

    internal NavidromeWindow? Navidrome => _navidrome;

    /// <summary>Opens the Navidrome window, or brings the one that's open to the front.</summary>
    internal NavidromeWindow ShowNavidrome()
    {
        if (_navidrome is { } open)
        {
            open.Activate();
            return open;
        }
        var window = new NavidromeWindow(new NavidromeViewModel(Vm, NavidromeConnection.Client));
        window.Closed += (_, _) => _navidrome = null;
        _navidrome = window;
        window.Show(this);
        return window;
    }

    private DownloadsWindow? _downloads;

    internal DownloadsWindow? Downloads => _downloads;

    /// <summary>Lets the view model (the Navidrome window's button) and the top bar's status open the downloads.</summary>
    private void SetUpDownloads()
    {
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.ShowDownloadsRequested += () => ShowDownloadsWindow();
        };
        // A tap on the status in the top bar, while songs are coming down, opens the list.
        BackgroundStatusPanel.Tapped += (_, _) =>
        {
            if (Vm.Downloads.IsBusy)
                ShowDownloadsWindow();
        };
    }

    private void OnDownloadsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowDownloadsWindow();

    /// <summary>Opens the downloads list (the top bar's button, Ctrl+Shift+J), or brings the one that's open to the front.</summary>
    internal DownloadsWindow ShowDownloadsWindow()
    {
        if (_downloads is { } open)
        {
            open.Activate();
            return open;
        }
        var window = new DownloadsWindow(Vm.Downloads);
        window.Closed += (_, _) => _downloads = null;
        _downloads = window;
        window.Show(this);
        return window;
    }
}