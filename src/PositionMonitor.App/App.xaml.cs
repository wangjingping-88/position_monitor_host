using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Text.Json;
using PositionMonitor.App.Services;
using PositionMonitor.Core.Protocol;
using PositionMonitor.Update;

namespace PositionMonitor.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            if (e.Args.Contains("--screenshot") || e.Args.Contains("--verify-mqtt"))
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "启动异常.txt"), args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
                return;
            }
            MessageBox.Show(args.Exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        MainWindow window;
        try { window = new MainWindow(); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "启动异常.txt"), error.ToString());
            Shutdown(1);
            return;
        }
        MainWindow = window;
        Window? backdrop = null;
        if (e.Args.Contains("--native-frame") && !e.Args.Contains("--maximized"))
        {
            backdrop = new Window { Width = window.Width + 80, Height = window.Height + 80, Background = Brushes.White, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            backdrop.Show(); window.Owner = backdrop; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        window.Show();
        await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
        UpdateStartupConfirmation.TryConfirmFromCommandLine(e.Args, UpdatePaths.DefaultUpdateRoot, out var confirmationError);
        if (confirmationError is not null) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "启动异常.txt"), confirmationError);
        var verify = Array.IndexOf(e.Args, "--verify-mqtt");
        if (verify >= 0 && verify + 2 < e.Args.Length)
        {
            var output = Path.GetFullPath(e.Args[verify + 2]);
            try
            {
                void VerifyStatusColor(string name, string color)
                {
                    window.UpdateLayout();
                    var text = (System.Windows.Controls.TextBlock)window.FindName(name);
                    if (text.Foreground is not SolidColorBrush brush || brush.Color != (Color)ColorConverter.ConvertFromString(color)) throw new InvalidOperationException($"状态颜色错误：{name}，{text.Text}。");
                }
                foreach (var name in new[] { "RegistrationStatusText", "SensorStatusText", "GnssStatusText", "BatteryStatusText" }) VerifyStatusColor(name, "#A87508");
                if (window.FindName("UpdateButton") is not System.Windows.Controls.Button { Content: "检查更新" } || window.Model.GetType().GetProperty("DemoCommand") is not null) throw new InvalidOperationException("检查更新入口缺失或离线演示未移除。");
                window.Model.ApplySettings(new ConnectionSettings { Host = "127.0.0.1", Port = int.Parse(e.Args[verify + 1]), Topic = "ucchip/up/sgw/1001/#\nucchip/up/sgw/1002/#\nucchip/up/sgw/704154/#" }, "");
                await window.Model.ToggleConnectionAsync();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while ((window.Model.Logs.Count(l => l.Name == "解析异常") < 2 || window.Model.GatewayCount < 1) && DateTime.UtcNow < deadline) await Task.Delay(50);
                if (window.Model.DeviceCount != 0 || window.Model.GatewayCount != 1 || window.Model.UpstreamView.Cast<PositionMonitor.App.ViewModels.LogRow>().Count() != 2) throw new InvalidOperationException("尚无设备时未显示所属网关的解析异常。");
                if (window.Model.DeviceRegistrationStatus != "请选择终端") throw new InvalidOperationException("未选设备时误显示注册状态。");
                var errorTabs = (System.Windows.Controls.TabControl)window.FindName("DetailsTabs");
                errorTabs.SelectedIndex = 1;
                window.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Capture(window, Path.ChangeExtension(output, ".异常可见.png"));
                await File.WriteAllTextAsync(Path.ChangeExtension(output, ".异常可见.json"), "{\"Success\":true}");
                errorTabs.SelectedIndex = 0;
                deadline = DateTime.UtcNow.AddSeconds(15);
                while (((window.Model.SelectedDevice?.State.History.Count ?? 0) < 2 || window.Model.DeviceCount < 4 || window.Model.GatewayCount < 3) && DateTime.UtcNow < deadline) await Task.Delay(100);
                if (window.Model.SelectedDevice?.State.Latest?.Sample.AngleX != 12.34) throw new InvalidOperationException("界面没有显示实际 MQTT 倾角。");
                if (window.Model.SelectedDevice.State.AppVersion != "V1.2.3") throw new InvalidOperationException("设备版本未更新。");
                if (window.Model.Logs.Count(l => l.Name == "解析异常") != 3 || window.Model.Gateways.Any(g => g.Id == "直接透传")) throw new InvalidOperationException("无效透传未记录异常或生成了虚假网关。");
                string InfoText(string name) => ((System.Windows.Controls.TextBlock)window.FindName(name)).Text;
                var originalGateway = window.Model.SelectedGateway;
                foreach (var name in new[] { "RegistrationStatusText", "SensorStatusText", "GnssStatusText", "BatteryStatusText" }) VerifyStatusColor(name, "#087F78");
                if (!window.Model.Gateways.Select(g => g.ListNumber).SequenceEqual(Enumerable.Range(1, window.Model.GatewayCount))) throw new InvalidOperationException("网关列表序号不是连续发现顺序。");
                window.Model.SelectedGateway = window.Model.Gateways.Single(g => g.Id == "704154");
                if (window.Model.SelectedDevice?.Route?.Source != 51877 || InfoText("AppVersionText") != "V0.1.0" || InfoText("HardwareVersionText") != "V0.1.0" || window.Model.AngleXValue is not null || window.Model.UpstreamView.Cast<PositionMonitor.App.ViewModels.LogRow>().Single().Name != "设备注册") throw new InvalidOperationException("现场数组注册未正确显示终端、版本及无效测量。");
                if (window.Model.SelectedDevice.Label != "51877" || window.Model.SelectedDevice.IdentifierTooltip != "51877（0xCAA5）" || InfoText("RegistrationStatusText") != "已注册") throw new InvalidOperationException("设备十进制地址、悬停提示或实际注册状态未显示。");
                VerifyStatusColor("RegistrationStatusText", "#087F78"); VerifyStatusColor("SensorStatusText", "#C4473A"); VerifyStatusColor("GnssStatusText", "#C4473A"); VerifyStatusColor("BatteryStatusText", "#A87508");
                Capture(window, Path.ChangeExtension(output, ".数组注册.png"));
                var informationFields = (System.Windows.Controls.Grid)window.FindName("DeviceInformationFields");
                var fieldTexts = informationFields.Children.OfType<System.Windows.Controls.TextBlock>().ToArray();
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 4; column += 2)
                    {
                        var label = fieldTexts.Single(t => System.Windows.Controls.Grid.GetRow(t) == row && System.Windows.Controls.Grid.GetColumn(t) == column);
                        var value = fieldTexts.Single(t => System.Windows.Controls.Grid.GetRow(t) == row && System.Windows.Controls.Grid.GetColumn(t) == column + 1);
                        var labelBaseline = label.TranslatePoint(new Point(0, label.BaselineOffset), informationFields).Y;
                        var valueBaseline = value.TranslatePoint(new Point(0, value.BaselineOffset), informationFields).Y;
                        if (Math.Abs(labelBaseline - valueBaseline) > .1) throw new InvalidOperationException($"设备信息标签和值的基线未对齐：{label.Text}。");
                    }
                window.Model.SelectedGateway = originalGateway;
                var informationCard = (FrameworkElement)window.FindName("DeviceInformationCard");
                if (InfoText("AppVersionText") != "V1.2.3" || InfoText("HardwareVersionText") != "V1.0.0" || !informationCard.IsVisible) throw new InvalidOperationException("注册信息没有更新到常驻设备信息。");
                if (window.Model.HeartbeatCount != 4 || window.Model.Logs.Any(l => l.Name.Contains("网关"))) throw new InvalidOperationException("实时心跳计数或网关消息分流错误。");
                if (new ConnectionSettings().Host != "117.172.29.2" || new ConnectionSettings().Port != 36106) throw new InvalidOperationException("默认连接参数错误。");
                async Task Confirm(ControlRequest request, string expected)
                {
                    await window.Model.SendControlAsync(request);
                    var operation = window.Model.Operations[0];
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (!operation.Completed && DateTime.UtcNow < until) await Task.Delay(50);
                    if (!operation.Completed || !operation.Status.Contains(expected)) throw new InvalidOperationException($"控制结果错误：{operation.Status}");
                }
                await Confirm(new("ucchip", 1001, 0x12345678, ControlKind.SetPeriod, Period: 60), "周期已设为 60 秒");
                if (InfoText("DevicePeriodText") != "60 秒") throw new InvalidOperationException("周期响应没有更新常驻设备信息。");
                await Confirm(new("ucchip", 1001, 0x12345678, ControlKind.QueryInfo), "终端已确认");
                if (InfoText("AppVersionText") != "V1.2.4" || window.Model.DeviceInformationTime == "—") throw new InvalidOperationException("设备查询响应没有自动刷新常驻版本。");
                await Confirm(new("ucchip", 1001, 0x12345678, ControlKind.Reboot), "设备忙");
                await Confirm(new("ucchip", 1001, 0, ControlKind.Wake), "网关已接受唤醒");
                // 回环服务器省略 PUBACK，但终端响应仍可能正常或迟到。
                await window.Model.SendControlAsync(new("ucchip", 1001, 0x12345678, ControlKind.QueryPeriod));
                var lateOperation = window.Model.Operations[0];
                if (lateOperation.Completed || !lateOperation.Status.Contains("服务器发布确认超时")) throw new InvalidOperationException("发布确认超时提前结束了终端响应等待。");
                Capture(window, Path.ChangeExtension(output, ".发布确认超时.png"));
                // 直接进入业务超时状态，避免联调夹具额外等待 60 秒。
                lateOperation.Timeout();
                if (!lateOperation.Completed || !lateOperation.CanMatchResponse) throw new InvalidOperationException("业务超时后未保留迟到响应匹配。");
                var lateDeadline = DateTime.UtcNow.AddSeconds(5);
                while (lateOperation.CanMatchResponse && DateTime.UtcNow < lateDeadline) await Task.Delay(50);
                if (!lateOperation.Completed || lateOperation.CanMatchResponse || lateOperation.Status != "终端确认：实际周期 60 秒") throw new InvalidOperationException($"迟到查询响应未更新执行记录：{lateOperation.Status}");
                if (window.Model.Operations.Count != 5 || window.Model.Operations.Select(o => o.Number).Distinct().Count() != 5) throw new InvalidOperationException("命令发生自动重发或序号冲突。");
                Capture(window, Path.ChangeExtension(output, ".迟到响应确认.png"));
                if (window.Model.Logs.Any(l => l.Result == "下行发送" || l.Name == "自动回执")) throw new InvalidOperationException("下行内容混入上行列表。");
                var firstGateway = window.Model.SelectedGateway!;
                if (firstGateway.Label != "网关 1001" || window.Model.SelectedDevice!.GatewayLabel != "网关 1001" || window.Model.Operations.Any(o => o.Target.Contains("0x")) || !firstGateway.IdentifierTooltip.Contains("1001（0x3E9）") || !window.Model.Operations[2].IdentifierTooltip.Contains("305419896（0x12345678）")) throw new InvalidOperationException("网关标题或执行记录目标的十进制显示及悬停格式不统一。");
                if (firstGateway.DeviceCount != 1 || window.Model.DevicesView.Cast<PositionMonitor.App.ViewModels.DeviceItem>().Count() != 1) throw new InvalidOperationException("网关 1001 的设备分组不正确。");
                window.Model.SelectedGateway = window.Model.Gateways.Single(g => g.Id == "1002");
                if (window.Model.DevicesView.Cast<PositionMonitor.App.ViewModels.DeviceItem>().Count() != 2 || window.Model.SelectedDevice?.State.Latest?.Sample.AngleX != 55.55) throw new InvalidOperationException("切换网关后同地址设备的数据未隔离。");
                if (InfoText("AppVersionText") != "V1.2.3" || InfoText("DevicePeriodText") != "—") throw new InvalidOperationException("切换网关后常驻设备信息未隔离。");
                if (!window.Model.OperationsView.IsEmpty || window.Model.DownstreamView.Cast<PositionMonitor.App.ViewModels.LogRow>().Any(l => l.GatewayKey != "ucchip/sgw/1002")) throw new InvalidOperationException("下行记录串到其他网关。");
                if (window.Model.UpstreamView.Cast<PositionMonitor.App.ViewModels.LogRow>().Any(l => l.GatewayKey != "ucchip/sgw/1002")) throw new InvalidOperationException("上行列表未随网关切换。");
                if (!window.Model.SelectedGateway.Messages.Any(l => l.Name == "终端链路状态") || !window.Model.SelectedGateway.Messages.Any(l => l.Name == "终端在线事件")) throw new InvalidOperationException("非 cmd100 的直接交互未显示。");
                var upstreamTabs = (System.Windows.Controls.TabControl)window.FindName("UpstreamMessageTabs");
                var outerTabs = (System.Windows.Controls.TabControl)window.FindName("DetailsTabs");
                outerTabs.SelectedIndex = 1; upstreamTabs.SelectedIndex = 0; window.UpdateLayout();
                var gatewayMessages = (System.Windows.Controls.DataGrid)window.FindName("GatewayMessagesGrid");
                if (upstreamTabs.Items.Count != 2 || gatewayMessages.Items.Count != window.Model.SelectedGateway.Messages.Count || !gatewayMessages.IsVisible) throw new InvalidOperationException("网关交互没有迁移到上行报文子页。");
                if (gatewayMessages.Items.Cast<PositionMonitor.App.ViewModels.LogRow>().Any(row => row.GatewayKey != "ucchip/sgw/1002" || row.Name.Contains("心跳"))) throw new InvalidOperationException("网关交互子页串入其他网关或心跳。");
                window.Model.SelectedGatewayLog = window.Model.SelectedGateway.Messages.Single(row => row.Name == "终端在线事件");
                if (!window.Model.GatewayLogDetail.Contains("305419896") || !window.Model.GatewayLogDetail.Contains("网关 1002\n") || !window.Model.SelectedGatewayLog.IdentifierTooltip.Contains("305419896（0x12345678）") || !window.Model.SelectedGatewayLog.IdentifierTooltip.Contains("1002（0x3EA）")) throw new InvalidOperationException("网关交互中的十进制 ID 或悬停提示不统一。");
                await Task.Delay(100); await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                if (gatewayMessages.Columns[0].ActualWidth < 69 || gatewayMessages.Columns[1].ActualWidth < 149) throw new InvalidOperationException("网关交互的时间或消息列被挤压。");
                static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
                {
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                        foreach (var descendant in Descendants(child)) yield return descendant;
                    }
                }
                var selectedRow = (System.Windows.Controls.DataGridRow)gatewayMessages.ItemContainerGenerator.ContainerFromItem(window.Model.SelectedGatewayLog);
                var selectedCells = Descendants(selectedRow).OfType<System.Windows.Controls.DataGridCell>().Where(cell => cell.IsSelected).ToArray();
                foreach (var cell in selectedCells)
                {
                    if (cell.Background is not SolidColorBrush brush || brush.Color != Color.FromRgb(8, 127, 120)) throw new InvalidOperationException("报文选中高亮未使用主题绿色。");
                    var text = Descendants(cell).OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
                    if (text is not null && Math.Abs(text.TranslatePoint(new Point(0, text.ActualHeight / 2), cell).Y - cell.ActualHeight / 2) > 1) throw new InvalidOperationException("高亮报文文字没有垂直居中。");
                }
                if (selectedCells.Length != 3) throw new InvalidOperationException("网关交互选中行没有完整高亮。");
                Capture(window, Path.ChangeExtension(output, ".网关交互.png"));
                upstreamTabs.SelectedIndex = 1; window.UpdateLayout();
                var terminalMessages = (System.Windows.Controls.DataGrid)window.FindName("TerminalMessagesGrid");
                if (!terminalMessages.IsVisible || terminalMessages.Items.Count != window.Model.UpstreamView.Cast<PositionMonitor.App.ViewModels.LogRow>().Count()) throw new InvalidOperationException("终端透传子页内容不正确。");
                async Task VerifyMessageLayout()
                {
                    outerTabs.SelectedIndex = 1;
                    double? tableY = null, tableHeight = null, detailY = null;
                    for (var cycle = 0; cycle < 4; cycle++)
                    {
                        upstreamTabs.SelectedIndex = cycle % 2;
                        await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                        var table = cycle % 2 == 0 ? gatewayMessages : terminalMessages;
                        var layout = (System.Windows.Controls.Grid)window.FindName(cycle % 2 == 0 ? "GatewayMessageLayout" : "TerminalMessageLayout");
                        var detail = layout.Children.OfType<System.Windows.Controls.TextBox>().Single();
                        var y = table.TranslatePoint(new Point(), window).Y;
                        var dy = detail.TranslatePoint(new Point(), window).Y;
                        if (tableY is { } ty && (Math.Abs(y - ty) > .1 || Math.Abs(table.ActualHeight - tableHeight!.Value) > .1 || Math.Abs(dy - detailY!.Value) > .1)) throw new InvalidOperationException("切换报文子页时表头或详情区上下跳动。");
                        tableY = y; tableHeight = table.ActualHeight; detailY = dy;
                        var headers = Descendants(table).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Where(header => header.Column is not null).ToArray();
                        if (headers.Any(header => Math.Abs(header.ActualHeight - 34) > .1)) throw new InvalidOperationException("报文表头高度不一致。");
                        var column = table.Columns.Last(); var previousWidth = column.Width; var previousMinWidth = column.MinWidth;
                        try
                        {
                            column.MinWidth = table.ActualWidth + 100;
                            column.Width = new System.Windows.Controls.DataGridLength(column.MinWidth);
                            await Task.Delay(100);
                            await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                            var horizontals = Descendants(table).OfType<System.Windows.Controls.Primitives.ScrollBar>().Where(bar => bar.Orientation == System.Windows.Controls.Orientation.Horizontal).ToArray();
                            if (!horizontals.Any(bar => bar.IsVisible)) throw new InvalidOperationException($"手动扩大列后横向滚动条未出现：{table.Name}，列宽 {column.ActualWidth}，表宽 {table.ActualWidth}，滚动条 {string.Join(",", horizontals.Select(bar => $"{bar.Name}:{bar.Visibility}"))}。");
                            if (Math.Abs(table.TranslatePoint(new Point(), window).Y - y) > .1 || Math.Abs(table.ActualHeight - tableHeight.Value) > .1 || Math.Abs(detail.TranslatePoint(new Point(), window).Y - dy) > .1) throw new InvalidOperationException("横向滚动条挤动了报文页面。");
                        }
                        finally { column.MinWidth = previousMinWidth; column.Width = previousWidth; await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle); }
                    }
                }
                await VerifyMessageLayout();
                if (window.Model.DevicesView.Cast<PositionMonitor.App.ViewModels.DeviceItem>().Select(d => d.ListNumber).Order().SequenceEqual(new[] { 1, 2 }) != true) throw new InvalidOperationException("当前网关下设备序号不正确。");
                var gatewayContainer = (System.Windows.Controls.ListBoxItem)((System.Windows.Controls.ListBox)window.FindName("GatewayList")).ItemContainerGenerator.ContainerFromItem(window.Model.SelectedGateway);
                var deviceContainer = (System.Windows.Controls.ListBoxItem)((System.Windows.Controls.ListBox)window.FindName("DeviceList")).ItemContainerGenerator.ContainerFromItem(window.Model.SelectedDevice);
                if (Descendants(gatewayContainer).OfType<System.Windows.Controls.TextBlock>().Single(t => t.Name == "GatewayNumberBadge").Text != window.Model.SelectedGateway!.ListNumber.ToString() || Descendants(deviceContainer).OfType<System.Windows.Controls.TextBlock>().Single(t => t.Name == "DeviceNumberBadge").Text != window.Model.SelectedDevice!.ListNumber.ToString()) throw new InvalidOperationException("序号没有显示在网关或设备卡片中。");
                foreach (var container in new[] { gatewayContainer, deviceContainer })
                {
                    var labels = Descendants(container).OfType<System.Windows.Controls.TextBlock>().ToArray();
                    if (labels.Any(t => t.Text.Contains("0x")) || !labels.Any(t => t.ToolTip?.ToString()?.Contains("0x") == true)) throw new InvalidOperationException("导航卡片仍有十六进制占位，或缺少悬停提示。");
                }
                window.Model.Search = "0x12345678";
                if (window.Model.DevicesView.Cast<PositionMonitor.App.ViewModels.DeviceItem>().Count() != 1) throw new InvalidOperationException("十六进制搜索不再兼容。");
                window.Model.Search = "";
                outerTabs.SelectedIndex = 0;
                var panel = (PositionMonitor.App.Controls.ControlPanel)window.FindName("DownlinkPanel");
                panel.SelectKind(ControlKind.QueryInfo);
                var target = panel.CurrentRequest();
                if (target.Gateway != 1002 || target.Target != 0x12345678) throw new InvalidOperationException("内嵌控制的目标没有随选择更新。");
                if (InfoText("TerminalIdentityText") != $"已记录 {window.Model.SelectedDevice!.State.Reports} 条采样" || ((System.Windows.Controls.TextBlock)panel.FindName("ControlTargetText")).Text != "网关 1002 → 终端 305419896") throw new InvalidOperationException("采样数量或下行目标未随终端选择更新。");
                window.Model.SelectedDevice = window.Model.DevicesView.Cast<PositionMonitor.App.ViewModels.DeviceItem>().Single(d => d.Route?.Source == 0xAABBCCDD);
                if (window.Model.SelectedDevice.State.History.Count != 0 || !window.Model.UpstreamView.IsEmpty || window.Model.AngleXValue is not null || panel.CurrentRequest().Target != 0xAABBCCDD) throw new InvalidOperationException("仅链路状态设备错误地继承了其他设备数据。");
                if (InfoText("AppVersionText") != "—" || InfoText("HardwareVersionText") != "—" || InfoText("DevicePeriodText") != "—") throw new InvalidOperationException("链路设备继承了上一设备的注册信息。");
                if (InfoText("RegistrationStatusText") != "待注册") throw new InvalidOperationException("仅链路消息设备误标为已注册。");
                foreach (var name in new[] { "RegistrationStatusText", "SensorStatusText", "GnssStatusText", "BatteryStatusText" }) VerifyStatusColor(name, "#A87508");
                if (window.Model.SelectedDevice.ListNumber != 2) throw new InvalidOperationException("切换设备改变了设备发现序号。");
                using (var preview = JsonDocument.Parse(panel.PreviewText.Split('\n', 2)[1]))
                    if (preview.RootElement.GetProperty("dst").GetUInt32() != 0xAABBCCDD) throw new InvalidOperationException("下行预览没有更新到所选终端地址。");
                Capture(window, Path.ChangeExtension(output, ".链路设备.png"));
                window.Model.SelectedGateway = firstGateway;
                window.Model.Search = "不存在的设备";
                if (!window.Model.DevicesView.IsEmpty) throw new InvalidOperationException("设备筛选无效。");
                if (InfoText("AppVersionText") != "—") throw new InvalidOperationException("未选设备时没有清空常驻信息。");
                window.Model.Search = "305419896";
                if (window.Model.DevicesView.IsEmpty) throw new InvalidOperationException("十进制设备地址无法检索。");
                window.Model.Search = "12345678";
                if (window.Model.DevicesView.IsEmpty) throw new InvalidOperationException("设备无法检索。");
                var tabs = (System.Windows.Controls.TabControl)window.FindName("DetailsTabs");
                var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("DashboardScroll");
                window.UpdateLayout(); var dashboardWidth = scroll.ViewportWidth;
                if (tabs.Items.Count != 3 || informationCard.TranslatePoint(new Point(), window).Y >= tabs.TranslatePoint(new Point(), window).Y) throw new InvalidOperationException("设备信息没有位于上行区域的第一项。");
                var informationPosition = informationCard.TranslatePoint(new Point(), window);
                var dashboard = (FrameworkElement)scroll.Content;
                var originalHeight = dashboard.Height;
                var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                dashboard.Height = 30; window.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (scroll.ComputedVerticalScrollBarVisibility != Visibility.Collapsed || bar.IsVisible) throw new InvalidOperationException("没有溢出内容时仍显示滚动条。");
                dashboard.Height = scroll.ViewportHeight + 300; window.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible || bar.ActualWidth > 8.1 || Math.Abs(scroll.ViewportWidth - dashboardWidth) > .1) throw new InvalidOperationException("出现滚动条后挤动布局或样式未应用。");
                scroll.ScrollToEnd(); window.UpdateLayout();
                if (scroll.VerticalOffset < 100) throw new InvalidOperationException("溢出内容无法滚动查看。");
                if (!informationCard.IsVisible || (informationCard.TranslatePoint(new Point(), window) - informationPosition).Length > .1) throw new InvalidOperationException("实时数据滚动时设备信息未保持常驻。");
                bar.ApplyTemplate();
                var track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
                var beforeDrag = scroll.VerticalOffset;
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, -20));
                track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, -20, false));
                window.UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (scroll.VerticalOffset >= beforeDrag - 1) throw new InvalidOperationException("拖动滚动条没有滚动内容。");
                dashboard.Height = originalHeight; scroll.ScrollToTop(); window.UpdateLayout();
                var gatewayList = (FrameworkElement)window.FindName("GatewayList");
                var deviceList = (FrameworkElement)window.FindName("DeviceList");
                if (!(gatewayList.TranslatePoint(new Point(), window).X < deviceList.TranslatePoint(new Point(), window).X && deviceList.TranslatePoint(new Point(), window).X < tabs.TranslatePoint(new Point(), window).X && tabs.TranslatePoint(new Point(), window).X < panel.TranslatePoint(new Point(), window).X)) throw new InvalidOperationException("四列布局次序错误。");
                var connectButton = (FrameworkElement)window.FindName("ConnectButton");
                var buttonPosition = connectButton.TranslatePoint(new Point(), window);
                ((System.Windows.Controls.Button)window.FindName("SettingsButton")).Focus(); window.UpdateLayout();
                if ((connectButton.TranslatePoint(new Point(), window) - buttonPosition).Length > .1) throw new InvalidOperationException("按钮获得焦点后挤动了相邻控件。");
                for (var tab = 0; tab < tabs.Items.Count; tab++)
                {
                    tabs.SelectedIndex = tab; window.UpdateLayout(); await Task.Delay(60);
                    if (Math.Abs(scroll.ViewportWidth - dashboardWidth) > .1) throw new InvalidOperationException("切换页签导致布局宽度偏移。");
                    if (!informationCard.IsVisible || InfoText("AppVersionText") != "V1.2.4" || (informationCard.TranslatePoint(new Point(), window) - informationPosition).Length > .1) throw new InvalidOperationException("切换页签时常驻设备信息消失或偏移。");
                }
                tabs.SelectedIndex = 0;
                var historyTabs = (System.Windows.Controls.TabControl)panel.FindName("HistoryTabs");
                var formScroll = (System.Windows.Controls.ScrollViewer)panel.FindName("FormScroll");
                window.UpdateLayout();
                if (historyTabs.ActualHeight < 180 || historyTabs.TranslatePoint(new Point(0, historyTabs.ActualHeight), panel).Y < panel.ActualHeight - 40) throw new InvalidOperationException("下行记录没有利用底部剩余空间。");
                if (!panel.PreviewText.Contains("\n  \"cmd\"")) throw new InvalidOperationException("下行发送预览 JSON 未展开。");
                historyTabs.SelectedIndex = 1;
                window.Model.SelectedDownlinkLog = window.Model.DownstreamView.Cast<PositionMonitor.App.ViewModels.LogRow>().First(l => l.Result == "下行发送");
                window.UpdateLayout(); Capture(window, Path.ChangeExtension(output, ".下行展开报文.png"));
                if (!window.Model.DownlinkDetail.Contains("\n  \"cmd\"")) throw new InvalidOperationException("下行记录 JSON 未展开。");
                historyTabs.SelectedIndex = 0;
                var previousSize = new Size(window.Width, window.Height);
                window.Width = 1060; window.Height = 720; panel.SelectKind(ControlKind.Wake);
                tabs.SelectedIndex = 1; upstreamTabs.SelectedIndex = 1;
                await Task.Delay(100); await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                formScroll.ScrollToEnd(); window.UpdateLayout();
                Capture(window, Path.ChangeExtension(output, ".最小窗口记录.png"));
                await VerifyMessageLayout();
                if (terminalMessages.ActualHeight < 65 || historyTabs.ActualHeight < 90 || formScroll.ActualHeight > formScroll.MaxHeight + 1) throw new InvalidOperationException($"最小窗口记录区域尺寸异常：终端表格 {terminalMessages.ActualHeight:F1}，下行记录 {historyTabs.ActualHeight:F1}，表单 {formScroll.ActualHeight:F1} / {formScroll.MaxHeight:F1}。");
                window.Width = previousSize.Width; window.Height = previousSize.Height;
                panel.SelectKind(ControlKind.QueryInfo); tabs.SelectedIndex = 0; formScroll.ScrollToTop();
                await Task.Delay(100); window.UpdateLayout();
                var connection = new ConnectionWindow(window.Model.Settings, "") { Owner = window };
                connection.Show(); connection.UpdateLayout();
                var topicBox = (System.Windows.Controls.TextBox)connection.FindName("TopicBox");
                if (!topicBox.AcceptsReturn || topicBox.LineCount < 2) throw new InvalidOperationException("多主题设置未按行显示。");
                ((System.Windows.Controls.TextBox)connection.FindName("GatewayBox")).Text = "1003";
                var addTopic = (System.Windows.Controls.Button)connection.FindName("AddTopicButton");
                addTopic.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                addTopic.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var filters = PositionMonitor.Core.Mqtt.SubscriptionFilters.Parse(topicBox.Text);
                if (filters.Length != 4 || !filters.Contains("ucchip/up/sgw/1001/#") || !filters.Contains("ucchip/up/sgw/1002/#") || !filters.Contains("ucchip/up/sgw/704154/#") || !filters.Contains("ucchip/up/sgw/1003/#")) throw new InvalidOperationException("添加主题覆盖了原有配置或添加了重复项。");
                if (((System.Windows.Controls.TextBlock)connection.FindName("GatewayIdDisplay")).Text != "1003" || ((System.Windows.Controls.TextBox)connection.FindName("GatewayBox")).ToolTip?.ToString() != "1003（0x3EB）") throw new InvalidOperationException("设置窗口缺少网关 ID 十进制显示或十六进制悬停提示。");
                Capture(connection, Path.ChangeExtension(output, ".订阅设置.png")); connection.Close();
                var control = new ControlWindow(window.Model) { Owner = window };
                control.Show(); control.UpdateLayout(); Capture(control, Path.ChangeExtension(output, ".控制.png")); control.Close();
                var records = window.Model.SelectedDevice.State.History.Count;
                if (!window.Model.BeginApplicationUpdate(out _) || window.Model.CanSendControl || window.Model.CanOperate) throw new InvalidOperationException("准备更新时未冻结控制及连接操作。");
                window.Model.CancelApplicationUpdate();
                if (!window.Model.CanSendControl || !window.Model.CanOperate) throw new InvalidOperationException("取消更新后未恢复操作。");
                await window.Model.StopSessionAsync();
                if (window.Model.DeviceCount != 4 || window.Model.GatewayCount != 3 || window.Model.SelectedGateway?.Id != "1001" || window.Model.SelectedDevice?.State.History.Count != records || window.Model.HeartbeatCount != 4 || window.Model.Operations.Count != 5 || InfoText("AppVersionText") != "V1.2.4") throw new InvalidOperationException("断开连接丢失真实分组、数据或下行记录。");
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
                if (chrome is null || chrome.CaptionHeight != 64 || chrome.ResizeBorderThickness.Left <= 0 || !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome((FrameworkElement)window.FindName("HeaderActions"))) throw new InvalidOperationException("顶部拖动、缩放或菜单点击区域未正确配置。");
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                var nativeHits = NativeWindowFrame.VerifyCaption(window);
                SystemCommands.MinimizeWindow(window); await Task.Delay(100);
                if (window.WindowState != WindowState.Minimized) throw new InvalidOperationException("最小化按钮无效。");
                SystemCommands.RestoreWindow(window); await Task.Delay(100);
                SystemCommands.MaximizeWindow(window); await Task.Delay(100); window.UpdateLayout();
                if (window.WindowState != WindowState.Maximized) throw new InvalidOperationException("最大化按钮无效。");
                await VerifyMessageLayout();
                outerTabs.SelectedIndex = 0;
                await Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                NativeWindowFrame.VerifyCaption(window);
                var content = (FrameworkElement)window.Content;
                var work = SystemParameters.WorkArea;
                if (content.ActualWidth > work.Width + 2 || content.ActualHeight > work.Height + 2) throw new InvalidOperationException($"最大化内容超出工作区：{content.ActualWidth}×{content.ActualHeight}，工作区 {work}。");
                Capture(window, Path.ChangeExtension(output, ".最大化.png"));
                SystemCommands.RestoreWindow(window); await Task.Delay(100);
                if (window.WindowState != WindowState.Normal) throw new InvalidOperationException("还原按钮无效。");
                Capture(window, Path.ChangeExtension(output, ".png"));
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = true, Devices = window.Model.DeviceCount, Gateways = window.Model.GatewayCount, SubscriptionTopics = window.Model.Settings.SubscriptionTopics, X = window.Model.AngleX, Version = window.Model.SelectedDevice.State.AppVersion, Heartbeats = window.Model.HeartbeatCount, Commands = window.Model.Operations.Select(o => o.Status).ToArray(), DashboardWidth = dashboardWidth, UpDownSeparated = true, DeviceInformationAlwaysVisible = true, NativeCaptionHits = nativeHits, NativeFrameRendering = true }));
            }
            catch (Exception error) { await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Success = false, Error = error.ToString() })); }
            SystemCommands.CloseWindow(window);
            return;
        }
        var index = Array.IndexOf(e.Args, "--screenshot");
        if (index >= 0 && index + 1 < e.Args.Length)
        {
            if (e.Args.Contains("--compact")) { window.Width = 1060; window.Height = 720; }
            if (e.Args.Contains("--maximized")) window.WindowState = WindowState.Maximized;
            if (e.Args.Contains("--gateway-two")) window.Model.SelectedGateway = window.Model.Gateways.FirstOrDefault(g => g.Id == "1002");
            if (e.Args.Contains("--upstream-preview")) ((System.Windows.Controls.TabControl)window.FindName("DetailsTabs")).SelectedIndex = 1;
            if (e.Args.Contains("--gateway-messages-preview"))
            {
                ((System.Windows.Controls.TabControl)window.FindName("DetailsTabs")).SelectedIndex = 1;
                ((System.Windows.Controls.TabControl)window.FindName("UpstreamMessageTabs")).SelectedIndex = 0;
            }
            if (e.Args.Contains("--samples-preview")) ((System.Windows.Controls.TabControl)window.FindName("DetailsTabs")).SelectedIndex = 2;
            if (e.Args.Contains("--response-preview")) ((System.Windows.Controls.TabControl)window.FindName("DetailsTabs")).SelectedIndex = 0;
            var inlinePanel = (PositionMonitor.App.Controls.ControlPanel)window.FindName("DownlinkPanel");
            if (e.Args.Contains("--downstream-preview")) ((System.Windows.Controls.TabControl)inlinePanel.FindName("HistoryTabs")).SelectedIndex = 1;
            if (e.Args.Contains("--period-preview")) inlinePanel.SelectKind(ControlKind.SetPeriod);
            if (e.Args.Contains("--wake-inline-preview")) inlinePanel.SelectKind(ControlKind.Wake);
            await Task.Delay(800);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var path = Path.GetFullPath(e.Args[index + 1]);
            Window target = window;
            if (e.Args.Contains("--update-preview"))
            {
                var update = new UpdateWindow(window) { Owner = window };
                target = update;
                update.Show();
                await update.CheckAsync();
            }
            if (e.Args.Contains("--settings-preview"))
            {
                target = new ConnectionWindow(window.Model.Settings, "") { Owner = window };
                target.Show();
                await Task.Delay(300);
            }
            if (e.Args.Contains("--control-preview"))
            {
                target = new ControlWindow(window.Model) { Owner = window };
                target.Show();
                if (e.Args.Contains("--wake-preview")) ((ControlWindow)target).SelectKind(ControlKind.Wake);
                await Task.Delay(300);
            }
            var hover = e.Args.Contains("--hover-minimize") ? 0 : e.Args.Contains("--hover-maximize") ? 1 : e.Args.Contains("--hover-close") ? 2 : -1;
            if (hover >= 0) { NativeWindowFrame.PreviewHover(window, hover); await Task.Delay(100); }
            if (e.Args.Contains("--native-frame")) NativeWindowFrame.Capture(target, path, e.Args.Contains("--native-offscreen"));
            else Capture(target, path);
            if (target != window) target.Close();
            if (backdrop is not null) { window.Owner = null; backdrop.Close(); }
            window.Close();
        }
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        if (window.FindName("DashboardScroll") is System.Windows.Controls.ScrollViewer scroll) { scroll.ScrollToTop(); window.UpdateLayout(); }
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
