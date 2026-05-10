using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevToolsManager.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _currentPage = null!;

    private readonly SdkListPageViewModel _sdkListPage;
    private readonly CatalogPageViewModel _catalogPage;
    private readonly BootstrapPageViewModel? _bootstrapPage;

    public MainWindowViewModel(
        SdkListPageViewModel sdkListPage,
        CatalogPageViewModel catalogPage,
        BootstrapPageViewModel? bootstrapPage)
    {
        _sdkListPage = sdkListPage;
        _catalogPage = catalogPage;
        _bootstrapPage = bootstrapPage;

        if (bootstrapPage is not null)
        {
            CurrentPage = bootstrapPage;
        }
        else
        {
            ShowSdkList();
        }
    }

    [RelayCommand]
    private void ShowSdkList()
    {
        _sdkListPage.Refresh();
        CurrentPage = _sdkListPage;
    }

    [RelayCommand]
    private async Task ShowCatalogAsync()
    {
        CurrentPage = _catalogPage;
        if (!_catalogPage.LoadCatalogCommand.IsRunning)
        {
            await _catalogPage.LoadCatalogCommand.ExecuteAsync(null);
        }
    }

    public bool HasBootstrap => _bootstrapPage is not null;
}
