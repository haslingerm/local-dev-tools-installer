using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevToolsManager.Core.Catalog;
using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Install;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.State;

namespace DevToolsManager.App.ViewModels;

public sealed class DotnetTabViewModel : ProductTabViewModel
{
    private readonly ReleasesCatalogClient _catalog;
    private readonly SdkInstaller _installer;
    private readonly SdkUninstaller _switcher;
    private readonly SdkDiscovery _discovery;
    private readonly BootstrapManager _bootstrap;
    private readonly StateManager _stateManager;
    private readonly CatalogPageViewModel _allVersions;

    private SdkRelease? _latestRelease;

    public DotnetTabViewModel(
        ReleasesCatalogClient catalog,
        SdkInstaller installer,
        SdkUninstaller switcher,
        SdkDiscovery discovery,
        BootstrapManager bootstrap,
        StateManager stateManager,
        CatalogPageViewModel allVersions)
    {
        _catalog = catalog;
        _installer = installer;
        _switcher = switcher;
        _discovery = discovery;
        _bootstrap = bootstrap;
        _stateManager = stateManager;
        _allVersions = allVersions;
    }

    public override string DisplayName => ".NET SDK";
    public override string Tagline => "Microsoft's .NET runtime and SDK.";
    public override bool SupportsOpen => false;
    public override ViewModelBase? AllVersionsBrowser => _allVersions;

    protected override async Task OnAllVersionsExpandedAsync()
    {
        if (!_allVersions.HasLoaded && !_allVersions.LoadCatalogCommand.IsRunning)
        {
            await _allVersions.LoadCatalogCommand.ExecuteAsync(null);
        }
    }

    protected override async Task LoadStateAsync(CancellationToken ct)
    {
        var entries = await _catalog.GetIndexAsync(ct);
        var latestChannel = entries
            .OrderByDescending(e => ParseChannel(e.ChannelVersion))
            .FirstOrDefault(e => string.Equals(e.SupportPhase, "active", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e.SupportPhase, "preview", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e.SupportPhase, "go-live", StringComparison.OrdinalIgnoreCase))
            ?? entries.OrderByDescending(e => ParseChannel(e.ChannelVersion)).FirstOrDefault();

        if (latestChannel is null)
        {
            Status = ProductInstallStatus.CatalogUnavailable;
            ErrorMessage = "No .NET release channels found.";
            return;
        }

        IReadOnlyList<SdkRelease> releases;
        try
        {
            releases = await _catalog.GetReleasesForChannelAsync(latestChannel, ct);
        }
        catch (Exception ex)
        {
            Status = ProductInstallStatus.CatalogUnavailable;
            ErrorMessage = ex.Message;
            return;
        }

        _latestRelease = releases
            .OrderByDescending(r => ParseVersion(r.Version))
            .FirstOrDefault();

        if (_latestRelease is null)
        {
            Status = ProductInstallStatus.CatalogUnavailable;
            ErrorMessage = "No .NET SDK download available for this platform.";
            return;
        }

        LatestVersion = _latestRelease.Version;
        LatestSizeBytes = _latestRelease.Size;

        var state = _stateManager.Load();
        var managed = _discovery.Scan(state.ActiveVersion)
            .Where(s => s.Source == SdkSource.Managed)
            .ToList();

        var installed = managed
            .OrderByDescending(s => ParseVersion(s.Version))
            .FirstOrDefault();

        if (installed is null)
        {
            InstalledVersion = null;
            Status = ProductInstallStatus.NotInstalled;
        }
        else
        {
            InstalledVersion = installed.Version;
            Status = string.Equals(installed.Version, _latestRelease.Version, StringComparison.OrdinalIgnoreCase)
                ? ProductInstallStatus.UpToDate
                : ProductInstallStatus.NeedsUpdate;
        }
    }

    protected override async Task PerformInstallAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (_latestRelease is null)
        {
            throw new InvalidOperationException("Latest release not loaded.");
        }

        // Plan §5.3 — bootstrap is invisible; runs on first .NET install.
        await _bootstrap.EnsureAsync(ct);

        await _installer.InstallAsync(_latestRelease, progress, ct);

        // Make the new SDK active so Rider / shell see it as default.
        var installed = _discovery.Scan(activeVersion: null)
            .FirstOrDefault(s => s.Source == SdkSource.Managed
                                 && string.Equals(s.Version, _latestRelease.Version, StringComparison.OrdinalIgnoreCase));
        if (installed is not null)
        {
            await _switcher.SwitchDefaultAsync(installed, ct);
        }
    }

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v.Split('-')[0], out var parsed);
        return parsed ?? new Version(0, 0);
    }

    private static Version ParseChannel(string v)
    {
        Version.TryParse(v, out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
