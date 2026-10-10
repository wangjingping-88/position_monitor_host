using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PositionMonitor.Core.Mqtt;

namespace PositionMonitor.Core.Protocol;

public enum ControlKind { QueryInfo, QueryPeriod, SetPeriod, Reboot, SetRadio, SetRange, Wake }

public sealed record PagingOptions(byte Frequency = 160, ushort AwakenId = 30, uint SendTime = 5000)
{
    public void Validate()
    {
        if (Frequency is < 1 or > 200 || AwakenId > 16383 || SendTime is < 2500 or > 4294967294)
            throw new ArgumentException("唤醒频点须为 1～200，唤醒组号 0～16383，持续时间 2500～4294967294 毫秒。");
    }
    public object Json() => new { freq = Frequency, sym_len = 2, awaken_id = AwakenId, send_time = SendTime, tx_mode = 1 };
}

public sealed record ControlRequest(string Company, uint Gateway, uint Target, ControlKind Kind,
    uint Period = 14400, sbyte Power = 10, byte[]? Frequencies = null, byte Range = 0,
    PagingOptions? Paging = null)
{
    public string DeviceKey => $"{Company}/sgw/{Gateway}/{Target}";
    public string Name => Kind switch
    {
        ControlKind.QueryInfo => "查询设备信息", ControlKind.QueryPeriod => "查询上报周期",
        ControlKind.SetPeriod => "设置上报周期", ControlKind.Reboot => "重启终端",
        ControlKind.SetRadio => "设置功率与频点", ControlKind.SetRange => "设置加速度量程", _ => "远程唤醒"
    };
    public byte Command => Kind switch
    {
        ControlKind.QueryInfo => 1, ControlKind.QueryPeriod => 3, ControlKind.SetPeriod => 4,
        ControlKind.Reboot => 5, ControlKind.SetRadio => 6, ControlKind.SetRange => 7, _ => 0
    };
    public static uint Address(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return uint.Parse(text, CultureInfo.InvariantCulture);
    }
    public byte[] Frame(byte number)
    {
        if (number == 0) throw new ArgumentException("包序号不能为 0。");
        byte[] data = Kind switch
        {
            ControlKind.QueryInfo or ControlKind.QueryPeriod or ControlKind.Reboot => [],
            ControlKind.SetPeriod when Period is >= 2 and <= 259200 => new byte[4],
            ControlKind.SetRadio when Power is >= -16 and <= 22 && Frequencies is { Length: >= 1 and <= 8 } frequencies && frequencies.All(f => f <= 200) && frequencies.Distinct().Count() == frequencies.Length => [(byte)Power, (byte)frequencies.Length, .. frequencies],
            ControlKind.SetRange when Range <= 3 => [Range],
            _ => throw new ArgumentException("控制参数超出协议范围。周期 2～259200 秒，功率 -16～22 dBm，频点 0～200 且 1～8 个不重复，量程枚举 0～3。")
        };
        if (Kind == ControlKind.SetPeriod) BinaryPrimitives.WriteUInt32LittleEndian(data, Period);
        return [0x10, number, Command, .. data];
    }
    public MqttApplicationMessage Build(byte number, int sequence)
    {
        if (string.IsNullOrWhiteSpace(Company) || Company.IndexOfAny(['/', '+', '#', '\0']) >= 0 || Company.Any(char.IsWhiteSpace))
            throw new ArgumentException("公司前缀须为单个主题段，不能包含通配符。");
        if (Gateway is 0 or uint.MaxValue) throw new ArgumentException("请指定真实网关 ID。");
        if (Kind != ControlKind.Wake && Target is 0 or uint.MaxValue) throw new ArgumentException("请指定真实终端地址，不能广播控制命令。");
        if (sequence <= 0) throw new ArgumentException("MQTT 主题序号必须大于 0。");
        Paging?.Validate();
        object json;
        if (Kind == ControlKind.Wake)
        {
            var paging = Paging ?? new(); paging.Validate();
            json = new { cmd = 2, ver = "v2.0", src = 0, dst = 0, paging = paging.Json() };
        }
        else if (Paging is not null)
            json = new { cmd = 2, ver = "v2.0", src = 0, dst = Target, paging = Paging.Json(), data = new { fmt = "hex", uc = Convert.ToHexString(Frame(number)) } };
        else json = new { cmd = 100, ver = "v2.0", src = 0, dst = Target, fmt = "hex", uc = Convert.ToHexString(Frame(number)) };
        return new($"{Company}/down/sgw/{Gateway}/{sequence}", JsonSerializer.SerializeToUtf8Bytes(json), 1);
    }
    public string Result(Frame response)
    {
        if (response.Status is > 0) return "终端执行失败：" + ProtocolDecoder.StatusText(response.Status.Value);
        if (Kind == ControlKind.SetPeriod && response.Period != Period) return $"周期未按请求生效：实际 {response.Period} 秒，请求 {Period} 秒";
        if (Kind == ControlKind.QueryPeriod) return $"终端确认：实际周期 {response.Period} 秒";
        if (Kind == ControlKind.SetRadio && (response.Fields["发射功率"] != $"{Power} dBm" || response.Fields["频点列表"] != string.Join("、", Frequencies!))) return "终端返回的功率或频点与请求不一致";
        if (Kind == ControlKind.SetRange && response.Fields["加速度计量程"] != $"±{2 << Range}g") return "终端返回的量程与请求不一致";
        return Kind == ControlKind.SetPeriod ? $"终端确认：周期已设为 {response.Period} 秒" : "终端已确认";
    }
}

public sealed record GatewayNotice(string Scope, string Gateway, string Sequence, bool Heartbeat, string? Result)
{
    public static GatewayNotice? Decode(MqttApplicationMessage message)
    {
        var payload = message.Payload.Span;
        var index = 0;
        while (index < payload.Length && char.IsWhiteSpace((char)payload[index])) index++;
        if (index == payload.Length || payload[index] != '{') return null;
        using var document = JsonDocument.Parse(message.Payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.Number || !cmd.TryGetInt32(out var command)) return null;
        var parts = message.Topic.Split('/');
        var up = Array.IndexOf(parts, "up");
        if (up < 1 || parts.Length < up + 3 || parts[up + 1] != "sgw" || !uint.TryParse(parts[up + 2], out var gateway)) return null;
        var scope = string.Join('/', parts[..up]) + "/sgw/" + gateway;
        var sequence = parts.Length > up + 3 ? parts[up + 3] : "";
        var validSource = root.TryGetProperty("src", out var src) && src.ValueKind == JsonValueKind.Number && src.TryGetUInt32(out var source) && source == gateway;
        if (command == 1 && validSource) return new(scope, gateway.ToString(), sequence, true, null);
        if (validSource && command is 2 or 100)
        {
            var field = command == 2 ? "paging" : "uc";
            if (root.TryGetProperty(field, out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("res", out var res) && res.ValueKind == JsonValueKind.String)
                return new(scope, gateway.ToString(), sequence, false, res.GetString());
        }
        return null;
    }
}
