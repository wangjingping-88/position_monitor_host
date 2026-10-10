using System.Windows;
using PositionMonitor.App.ViewModels;
using PositionMonitor.Core.Protocol;
namespace PositionMonitor.App;
public partial class ControlWindow : Window
{
    public ControlWindow(MainViewModel model) { InitializeComponent(); DataContext = model; }
    public void SelectKind(ControlKind kind) => Panel.SelectKind(kind);
}
