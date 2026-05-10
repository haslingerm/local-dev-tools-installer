using DevToolsManager.Core.Models;

namespace DevToolsManager.App.ViewModels;

public sealed class SdkItemViewModel : ViewModelBase
{
    public SdkInfo Sdk { get; }

    public SdkItemViewModel(SdkInfo sdk) => Sdk = sdk;

    public string Version => Sdk.Version;
    public string InstallPath => Sdk.InstallPath;
    public string SourceLabel => Sdk.Source == SdkSource.Managed ? "Managed" : "System";
    public bool IsActive => Sdk.IsActive;
    public bool IsManaged => Sdk.Source == SdkSource.Managed;
    public bool CanSetDefault => IsManaged && !IsActive;
    public bool CanUninstall => IsManaged;
}
