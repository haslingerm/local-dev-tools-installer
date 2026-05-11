using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DevToolsManager.App.Views;

public partial class ProductTabView : UserControl
{
    public ProductTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
