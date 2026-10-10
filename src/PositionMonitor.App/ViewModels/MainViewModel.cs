using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PositionMonitor.App.Services;
using PositionMonitor.Core.Mqtt;
using PositionMonitor.Core.Protocol;

namespace PositionMonitor.App.ViewModels;

public sealed class DeviceItem(DeviceState state) : Observable
{
    public int ListNumber { get; init; }
    public DeviceState State { get; } = state;
    public MessageEnvelope? Route { get; set; }
    public string GatewayKey { get; set; } = "";
    public string GatewayStatus { get; set; } = "等待应用注册";
    public string Label => Route?.Source is { } address ? IdentifierDisplay.Format(address) : State.Label;
    public string PrimaryLabel => Route?.Source is { } address ? address.ToString(CultureInfo.InvariantCulture) : State.Label;
    public string IdentifierTooltip => Route?.Source is { } address ? IdentifierDisplay.Tooltip(address) : State.Label;
    public string GatewayLabel => uint.TryParse(State.Gateway, out _) ? $"网关 {IdentifierDisplay.Format(State.Gateway)}" : State.Gateway;
    public string Freshness => State.Freshness(DateTimeOffset.UtcNow);
    public void Update() => Refresh();
}

public sealed class GatewayItem(string key, string company, string id) : Observable
{
    public int ListNumber { get; init; }
    public string Key { get; } = key;
    public string Company { get; } = company;
    public string Id { get; } = id;
    public string Label => uint.TryParse(Id, out _) ? $"网关 {IdentifierDisplay.Format(Id)}" : Id;
    public string PrimaryLabel => uint.TryParse(Id, out var id) ? IdentifierDisplay.Format(id) : Id;
    public string IdentifierTooltip => $"{(uint.TryParse(Id, out _) ? "网关 " : "")}{IdentifierDisplay.Tooltip(Id)}\n{Version}";
    public ObservableCollection<LogRow> Messages { get; } = [];
    public long Heartbeats { get; private set; }
    public DateTimeOffset? LastHeartbeat { get; private set; }
    public DateTimeOffset? LastReceived { get; private set; }
    public int DeviceCount { get; set; }
    public string Version { get; set; } = "尚无版本信息";
    public string DeviceCountText => $"{DeviceCount} 台终端";
    public string HeartbeatText => LastHeartbeat is { } time ? $"心跳 {time.ToOffset(TimeSpan.FromHours(8)):HH:mm:ss}" : "尚无实时心跳";
    public string ReceivedText => LastReceived is { } time ? $"最近消息 {time.ToOffset(TimeSpan.FromHours(8)):HH:mm:ss}" : "等待网关消息";
    public void Receive(DateTimeOffset time, bool retained) { if (!retained) LastReceived = time; Refresh(); }
    public void Beat(DateTimeOffset time) { Heartbeats++; LastHeartbeat = time; Refresh(); }
    public void Update() => Refresh();
}

public sealed class ControlOperation(ControlRequest request, byte number, int sequence) : Observable
{
    public ControlRequest Request { get; } = request;
    public byte Number { get; } = number;
    public int Sequence { get; } = sequence;
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public string Time => Created.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
    public string Target => Request.Kind == ControlKind.Wake ? $"网关 {IdentifierDisplay.Format(Request.Gateway)}" : $"网关 {IdentifierDisplay.Format(Request.Gateway)} / 终端 {IdentifierDisplay.Format(Request.Target)}";
    public string IdentifierTooltip => Request.Kind == ControlKind.Wake ? $"网关 {IdentifierDisplay.Tooltip(Request.Gateway)}" : $"网关 {IdentifierDisplay.Tooltip(Request.Gateway)}\n终端 {IdentifierDisplay.Tooltip(Request.Target)}";
    public string Name => Request.Name;
    public bool Completed { get; private set; }
    private bool _timedOut;
    public bool CanMatchResponse => !Completed || _timedOut && DateTimeOffset.UtcNow - Created < TimeSpan.FromMinutes(5);
    public string Status { get; private set; } = "正在发送…";
    public void Set(string status, bool completed = false) { if (Completed) return; Status = status; Completed = completed; Refresh(); }
    public void Timeout()
    {
        if (Completed) return;
        _timedOut = true; Completed = true;
        Status = "终端响应超时，未自动重发；仍接收迟到响应，请核对终端在线状态"; Refresh();
    }
    public void Confirm(string status)
    {
        if (!CanMatchResponse) return;
        _timedOut = false; Completed = false; Set(status, true);
    }
}

public sealed record HeartbeatState(string Gateway, long Count, DateTimeOffset Received)
{
    public string Time => Received.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss");
}

public sealed record LogRow(DateTimeOffset Received, string Device, string Name, string Result, string Detail, string? DeviceKey = null, string? GatewayKey = null, string Direction = "上行")
{
    public string Time => Received.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
    public string? AdditionalIdentifierTooltip { get; init; }
    public string IdentifierTooltip => AdditionalIdentifierTooltip ?? string.Join("\n", new[] {
        GatewayKey is { } gateway ? $"网关 {IdentifierDisplay.Tooltip(gateway.Split('/').Last())}" : null,
        DeviceKey is { } device ? $"终端 {IdentifierDisplay.Tooltip(device.Split('/').Last())}" : null
    }.Where(s => s is not null));
}

public sealed record SampleRow(SampleRecord Record)
{
    public string Time => Record.PlotTime.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss");
    public byte Number => Record.Number;
    public string X => MainViewModel.Number(Record.Sample.AngleX, "0.00");
    public string Y => MainViewModel.Number(Record.Sample.AngleY, "0.00");
    public string Z => MainViewModel.Number(Record.Sample.AngleZ, "0.00");
    public string Battery => Record.Sample.BatteryMillivolts is { } v ? (v / 1000.0).ToString("0.000") : "无效";
    public string State => Record.Retained ? "保留消息" : Record.Sample.GnssValid ? "定位有效" : "定位无效";
}

public sealed class MainViewModel : Observable, IAsyncDisposable
{
    private readonly Channel<(MqttApplicationMessage Message, DateTimeOffset Time)> _incoming = Channel.CreateBounded<(MqttApplicationMessage, DateTimeOffset)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Channel<(MqttSession Session, MqttApplicationMessage Message)> _acks = Channel.CreateBounded<(MqttSession, MqttApplicationMessage)>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentQueue<string> _notices = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _ackWorker;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DeviceItem> _deviceMap = [];
    private readonly ObservableCollection<DeviceItem> _devices = [];
    private readonly Dictionary<string, HeartbeatState> _heartbeats = [];
    private readonly Dictionary<string, GatewayItem> _gatewayMap = [];
    private GatewayItem? _selectedGateway;
    private LogRow? _selectedGatewayLog;
    private LogRow? _selectedDownlinkLog;
    private MqttSession? _session;
    private DeviceItem? _selectedDevice;
    private LogRow? _selectedLog;
    private string _search = "";
    private string _password = "";
    private string? _connectionIdentity;
    private string _status = "填写连接设置后连接服务器。";
    private bool _busy, _dataDirty, _heartbeatDirty, _preparingUpdate;
    private int _rangeIndex, _ackSequence = Random.Shared.Next(1000, 1000000), _packetNumber;
    private long _received, _errors, _duplicates, _dropped;
    public ConnectionSettings Settings { get; private set; }
    public ICollectionView DevicesView { get; }
    public ICollectionView UpstreamView { get; }
    public ICollectionView DownstreamView { get; }
    public ICollectionView OperationsView { get; }
    public ObservableCollection<GatewayItem> Gateways { get; } = [];
    public ObservableCollection<LogRow> Logs { get; } = [];
    public ObservableCollection<LogRow> DownlinkLogs { get; } = [];
    public ObservableCollection<ControlOperation> Operations { get; } = [];
    public bool CanSendControl => !_preparingUpdate && _session?.IsSubscribed == true;
    public GatewayItem? SelectedGateway
    {
        get => _selectedGateway;
        set
        {
            if (ReferenceEquals(_selectedGateway, value)) return;
            _selectedGateway = value; _selectedGatewayLog = value?.Messages.FirstOrDefault();
            _selectedDevice = null; DevicesView.Refresh();
            _selectedDevice = DevicesView.Cast<DeviceItem>().FirstOrDefault();
            UpdateSelection(); Refresh();
        }
    }
    public int GatewayCount => Gateways.Count;
    public string SelectedGatewayTitle => SelectedGateway?.Label ?? "请先选择网关";
    public string? SelectedGatewayTooltip => SelectedGateway?.IdentifierTooltip;
    public string? SelectedDeviceTooltip => SelectedDevice?.IdentifierTooltip;
    public string ControlTargetTitle => SelectedGateway is null ? "请选择网关" : $"{SelectedGateway.Label} → {(SelectedDevice is null ? "请选择终端" : $"终端 {SelectedDevice.Label}")}";
    public string? ControlTargetTooltip => SelectedGateway is null ? null : $"{SelectedGateway.IdentifierTooltip}{(SelectedDevice is null ? "" : $"\n终端 {SelectedDevice.IdentifierTooltip}")}";
    public string DeviceListCaption => $"{DevicesView.Cast<DeviceItem>().Count()} 台终端";
    public LogRow? SelectedGatewayLog { get => _selectedGatewayLog; set { _selectedGatewayLog = value; Changed(); Changed(nameof(GatewayLogDetail)); } }
    public string GatewayLogDetail => SelectedGatewayLog?.Detail ?? "选择网关交互查看详情；心跳在网关列表独立统计。";
    public LogRow? SelectedDownlinkLog { get => _selectedDownlinkLog; set { _selectedDownlinkLog = value; Changed(); Changed(nameof(DownlinkDetail)); } }
    public string DownlinkDetail => SelectedDownlinkLog?.Detail ?? "选择下行报文查看发送内容。";
    public Visibility NoGatewaysVisibility => Gateways.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSelectedDeviceVisibility => SelectedDevice is null ? Visibility.Visible : Visibility.Collapsed;
    public long HeartbeatCount => _heartbeats.Values.Sum(h => h.Count);
    public IReadOnlyList<HeartbeatState> Heartbeats => _heartbeats.Values.OrderByDescending(h => h.Received).ToArray();
    public string HeartbeatGateway => _heartbeats.Count == 0 ? "等待网关心跳" : $"最近：网关 {IdentifierDisplay.Format(Heartbeats[0].Gateway)}";
    public string HeartbeatTime => _heartbeats.Count == 0 ? "收到心跳时红心跳动 +1" : $"{Heartbeats[0].Time} · 共 {_heartbeats.Count} 个网关";
    public string HeartbeatTooltip => _heartbeats.Count == 0 ? "只统计主题与 src 一致的实时网关心跳；保留消息不计数。" : string.Join("\n", Heartbeats.Select(h => $"网关 {IdentifierDisplay.Format(h.Gateway)}：{h.Count} 次，最近 {h.Time}"));
    public IReadOnlyList<SampleRecord> ChartRecords { get; private set; } = [];
    public IReadOnlyList<SampleRow> SampleRows { get; private set; } = [];

    public MainViewModel()
    {
        try { Settings = SettingsStore.Load(); _password = SettingsStore.Password(Settings); }
        catch (Exception error) { Settings = new(); _status = $"连接配置读取失败，请重新设置：{error.Message}"; }
        DevicesView = CollectionViewSource.GetDefaultView(_devices);
        DevicesView.Filter = obj => obj is DeviceItem item && item.GatewayKey == SelectedGateway?.Key && (item.Label.Contains(Search, StringComparison.OrdinalIgnoreCase) || item.IdentifierTooltip.Contains(Search, StringComparison.OrdinalIgnoreCase) || item.State.Label.Contains(Search, StringComparison.OrdinalIgnoreCase) || item.State.Gateway.Contains(Search, StringComparison.OrdinalIgnoreCase));
        UpstreamView = CollectionViewSource.GetDefaultView(Logs);
        UpstreamView.Filter = obj => obj is LogRow row && row.GatewayKey == SelectedGateway?.Key &&
            (SelectedDevice is null ? row.Name == "解析异常" : row.DeviceKey == SelectedDevice.State.Key || row.Name == "解析异常" && row.DeviceKey is null);
        DownstreamView = CollectionViewSource.GetDefaultView(DownlinkLogs);
        DownstreamView.Filter = obj => obj is LogRow row && row.GatewayKey == SelectedGateway?.Key && (row.DeviceKey is null || row.DeviceKey == SelectedDevice?.State.Key);
        OperationsView = CollectionViewSource.GetDefaultView(Operations);
        OperationsView.Filter = obj => obj is ControlOperation operation && $"{operation.Request.Company}/sgw/{operation.Request.Gateway}" == SelectedGateway?.Key && (operation.Request.Kind == ControlKind.Wake || operation.Request.DeviceKey == SelectedDevice?.State.Key);
        _ackWorker = Task.Run(AcknowledgeLoopAsync);
        _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Password => _password;
    public string Status => _status;
    public string ShortStatus => _session?.IsSubscribed == true ? "已订阅" : _session is not null ? "连接中 / 重连" : "未连接";
    public string StatusBackground => _session?.IsSubscribed == true ? "#E2F3EF" : "#EAF0F3";
    public string StatusForeground => _session?.IsSubscribed == true ? "#087F78" : "#607780";
    public string ConnectLabel => _session is null ? "连接服务器" : "断开连接";
    public bool CanOperate => !_busy && !_preparingUpdate;
    public bool BeginApplicationUpdate(out string reason)
    {
        if (!CanOperate) { reason = "当前正在连接或准备更新，请稍后再试。"; return false; }
        if (Operations.Any(o => !o.Completed)) { reason = "有下行命令正在等待响应，请等待执行完成后再更新。"; return false; }
        _preparingUpdate = true; Refresh(); reason = ""; return true;
    }
    public void CancelApplicationUpdate() { _preparingUpdate = false; Refresh(); }
    public bool HasSession => _session is not null;
    public string ModeLabel => "MQTT 订阅";
    public string Subscription
    {
        get
        {
            var topics = Settings.Topic.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            return string.Join(Environment.NewLine, topics.Take(3)) + (topics.Length > 3 ? $"\n… 共 {topics.Length} 个主题" : "");
        }
    }
    public int DeviceCount => _devices.Count;
    public Visibility NoDevicesVisibility => DevicesView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    public string Counters => $"接收 {_received}   重复 {_duplicates}   异常 {_errors}   队列丢弃 {Interlocked.Read(ref _dropped)}";
    public string Search
    {
        get => _search;
        set
        {
            _search = value;
            DevicesView.Refresh();
            // 筛选清空 ListBox 选中项后，恢复匹配结果中的第一台终端。
            if (_selectedDevice is null || !DevicesView.Contains(_selectedDevice)) _selectedDevice = DevicesView.Cast<DeviceItem>().FirstOrDefault();
            UpdateSelection(); Refresh();
        }
    }
    public DeviceItem? SelectedDevice { get => _selectedDevice; set { if (value is not null && value.GatewayKey != SelectedGateway?.Key) return; _selectedDevice = value; UpdateSelection(); Refresh(); } }
    public LogRow? SelectedLog { get => _selectedLog; set { _selectedLog = value; Changed(); Changed(nameof(LogDetail)); } }
    public string LogDetail => SelectedLog?.Detail ?? "选择一条报文查看主题、原始载荷与解析结果。";
    public int RangeIndex { get => _rangeIndex; set { _rangeIndex = value; Changed(); Changed(nameof(ChartMinutes)); } }
    public int ChartMinutes => RangeIndex switch { 0 => 5, 1 => 30, _ => 0 };
    public string TrendEmptyHint => ChartRecords.Count == 0 ? "（接收有效采样后，三轴倾角将在这里绘制）" : "";
    private DeviceState? Device => SelectedDevice?.State;
    private Sample? Latest => Device?.Latest?.Sample;
    public string SelectedTitle => SelectedDevice?.Label ?? "请选择终端";
    public string DeviceRegistrationStatus => Device is null ? "请选择终端" : Device.HardwareVersion is not null ? "已注册" : "待注册";
    public string DeviceRegistrationColor => StatusColor(Device?.HardwareVersion is not null ? true : null);
    public string UpstreamCaption => Device is null ? "当前网关的解析异常；选择终端后查看其上行消息" : "当前终端的上行消息与所属网关未识别来源的解析异常";
    public string SelectedSubtitle => Device is null ? "在左侧选择网关，再选择所属终端。" : $"已记录 {Device.Reports} 条采样";
    public string ReceiptTime => Device?.Latest is { } r ? $"最近接收 {r.Received.ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm:ss}" : "尚未接收采样";
    public string Freshness => Device?.Freshness(DateTimeOffset.UtcNow) ?? "";
    public double? AngleXValue => Latest?.AngleX;
    public double? AngleYValue => Latest?.AngleY;
    public string AngleX => Angle(Latest?.AngleX);
    public string AngleY => Angle(Latest?.AngleY);
    public string AngleZ => Angle(Latest?.AngleZ);
    public string Longitude => Number(Latest?.Longitude, "0.000000", "°");
    public string Latitude => Number(Latest?.Latitude, "0.000000", "°");
    public string Altitude => Number(Latest?.Altitude, "0.0", " m");
    public string Hdop => Number(Latest?.Hdop, "0.0");
    public string Battery => Latest?.BatteryMillivolts is { } v ? $"{v / 1000.0:0.000} V" : "—";
    public string BatteryMillivolts => Latest?.BatteryMillivolts is { } v ? $"{v} mV" : "电池电压暂无有效数据";
    public string SensorStatus => Latest is null ? "等待采样" : Latest.SensorValid ? "传感器有效" : "倾角含无效数据";
    public string GnssStatus => Latest is null ? "等待定位" : Latest.GnssValid ? "定位有效" : "定位无效";
    public string SensorStatusColor => StatusColor(Latest?.SensorValid);
    public string GnssStatusColor => StatusColor(Latest?.GnssValid);
    public string BatteryStatus => Latest is null || !Latest.HasBatteryMeasurement ? "等待上报" : Latest.BatteryMillivolts is not null ? "电压有效" : "电压无效";
    public string BatteryStatusColor => StatusColor(Latest is null || !Latest.HasBatteryMeasurement ? null : Latest.BatteryMillivolts is not null);
    private static string StatusColor(bool? valid) => valid switch { true => "#087F78", false => "#C4473A", null => "#A87508" };
    public string VersionDetails => $"硬件版本      {Device?.HardwareVersion ?? "—"}\n应用版本      {Device?.AppVersion ?? "—"}\nWIoTa 版本   {Device?.WiotaVersion ?? "—"}\nWIoTa SNR   {(Device?.Snr is { } snr ? $"{snr} dB" : "—")}";
    public string DeviceHardwareVersion => Device?.HardwareVersion ?? "—";
    public string DeviceAppVersion => Device?.AppVersion ?? "—";
    public string DeviceWiotaVersion => Device?.WiotaVersion ?? "—";
    public string DeviceSnr => Device?.Snr is { } snr ? $"{snr} dB" : "—";
    public string DevicePeriod => Device?.Period is { } period ? $"{period} 秒" : "—";
    public string DeviceInformationTime => Device?.LastInformationReceived is { } time ? time.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss") : "—";
    public string DeviceConfigurationSummary
    {
        get
        {
            var fields = Device?.Configuration.Where(p => p.Key is not ("方向" or "硬件版本" or "应用版本" or "WIoTa 版本" or "WIoTa SNR" or "上报周期")).Select(p => $"{p.Key}：{p.Value}").ToArray() ?? [];
            return fields.Length > 0 ? string.Join("  ·  ", fields) : Device is null ? "选择终端后查看；收到注册或查询响应自动更新。" : Device.HardwareVersion is not null || Device.Configuration.Count > 0 ? "信息随注册及查询响应更新。" : "等待终端注册或查询响应。";
        }
    }
    public string SampleTime => Latest?.Utc is { } utc ? $"终端 UTC     {DateTimeOffset.FromUnixTimeSeconds(utc):yyyy-MM-dd HH:mm:ss}\n北京时间     {DateTimeOffset.FromUnixTimeSeconds(utc).ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}" : "终端时间无效，趋势使用接收时间。";
    public string ConfigurationDetails => Device?.Configuration.Count > 0 ? string.Join("\n", Device.Configuration.Where(p => p.Key != "方向").Select(p => $"{p.Key}    {p.Value}")) : "接收到配置响应后显示实际生效参数。";
    public static string Number(double? value, string format, string unit = "") => value is { } number ? number.ToString(format, CultureInfo.InvariantCulture) + unit : "—";
    private static string Angle(double? value) => Number(value, "+0.00;-0.00;0.00", "°");
    public void Notify(string message) { _status = message; Refresh(); }
    public void ApplySettings(ConnectionSettings settings, string password) { Settings = settings; _password = password; Refresh(); }

    public async Task ToggleConnectionAsync()
    {
        if (!CanOperate) return;
        _busy = true; Refresh();
        try
        {
            if (_session is not null) await StopSessionAsync();
            else
            {
                Settings.Validate(_password);
                var identity = $"{Settings.UseTls}/{Settings.Host.ToLowerInvariant()}/{Settings.Port}/{Settings.UserName}";
                if (_connectionIdentity is not null && _connectionIdentity != identity) Clear();
                _connectionIdentity = identity;
                var session = new MqttSession();
                session.MessageReceived += (_, message) =>
                {
                    if (!_incoming.Writer.TryWrite((message, DateTimeOffset.UtcNow))) Interlocked.Increment(ref _dropped);
                };
                session.StateChanged += (_, state) => _notices.Enqueue(state);
                _session = session;
                session.Start(Settings.Options(_password), Settings.SubscriptionTopics, Settings.Qos);
                _status = "正在连接服务器…";
            }
        }
        catch (Exception error) { _status = $"连接操作失败：{error.Message}"; }
        finally { _busy = false; Refresh(); }
    }

    public async Task StopSessionAsync()
    {
        var session = _session;
        _session = null;
        foreach (var operation in Operations.Where(o => !o.Completed).ToArray()) operation.Set("连接已断开，执行结果未确认；未自动重发", true);
        if (session is not null) await session.DisposeAsync();
        while (_incoming.Reader.TryRead(out _)) { }
        while (_notices.TryDequeue(out _)) { }
        _status = "已断开，已接收的数据仍可查看。";
        Refresh();
    }

    private void Clear()
    {
        _deviceMap.Clear(); _devices.Clear(); Logs.Clear(); _heartbeats.Clear(); _gatewayMap.Clear(); Gateways.Clear(); DownlinkLogs.Clear(); Operations.Clear();
        _selectedGateway = null; _selectedGatewayLog = null; _selectedDownlinkLog = null; _selectedDevice = null; _selectedLog = null;
        _received = _errors = _duplicates = _dropped = 0;
        ChartRecords = []; SampleRows = [];
    }

    private void Tick()
    {
        var changed = false;
        var statusChanged = false;
        while (_notices.TryDequeue(out var notice)) { _status = notice; changed = statusChanged = true; }
        for (var i = 0; i < 100 && _incoming.Reader.TryRead(out var item); i++) { Process(item.Message, item.Time); changed = true; }
        foreach (var device in _devices) device.Update();
        foreach (var operation in Operations.Where(o => !o.Completed && DateTimeOffset.UtcNow - o.Created > TimeSpan.FromSeconds(o.Request.Paging is null ? 60 : 90)).ToArray())
            operation.Timeout();
        if (changed && _dataDirty || statusChanged) { if (_dataDirty) UpdateSelection(); _dataDirty = false; Refresh(); }
        else { Changed(nameof(Freshness)); Changed(nameof(Counters)); Changed(nameof(ShortStatus)); Changed(nameof(CanSendControl)); }
        if (_heartbeatDirty)
        {
            _heartbeatDirty = false;
            foreach (var property in new[] { nameof(HeartbeatCount), nameof(HeartbeatGateway), nameof(HeartbeatTime), nameof(HeartbeatTooltip), nameof(Heartbeats) }) Changed(property);
        }
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void Process(MqttApplicationMessage message, DateTimeOffset received)
    {
        _received++;
        try
        {
            var messages = Settings.PayloadMode == PayloadMode.Auto ? GatewayPayload.Split(message) : [message];
            foreach (var part in messages) ProcessPayload(part, received, message);
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            _errors++; _dataDirty = true;
            AddLog(new(received, "—", "解析异常", "查看详情", TransportDetail(message, received) + "\n原因：" + error.Message, GatewayKey: GatewayPayload.GatewayKey(message.Topic)));
            _status = $"收到无法解析的报文：{error.Message}";
        }
    }

    private static string TransportDetail(MqttApplicationMessage message, DateTimeOffset received)
    {
        var preview = message.Payload.Span[..Math.Min(4096, message.Payload.Length)];
        return $"主题：{message.Topic}\n接收（北京时间）：{received.ToOffset(TimeSpan.FromHours(8)):O}\nQoS：{message.QualityOfService}  保留消息：{message.Retain}\nMQTT 原始 Hex：{Convert.ToHexString(preview)}{(message.Payload.Length > 4096 ? "（详情截取前 4096 字节）" : "")}\nMQTT 原始文本：\n{Services.MessageDisplay.Payload(preview)}\n";
    }

    private void ProcessPayload(MqttApplicationMessage message, DateTimeOffset received, MqttApplicationMessage original)
    {
        var preview = message.Payload.Span[..Math.Min(4096, message.Payload.Length)];
        string detail = TransportDetail(original, received);
        if (!ReferenceEquals(message, original)) detail += $"当前数组项：\n{Services.MessageDisplay.Payload(preview)}\n";
        string device = "—";
        string? deviceKey = null, gatewayKey = null;
        try
        {
            var packet = (Settings.PayloadMode == PayloadMode.Auto) ? GatewayPacket.Decode(message) : null;
            var gateway = packet is null ? null : EnsureGateway(packet.Key, packet.Company, packet.Gateway.ToString());
            gatewayKey = gateway?.Key;
            gateway?.Receive(received, message.Retain);
            if (packet?.Command == 0 && gateway is not null)
            {
                gateway.Version = $"硬件 {TextField(packet, "hw_ver")} / 固件 {TextField(packet, "sw_ver")}";
                gateway.Update();
            }
            var gatewayNotice = (Settings.PayloadMode == PayloadMode.Auto) ? GatewayNotice.Decode(message) : null;
            if (gatewayNotice is not null)
            {
                if (gatewayNotice.Heartbeat && !message.Retain)
                {
                    if (_heartbeats.Count < 256 || _heartbeats.ContainsKey(gatewayNotice.Scope))
                    {
                        _heartbeats.TryGetValue(gatewayNotice.Scope, out var previous);
                        _heartbeats[gatewayNotice.Scope] = new(gatewayNotice.Gateway, (previous?.Count ?? 0) + 1, received);
                        _heartbeatDirty = true;
                        gateway?.Beat(received);
                    }
                }
                if (!gatewayNotice.Heartbeat && !message.Retain && gatewayNotice.Result is { } result)
                {
                    var operation = Operations.FirstOrDefault(o => o.CanMatchResponse && o.Sequence.ToString() == gatewayNotice.Sequence && $"{o.Request.Company}/sgw/{o.Request.Gateway}" == gatewayNotice.Scope);
                    if (operation is not null)
                    {
                        var success = result == "suc";
                        var status = success ? operation.Request.Kind == ControlKind.Wake ? "网关已接受唤醒，终端状态待上报确认" : "网关反馈发送成功，等待终端响应" : $"网关执行失败：{result}";
                        if (!success || operation.Request.Kind == ControlKind.Wake) operation.Confirm(status);
                        else operation.Set(status);
                    }
                }
                if (!gatewayNotice.Heartbeat && packet?.Command == 100)
                {
                    var target = packet.Body.TryGetProperty("dst", out var dst) && dst.ValueKind == System.Text.Json.JsonValueKind.Number && dst.TryGetUInt32(out var id) ? $"{packet.Key}/{id}" : null;
                    AddDownlink(new(received, gateway?.Label ?? gatewayNotice.Gateway, "网关发送反馈", gatewayNotice.Result ?? "查看详情", detail + Services.MessageDisplay.Payload(preview), target, gatewayKey) { AdditionalIdentifierTooltip = packet.IdentifierTooltip });
                }
                else if (!gatewayNotice.Heartbeat && gateway is not null && packet is not null) AddGatewayLog(gateway, new(received, gateway.Label, packet.Name, packet.Summary, GatewayDetail(packet, detail), GatewayKey: gateway.Key) { AdditionalIdentifierTooltip = packet.IdentifierTooltip });
                return;
            }
            if (packet is { Command: not 100 } && gateway is not null)
            {
                AddGatewayLog(gateway, new(received, gateway.Label, packet.Name, packet.Summary, GatewayDetail(packet, detail), GatewayKey: gateway.Key) { AdditionalIdentifierTooltip = packet.IdentifierTooltip });
                if (packet.Terminal is { } source && source is not (0 or uint.MaxValue))
                {
                    var route = new MessageEnvelope($"{packet.Key}/{source}", $"0x{source:X8}", gateway.Id, message.Topic, [], message.Retain, source, $"{packet.Company}/down/sgw/{packet.Gateway}");
                    var terminal = EnsureDevice(route, gateway);
                    terminal.GatewayStatus = message.Retain ? "历史网关状态" : packet.OnlineStatus == 1 ? "网关报告在线" : packet.OnlineStatus == 0 ? "网关报告离线" : "已收到链路状态";
                    terminal.Update(); _dataDirty = true;
                }
                return;
            }
            var envelope = EnvelopeDecoder.Decode(message, Settings.PayloadMode);
            if (envelope is null) return;
            device = envelope.Source is { } address ? IdentifierDisplay.Format(address) : envelope.DeviceLabel;
            detail += $"终端：{device}\n";
            deviceKey = envelope.Source is not null ? envelope.DeviceKey : null;
            detail += $"应用帧 Hex：{Convert.ToHexString(envelope.Bytes)}\n";
            var frame = ProtocolDecoder.Decode(envelope.Bytes);
            if (frame.Destinations.Length > 0) detail += "目标终端：" + string.Join("、", frame.Destinations.Select(IdentifierDisplay.Format)) + "\n";
            gateway ??= EnsureGateway("raw", "", "直接透传"); gatewayKey = gateway.Key;
            deviceKey = envelope.DeviceKey;
            var item = EnsureDevice(envelope, gateway);
            item.Route = envelope;
            if (!message.Retain) item.GatewayStatus = "已收到应用消息";
            var unique = item.State.Apply(frame, envelope, received);
            _dataDirty = true;
            if (frame.IsResponse && !message.Retain)
            {
                var operation = Operations.FirstOrDefault(o => o.CanMatchResponse && o.Request.DeviceKey == envelope.DeviceKey && o.Number == frame.Number && o.Request.Command == frame.Command && (frame.Command != 1 || frame.Sample is not null));
                operation?.Confirm(operation.Request.Result(frame));
            }
            if (!unique) _duplicates++;
            detail += $"命令：0x{frame.Command:X2} {frame.Name}  包序号：{frame.Number}  Property：0x{frame.Property:X2}\n" + string.Join("\n", frame.Fields.Select(p => $"{p.Key}：{p.Value}"));
            if (frame.Sample is { } sample) detail += $"\nUTC：{sample.Utc?.ToString() ?? "无效"}\n经度：{Number(sample.Longitude, "0.000000")}  纬度：{Number(sample.Latitude, "0.000000")}\n海拔：{Number(sample.Altitude, "0.0", " m")}  HDOP：{Number(sample.Hdop, "0.0")}\n电池：{sample.BatteryMillivolts?.ToString() ?? "无效"} mV\n倾角：X {Angle(sample.AngleX)}  Y {Angle(sample.AngleY)}  Z {Angle(sample.AngleZ)}";
            AddLog(new(received, device, frame.Name, unique ? message.Retain ? "保留消息" : "解析成功" : "重复报文", detail, deviceKey, gatewayKey, frame.IsResponse ? "设置 / 查询响应" : frame.Sample is not null ? "主动上报" : "上行请求")
            {
                AdditionalIdentifierTooltip = string.Join("\n", new[] { $"网关 {IdentifierDisplay.Tooltip(envelope.Gateway)}", envelope.Source is { } tooltipSource ? $"终端 {IdentifierDisplay.Tooltip(tooltipSource)}" : null }
                    .Where(s => s is not null).Concat(frame.Destinations.Select(id => $"目标终端 {IdentifierDisplay.Tooltip(id)}")))
            });
            // 重发注册也必须回执；保留消息不得触发回执。
            if (Settings.AutoAck && frame.NeedsAcknowledgment && !message.Retain && _session is { } session)
            {
                var reply = envelope.Acknowledgment(frame, ++_ackSequence, Settings.RawReplyTopic);
                if (reply is null) _status = "收到要求确认的报文，但无法确定回执路由，请配置直接透传回执主题。";
                else if (!_acks.Writer.TryWrite((session, reply))) _status = "回执队列已满，本次未确认；请检查服务器连接。";
                else AddDownlink(new(received, device, "自动回执", "已加入发送队列", Services.MessageDisplay.Message(reply), deviceKey, gatewayKey));
            }
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            _errors++;
            _dataDirty = true;
            gatewayKey ??= GatewayPayload.GatewayKey(message.Topic);
            AddLog(new(received, device, "解析异常", "查看详情", detail + "\n原因：" + error.Message, deviceKey, gatewayKey));
            _status = $"收到无法解析的报文：{error.Message}";
        }
    }

    public async Task SendControlAsync(ControlRequest request)
    {
        if (!CanSendControl || _session is not { } session) throw new InvalidOperationException("请先连接并完成订阅，且不要在更新期间发送控制命令。");
        for (var attempts = 0; attempts < 255; attempts++)
        {
            _packetNumber = _packetNumber % 255 + 1;
            if (!Operations.Any(o => o.CanMatchResponse && o.Request.DeviceKey == request.DeviceKey && o.Number == _packetNumber)) break;
            if (attempts == 254) throw new InvalidOperationException("该终端等待确认的命令过多，请稍后再试。");
        }
        var message = request.Build((byte)_packetNumber, ++_ackSequence);
        var operation = new ControlOperation(request, (byte)_packetNumber, _ackSequence);
        Operations.Insert(0, operation);
        if (Operations.Count > 200 && !Operations.Last().CanMatchResponse) Operations.RemoveAt(Operations.Count - 1);
        AddDownlink(new(DateTimeOffset.UtcNow, operation.Target, request.Name, "下行发送", Services.MessageDisplay.Message(message), request.Kind == ControlKind.Wake ? null : request.DeviceKey, $"{request.Company}/sgw/{request.Gateway}"));
        try
        {
            await session.PublishAsync(message, _shutdown.Token);
            if (operation.Status == "正在发送…") operation.Set("MQTT 已确认接收，等待终端响应");
            Notify($"{request.Name}已发送，查看下行控制中的执行记录。");
        }
        catch (TimeoutException)
        {
            operation.Set("MQTT 服务器发布确认超时，继续等待终端响应；未自动重发");
            Notify(operation.Status);
        }
        catch (Exception error) { operation.Set($"发送未确认：{error.Message}；未自动重发", true); Notify(operation.Status); }
    }

    private void AddLog(LogRow row)
    {
        Logs.Insert(0, row);
        if (Logs.Count > 1000) Logs.RemoveAt(Logs.Count - 1);
        if (_selectedLog is null) { _selectedLog = row; Changed(nameof(SelectedLog)); Changed(nameof(LogDetail)); }
    }
    private void UpdateSelection()
    {
        ChartRecords = Device?.History.ToArray() ?? [];
        SampleRows = Device?.History.TakeLast(200).Reverse().Select(r => new SampleRow(r)).ToArray() ?? [];
        UpstreamView.Refresh(); DownstreamView.Refresh(); OperationsView.Refresh();
        if (_selectedLog is null || !UpstreamView.Contains(_selectedLog)) _selectedLog = UpstreamView.Cast<LogRow>().FirstOrDefault();
        if (_selectedDownlinkLog is null || !DownstreamView.Contains(_selectedDownlinkLog)) _selectedDownlinkLog = DownstreamView.Cast<LogRow>().FirstOrDefault();
    }

    private GatewayItem EnsureGateway(string key, string company, string id)
    {
        if (_gatewayMap.TryGetValue(key, out var gateway)) return gateway;
        if (_gatewayMap.Count >= 256) throw new InvalidDataException("网关超过 256 个，请缩小订阅范围。");
        gateway = new(key, company, id) { ListNumber = Gateways.Count + 1 }; _gatewayMap.Add(key, gateway); Gateways.Add(gateway);
        if (_selectedGateway is null) SelectedGateway = gateway;
        Changed(nameof(GatewayCount)); Changed(nameof(NoGatewaysVisibility));
        return gateway;
    }
    private DeviceItem EnsureDevice(MessageEnvelope envelope, GatewayItem gateway)
    {
        if (_deviceMap.TryGetValue(envelope.DeviceKey, out var item)) return item;
        if (_deviceMap.Count >= 256) throw new InvalidDataException("终端超过 256 台，请缩小订阅范围。");
        item = new(new(envelope.DeviceKey, envelope.DeviceLabel, envelope.Gateway)) { Route = envelope, GatewayKey = gateway.Key, ListNumber = gateway.DeviceCount + 1 };
        _deviceMap.Add(envelope.DeviceKey, item); _devices.Add(item);
        gateway.DeviceCount++; gateway.Update();
        if (_selectedDevice is null && SelectedGateway == gateway && DevicesView.Contains(item)) _selectedDevice = item;
        return item;
    }
    private void AddGatewayLog(GatewayItem gateway, LogRow row)
    {
        gateway.Messages.Insert(0, row);
        if (gateway.Messages.Count > 300) gateway.Messages.RemoveAt(300);
        if (SelectedGateway == gateway && _selectedGatewayLog is null) SelectedGatewayLog = row;
    }
    private static string TextField(GatewayPacket packet, string field) => packet.Body.TryGetProperty(field, out var value) ? value.ToString() : "—";
    private static string GatewayDetail(GatewayPacket packet, string transport) => $"网关 {IdentifierDisplay.Format(packet.Gateway)}\n{packet.Name}\n{packet.Summary}\n" + System.Text.Json.JsonSerializer.Serialize(packet.Body, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n\n" + transport;
    private void AddDownlink(LogRow row)
    {
        DownlinkLogs.Insert(0, row);
        if (DownlinkLogs.Count > 1000) DownlinkLogs.RemoveAt(1000);
        if (_selectedDownlinkLog is null && DownstreamView.Contains(row)) SelectedDownlinkLog = row;
    }
    private async Task AcknowledgeLoopAsync()
    {
        try
        {
            await foreach (var ack in _acks.Reader.ReadAllAsync(_shutdown.Token))
            {
                try { await ack.Session.PublishAsync(ack.Message, _shutdown.Token); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception error) { _notices.Enqueue($"回执发送失败：{error.Message}"); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        _shutdown.Cancel();
        await StopSessionAsync();
        await _ackWorker;
        _shutdown.Dispose();
    }
}
