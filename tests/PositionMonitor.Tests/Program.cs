using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using PositionMonitor.Core.Mqtt;
using PositionMonitor.Core.Protocol;
using PositionMonitor.Tests;

var tests = new List<(string Name, Func<Task> Run)>();
void Check(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void Async(string name, Func<Task> action) => tests.Add((name, action));
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"预期 {expected}，实际 {actual}"); }
static void True(bool value) { if (!value) throw new Exception("条件不满足"); }
static void Bad(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException or FormatException) { return; } throw new Exception("未拒绝非法输入"); }
static byte[] Hex(string value) => EnvelopeDecoder.Hex(value);
static Frame Decode(string value) => ProtocolDecoder.Decode(Hex(value));
const string reportHex = "00 03 02 07 A4 E6 E9 67 74 15 F0 06 6E F7 60 02 F4 01 00 08 E4 0C D2 04 C9 FD 0C 00";
const string registrationHex = "10 01 01 A4 E6 E9 67 74 15 F0 06 6E F7 60 02 F4 01 00 08 D2 04 C9 FD 0C 00 00 10 03 12 00 15 0A";
const string actualRegistrationHex = "100101000000000000008000000080000080FF00800080008000010001000080";
Check("电池字段区分尚未提供与无效上报", () =>
{
    True(!Decode(registrationHex).Sample!.HasBatteryMeasurement);
    True(Decode(reportHex).Sample!.HasBatteryMeasurement);
    var invalid = Hex(reportHex); invalid[20] = 255; invalid[21] = 255;
    var sample = ProtocolDecoder.Decode(invalid).Sample!;
    True(sample.HasBatteryMeasurement); True(sample.BatteryMillivolts is null);
});
Check("协议示例：倾角、坐标、电压、海拔", () =>
{
    var sample = Decode(reportHex).Sample!;
    Equal<double?>(12.34, sample.AngleX); Equal<double?>(-5.67, sample.AngleY); Equal<double?>(0.12, sample.AngleZ);
    Equal<double?>(116.397428, sample.Longitude); Equal<double?>(39.909230, sample.Latitude);
    Equal<double?>(50, sample.Altitude); Equal<double?>(0.8, sample.Hdop); Equal<ushort?>(3300, sample.BatteryMillivolts);
    True(sample.GnssValid && sample.SensorValid && sample.ClockValid);
});
Check("注册与版本号", () => { var frame = Decode(registrationHex); Equal("V1.2.3", frame.AppVersion); Equal("V1.0.0", frame.HardwareVersion); Equal("V1.5.0", frame.WiotaVersion); Equal<sbyte?>(10, frame.Snr); True(frame.NeedsAcknowledgment); });
Check("设备信息查询和响应", () => { Equal<Sample?>(null, Decode("10 02 01").Sample); True(Decode("08 02 01" + registrationHex[8..]).IsResponse); });
Check("注册与上报确认", () => { Equal<byte?>(0, Decode("08 01 01 00").Status); Equal<byte?>(6, Decode("08 03 02 06").Status); });
Check("查询与设置上报周期", () => { Decode("10 04 03"); Equal<uint?>(14400, Decode("08 04 03 40 38 00 00").Period); Equal<uint?>(2, Decode("08 05 04 02 00 00 00").Period); });
Check("重启响应状态", () => { Decode("10 06 05"); Equal<byte?>(5, Decode("08 06 05 05").Status); });
Check("组播地址与通信配置", () => { var frame = Decode("90 02 78 56 34 12 F0 DE BC 9A 07 06 0A 03 A0 A1 A2"); Equal(2, frame.Destinations.Length); Equal(0x12345678u, frame.Destinations[0]); Equal("160、161、162", frame.Fields["频点列表"]); });
Check("通信和量程配置响应", () => { Equal("10 dBm", Decode("08 07 06 00 0A 03 A0 A1 A2").Fields["发射功率"]); Equal("±8g", Decode("08 08 07 00 02").Fields["加速度计量程"]); Decode("10 08 07 02"); });
Check("24 位负海拔与边界", () => { Equal(-1, ProtocolDecoder.I24([255, 255, 255])); Equal(-8388608, ProtocolDecoder.I24([0, 0, 128])); var bytes = Hex(reportHex); bytes[16] = 0x0C; bytes[17] = 0xFE; bytes[18] = 0xFF; Equal<double?>(-50, ProtocolDecoder.Decode(bytes).Sample!.Altitude); });
Check("全部无效值不显示成零", () =>
{
    var bytes = Hex(reportHex);
    Array.Clear(bytes, 4, 4);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), int.MinValue); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), int.MinValue);
    bytes[16] = 0; bytes[17] = 0; bytes[18] = 128; bytes[19] = 255;
    bytes[20] = 255; bytes[21] = 255;
    for (var i = 22; i < 28; i += 2) { bytes[i] = 0; bytes[i + 1] = 128; }
    var s = ProtocolDecoder.Decode(bytes).Sample!;
    True(s.Utc is null && s.Longitude is null && s.Latitude is null && s.Altitude is null && s.Hdop is null && s.BatteryMillivolts is null && s.AngleX is null && s.AngleY is null && s.AngleZ is null);
});
Check("Flags 关闭时数据置为无效，电压独立", () => { var bytes = Hex(reportHex); bytes[3] = 0; var s = ProtocolDecoder.Decode(bytes).Sample!; True(s.Utc is null && s.Longitude is null && s.AngleX is null); Equal<ushort?>(3300, s.BatteryMillivolts); });
Check("越界经纬度与角度置为无效", () => { var bytes = Hex(reportHex); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 180000001); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 9001); var s = ProtocolDecoder.Decode(bytes).Sample!; True(s.Longitude is null && s.AngleX is null && !s.GnssValid && !s.SensorValid); });
foreach (var value in new[] { "", "00 01", "00 00 02", "02 01 03", "18 01 03", "10 01 03 00", "08 01 03 01 00 00 00", "80 00 01 03", "90 01 00 00 00 00 01 03", "90 02 01 00 00 00 01 00 00 00 01 03", "00 01 FF", "08 01 07 00 04", "08 01 05 07", "10 01 06 17 01 A0", "10 01 06 0A 02 A0 A0", "10 01 06 0A 01 C9", "10 01 06 0A 00" })
    Check($"拒绝非法帧 {value}", () => Bad(() => Decode(value)));
Check("拒绝上报保留位和错误长度", () => { var bytes = Hex(reportHex); bytes[3] = 8; Bad(() => ProtocolDecoder.Decode(bytes)); Bad(() => ProtocolDecoder.Decode(Hex(reportHex)[..^1])); });
Check("Hex 格式校验", () => { Equal(3, Hex("00\n03\t02").Length); Bad(() => Hex("123")); Bad(() => Hex("GG")); });
static MqttApplicationMessage JsonMessage(string data, string format = "hex", bool retain = false, string topic = "ucchip/up/sgw/1001/7") => new(topic, JsonSerializer.SerializeToUtf8Bytes(new { cmd = 100, src = 0x12345678u, fmt = format, data }), 1, retain);
static MqttApplicationMessage AsArray(params MqttApplicationMessage[] messages) => messages[0] with { Payload = Encoding.UTF8.GetBytes("[" + string.Join(",", messages.Select(m => Encoding.UTF8.GetString(m.Payload.Span))) + "]") };
static MqttApplicationMessage ActualRegistration() => new("ucchip/up/sgw/704154/28951", JsonSerializer.SerializeToUtf8Bytes(new[] { new { cmd = 100, ver = "v2.0", src = 51877, fmt = "hex", data = actualRegistrationHex } }), 1);
Check("现场数组注册：无效测量仍解析版本及回执", () =>
{
    var original = ActualRegistration(); var messages = GatewayPayload.Split(original); Equal(1, messages.Count);
    var envelope = EnvelopeDecoder.Decode(messages[0])!; var frame = ProtocolDecoder.Decode(envelope.Bytes);
    Equal("704154", envelope.Gateway); Equal<uint?>(51877, envelope.Source); Equal("设备注册", frame.Name);
    Equal("V0.1.0", frame.HardwareVersion); Equal("V0.1.0", frame.AppVersion); Equal("V0.0.0", frame.WiotaVersion); Equal<sbyte?>(null, frame.Snr);
    True(frame.Sample is { Utc: null, Longitude: null, Latitude: null, Altitude: null, Hdop: null, AngleX: null, AngleY: null, AngleZ: null });
    using var reply = JsonDocument.Parse(envelope.Acknowledgment(frame, 28952)!.Payload);
    Equal("08010100", reply.RootElement.GetProperty("uc").GetString()); Equal(51877u, reply.RootElement.GetProperty("dst").GetUInt32());
    Equal("ucchip/down/sgw/704154/28952", envelope.Acknowledgment(frame, 28952)!.Topic);
});
Check("数组批量分流并保留主题 QoS 与保留标志", () =>
{
    var registration = JsonMessage(registrationHex, retain: true);
    var messages = GatewayPayload.Split(AsArray(registration, registration with { Payload = Encoding.UTF8.GetBytes("{\"cmd\":1,\"src\":1001}") }));
    Equal(2, messages.Count); True(messages.All(m => m.Topic == registration.Topic && m.QualityOfService == 1 && m.Retain));
    Equal("设备注册", ProtocolDecoder.Decode(EnvelopeDecoder.Decode(messages[0])!.Bytes).Name);
    True(GatewayNotice.Decode(messages[1])!.Heartbeat);
});
Check("单对象和原始二进制不改变载荷", () =>
{
    foreach (var message in new[] { JsonMessage(reportHex), new MqttApplicationMessage("device/up", Hex(reportHex)) })
        True(ReferenceEquals(message, GatewayPayload.Split(message).Single()));
});
Check("空数组无事件，拒绝非对象数组与错误 JSON", () =>
{
    Equal(0, GatewayPayload.Split(new("topic", Encoding.UTF8.GetBytes("[]"))).Count);
    foreach (var text in new[] { "[null]", "[1]", "[\"data\"]", "[{} , []]" }) Bad(() => GatewayPayload.Split(new("topic", Encoding.UTF8.GetBytes(text))));
    try { GatewayPayload.Split(new("topic", Encoding.UTF8.GetBytes("[{"))); throw new Exception("错误 JSON 未拒绝"); }
    catch (JsonException) { }
});
Check("限制数组项数与总载荷", () =>
{
    Equal(256, GatewayPayload.Split(new("topic", Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("{}", 256)) + "]"))).Count);
    Bad(() => GatewayPayload.Split(new("topic", Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("{}", 257)) + "]"))));
    Bad(() => GatewayPayload.Split(new("topic", new byte[65537])));
});
Check("解析异常网关归属不使用 MQTT 消息序号", () =>
{
    Equal("ucchip/sgw/704154", GatewayPayload.GatewayKey("ucchip/up/sgw/704154/28951"));
    Equal("company/branch/sgw/1", GatewayPayload.GatewayKey("company/branch/up/sgw/1/7"));
    True(GatewayPayload.GatewayKey("device/up") is null && GatewayPayload.GatewayKey("ucchip/up/sgw/0/7") is null);
});
Check("JSON Hex 与 Base64 解包", () => { var a = EnvelopeDecoder.Decode(JsonMessage(reportHex))!; var b = EnvelopeDecoder.Decode(JsonMessage(Convert.ToBase64String(Hex(reportHex)), "base64"))!; Equal(Convert.ToHexString(a.Bytes), Convert.ToHexString(b.Bytes)); Equal("1001", a.Gateway); Equal("0x12345678", a.DeviceLabel); });
Check("数值 ID 十进制显示与十六进制提示不改写路由", () =>
{
    Equal("1", IdentifierDisplay.Format(1)); Equal("1（0x1）", IdentifierDisplay.Tooltip(1));
    Equal("704154", IdentifierDisplay.Format("704154")); Equal("704154（0xABE9A）", IdentifierDisplay.Tooltip("704154"));
    Equal("4294967295", IdentifierDisplay.Format(uint.MaxValue)); Equal("4294967295（0xFFFFFFFF）", IdentifierDisplay.Tooltip(uint.MaxValue));
    Equal("+", IdentifierDisplay.Format("+")); Equal("直接透传", IdentifierDisplay.Format("直接透传"));
    var online = GatewayPacket.Decode(new("ucchip/up/sgw/704154/1", Encoding.UTF8.GetBytes("{\"cmd\":201,\"dev_id\":51833,\"status\":1}")))!;
    True(online.Summary.Contains("51833") && !online.Summary.Contains("0x"));
    True(online.IdentifierTooltip.Contains("51833（0xCA79）") && online.IdentifierTooltip.Contains("704154（0xABE9A）")); Equal("ucchip/sgw/704154", online.Key);
});
Check("来源按网关隔离，独立序号不改变设备归属", () => { var a = EnvelopeDecoder.Decode(JsonMessage(reportHex))!; var b = EnvelopeDecoder.Decode(JsonMessage(reportHex, topic: "ucchip/up/sgw/1001/8"))!; var c = EnvelopeDecoder.Decode(JsonMessage(reportHex, topic: "ucchip/up/sgw/1002/7"))!; Equal(a.DeviceKey, b.DeviceKey); True(a.DeviceKey != c.DeviceKey); });
Check("直接透传 Hex 与二进制", () => { var a = EnvelopeDecoder.Decode(new("terminal/1", Hex(reportHex)))!; var b = EnvelopeDecoder.Decode(new("terminal/1", Encoding.UTF8.GetBytes(reportHex)))!; Equal(Convert.ToHexString(a.Bytes), Convert.ToHexString(b.Bytes)); True(a.Source is null); });
Check("其他网关命令忽略", () => True(EnvelopeDecoder.Decode(new("ucchip/up/sgw/1", Encoding.UTF8.GetBytes("{\"cmd\":200}"))) is null));
Check("拒绝无法识别来源及非法格式", () => { foreach (var json in new[] { "{\"cmd\":100,\"fmt\":\"hex\",\"data\":\"00\"}", "{\"cmd\":100,\"src\":0,\"fmt\":\"hex\",\"data\":\"00\"}", "{\"cmd\":100,\"src\":1,\"fmt\":\"string\",\"data\":\"00\"}" }) Bad(() => EnvelopeDecoder.Decode(new("topic", Encoding.UTF8.GetBytes(json)))); });
Check("回执路由与包序号一致", () => { var envelope = EnvelopeDecoder.Decode(JsonMessage(registrationHex))!; var reply = envelope.Acknowledgment(Decode(registrationHex), 10)!; Equal("ucchip/down/sgw/1001/10", reply.Topic); using var json = JsonDocument.Parse(reply.Payload); Equal("08010100", json.RootElement.GetProperty("uc").GetString()); Equal(0x12345678u, json.RootElement.GetProperty("dst").GetUInt32()); });
Check("保留消息和不要求确认的帧不回复", () => { True(EnvelopeDecoder.Decode(JsonMessage(registrationHex, retain: true))!.Acknowledgment(Decode(registrationHex), 1) is null); True(EnvelopeDecoder.Decode(JsonMessage(reportHex))!.Acknowledgment(Decode(reportHex), 1) is null); });
Check("直接透传需明确回执主题", () => { var envelope = EnvelopeDecoder.Decode(new("device/1/up", Hex(registrationHex)))!; True(envelope.Acknowledgment(Decode(registrationHex), 1) is null); Equal("08010100", Convert.ToHexString(envelope.Acknowledgment(Decode(registrationHex), 1, "device/1/down")!.Payload.Span)); });
Check("重复数据去重，过期后可再次记录", () => { var e = EnvelopeDecoder.Decode(JsonMessage(reportHex))!; var state = new DeviceState(e.DeviceKey, e.DeviceLabel, e.Gateway); var frame = Decode(reportHex); var now = DateTimeOffset.UtcNow; True(state.Apply(frame, e, now)); True(!state.Apply(frame, e, now.AddSeconds(1))); Equal(1, state.History.Count); True(state.Apply(frame, e, now.AddMinutes(6))); Equal(2, state.History.Count); });
Check("迟到采样不会覆盖最新采样", () => { var e = EnvelopeDecoder.Decode(JsonMessage(reportHex))!; var state = new DeviceState(e.DeviceKey, e.DeviceLabel, e.Gateway); var now = DateTimeOffset.UtcNow; state.Apply(Decode(reportHex), e, now); var bytes = Hex(reportHex); bytes[1] = 4; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1); var older = e with { Bytes = bytes }; state.Apply(ProtocolDecoder.Decode(bytes), older, now); Equal<byte>(3, state.Latest!.Number); Equal(2, state.History.Count); });
Check("未知周期不误报离线，已知周期才判断超时", () => { var e = EnvelopeDecoder.Decode(JsonMessage(reportHex))!; var state = new DeviceState(e.DeviceKey, e.DeviceLabel, e.Gateway); var now = DateTimeOffset.UtcNow; state.Apply(Decode(reportHex), e, now); True(state.Freshness(now.AddHours(5)) != "上报超时"); var bytes = Hex("08 01 03 40 38 00 00"); state.Apply(ProtocolDecoder.Decode(bytes), e with { Bytes = bytes }, now); Equal("上报超时", state.Freshness(now.AddHours(9))); });
Check("主题通配符校验", () => { Mqtt311Client.ValidateTopic("ucchip/up/sgw/+/#"); foreach (var topic in new[] { "a/#/b", "a+", "a/b#", "a/\0" }) Bad(() => Mqtt311Client.ValidateTopic(topic)); });
Check("多主题逐行解析、空行、去重与大小写", () =>
{
    var topics = SubscriptionFilters.Parse("  ucchip/up/sgw/1001/# \r\n\nucchip/up/sgw/1002/#\r\nucchip/up/sgw/1001/#\nUCCHIP/up/sgw/1001/#");
    Equal(3, topics.Length); Equal("ucchip/up/sgw/1001/#", topics[0]); Equal("ucchip/up/sgw/1002/#", topics[1]); Equal("UCCHIP/up/sgw/1001/#", topics[2]);
});
Check("多主题任一项非法、空配置或数量超限均拒绝", () =>
{
    Bad(() => SubscriptionFilters.Parse("\r\n  "));
    Bad(() => SubscriptionFilters.Parse("valid/#\na/#/invalid"));
    Equal(64, SubscriptionFilters.Normalize(Enumerable.Range(1, 64).Select(i => $"test/{i}/#")).Length);
    Bad(() => SubscriptionFilters.Normalize(Enumerable.Range(1, 65).Select(i => $"test/{i}/#")));
});
Check("所有截断长度均安全拒绝", () => { var bytes = Hex(reportHex); for (var i = 0; i < bytes.Length; i++) { var truncated = bytes[..i]; Bad(() => ProtocolDecoder.Decode(truncated)); } });
Check("缓存上限与序号回绕", () =>
{
    var e = EnvelopeDecoder.Decode(JsonMessage(reportHex))!; var state = new DeviceState(e.DeviceKey, e.DeviceLabel, e.Gateway);
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < 2100; i++)
    {
        var bytes = Hex(reportHex); bytes[1] = (byte)(i % 255 + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(1700000000 + i));
        state.Apply(ProtocolDecoder.Decode(bytes), e with { Bytes = bytes }, now.AddSeconds(i * 2));
    }
    Equal(2000, state.History.Count); Equal(2100L, state.Reports);
});
Check("下行周期小端与单播 uc 字段", () =>
{
    var request = new ControlRequest("ucchip", 1001, 0x12345678, ControlKind.SetPeriod, Period: 60);
    Equal("1009043C000000", Convert.ToHexString(request.Frame(9)));
    var message = request.Build(9, 77); Equal("ucchip/down/sgw/1001/77", message.Topic);
    using var json = JsonDocument.Parse(message.Payload);
    Equal("1009043C000000", json.RootElement.GetProperty("uc").GetString());
    Equal(0x12345678u, json.RootElement.GetProperty("dst").GetUInt32()); True(!message.Retain);
});
Check("所有终端控制帧均能按协议解析", () =>
{
    foreach (var kind in Enum.GetValues<ControlKind>().Where(k => k != ControlKind.Wake))
    {
        var request = new ControlRequest("ucchip", 1001, 3, kind, Frequencies: [160, 161], Range: 2);
        var frame = ProtocolDecoder.Decode(request.Frame(255)); True(!frame.IsResponse); Equal(request.Command, frame.Command); Equal((byte)255, frame.Number);
    }
});
Check("拒绝广播控制和不确定路由", () =>
{
    var request = new ControlRequest("ucchip", 1001, 3, ControlKind.Reboot);
    foreach (var invalid in new[] { request with { Target = 0 }, request with { Target = uint.MaxValue }, request with { Gateway = 0 }, request with { Company = "a/#" } }) Bad(() => invalid.Build(1, 1));
    Bad(() => request.Build(0, 1)); Bad(() => request.Build(1, 0));
});
Check("控制参数边界与非法重复频点", () =>
{
    var request = new ControlRequest("ucchip", 1001, 3, ControlKind.SetPeriod);
    foreach (var period in new uint[] { 2, 259200 }) (request with { Period = period }).Build(1, 1);
    foreach (var period in new uint[] { 0, 1, 259201 }) Bad(() => (request with { Period = period }).Build(1, 1));
    Bad(() => (request with { Kind = ControlKind.SetRadio, Frequencies = [160, 160] }).Build(1, 1));
    Bad(() => (request with { Kind = ControlKind.SetRadio, Frequencies = [201] }).Build(1, 1));
    Bad(() => (request with { Kind = ControlKind.SetRadio, Power = 23, Frequencies = [160] }).Build(1, 1));
    Bad(() => (request with { Kind = ControlKind.SetRange, Range = 4 }).Build(1, 1));
});
Check("独立唤醒与先唤醒后单播的不同封装", () =>
{
    var wake = new ControlRequest("ucchip", 1001, 0, ControlKind.Wake, Paging: new());
    using var json = JsonDocument.Parse(wake.Build(1, 5).Payload);
    Equal(2, json.RootElement.GetProperty("cmd").GetInt32()); Equal(0u, json.RootElement.GetProperty("dst").GetUInt32());
    Equal(2, json.RootElement.GetProperty("paging").GetProperty("sym_len").GetInt32()); Equal(1, json.RootElement.GetProperty("paging").GetProperty("tx_mode").GetInt32());
    using var data = JsonDocument.Parse((wake with { Kind = ControlKind.Reboot, Target = 3 }).Build(8, 6).Payload);
    Equal("100805", data.RootElement.GetProperty("data").GetProperty("uc").GetString()); Equal(3u, data.RootElement.GetProperty("dst").GetUInt32());
});
Check("唤醒参数依据当前固件约束", () =>
{
    new PagingOptions(1, 0, 2500).Validate(); new PagingOptions(200, 16383, 4294967294).Validate();
    Bad(() => new PagingOptions(0).Validate()); Bad(() => new PagingOptions(201).Validate());
    Bad(() => new PagingOptions(AwakenId: 16384).Validate()); Bad(() => new PagingOptions(SendTime: 2499).Validate());
});
Check("控制结果按实际周期和状态判断", () =>
{
    var request = new ControlRequest("ucchip", 1001, 3, ControlKind.SetPeriod, Period: 60);
    True(request.Result(Decode("08 01 04 02 00 00 00")).Contains("未按请求生效"));
    True(request.Result(Decode("08 01 04 3C 00 00 00")).Contains("已设为 60"));
    True((request with { Kind = ControlKind.Reboot }).Result(Decode("08 01 05 06")).Contains("设备忙"));
});
Check("心跳来源与主题必须一致，发送反馈不能当业务数据", () =>
{
    var heartbeat = new MqttApplicationMessage("ucchip/up/sgw/1001/7", Encoding.UTF8.GetBytes("{\"cmd\":1,\"src\":1001}"));
    True(GatewayNotice.Decode(heartbeat)?.Heartbeat == true);
    True(GatewayNotice.Decode(heartbeat with { Topic = "ucchip/up/sgw/1002/7" }) is null);
    var feedback = heartbeat with { Payload = Encoding.UTF8.GetBytes("{\"cmd\":100,\"src\":1001,\"dst\":3,\"uc\":{\"res\":\"suc\"}}") };
    Equal("suc", GatewayNotice.Decode(feedback)!.Result); True(EnvelopeDecoder.Decode(feedback) is null);
    var stringSource = feedback with { Payload = Encoding.UTF8.GetBytes("{\"cmd\":100,\"src\":\"0x12345678\",\"fmt\":\"hex\",\"data\":\"100103\"}") };
    True(GatewayNotice.Decode(stringSource) is null); Equal(0x12345678u, EnvelopeDecoder.Decode(stringSource)!.Source!.Value);
});
static MqttClientOptions Options(TestBroker broker, string? user = null, string? password = null, ushort keepalive = 30) => new("127.0.0.1", broker.Port, Guid.NewGuid().ToString("N"), UserName: user, Password: password, KeepAliveSeconds: keepalive);
Check("网关启动消息无需应用帧即可发现网关", () =>
{
    var packet = GatewayPacket.Decode(new("ucchip/up/sgw/1002/1", Encoding.UTF8.GetBytes("{\"cmd\":0,\"src\":1002,\"hw_ver\":\"V1.0\",\"sw_ver\":\"1\"}")))!;
    Equal("ucchip/sgw/1002", packet.Key); Equal("网关启动", packet.Name); True(packet.Terminal is null);
    Equal("V1.0", packet.Body.GetProperty("hw_ver").GetString());
});
Check("在线事件从 dev_id 识别所属终端", () =>
{
    var packet = GatewayPacket.Decode(new("ucchip/up/sgw/1001/5", Encoding.UTF8.GetBytes("{\"cmd\":201,\"dev_id\":305419896,\"status\":1}")))!;
    Equal(0x12345678u, packet.Terminal!.Value); Equal(1u, packet.OnlineStatus!.Value); True(packet.Summary.Contains("在线"));
});
Check("链路状态从 src 识别终端且不冒充应用注册", () =>
{
    var packet = GatewayPacket.Decode(new("ucchip/up/sgw/1002/6", Encoding.UTF8.GetBytes("{\"cmd\":20,\"src\":2864434397,\"freq\":160,\"snr\":18}")))!;
    Equal(0xAABBCCDDu, packet.Terminal!.Value); Equal("终端链路状态", packet.Name); True(packet.OnlineStatus is null);
});
Check("公司与网关构成唯一分组身份", () =>
{
    var data = Encoding.UTF8.GetBytes("{\"cmd\":201,\"dev_id\":3,\"status\":0}");
    var a = GatewayPacket.Decode(new("a/up/sgw/1001/1", data))!;
    var b = GatewayPacket.Decode(new("a/up/sgw/1002/1", data))!;
    var c = GatewayPacket.Decode(new("b/up/sgw/1001/1", data))!;
    True(a.Key != b.Key && a.Key != c.Key); True(a.Summary.Contains("离线"));
});
Check("网关消息拒绝保留地址，未知命令保留内容", () =>
{
    var data = Encoding.UTF8.GetBytes("{\"cmd\":999,\"value\":42}");
    True(GatewayPacket.Decode(new("a/up/sgw/0/1", data)) is null);
    True(GatewayPacket.Decode(new("a/up/sgw/+/1", data)) is null);
    True(GatewayPacket.Decode(new("a/down/sgw/1001/1", data)) is null);
    Equal(42, GatewayPacket.Decode(new("a/up/sgw/1001/1", data))!.Body.GetProperty("value").GetInt32());
});
Async("真实 TCP MQTT：认证、订阅、QoS1 收发与取消订阅", async () =>
{
    await using var broker = new TestBroker { UserName = "测试用户", Password = "测试密码" };
    await using var subscriber = new Mqtt311Client(); await using var publisher = new Mqtt311Client();
    var arrived = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    subscriber.MessageReceived += (_, m) => arrived.TrySetResult(m);
    await subscriber.ConnectAsync(Options(broker, "测试用户", "测试密码"));
    await subscriber.SubscribeAsync("ucchip/up/sgw/+/#", 1);
    await publisher.ConnectAsync(Options(broker, "测试用户", "测试密码"));
    await publisher.PublishAsync(JsonMessage(reportHex));
    var message = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Equal<double?>(12.34, ProtocolDecoder.Decode(EnvelopeDecoder.Decode(message)!.Bytes).Sample!.AngleX);
    await subscriber.UnsubscribeAsync("ucchip/up/sgw/+/#"); Equal(0, broker.Subscriptions);
});
Async("MQTT 拒绝错误账号和订阅权限", async () =>
{
    await using var broker = new TestBroker { UserName = "user", Password = "pass" };
    await using var client = new Mqtt311Client();
    try { await client.ConnectAsync(Options(broker, "wrong", "wrong")); throw new Exception("错误账号被接受"); } catch (InvalidOperationException) { }
    await client.ConnectAsync(Options(broker, "user", "pass"));
    try { await client.SubscribeAsync("denied/#"); throw new Exception("订阅拒绝未报告"); } catch (InvalidOperationException) { }
});
Async("未收到 SUBACK 时可取消且不误报订阅成功", async () =>
{
    await using var broker = new TestBroker { SubscribeResponses = false };
    await using var client = new Mqtt311Client(); await client.ConnectAsync(Options(broker));
    using var cancel = new CancellationTokenSource(250);
    try { await client.SubscribeAsync("test/#", cancellationToken: cancel.Token); throw new Exception("未等待订阅确认"); } catch (OperationCanceledException) { }
});
Async("会话断线重连后恢复订阅", async () =>
{
    await using var broker = new TestBroker(); await using var session = new MqttSession();
    var connected = 0; var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.StateChanged += (_, state) => { if (state == "已连接并订阅" && Interlocked.Increment(ref connected) == 2) reconnected.TrySetResult(); };
    session.Start(Options(broker), "test/#", 1);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!session.IsSubscribed) await Task.Delay(20, timeout.Token);
    broker.DropClients(); await reconnected.Task.WaitAsync(timeout.Token);
    True(session.IsSubscribed); Equal(1, broker.Subscriptions);
});
Async("MQTT 心跳响应超时通知", async () =>
{
    await using var broker = new TestBroker { PingResponses = false }; await using var client = new Mqtt311Client();
    var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously); client.ConnectionFaulted += (_, e) => fault.TrySetResult(e);
    await client.ConnectAsync(Options(broker, keepalive: 1));
    True(await fault.Task.WaitAsync(TimeSpan.FromSeconds(5)) is TimeoutException);
});
Async("多主题会话同时接收两个网关，重连后恢复全部订阅", async () =>
{
    await using var broker = new TestBroker(); await using var session = new MqttSession();
    var connected = 0; var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
    session.StateChanged += (_, state) => { if (state == "已连接并订阅" && Interlocked.Increment(ref connected) == 2) reconnected.TrySetResult(); };
    session.MessageReceived += (_, m) => received.Enqueue(m.Topic);
    session.Start(Options(broker), new[] { "ucchip/up/sgw/1001/#", "ucchip/up/sgw/1002/#", "ucchip/up/sgw/1001/#" }, 1);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!session.IsSubscribed) await Task.Delay(20, timeout.Token);
    Equal(2, broker.Subscriptions);
    async Task ReceiveBoth()
    {
        received.Clear();
        await broker.BroadcastAsync("ucchip/up/sgw/1001/1", Encoding.UTF8.GetBytes("first"));
        await broker.BroadcastAsync("ucchip/up/sgw/1002/1", Encoding.UTF8.GetBytes("second"));
        await broker.BroadcastAsync("ucchip/up/sgw/1003/1", Encoding.UTF8.GetBytes("excluded"));
        while (received.Count < 2) await Task.Delay(20, timeout.Token);
        await Task.Delay(100, timeout.Token);
        Equal(2, received.Count); True(received.Contains("ucchip/up/sgw/1001/1")); True(received.Contains("ucchip/up/sgw/1002/1"));
    }
    await ReceiveBoth();
    broker.DropClients(); await reconnected.Task.WaitAsync(timeout.Token);
    True(session.IsSubscribed); Equal(2, broker.Subscriptions); await ReceiveBoth();
});
Async("多主题部分订阅被拒绝时不误报就绪并清理连接", async () =>
{
    await using var broker = new TestBroker(); await using var session = new MqttSession();
    var error = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ready = false;
    session.StateChanged += (_, state) => { if (state == "已连接并订阅") ready = true; else if (state.Contains("连接未就绪")) error.TrySetResult(state); };
    session.Start(Options(broker), new[] { "valid/#", "denied/#" }, 1);
    var state = await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
    True(state.Contains("denied/#")); True(!session.IsSubscribed && !ready);
    using var timeout = new CancellationTokenSource(800);
    while (broker.Subscriptions != 0) await Task.Delay(20, timeout.Token);
});
Async("TLS 加密握手、订阅以及默认拒绝未受信任证书", async () =>
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    // Windows Schannel 服务端需要导入后带有可用私钥句柄的测试证书；不加入信任存储。
    using var cert = new X509Certificate2(generated.Export(X509ContentType.Pfx));
    await using var broker = new TestBroker { Certificate = cert };
    await using var rejected = new Mqtt311Client();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    try { await rejected.ConnectAsync(Options(broker) with { UseTls = true }, timeout.Token); throw new Exception("自签名证书被默认接受"); }
    catch (AuthenticationException) { }
    catch (IOException error) { throw new Exception("TLS 服务端诊断：" + string.Join("\n", broker.Errors.Select(e => e.ToString())), error); }
    // 仅测试夹具绕过自签名证书；实际界面始终验证证书。
    await using var accepted = new Mqtt311Client();
    await accepted.ConnectAsync(Options(broker) with { UseTls = true, AcceptAnyServerCertificate = true }, timeout.Token);
    await accepted.SubscribeAsync("test/#", cancellationToken: timeout.Token);
    True(accepted.IsConnected);
});
Async("MQTT 应用注册确认端到端收发", async () =>
{
    await using var broker = new TestBroker(); await using var receiver = new Mqtt311Client(); await using var device = new Mqtt311Client();
    await receiver.ConnectAsync(Options(broker)); await receiver.SubscribeAsync("ucchip/up/sgw/+/#");
    await device.ConnectAsync(Options(broker)); await device.SubscribeAsync("ucchip/down/sgw/1001/#");
    var incoming = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var acknowledgment = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    receiver.MessageReceived += (_, m) => incoming.TrySetResult(m); device.MessageReceived += (_, m) => acknowledgment.TrySetResult(m);
    await device.PublishAsync(JsonMessage(registrationHex)); var message = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var envelope = EnvelopeDecoder.Decode(message)!; await receiver.PublishAsync(envelope.Acknowledgment(ProtocolDecoder.Decode(envelope.Bytes), 1)!);
    var reply = await acknowledgment.Task.WaitAsync(TimeSpan.FromSeconds(3));
    using var json = JsonDocument.Parse(reply.Payload); Equal("08010100", json.RootElement.GetProperty("uc").GetString());
});

var appDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/PositionMonitor.App/bin/Release/net8.0-windows/PositionMonitor.dll"));
if (File.Exists(appDll)) Async("桌面程序：MQTT 数据、心跳过滤、下行确认、布局与缓存隔离", async () =>
{
    var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/桌面联调.json"));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    if (File.Exists(output)) File.Delete(output);
    var errorsReady = Path.ChangeExtension(output, ".异常可见.json");
    if (File.Exists(errorsReady)) File.Delete(errorsReady);
    await using var broker = new TestBroker();
    broker.SuppressPublishAcknowledgment = (topic, payload) =>
    {
        if (!topic.StartsWith("ucchip/down/sgw/1001/", StringComparison.Ordinal)) return false;
        using var json = JsonDocument.Parse(payload);
        if (!json.RootElement.TryGetProperty("uc", out var uc) || uc.ValueKind != JsonValueKind.String) return false;
        var bytes = Hex(uc.GetString()!);
        return bytes[0] == 0x10 && bytes[2] is 1 or 3;
    };
    await using var device = new Mqtt311Client();
    await device.ConnectAsync(Options(broker)); await device.SubscribeAsync("ucchip/down/sgw/1001/#"); await device.SubscribeAsync("ucchip/down/sgw/704154/#");
    var ack = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var actualAck = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    device.MessageReceived += async (_, m) =>
    {
        try
        {
            using var json = JsonDocument.Parse(m.Payload);
            var root = json.RootElement;
            if (root.GetProperty("cmd").GetInt32() == 2)
            {
                await device.PublishAsync(new(m.Topic.Replace("/down/", "/up/"), Encoding.UTF8.GetBytes("{\"cmd\":2,\"src\":1001,\"paging\":{\"res\":\"suc\"}}"), 1));
                return;
            }
            var bytes = Hex(root.GetProperty("uc").GetString()!);
            if (bytes[0] == 8) { if (root.GetProperty("dst").GetUInt32() == 51877) actualAck.TrySetResult(m); else ack.TrySetResult(m); return; }
            // 夹具仅监听回环 MQTT，模拟终端响应，不操作远程设备。
            await device.PublishAsync(new(m.Topic.Replace("/down/", "/up/"), Encoding.UTF8.GetBytes("{\"cmd\":100,\"src\":1001,\"dst\":305419896,\"uc\":{\"res\":\"suc\"}}"), 1));
            var response = bytes[2] is 3 or 4 ? new byte[] { 8, bytes[1], bytes[2], 60, 0, 0, 0 } : new byte[] { 8, bytes[1], 5, 6 };
            if (bytes[2] == 3) await Task.Delay(9500);
            if (bytes[2] == 1)
            {
                response = Hex(registrationHex);
                response[0] = 8; response[1] = bytes[1]; response[27] = 4;
            }
            if (bytes[2] == 4)
            {
                await device.PublishAsync(JsonMessage(Convert.ToHexString(response), retain: true));
                var unrelated = (byte[])response.Clone(); unrelated[1] = 255;
                await device.PublishAsync(JsonMessage(Convert.ToHexString(unrelated)));
                await Task.Delay(100);
            }
            await device.PublishAsync(AsArray(JsonMessage(Convert.ToHexString(response))));
        }
        catch (Exception error) { ack.TrySetException(error); }
    };
    var start = new ProcessStartInfo("D:\\Program Files\\dotnet\\dotnet.exe") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(appDll); start.ArgumentList.Add("--verify-mqtt"); start.ArgumentList.Add(broker.Port.ToString()); start.ArgumentList.Add(output);
    using var process = Process.Start(start)!;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (broker.Subscriptions < 5) await Task.Delay(20, timeout.Token);
        for (var i = 0; i < 3; i++) await device.PublishAsync(new("ucchip/up/sgw/1001/0", Encoding.UTF8.GetBytes("{\"cmd\":1,\"src\":1001}"), 1));
        // 无效透传只记解析异常，不能生成零设备的虚假网关分组。
        await device.PublishAsync(new("ucchip/up/sgw/1001/invalid-frame", Encoding.UTF8.GetBytes("0000"), 1));
        await device.PublishAsync(new("ucchip/up/sgw/1001/invalid-hex", Encoding.UTF8.GetBytes("ABC"), 1));
        while (!File.Exists(errorsReady)) await Task.Delay(20, timeout.Token);
        await device.PublishAsync(ActualRegistration());
        await device.PublishAsync(new("ucchip/up/sgw/1001/0", Encoding.UTF8.GetBytes("{\"cmd\":1,\"src\":1001}"), 1, true));
        await device.PublishAsync(new("ucchip/up/sgw/1001/0", Encoding.UTF8.GetBytes("{\"cmd\":1,\"src\":9999}"), 1));
        // 批量项中一帧无效，后续注册和采样仍应继续处理。
        await device.PublishAsync(AsArray(JsonMessage("10010100"), JsonMessage(registrationHex), JsonMessage(reportHex)));
        await device.PublishAsync(new("ucchip/up/sgw/1002/1", Encoding.UTF8.GetBytes("{\"cmd\":0,\"src\":1002,\"hw_ver\":\"V1.0\",\"sw_ver\":\"1\"}"), 1));
        await device.PublishAsync(AsArray(new MqttApplicationMessage("ucchip/up/sgw/1002/2", Encoding.UTF8.GetBytes("{\"cmd\":1,\"src\":1002}"), 1)));
        await device.PublishAsync(new("ucchip/up/sgw/1002/3", Encoding.UTF8.GetBytes("{\"cmd\":201,\"dev_id\":305419896,\"status\":1}"), 1));
        await device.PublishAsync(new("ucchip/up/sgw/1002/4", Encoding.UTF8.GetBytes("{\"cmd\":20,\"src\":2864434397,\"freq\":160,\"rssi\":24}"), 1));
        var otherReport = Hex(reportHex); BinaryPrimitives.WriteInt16LittleEndian(otherReport.AsSpan(22), 5555);
        await device.PublishAsync(AsArray(JsonMessage(registrationHex, topic: "ucchip/up/sgw/1002/5"), JsonMessage(Convert.ToHexString(otherReport), topic: "ucchip/up/sgw/1002/6")));
        var reply = await ack.Task.WaitAsync(timeout.Token);
        using var ackJson = JsonDocument.Parse(reply.Payload); Equal("08010100", ackJson.RootElement.GetProperty("uc").GetString());
        var actualReply = await actualAck.Task.WaitAsync(timeout.Token);
        True(actualReply.Topic.StartsWith("ucchip/down/sgw/704154/", StringComparison.Ordinal));
        using var actualAckJson = JsonDocument.Parse(actualReply.Payload); Equal("08010100", actualAckJson.RootElement.GetProperty("uc").GetString()); Equal(51877u, actualAckJson.RootElement.GetProperty("dst").GetUInt32());
        await process.WaitForExitAsync(timeout.Token);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        if (!result.RootElement.GetProperty("Success").GetBoolean()) throw new Exception(result.RootElement.ToString());
        Equal(4, result.RootElement.GetProperty("Devices").GetInt32()); Equal(3, result.RootElement.GetProperty("Gateways").GetInt32());
        Equal(4, result.RootElement.GetProperty("Heartbeats").GetInt32()); Equal(5, result.RootElement.GetProperty("Commands").GetArrayLength());
        Equal("V1.2.4", result.RootElement.GetProperty("Version").GetString()); True(result.RootElement.GetProperty("DeviceInformationAlwaysVisible").GetBoolean());
        True(result.RootElement.GetProperty("NativeFrameRendering").GetBoolean());
        True(result.RootElement.GetProperty("NativeCaptionHits").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(new[] { 8, 9, 20 }));
    }
    finally { if (!process.HasExited) process.Kill(true); }
});

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"通过：{test.Name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"失败：{test.Name}\n{error}"); }
}
Console.WriteLine($"合计 {tests.Count} 项，通过 {tests.Count - failures} 项，失败 {failures} 项。");
return failures == 0 ? 0 : 1;
