using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DevToolsManager.App.Views;

public partial class CleanupTabView : UserControl
{
    public CleanupTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
