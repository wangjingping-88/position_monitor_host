using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using PositionMonitor.App.ViewModels;
using PositionMonitor.App.Services;

namespace PositionMonitor.App;

public partial class MainWindow : Window
{
    public MainViewModel Model { get; } = new();
    private bool _closing, _closed;
    public MainWindow()
    {
        InitializeComponent();
        Background = Brushes.Transparent;
        DataContext = Model;
        Closing += WindowClosing;
        StateChanged += (_, _) => QueueWindowFrameUpdate();
        SizeChanged += (_, _) => QueueWindowFrameUpdate();
        ContentRendered += (_, _) => QueueWindowFrameUpdate();
    }
    private bool _frameUpdatePending;
    private void QueueWindowFrameUpdate()
    {
        if (_frameUpdatePending) return;
        _frameUpdatePending = true;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            _frameUpdatePending = false;
            if (_closing) return;
            UpdateWindowFrame();
        }));
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        QueueWindowFrameUpdate();
    }
    private void UpdateWindowFrame()
    {
        if (WindowState != WindowState.Maximized) WindowContent.Margin = new Thickness(0);
        else
        {
            // 最大化窗口的不可见缩放边框位于工作区之外，按当前 DPI 将内容收回工作区。
            var scale = VisualTreeHelper.GetDpi(this);
            var dpi = (uint)Math.Round(96 * scale.DpiScaleX);
            var padding = GetSystemMetricsForDpi(92, dpi);
            var horizontal = (GetSystemMetricsForDpi(32, dpi) + padding) / scale.DpiScaleX;
            var vertical = (GetSystemMetricsForDpi(33, dpi) + padding) / scale.DpiScaleY;
            WindowContent.Margin = new Thickness(horizontal, vertical, horizontal, vertical);
        }
        WindowContent.UpdateLayout();
        if (NativeWindowFrame.CaptionSize(this, CaptionLayout) is { } caption)
        {
            NativeCaptionColumn.Width = new GridLength(caption.Width);
            NativeCaptionFill.Height = Math.Max(0, CaptionLayout.ActualHeight - caption.Height);
        }
    }
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    private async void ConnectClick(object sender, RoutedEventArgs e) => await Model.ToggleConnectionAsync();
    private void ControlClick(object sender, RoutedEventArgs e) => new ControlWindow(Model) { Owner = this }.ShowDialog();
    private void UpstreamTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DetailsTabs) && DetailsTabs.SelectedIndex == 1) Model.SelectedLog = Model.UpstreamView.Cast<LogRow>().FirstOrDefault();
    }
    private void AngleLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (AngleTrend is null || AngleReadingColumn is null || AngleTrendColumn is null) return;
        var compact = e.NewSize.Width < 600;
        AngleReadingColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(240);
        AngleTrendColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(AngleTrend, compact ? 0 : 1);
        System.Windows.Controls.Grid.SetRow(AngleTrend, compact ? 1 : 0);
        AngleTrend.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
    }
    private void GatewayDetailClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Model.SelectedGatewayLog is not { } row) return;
        var detail = new System.Windows.Controls.TextBox { Text = row.Detail, ToolTip = row.IdentifierTooltip, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, FontFamily = new("Consolas, Microsoft YaHei UI"), FontSize = 12, Margin = new(20) };
        new Window { Title = $"{Model.SelectedGatewayTitle} · {row.Name}", Owner = this, Style = (Style)FindResource("AppWindow"), Width = 640, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = detail }.Show();
    }
    private async void SettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionWindow(Model.Settings, Model.Password) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (Model.HasSession) await Model.StopSessionAsync();
        Model.ApplySettings(dialog.Settings, dialog.Password);
        Model.Notify("连接设置已保存。密码按勾选项保存到 Windows 凭据管理器。");
        if (dialog.ConnectAfterSave) await Model.ToggleConnectionAsync();
    }
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Model.LogDetail); Model.Notify("已复制报文详情。"); }
        catch (Exception error) { Model.Notify($"复制失败：{error.Message}"); }
    }
    private void GatewayCopyClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Model.GatewayLogDetail); Model.Notify("已复制网关交互报文详情。"); }
        catch (Exception error) { Model.Notify($"复制失败：{error.Message}"); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        await Model.DisposeAsync();
        _closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
