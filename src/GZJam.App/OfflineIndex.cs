using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GZJam.Audio;

namespace GZJam.App;

/// <summary>
/// Which songs from the server are on this computer, as the song list and queue show it (a blue
/// mark by the title). The list asks the cache directly (<see cref="RemoteSongs.IsCached"/>); this
/// only says when to ask again: <see cref="Version"/> goes up when a song finishes downloading or
/// the server changes, and the marks bound to it look again.
/// </summary>
public sealed partial class OfflineIndex : ObservableObject
{
    public static OfflineIndex Instance { get; } = new();

    private OfflineIndex()
    {
        RemoteSongs.TransferChanged += transfer =>
        {
            if (transfer.State == RemoteTransferState.Done)
                Dispatcher.UIThread.Post(Changed);
        };
        NavidromeConnection.Changed += () => Dispatcher.UIThread.Post(Changed);
    }

    [ObservableProperty]
    public partial int Version { get; private set; }

    private void Changed() => Version++;

    /// <summary>The marks look again (the folder songs are kept in has changed).</summary>
    public void Refresh() => Version++;
}
