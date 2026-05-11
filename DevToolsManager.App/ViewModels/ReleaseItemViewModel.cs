using CommunityToolkit.Mvvm.ComponentModel;
using DevToolsManager.Core.Models;

namespace DevToolsManager.App.ViewModels;

public partial class ReleaseItemViewModel : ViewModelBase
{
    public SdkRelease Release { get; }

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

    [ObservableProperty]
    private bool _hasUnverifiedConfirmation;

    public ReleaseItemViewModel(SdkRelease release)
    {
        Release = release;
        _isInstalled = release.IsInstalled;
    }

    public string Version => Release.Version;
    public string ChannelVersion => Release.ChannelVersion;
    public long Size => Release.Size;
    public string SizeLabel => Release.Size > 0 ? $"{Release.Size / 1_048_576.0:F0} MB" : "";
    public bool HasSideload => Release.HasSideload;
    public bool IsVerified => Release.IsHashVerified;
    public bool RequiresConfirmation => HasSideload && !IsVerified;

    public string ButtonLabel
    {
        get
        {
            if (HasUnverifiedConfirmation)
            {
                return "Confirm install (unverified)";
            }
            return HasSideload ? "Install (no download)" : "Download & Install";
        }
    }

    public string BadgeText => HasSideload
        ? (IsVerified ? "Local file" : "Local file (unverified)")
        : "";

    public string UnverifiedWarning =>
        "This local SDK archive is not verified against Microsoft release metadata.\n" +
        "Installing it will execute its dotnet binary during validation.";

    partial void OnHasUnverifiedConfirmationChanged(bool value) =>
        OnPropertyChanged(nameof(ButtonLabel));
}
