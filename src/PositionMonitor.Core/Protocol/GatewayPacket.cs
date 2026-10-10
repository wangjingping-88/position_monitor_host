using System.Text.Json;
using PositionMonitor.Core.Mqtt;

namespace PositionMonitor.Core.Protocol;

/// <summary>网关承载消息；业务 cmd=100 的应用帧另由 EnvelopeDecoder 解码。</summary>
public sealed record GatewayPacket(string Company, uint Gateway, string Sequence, int Command, JsonElement Body)
{
    public string Key => $"{Company}/sgw/{Gateway}";
    public string IdentifierTooltip => string.Join("\n", new[] { $"网关 {IdentifierDisplay.Tooltip(Gateway)}" }.Concat(
        Body.EnumerateObject().Where(p => p.Name is "src" or "dst" or "dev_id" or "gw_id" or "gateway_id" or "id")
        .Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetUInt32(out _))
        .Select(p => $"{p.Name}：{IdentifierDisplay.Tooltip(p.Value.GetUInt32())}")));
    public string Name => Command switch
    {
        0 => "网关启动", 1 => "网关心跳", 2 => "网关配置反馈", 3 => "网关配置上报",
        6 => "OTA 提示", 7 => "扫频结果", 9 => "OTA 状态", 20 => "终端链路状态",
        100 => "业务发送反馈", 101 => "数据回环", 151 => "终端版本", 201 => "终端在线事件",
        _ => $"网关消息 cmd={Command}"
    };
    public uint? Terminal => Command switch { 20 => Number("src"), 201 => Number("dev_id"), _ => null };
    public uint? OnlineStatus => Command == 201 && Number("status") is <= 1 and { } state ? state : null;
    private uint? Number(string field) => Body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number) ? number : null;
    public string Summary => Command switch
    {
        201 when Terminal is { } terminal => $"{IdentifierDisplay.Format(terminal)} · {(OnlineStatus == 1 ? "在线" : OnlineStatus == 0 ? "离线" : "状态未知")}",
        20 when Terminal is { } terminal => $"{IdentifierDisplay.Format(terminal)} · 频点 {Number("freq")?.ToString() ?? "—"}",
        151 when Number("src") is { } terminal => $"{IdentifierDisplay.Format(terminal)} · 终端版本反馈",
        0 => "网关启动完成",
        _ => string.Join(" · ", Body.EnumerateObject().Where(p => p.Name is not ("cmd" or "ver" or "src")).Take(3).Select(p => $"{p.Name}: {DisplayValue(p)}"))
    };
    private static string DisplayValue(JsonProperty field) => (field.Name is "dst" or "dev_id" or "gw_id" or "gateway_id" or "id") && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetUInt32(out var id) ? IdentifierDisplay.Format(id) : field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? "查看详情" : field.Value.ToString();
    public static GatewayPacket? Decode(MqttApplicationMessage message)
    {
        var span = message.Payload.Span;
        var start = 0;
        while (start < span.Length && char.IsWhiteSpace((char)span[start])) start++;
        if (start == span.Length || span[start] != '{') return null;
        using var json = JsonDocument.Parse(message.Payload);
        var body = json.RootElement;
        if (!body.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.Number || !cmd.TryGetInt32(out var command)) return null;
        var parts = message.Topic.Split('/');
        var up = Array.IndexOf(parts, "up");
        if (up < 1 || parts.Length < up + 3 || parts[up + 1] != "sgw" || !uint.TryParse(parts[up + 2], out var gateway) || gateway is 0 or uint.MaxValue) return null;
        return new(string.Join('/', parts[..up]), gateway, parts.Length > up + 3 ? parts[up + 3] : "", command, body.Clone());
    }
}
