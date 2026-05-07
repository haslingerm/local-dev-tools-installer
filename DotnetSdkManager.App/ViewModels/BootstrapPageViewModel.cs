using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotnetSdkManager.Core.Install;
using DotnetSdkManager.Core.Platform;
using DotnetSdkManager.Core.State;

namespace DotnetSdkManager.App.ViewModels;

public partial class BootstrapPageViewModel : ViewModelBase
{
    private readonly IPlatformIntegration _platform;
    private readonly StateManager _stateManager;
    private readonly StubManager _stubManager;

    public Action? OnBootstrapped { get; set; }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _hasFailed;

    public BootstrapPageViewModel(IPlatformIntegration platform, StateManager stateManager, StubManager stubManager)
    {
        _platform = platform;
        _stateManager = stateManager;
        _stubManager = stubManager;
    }

    public string ActiveLinkPath => _platform.ActiveLinkPath;

    [RelayCommand]
    private async Task BootstrapAsync()
    {
        IsRunning = true;
        HasFailed = false;
        StatusMessage = "Setting up...";
        try
        {
            Directory.CreateDirectory(_platform.InstallRoot);

            var stubDir = await _stubManager.EnsureStubAsync();

            await _platform.WriteEnvironmentAsync(_platform.ActiveLinkPath);
            await _platform.CreateOrUpdateLinkAsync(stubDir);

            var state = _stateManager.Load();
            state.Bootstrapped = true;
            _stateManager.Save(state);

            StatusMessage = "Done! PATH has been updated.";
            await Task.Delay(800);
            OnBootstrapped?.Invoke();
        }
        catch (Exception ex)
        {
            HasFailed = true;
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}
