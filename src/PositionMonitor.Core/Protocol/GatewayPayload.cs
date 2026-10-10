using System.Text;
using System.Text.Json;
using PositionMonitor.Core.Mqtt;

namespace PositionMonitor.Core.Protocol;

/// <summary>网关既可发送单个 JSON 对象，也可在一条 MQTT 消息中发送对象数组。</summary>
public static class GatewayPayload
{
    public static IReadOnlyList<MqttApplicationMessage> Split(MqttApplicationMessage message)
    {
        if (message.Payload.Length > 65536) throw new InvalidDataException("应用载荷超过 64 KiB 限制。");
        var text = Encoding.UTF8.GetString(message.Payload.Span).Trim();
        if (!text.StartsWith('[')) return [message];
        using var document = JsonDocument.Parse(message.Payload);
        var array = document.RootElement;
        if (array.GetArrayLength() > 256) throw new InvalidDataException("网关 JSON 数组超过 256 项。");
        if (array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("网关 JSON 数组的每一项必须为消息对象。");
        return array.EnumerateArray().Select(item => message with
        {
            Payload = Encoding.UTF8.GetBytes(item.GetRawText())
        }).ToArray();
    }

    public static string? GatewayKey(string topic)
    {
        var parts = topic.Split('/');
        var up = Array.IndexOf(parts, "up");
        if (up < 1 || parts.Length < up + 3 || parts[up + 1] != "sgw" ||
            !uint.TryParse(parts[up + 2], out var gateway) || gateway is 0 or uint.MaxValue) return null;
        return $"{string.Join('/', parts[..up])}/sgw/{gateway}";
    }
}
