using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DevToolsManager.App.Views;

public partial class HomeTabView : UserControl
{
    public HomeTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
