using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GZJam.Library;

namespace GZJam.App.ViewModels;

/// <summary>A folder in the library tree.</summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly LibraryViewModel _owner;

    public FolderNodeViewModel(LibraryViewModel owner, FolderNode node, FolderNodeViewModel? parent)
    {
        _owner = owner;
        Node = node;
        Parent = parent;
        Children = node.Children.Select(c => new FolderNodeViewModel(owner, c, this)).ToList();
    }

    public FolderNode Node { get; }
    public FolderNodeViewModel? Parent { get; }
    public IReadOnlyList<FolderNodeViewModel> Children { get; }

    public string Name => Node.Name;
    public string CountText => Format.Count(Node.TrackCount);
    public bool IsRoot => Node.IsRoot;

    /// <summary>Songs on a server (Navidrome), not in a folder on this computer: nothing to rescan or open.</summary>
    public bool IsRemote => Node.FullPath.Contains("://", StringComparison.Ordinal);

    public bool IsLocal => !IsRemote;

    /// <summary>The server's own folder offers a sync, in place of a rescan.</summary>
    public bool ShowsSync => IsRemote && IsRoot;

    /// <summary>A music folder can be forgotten here; the server's songs go with Settings › Navidrome.</summary>
    public bool CanRemoveFromLibrary => IsRoot && IsLocal;
    public (long RootId, string Folder) Key => (Node.RootId, Node.RelativePath);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _owner.FolderExpanded();
    }

    /// <summary>"Jazz &amp; Swing › Classic Swing", including the root's name.</summary>
    public string DisplayPath => Parent is null ? Name : $"{Parent.DisplayPath} › {Name}";

    public IEnumerable<FolderNodeViewModel> SelfAndDescendants() =>
        Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    [RelayCommand]
    private void Rescan() => _owner.RescanRoot(Node.RootId);

    /// <summary>Every song's tags again, for tags edited by a program that keeps the file's date.</summary>
    [RelayCommand]
    private void ReadAllTagsAgain() => _owner.RescanRoot(Node.RootId, readAllTags: true);

    [RelayCommand]
    private async Task SyncWithServerAsync()
    {
        if (_owner.SyncRemote is { } sync)
            await sync();
    }

    /// <summary>Fetches every song under this folder from the server, so they play with no network.</summary>
    [RelayCommand]
    private void CacheForOffline() => _owner.CacheFolder?.Invoke(Node.RootId, Node.RelativePath, Name);

    [RelayCommand]
    private void RemoveFromLibrary() => _owner.RemoveRoot(Node.RootId);

    [RelayCommand]
    private void OpenInFileManager() => FileManager.Open(Node.FullPath);
}
