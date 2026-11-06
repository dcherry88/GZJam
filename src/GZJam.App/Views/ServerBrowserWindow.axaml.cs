using Avalonia.Controls;
using Avalonia.Input;
using GZJam.App.ViewModels;

namespace GZJam.App.Views;

/// <summary>
/// Searching the music servers and loading its songs. Not modal, so the decks stay a click
/// away. Enter searches, a double-click loads the song on a deck, Escape closes it.
/// </summary>
public partial class ServerBrowserWindow : Window
{
    public ServerBrowserWindow()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is ServerBrowserViewModel vm)
            {
                vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ResultsList.DoubleTapped += (_, _) =>
        {
            if (DataContext is ServerBrowserViewModel { Selected: not null } vm)
                vm.LoadOnDeckCommand.Execute(null);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Opened += (_, _) => QueryBox.Focus();
    }

    public ServerBrowserWindow(ServerBrowserViewModel vm) : this() => DataContext = vm;
}
