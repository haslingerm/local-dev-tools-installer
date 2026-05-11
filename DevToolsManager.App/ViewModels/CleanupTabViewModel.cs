using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Install;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.State;

namespace DevToolsManager.App.ViewModels;

public sealed partial class CleanupGroupViewModel : ViewModelBase
{
    public string Header { get; }
    public ObservableCollection<CleanupItemViewModel> Items { get; } = [];

    public CleanupGroupViewModel(string header) => Header = header;

    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// Lists managed installs (SDKs + IDEs) grouped per product, with lazy size
/// computation and per-row removal. Active versions surface a dimmed button
/// with tooltip — never a one-click removal of the active.
/// </summary>
public sealed partial class CleanupTabViewModel : ViewModelBase
{
    private readonly SdkDiscovery _sdkDiscovery;
    private readonly IdeDiscovery _ideDiscovery;
    private readonly SdkUninstaller _sdkUninstaller;
    private readonly IdeUninstaller _ideUninstaller;
    private readonly StateManager _stateManager;
    private readonly Action _onSdkChanged;

    [ObservableProperty]
    private ObservableCollection<CleanupGroupViewModel> _groups = [];

    [ObservableProperty]
    private string _totalLabel = "";

    [ObservableProperty]
    private bool _isLoading;

    private CancellationTokenSource? _confirmCts;

    public CleanupTabViewModel(
        SdkDiscovery sdkDiscovery,
        IdeDiscovery ideDiscovery,
        SdkUninstaller sdkUninstaller,
        IdeUninstaller ideUninstaller,
        StateManager stateManager,
        Action onSdkChanged)
    {
        _sdkDiscovery = sdkDiscovery;
        _ideDiscovery = ideDiscovery;
        _sdkUninstaller = sdkUninstaller;
        _ideUninstaller = ideUninstaller;
        _stateManager = stateManager;
        _onSdkChanged = onSdkChanged;
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            var state = _stateManager.Load();

            var sdks = _sdkDiscovery.Scan(state.ActiveVersion)
                .Where(s => s.Source == SdkSource.Managed)
                .ToList();

            var sdkGroup = new CleanupGroupViewModel(".NET SDKs");
            foreach (var sdk in sdks.OrderByDescending(s => ParseVersion(s.Version)))
            {
                sdkGroup.Items.Add(new CleanupItemViewModel(
                    CleanupItemKind.Sdk, sdk.Version, sdk.IsActive, sdk.InstallPath, sdk));
            }

            var groups = new List<CleanupGroupViewModel> { sdkGroup };

            foreach (var product in JetBrainsProductInfo.All)
            {
                var ides = _ideDiscovery.Scan(
                    product,
                    state.ActiveIdes.TryGetValue(JetBrainsProductInfo.Code(product), out var v) ? v : null);

                var group = new CleanupGroupViewModel(JetBrainsProductInfo.DisplayName(product));
                foreach (var ide in ides.OrderByDescending(i => ParseVersion(i.Version)))
                {
                    group.Items.Add(new CleanupItemViewModel(
                        CleanupItemKind.Ide, ide.Version, ide.IsActive, ide.InstallPath, ide));
                }
                groups.Add(group);
            }

            Groups = new ObservableCollection<CleanupGroupViewModel>(groups);
            _ = Task.Run(() => ComputeSizesAsync(groups, ct), ct);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ComputeSizesAsync(IReadOnlyList<CleanupGroupViewModel> groups, CancellationToken ct)
    {
        long total = 0;
        foreach (var group in groups)
        {
            foreach (var item in group.Items)
            {
                if (ct.IsCancellationRequested) return;
                var size = await Task.Run(() => DirectorySize(item.InstallPath), ct);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => item.SizeBytes = size);
                total += size;
            }
        }
        var snapshot = total;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => TotalLabel = FormatSize(snapshot));
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* permission / race — ignore */ }
            }
            return total;
        }
        catch { return 0; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 * 1024L => $"Total managed: {bytes / 1_048_576.0:F0} MB",
        _ => $"Total managed: {bytes / 1_073_741_824.0:F2} GB",
    };

    [RelayCommand]
    private async Task RemoveAsync(CleanupItemViewModel item)
    {
        if (item.IsActive || item.IsRemoving) return;

        if (!item.IsConfirming)
        {
            item.IsConfirming = true;
            _confirmCts?.Cancel();
            _confirmCts = new CancellationTokenSource();
            var token = _confirmCts.Token;
            try
            {
                await Task.Delay(2000, token);
                if (!token.IsCancellationRequested)
                {
                    item.IsConfirming = false;
                }
            }
            catch (OperationCanceledException) { /* superseded by a second click */ }
            return;
        }

        // Second click within the window → real removal.
        _confirmCts?.Cancel();
        item.IsConfirming = false;
        item.IsRemoving = true;
        item.StatusMessage = "Removing…";

        try
        {
            if (item.Kind == CleanupItemKind.Sdk && item.Payload is SdkInfo sdk)
            {
                var all = _sdkDiscovery.Scan(activeVersion: null);
                await _sdkUninstaller.UninstallAsync(sdk, all);
                _onSdkChanged();
            }
            else if (item.Kind == CleanupItemKind.Ide && item.Payload is IdeInfo ide)
            {
                await _ideUninstaller.UninstallAsync(ide);
            }
            await RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            item.StatusMessage = $"Failed: {ex.Message}";
            item.IsRemoving = false;
        }
    }

    private static Version ParseVersion(string v)
    {
        Version.TryParse(v.Split('-')[0], out var parsed);
        return parsed ?? new Version(0, 0);
    }
}
