using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DevToolsManager.App.ViewModels;

public enum CleanupItemKind { Sdk, Ide }

public partial class CleanupItemViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    [NotifyPropertyChangedFor(nameof(HasSize))]
    private long _sizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveLabel))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool _isConfirming;

    [ObservableProperty]
    private bool _isRemoving;

    [ObservableProperty]
    private string _statusMessage = "";

    public CleanupItemKind Kind { get; }
    public string Version { get; }
    public bool IsActive { get; }
    public string InstallPath { get; }
    public object Payload { get; }

    public CleanupItemViewModel(
        CleanupItemKind kind,
        string version,
        bool isActive,
        string installPath,
        object payload)
    {
        Kind = kind;
        Version = version;
        IsActive = isActive;
        InstallPath = installPath;
        Payload = payload;
    }

    public bool HasSize => SizeBytes > 0;

    public string SizeLabel => SizeBytes switch
    {
        <= 0 => "Computing…",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F0} KB",
        < 1024 * 1024 * 1024L => $"{SizeBytes / 1_048_576.0:F0} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB",
    };

    public bool CanRemove => !IsActive && !IsRemoving;

    public string RemoveLabel => IsConfirming ? "Confirm?" : "Remove";

    public string ActiveTooltip => IsActive ? "This is the active version." : "";
}
