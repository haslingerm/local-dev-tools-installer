using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotnetSdkManager.App.ViewModels;

public partial class ReleaseChannelViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "";

    public string ChannelVersion { get; }
    public string LatestSdk { get; }
    public string SupportPhase { get; }
    public string? EolDate { get; }
    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = [];

    public string ReleasesJsonUrl { get; }
    public bool IsSynthetic { get; }

    private readonly string? _customHeader;

    public ReleaseChannelViewModel(
        string channelVersion,
        string latestSdk,
        string supportPhase,
        string? eolDate,
        string releasesJsonUrl)
    {
        ChannelVersion = channelVersion;
        LatestSdk = latestSdk;
        SupportPhase = supportPhase;
        EolDate = eolDate;
        ReleasesJsonUrl = releasesJsonUrl;
    }

    private ReleaseChannelViewModel(string header)
    {
        ChannelVersion = "sideloaded";
        LatestSdk = "";
        SupportPhase = "Local";
        EolDate = null;
        ReleasesJsonUrl = "";
        IsSynthetic = true;
        _customHeader = header;
        _isExpanded = true;
    }

    public string Header => _customHeader ?? $".NET {ChannelVersion}  —  Latest SDK: {LatestSdk}  [{SupportPhase}]";

    public static ReleaseChannelViewModel CreateSideloadChannel() =>
        new("Sideloaded files (Unverified)");
}
