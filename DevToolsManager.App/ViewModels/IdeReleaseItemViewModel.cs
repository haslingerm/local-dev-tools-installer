using CommunityToolkit.Mvvm.ComponentModel;
using DevToolsManager.Core.Models;

namespace DevToolsManager.App.ViewModels;

public partial class IdeReleaseItemViewModel : ViewModelBase
{
    public IdeRelease Release { get; }

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasFailed;

    public IdeReleaseItemViewModel(IdeRelease release)
    {
        Release = release;
        _isInstalled = release.IsInstalled;
    }

    public string Version => Release.Version;
    public string Build => Release.Build;
    public long Size => Release.Size;
    public string SizeLabel => Release.Size > 0 ? $"{Release.Size / 1_048_576.0:F0} MB" : "";
    public bool HasSideload => Release.HasSideload;
    public string BadgeText => HasSideload ? "Local file" : "";
    public string ButtonLabel => HasSideload ? "Install (no download)" : "Download & Install";
}
