using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevToolsManager.Core.Catalog.JetBrains;
using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Install;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Sideload;

namespace DevToolsManager.App.ViewModels;

/// <summary>
/// "Show all versions" expander content for a JetBrains product tab. Flat list
/// of releases, latest-first, with a per-row install button. Sideloaded archives
/// without a matching catalog entry are merged in at the top.
/// </summary>
public partial class IdeCatalogBrowserViewModel : ViewModelBase
{
    private readonly JetBrainsCatalogClient _catalog;
    private readonly IdeInstaller _installer;
    private readonly IdeDiscovery _discovery;
    private readonly IdeSideloadScanner _sideloadScanner;
    private readonly JetBrainsProduct _product;
    private readonly Action _onInstalled;

    [ObservableProperty]
    private ObservableCollection<IdeReleaseItemViewModel> _releases = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasLoaded;

    public IdeCatalogBrowserViewModel(
        JetBrainsCatalogClient catalog,
        IdeInstaller installer,
        IdeDiscovery discovery,
        IdeSideloadScanner sideloadScanner,
        JetBrainsProduct product,
        Action onInstalled)
    {
        _catalog = catalog;
        _installer = installer;
        _discovery = discovery;
        _sideloadScanner = sideloadScanner;
        _product = product;
        _onInstalled = onInstalled;
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = "Fetching releases…";
        try
        {
            var sideloads = _sideloadScanner.Scan()
                .Where(s => s.Product == _product)
                .ToList();

            IReadOnlyList<IdeRelease> catalog = [];
            string? catalogError = null;
            try
            {
                catalog = await _catalog.GetReleasesAsync(_product, latestOnly: false, ct);
            }
            catch (Exception ex)
            {
                catalogError = ex.Message;
            }

            var installed = _discovery.Scan(_product)
                .Select(i => i.Version)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sideloadMap = sideloads.ToDictionary(s => s.Version, s => s.Path, StringComparer.OrdinalIgnoreCase);
            var combined = new List<IdeRelease>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in catalog)
            {
                sideloadMap.TryGetValue(r.Version, out var sl);
                combined.Add(r with
                {
                    IsInstalled = installed.Contains(r.Version),
                    SideloadPath = sl,
                });
                seen.Add(r.Version);
            }

            foreach (var sl in sideloads)
            {
                if (seen.Contains(sl.Version))
                {
                    continue;
                }
                combined.Add(new IdeRelease(
                    Product: sl.Product,
                    Version: sl.Version,
                    Build: "",
                    DownloadUrl: "",
                    ChecksumUrl: "",
                    Size: 0,
                    FileName: Path.GetFileName(sl.Path))
                {
                    IsInstalled = installed.Contains(sl.Version),
                    SideloadPath = sl.Path,
                });
            }

            var ordered = combined
                .OrderByDescending(r => ParseVersion(r.Version))
                .Select(r => new IdeReleaseItemViewModel(r));

            Releases = new ObservableCollection<IdeReleaseItemViewModel>(ordered);
            HasLoaded = true;

            StatusMessage = catalogError switch
            {
                null => Releases.Count == 0 ? "No releases found." : "",
                _ when sideloads.Count > 0
                    => $"Catalog unavailable ({catalogError}). Sideloaded files still listed.",
                _ => $"Failed to load catalog: {catalogError}",
            };
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync(IdeReleaseItemViewModel item, CancellationToken ct)
    {
        item.IsInstalling = true;
        item.HasFailed = false;
        item.StatusMessage = "Starting…";
        item.ProgressPercent = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            item.ProgressPercent = p.Percent ?? 0;
            item.StatusMessage = p.Phase switch
            {
                InstallPhase.Downloading => $"Downloading… {p.Percent:F0}%",
                InstallPhase.Verifying => "Verifying SHA-256…",
                InstallPhase.Extracting => "Extracting…",
                InstallPhase.SmokeTesting => "Smoke testing…",
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
            _onInstalled();
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

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v, out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
