using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PositionMonitor.App.ViewModels;
using PositionMonitor.Core.Protocol;

namespace PositionMonitor.App.Controls;

public partial class ControlPanel : UserControl
{
    private MainViewModel? _model;
    private bool _ready, _sending;
    private string? _deviceKey;
    public ControlPanel()
    {
        InitializeComponent(); _ready = true;
        SizeChanged += (_, _) => FormScroll.MaxHeight = Math.Max(140, ActualHeight * .62);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => { if (IsLoaded) Attach(); };
        UpdatePreview();
    }
    private void Detach() { if (_model is not null) _model.PropertyChanged -= ModelChanged; _model = null; }
    private void Attach()
    {
        Detach(); _model = DataContext as MainViewModel;
        if (_model is not null) _model.PropertyChanged += ModelChanged;
        UpdatePreview();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MainViewModel.SelectedDevice) or nameof(MainViewModel.SelectedGateway) or nameof(MainViewModel.CanSendControl)) UpdatePreview();
    }
    public void SelectKind(ControlKind kind) => KindBox.SelectedIndex = (int)kind;
    public string PreviewText => PreviewBox.Text;
    public ControlRequest CurrentRequest()
    {
        var gateway = _model?.SelectedGateway ?? throw new ArgumentException("先选择左侧网关。");
        if (!uint.TryParse(gateway.Id, out var id)) throw new ArgumentException("直接透传当前仅支持接收，请使用标准网关路由发送控制。");
        var kind = (ControlKind)KindBox.SelectedIndex;
        var source = kind == ControlKind.Wake ? 0 : _model?.SelectedDevice?.Route?.Source ?? throw new ArgumentException("先选择该网关下的终端设备。");
        var paging = kind == ControlKind.Wake || WakeFirst.IsChecked == true ? new PagingOptions(byte.Parse(WakeFrequencyBox.Text), ushort.Parse(WakeIdBox.Text), uint.Parse(WakeTimeBox.Text)) : null;
        return new(gateway.Company, id, source, kind,
            kind == ControlKind.SetPeriod ? uint.Parse(PeriodBox.Text) : 14400,
            kind == ControlKind.SetRadio ? sbyte.Parse(PowerBox.Text) : (sbyte)10,
            kind == ControlKind.SetRadio ? FrequenciesBox.Text.Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries).Select(byte.Parse).ToArray() : null,
            (byte)Math.Max(0, RangeBox.SelectedIndex), paging);
    }
    private void InputChanged(object sender, RoutedEventArgs e) { if (_ready) UpdatePreview(); }
    private void UpdatePreview()
    {
        if (!_ready) return;
        WakeIdDisplay.Text = ushort.TryParse(WakeIdBox.Text, out var group) ? IdentifierDisplay.Format(group) : "请输入十进制组号";
        WakeIdBox.ToolTip = ushort.TryParse(WakeIdBox.Text, out group) ? IdentifierDisplay.Tooltip(group) : null;
        WakeIdDisplay.ToolTip = WakeIdBox.ToolTip;
        var key = _model?.SelectedDevice?.State.Key;
        if (key != _deviceKey)
        {
            _deviceKey = key;
            PeriodBox.Text = (_model?.SelectedDevice?.State.Period ?? 14400).ToString();
        }
        var kind = (ControlKind)KindBox.SelectedIndex;
        PeriodPanel.Visibility = kind == ControlKind.SetPeriod ? Visibility.Visible : Visibility.Collapsed;
        RadioPanel.Visibility = kind == ControlKind.SetRadio ? Visibility.Visible : Visibility.Collapsed;
        RangePanel.Visibility = kind == ControlKind.SetRange ? Visibility.Visible : Visibility.Collapsed;
        WakeFirst.Visibility = kind == ControlKind.Wake ? Visibility.Collapsed : Visibility.Visible;
        PagingPanel.Visibility = kind == ControlKind.Wake || WakeFirst.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var message = CurrentRequest().Build(1, 1);
            PreviewBox.Text = Services.MessageDisplay.Message(message);
            HintText.Text = kind == ControlKind.Reboot ? "重启会中断终端运行，请核对当前目标。" : kind == ControlKind.Wake ? "网关接受唤醒后，需观察终端上报确认已醒。" : "终端响应在上行信息显示，执行结果在此更新。";
            SendButton.IsEnabled = _model?.CanSendControl == true && !_sending;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        { PreviewBox.Text = "选择网关、设备并填写有效参数后显示预览。"; HintText.Text = error.Message; SendButton.IsEnabled = false; }
    }
    private async void SendClick(object sender, RoutedEventArgs e)
    {
        if (_sending || _model is null) return;
        try
        {
            var request = CurrentRequest(); request.Build(1, 1);
            _sending = true; SendButton.Content = "发送中…"; SendButton.IsEnabled = false;
            await _model.SendControlAsync(request);
        }
        catch (Exception error) { HintText.Text = $"未发送：{error.Message}"; }
        finally { _sending = false; SendButton.Content = "发送命令"; UpdatePreview(); }
    }
}
