using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DevToolsManager.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public HomeTabViewModel HomeTab { get; }
    public ObservableCollection<ProductTabViewModel> ProductTabs { get; }
    public CleanupTabViewModel CleanupTab { get; }

    // Tab order: 0 Home, 1..N ProductTabs, N+1 Cleanup.
    private const int FirstProductTabIndex = 1;

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainWindowViewModel(
        HomeTabViewModel home,
        DotnetTabViewModel dotnet,
        RiderTabViewModel rider,
        WebStormTabViewModel webStorm,
        CleanupTabViewModel cleanup)
    {
        HomeTab = home;
        ProductTabs = [dotnet, rider, webStorm];
        CleanupTab = cleanup;

        // Kick off each tab's initial load — they all start in Loading state.
        foreach (var tab in ProductTabs)
        {
            _ = tab.RefreshCommand.ExecuteAsync(CancellationToken.None);
        }
        _ = CleanupTab.RefreshCommand.ExecuteAsync(CancellationToken.None);
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        // Re-fetch on tab entry. Plan §5.5: no auto-update notifications;
        // freshness is bounded by tab entry.
        var productIdx = value - FirstProductTabIndex;
        if (productIdx >= 0 && productIdx < ProductTabs.Count)
        {
            _ = ProductTabs[productIdx].RefreshCommand.ExecuteAsync(CancellationToken.None);
        }
        else if (value == FirstProductTabIndex + ProductTabs.Count)
        {
            _ = CleanupTab.RefreshCommand.ExecuteAsync(CancellationToken.None);
        }
        // value == 0 (Home) is a static page — nothing to refresh.
    }
}
