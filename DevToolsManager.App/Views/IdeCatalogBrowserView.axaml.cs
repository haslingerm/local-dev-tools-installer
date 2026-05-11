using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DevToolsManager.App.Views;

public partial class IdeCatalogBrowserView : UserControl
{
    public IdeCatalogBrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
