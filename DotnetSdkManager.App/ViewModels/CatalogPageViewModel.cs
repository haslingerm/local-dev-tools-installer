using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotnetSdkManager.Core.Catalog;
using DotnetSdkManager.Core.Discovery;
using DotnetSdkManager.Core.Install;
using DotnetSdkManager.Core.Models;
using DotnetSdkManager.Core.Platform;
using DotnetSdkManager.Core.Sideload;
using DotnetSdkManager.Core.State;

namespace DotnetSdkManager.App.ViewModels;

public partial class CatalogPageViewModel : ViewModelBase
{
    private readonly ReleasesCatalogClient _catalog;
    private readonly SdkInstaller _installer;
    private readonly SdkDiscovery _discovery;
    private readonly SideloadScanner _sideloadScanner;
    private readonly IPlatformIntegration _platform;
    private readonly StateManager _stateManager;

    [ObservableProperty]
    private ObservableCollection<ReleaseChannelViewModel> _channels = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "";

    public CatalogPageViewModel(
        ReleasesCatalogClient catalog,
        SdkInstaller installer,
        SdkDiscovery discovery,
        SideloadScanner sideloadScanner,
        IPlatformIntegration platform,
        StateManager stateManager)
    {
        _catalog = catalog;
        _installer = installer;
        _discovery = discovery;
        _sideloadScanner = sideloadScanner;
        _platform = platform;
        _stateManager = stateManager;
    }

    [RelayCommand]
    private async Task LoadCatalogAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Fetching release catalog...";

        try
        {
            var sideloads = _sideloadScanner.Scan()
                .Where(s => s.Rid == _platform.CurrentRid)
                .ToList();

            IReadOnlyList<ReleasesIndexEntry> entries = [];
            string? catalogError = null;
            try
            {
                entries = await _catalog.GetIndexAsync(ct);
            }
            catch (Exception ex)
            {
                catalogError = ex.Message;
            }

            var catalogChannelKeys = new HashSet<string>(
                entries.Select(e => e.ChannelVersion),
                StringComparer.OrdinalIgnoreCase);

            var channels = new List<ReleaseChannelViewModel>();

            var unmatchedSideloads = sideloads
                .Where(s => !catalogChannelKeys.Contains(MajorMinor(s.Version)))
                .ToList();

            if (unmatchedSideloads.Count > 0)
            {
                var state = _stateManager.Load();
                var installedVersions = _discovery.Scan(state.ActiveVersion)
                    .Select(s => s.Version)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var synthetic = ReleaseChannelViewModel.CreateSideloadChannel();
                foreach (var sl in unmatchedSideloads)
                {
                    var release = new SdkRelease(
                        Version: sl.Version,
                        ChannelVersion: MajorMinor(sl.Version),
                        DownloadUrl: "",
                        Hash: "",
                        Size: 0,
                        FileName: Path.GetFileName(sl.Path))
                    {
                        IsInstalled = installedVersions.Contains(sl.Version),
                        SideloadPath = sl.Path,
                        IsHashVerified = false,
                    };
                    synthetic.Releases.Add(new ReleaseItemViewModel(release));
                }
                channels.Add(synthetic);
            }

            foreach (var e in entries)
            {
                channels.Add(new ReleaseChannelViewModel(
                    e.ChannelVersion, e.LatestSdk, e.SupportPhase, e.EolDate, e.ReleasesJsonUrl));
            }

            Channels = new ObservableCollection<ReleaseChannelViewModel>(channels);

            if (catalogError is not null && unmatchedSideloads.Count == 0)
            {
                StatusMessage = $"Failed to load catalog: {catalogError}";
            }
            else if (catalogError is not null)
            {
                StatusMessage = $"Catalog unavailable ({catalogError}). Sideloaded files still listed.";
            }
            else
            {
                StatusMessage = "";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExpandChannelAsync(ReleaseChannelViewModel channel, CancellationToken ct)
    {
        if (channel.IsExpanded)
        {
            channel.IsExpanded = false;
            return;
        }

        if (channel.Releases.Count > 0 || channel.IsSynthetic)
        {
            channel.IsExpanded = true;
            return;
        }

        channel.IsLoading = true;
        channel.IsExpanded = true;
        try
        {
            var state = _stateManager.Load();
            var installed = _discovery.Scan(state.ActiveVersion);
            var installedVersions = installed.Select(s => s.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sideloads = _sideloadScanner.Scan()
                .Where(s => s.Rid == _platform.CurrentRid)
                .ToList();
            var sideloadMap = sideloads.ToDictionary(s => s.Version, s => s.Path, StringComparer.OrdinalIgnoreCase);

            var indexEntry = new ReleasesIndexEntry
            {
                ChannelVersion = channel.ChannelVersion,
                LatestSdk = channel.LatestSdk,
                SupportPhase = channel.SupportPhase,
                EolDate = channel.EolDate,
                ReleasesJsonUrl = channel.ReleasesJsonUrl,
            };

            var releases = await _catalog.GetReleasesForChannelAsync(indexEntry, ct);
            var catalogVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var release in releases)
            {
                catalogVersions.Add(release.Version);
                sideloadMap.TryGetValue(release.Version, out var sideloadPath);
                var enriched = release with
                {
                    IsInstalled = installedVersions.Contains(release.Version),
                    SideloadPath = sideloadPath,
                };
                channel.Releases.Add(new ReleaseItemViewModel(enriched));
            }

            foreach (var sl in sideloads.Where(s =>
                MajorMinor(s.Version).Equals(channel.ChannelVersion, StringComparison.OrdinalIgnoreCase)
                && !catalogVersions.Contains(s.Version)))
            {
                var unverified = new SdkRelease(
                    Version: sl.Version,
                    ChannelVersion: channel.ChannelVersion,
                    DownloadUrl: "",
                    Hash: "",
                    Size: 0,
                    FileName: Path.GetFileName(sl.Path))
                {
                    IsInstalled = installedVersions.Contains(sl.Version),
                    SideloadPath = sl.Path,
                    IsHashVerified = false,
                };
                channel.Releases.Add(new ReleaseItemViewModel(unverified));
            }
        }
        catch (Exception ex)
        {
            channel.StatusMessage = $"Failed to load: {ex.Message}";
        }
        finally
        {
            channel.IsLoading = false;
        }
    }

    private static string MajorMinor(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    [RelayCommand]
    private async Task InstallAsync(ReleaseItemViewModel item, CancellationToken ct)
    {
        if (item.RequiresConfirmation && !item.HasUnverifiedConfirmation)
        {
            item.HasUnverifiedConfirmation = true;
            item.StatusMessage = item.UnverifiedWarning;
            return;
        }

        item.HasUnverifiedConfirmation = false;
        item.IsInstalling = true;
        item.HasFailed = false;
        item.StatusMessage = "Starting...";
        item.ProgressPercent = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            item.ProgressPercent = p.Percent ?? 0;
            item.StatusMessage = p.Phase switch
            {
                InstallPhase.Downloading => $"Downloading... {p.Percent:F0}%",
                InstallPhase.Verifying => "Verifying SHA-512...",
                InstallPhase.Extracting => "Extracting...",
                InstallPhase.SmokeTesting => "Running smoke test...",
                InstallPhase.Done => p.Message,
                InstallPhase.Failed => p.Message,
                _ => p.Message,
            };
        });

        try
        {
            await _installer.InstallAsync(item.Release, progress, ct);
            item.IsInstalled = true;
            item.StatusMessage = "Installed successfully.";
        }
        catch (Exception ex)
        {
            item.HasFailed = true;
            item.StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            item.IsInstalling = false;
        }
    }
}
