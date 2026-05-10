using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Install;
using DevToolsManager.Core.Models;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Process;
using DevToolsManager.Core.State;

namespace DevToolsManager.App.ViewModels;

public partial class SdkListPageViewModel : ViewModelBase
{
    private readonly SdkDiscovery _discovery;
    private readonly SdkUninstaller _uninstaller;
    private readonly SdkSmokeTest _smokeTest;
    private readonly IPlatformIntegration _platform;
    private readonly StateManager _stateManager;

    [ObservableProperty]
    private ObservableCollection<SdkItemViewModel> _sdks = [];

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isEmpty;

    public SdkListPageViewModel(
        SdkDiscovery discovery,
        SdkUninstaller uninstaller,
        IProcessRunner runner,
        IPlatformIntegration platform,
        StateManager stateManager)
    {
        _discovery = discovery;
        _uninstaller = uninstaller;
        _smokeTest = new SdkSmokeTest(runner);
        _platform = platform;
        _stateManager = stateManager;
    }

    public void Refresh()
    {
        var state = _stateManager.Load();
        var list = _discovery.Scan(state.ActiveVersion);
        Sdks = new ObservableCollection<SdkItemViewModel>(
            list.Select(s => new SdkItemViewModel(s)));
        IsEmpty = Sdks.Count == 0;
        StatusMessage = IsEmpty ? "No .NET SDKs found." : "";
    }

    [RelayCommand]
    private async Task SetDefaultAsync(SdkItemViewModel item)
    {
        StatusMessage = $"Switching default to {item.Version}...";
        try
        {
            await _uninstaller.SwitchDefaultAsync(item.Sdk);
            var (ok, output) = await _smokeTest.TestDefaultSwitchAsync(_platform, item.Version);
            StatusMessage = ok
                ? $"Default switched to {item.Version}."
                : $"Switched, but shell verification failed: {output}";
            Refresh();
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(SdkItemViewModel item)
    {
        StatusMessage = $"Uninstalling {item.Version}...";
        try
        {
            var allSdks = Sdks.Select(s => s.Sdk).ToList();
            var fallback = await _uninstaller.UninstallAsync(item.Sdk, allSdks);
            StatusMessage = fallback is not null
                ? $"Uninstalled {item.Version}. {fallback}"
                : $"Uninstalled {item.Version}.";
            Refresh();
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }
}
