using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DevToolsManager.App.ViewModels;

namespace DevToolsManager.App;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // Walk up the inheritance chain so derived VMs without their own view
        // (e.g. DotnetTabViewModel) resolve to a base view (ProductTabView).
        var vmType = param.GetType();
        while (vmType is not null && vmType != typeof(object))
        {
            var name = vmType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            var viewType = Type.GetType(name);
            if (viewType is not null)
            {
                return (Control)Activator.CreateInstance(viewType)!;
            }
            vmType = vmType.BaseType;
        }

        return new TextBlock { Text = "Not Found: " + param.GetType().FullName };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
