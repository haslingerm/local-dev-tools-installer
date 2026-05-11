using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevToolsManager.Core.Models;

namespace DevToolsManager.App.ViewModels;

public enum ProductInstallStatus
{
    Loading,
    NotInstalled,
    NeedsUpdate,
    UpToDate,
    Installing,
    Failed,
    CatalogUnavailable,
}

/// <summary>
/// Shared view-model shape for the per-product tabs (.NET, Rider, WebStorm).
/// Surfaces "latest" + "currently installed" + a single big action button.
/// Concrete subclasses supply the catalog query, installer call, and (for
/// IDEs) launch logic.
/// </summary>
public abstract partial class ProductTabViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(ShowLatestCard))]
    [NotifyPropertyChangedFor(nameof(ShowInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowOpenButton))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private ProductInstallStatus _status = ProductInstallStatus.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    private string? _latestVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    private long _latestSizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private string? _installedVersion;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressMessage = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private string _toastMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllVersionsToggleLabel))]
    private bool _showAllVersions;

    public string AllVersionsToggleLabel =>
        ShowAllVersions ? "▾ Hide all versions" : "▸ Show all versions";

    public abstract string DisplayName { get; }

    /// <summary>Tagline below the header — "The cross-platform .NET IDE" etc.</summary>
    public virtual string Tagline => "";

    /// <summary>Whether the tab exposes a launch action when the product is up-to-date.</summary>
    public virtual bool SupportsOpen => false;

    /// <summary>VM hosted in the "Show all versions" expander. Concrete subclasses supply.</summary>
    public virtual ViewModelBase? AllVersionsBrowser => null;

    public bool IsLoading => Status == ProductInstallStatus.Loading;
    public bool IsInstalling => Status == ProductInstallStatus.Installing;
    public bool ShowProgress => IsInstalling;

    public bool ShowLatestCard =>
        Status != ProductInstallStatus.Loading
        && Status != ProductInstallStatus.CatalogUnavailable;

    public bool ShowInstallButton =>
        Status == ProductInstallStatus.NotInstalled
        || Status == ProductInstallStatus.NeedsUpdate
        || Status == ProductInstallStatus.Failed;

    public bool ShowOpenButton => SupportsOpen && Status == ProductInstallStatus.UpToDate;

    public string InstallButtonLabel
    {
        get
        {
            var size = LatestSizeBytes > 0 ? $" ({LatestSizeBytes / 1_048_576.0:F0} MB)" : "";
            return Status switch
            {
                ProductInstallStatus.NotInstalled => $"Install {LatestVersion}{size}",
                ProductInstallStatus.NeedsUpdate => $"Update to {LatestVersion}{size}",
                ProductInstallStatus.Failed => $"Retry install of {LatestVersion}{size}",
                _ => $"Install {LatestVersion}{size}",
            };
        }
    }

    public string StatusLabel => Status switch
    {
        ProductInstallStatus.Loading => "Loading…",
        ProductInstallStatus.NotInstalled => "Not installed",
        ProductInstallStatus.NeedsUpdate when InstalledVersion is not null
            => $"Currently installed: {InstalledVersion}",
        ProductInstallStatus.NeedsUpdate => "Older version installed",
        ProductInstallStatus.UpToDate when LatestVersion is not null
            => $"✓ {LatestVersion} — up to date",
        ProductInstallStatus.UpToDate => "✓ Up to date",
        ProductInstallStatus.Installing => ProgressMessage,
        ProductInstallStatus.Failed => "Install failed",
        ProductInstallStatus.CatalogUnavailable => "Catalog unavailable",
        _ => "",
    };

    /// <summary>
    /// Refresh the latest version and installed version. Called on tab entry.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        Status = ProductInstallStatus.Loading;
        ErrorMessage = "";
        try
        {
            await LoadStateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Status = ProductInstallStatus.CatalogUnavailable;
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task InstallLatestAsync(CancellationToken ct)
    {
        if (LatestVersion is null)
        {
            return;
        }

        Status = ProductInstallStatus.Installing;
        ErrorMessage = "";
        ToastMessage = "";
        ProgressPercent = 0;
        ProgressMessage = "Starting…";

        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressPercent = p.Percent ?? 0;
            ProgressMessage = p.Phase switch
            {
                InstallPhase.Downloading => $"Downloading… {p.Percent:F0}%",
                InstallPhase.Verifying => "Verifying…",
                InstallPhase.Extracting => "Extracting…",
                InstallPhase.SmokeTesting => "Running smoke test…",
                InstallPhase.Done => p.Message,
                InstallPhase.Failed => p.Message,
                _ => p.Message,
            };
            OnPropertyChanged(nameof(StatusLabel));
        });

        try
        {
            await PerformInstallAsync(progress, ct);
            InstalledVersion = LatestVersion;
            Status = ProductInstallStatus.UpToDate;
            ToastMessage = "Installed successfully.";
        }
        catch (OperationCanceledException)
        {
            Status = ProductInstallStatus.Failed;
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = ProductInstallStatus.Failed;
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task OpenAsync()
    {
        if (!SupportsOpen)
        {
            return;
        }
        try
        {
            await LaunchInstalledAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not launch: {ex.Message}";
        }
    }

    [RelayCommand]
    public Task ToggleAllVersionsAsync()
    {
        ShowAllVersions = !ShowAllVersions;
        return ShowAllVersions ? OnAllVersionsExpandedAsync() : Task.CompletedTask;
    }

    /// <summary>Load the latest version + installed-version state. Sets <see cref="Status"/>.</summary>
    protected abstract Task LoadStateAsync(CancellationToken ct);

    /// <summary>Perform the actual install of <see cref="LatestVersion"/>.</summary>
    protected abstract Task PerformInstallAsync(IProgress<InstallProgress> progress, CancellationToken ct);

    /// <summary>Optional. Launch the installed product. Only invoked when <see cref="SupportsOpen"/> is true.</summary>
    protected virtual Task LaunchInstalledAsync() => Task.CompletedTask;

    /// <summary>Optional hook called the first time the user expands "Show all versions".</summary>
    protected virtual Task OnAllVersionsExpandedAsync() => Task.CompletedTask;
}
