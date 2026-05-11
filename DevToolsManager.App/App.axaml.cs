using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DevToolsManager.App.ViewModels;
using DevToolsManager.App.Views;
using DevToolsManager.Core.Catalog;
using DevToolsManager.Core.Catalog.JetBrains;
using DevToolsManager.Core.Discovery;
using DevToolsManager.Core.Install;
using DevToolsManager.Core.Platform;
using DevToolsManager.Core.Process;
using DevToolsManager.Core.Sideload;
using DevToolsManager.Core.State;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;

namespace DevToolsManager.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = CreateMainViewModel(services);
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindowViewModel CreateMainViewModel(IServiceProvider services)
    {
        var platform = services.GetRequiredService<IPlatformIntegration>();
        var stateManager = services.GetRequiredService<StateManager>();
        var state = stateManager.Load();

        // Reconcile saved Bootstrapped flag with the live environment. If the
        // user edited their shell rc files or registry, IsBootstrapped() returns
        // false even when state.Bootstrapped was true. Trust the live check.
        var liveBootstrapped = platform.IsBootstrapped();
        if (state.Bootstrapped != liveBootstrapped)
        {
            state.Bootstrapped = liveBootstrapped;
            stateManager.Save(state);
        }

        return services.GetRequiredService<MainWindowViewModel>();
    }

    private static IServiceProvider BuildServices()
    {
        var sc = new ServiceCollection();

        sc.AddSingleton<HttpClient>(_ => new HttpClient
        {
            DefaultRequestHeaders = { { "User-Agent", "DevToolsManager/1.0" } }
        });
        sc.AddSingleton<IProcessRunner, CliWrapProcessRunner>();

#pragma warning disable CA1416
        if (OperatingSystem.IsWindows())
        {
            sc.AddSingleton<IPlatformIntegration>(p =>
                new WindowsPlatformIntegration(p.GetRequiredService<IProcessRunner>()));
        }
        else if (OperatingSystem.IsLinux())
        {
            sc.AddSingleton<IPlatformIntegration>(p =>
                new LinuxPlatformIntegration(p.GetRequiredService<IProcessRunner>()));
        }
        else
        {
            throw new PlatformNotSupportedException(
                "DevToolsManager currently supports only Windows and Linux.");
        }
#pragma warning restore CA1416

        sc.AddSingleton<StateManager>();
        sc.AddSingleton<SdkDiscovery>();
        sc.AddSingleton<IdeDiscovery>();
        sc.AddSingleton<ReleasesCatalogClient>();
        sc.AddSingleton<JetBrainsCatalogClient>();
        sc.AddSingleton<ProductInstaller>();
        sc.AddSingleton<SdkInstaller>();
        sc.AddSingleton<IdeInstaller>();
        sc.AddSingleton<SideloadScanner>();
        sc.AddSingleton<IdeSideloadScanner>();
        sc.AddSingleton<StubManager>();
        sc.AddSingleton<BootstrapManager>();
        sc.AddSingleton(p => new SdkUninstaller(
            p.GetRequiredService<IPlatformIntegration>(),
            p.GetRequiredService<IProcessRunner>(),
            p.GetRequiredService<SdkDiscovery>(),
            p.GetRequiredService<StateManager>(),
            p.GetRequiredService<StubManager>()));
        sc.AddSingleton<IdeUninstaller>();

        sc.AddTransient<CatalogPageViewModel>();
        sc.AddTransient<HomeTabViewModel>();
        sc.AddTransient<DotnetTabViewModel>();
        sc.AddTransient<RiderTabViewModel>();
        sc.AddTransient<WebStormTabViewModel>();
        sc.AddTransient<CleanupTabViewModel>(p => new CleanupTabViewModel(
            p.GetRequiredService<SdkDiscovery>(),
            p.GetRequiredService<IdeDiscovery>(),
            p.GetRequiredService<SdkUninstaller>(),
            p.GetRequiredService<IdeUninstaller>(),
            p.GetRequiredService<StateManager>(),
            onSdkChanged: () => { /* Cleanup tab self-refreshes */ }));
        sc.AddTransient<MainWindowViewModel>();

        return sc.BuildServiceProvider();
    }
}
