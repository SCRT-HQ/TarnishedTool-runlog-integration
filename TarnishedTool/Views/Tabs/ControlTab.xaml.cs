//

using TarnishedTool.ViewModels;

namespace TarnishedTool.Views.Tabs;

public partial class ControlTab
{
    public ControlTab(ControlViewModel controlViewModel)
    {
        InitializeComponent();
        DataContext = controlViewModel;
    }
}
