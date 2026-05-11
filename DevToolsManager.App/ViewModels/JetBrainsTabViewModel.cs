using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevToolsManager.Core.Catalog.JetBrains;
using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Install;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Sideload;
using DevToolsManager.Core.State;

namespace DevToolsManager.App.ViewModels;

/// <summary>
/// Common shape for the JetBrains product tabs — Rider, WebStorm. Concrete
/// subclasses only supply <see cref="JetBrainsTabViewModel.Product"/>.
/// </summary>
public abstract class JetBrainsTabViewModel : ProductTabViewModel
{
    private readonly JetBrainsCatalogClient _catalog;
    private readonly IdeInstaller _installer;
    private readonly IdeDiscovery _discovery;
    private readonly IdeSideloadScanner _sideloadScanner;
    private readonly IPlatformIntegration _platform;
    private readonly StateManager _stateManager;
    private readonly IdeCatalogBrowserViewModel _allVersions;

    private IdeRelease? _latestRelease;

    protected JetBrainsTabViewModel(
        JetBrainsCatalogClient catalog,
        IdeInstaller installer,
        IdeDiscovery discovery,
        IdeSideloadScanner sideloadScanner,
        IPlatformIntegration platform,
        StateManager stateManager)
    {
        _catalog = catalog;
        _installer = installer;
        _discovery = discovery;
        _sideloadScanner = sideloadScanner;
        _platform = platform;
        _stateManager = stateManager;
        _allVersions = new IdeCatalogBrowserViewModel(
            catalog, installer, discovery, sideloadScanner, Product,
            onInstalled: () => _ = RefreshAsync(CancellationToken.None));
    }

    protected abstract JetBrainsProduct Product { get; }

    public override string DisplayName => JetBrainsProductInfo.DisplayName(Product);
    public override string Tagline => JetBrainsProductInfo.Comment(Product);
    public override bool SupportsOpen => true;
    public override ViewModelBase? AllVersionsBrowser => _allVersions;

    protected override async Task LoadStateAsync(CancellationToken ct)
    {
        var releases = await _catalog.GetReleasesAsync(Product, latestOnly: true, ct);
        _latestRelease = releases.FirstOrDefault();

        if (_latestRelease is null)
        {
            Status = ProductInstallStatus.CatalogUnavailable;
            ErrorMessage = $"No {DisplayName} download available for this platform.";
            return;
        }

        LatestVersion = _latestRelease.Version;
        LatestSizeBytes = _latestRelease.Size;

        var state = _stateManager.Load();
        var code = JetBrainsProductInfo.Code(Product);
        state.ActiveIdes.TryGetValue(code, out var active);

        var installed = _discovery.Scan(Product, active);
        var current = installed
            .OrderByDescending(i => i.IsActive)
            .ThenByDescending(i => ParseVersion(i.Version))
            .FirstOrDefault();

        if (current is null)
        {
            InstalledVersion = null;
            Status = ProductInstallStatus.NotInstalled;
        }
        else
        {
            InstalledVersion = current.Version;
            Status = string.Equals(current.Version, _latestRelease.Version, StringComparison.OrdinalIgnoreCase)
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
        await _installer.InstallAsync(_latestRelease, progress, ct);
    }

    protected override Task LaunchInstalledAsync()
    {
        var activeLauncher = Path.Combine(
            _platform.IdeInstallRoot,
            JetBrainsProductInfo.Slug(Product),
            "active",
            JetBrainsProductInfo.LauncherForCurrentOs(Product));

        if (!File.Exists(activeLauncher))
        {
            throw new FileNotFoundException($"Launcher not found: {activeLauncher}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = activeLauncher,
            UseShellExecute = true,
        };
        Process.Start(psi);
        return Task.CompletedTask;
    }

    protected override async Task OnAllVersionsExpandedAsync()
    {
        if (!_allVersions.HasLoaded && !_allVersions.IsLoading)
        {
            await _allVersions.LoadCommand.ExecuteAsync(null);
        }
    }

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v, out var parsed);
        return parsed ?? new Version(0, 0);
    }
}

public sealed class RiderTabViewModel : JetBrainsTabViewModel
{
    public RiderTabViewModel(
        JetBrainsCatalogClient catalog,
        IdeInstaller installer,
        IdeDiscovery discovery,
        IdeSideloadScanner sideloadScanner,
        IPlatformIntegration platform,
        StateManager stateManager)
        : base(catalog, installer, discovery, sideloadScanner, platform, stateManager) { }

    protected override JetBrainsProduct Product => JetBrainsProduct.Rider;
}

public sealed class WebStormTabViewModel : JetBrainsTabViewModel
{
    public WebStormTabViewModel(
        JetBrainsCatalogClient catalog,
        IdeInstaller installer,
        IdeDiscovery discovery,
        IdeSideloadScanner sideloadScanner,
        IPlatformIntegration platform,
        StateManager stateManager)
        : base(catalog, installer, discovery, sideloadScanner, platform, stateManager) { }

    protected override JetBrainsProduct Product => JetBrainsProduct.WebStorm;
}
