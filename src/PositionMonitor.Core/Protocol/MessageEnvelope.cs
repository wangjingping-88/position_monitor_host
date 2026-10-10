using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PositionMonitor.Core.Mqtt;

namespace PositionMonitor.Core.Protocol;

public enum PayloadMode { Auto, Binary, Hex }

public sealed record MessageEnvelope(string DeviceKey, string DeviceLabel, string Gateway,
    string Topic, byte[] Bytes, bool Retained, uint? Source = null, string? DownstreamPrefix = null)
{
    public MqttApplicationMessage? Acknowledgment(Frame frame, int sequence, string rawReplyTopic = "")
    {
        if (Retained || !frame.NeedsAcknowledgment) return null;
        byte[] bytes = [8, frame.Number, frame.Command, 0];
        if (Source is not null && DownstreamPrefix is not null)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new { cmd = 100, ver = "v2.0", src = 0, dst = Source.Value, fmt = "hex", uc = Convert.ToHexString(bytes) });
            return new($"{DownstreamPrefix}/{sequence}", json, 1);
        }
        if (!string.IsNullOrWhiteSpace(rawReplyTopic)) return new(rawReplyTopic, bytes, 1);
        return null;
    }
}

public static partial class EnvelopeDecoder
{
    public static MessageEnvelope? Decode(MqttApplicationMessage message, PayloadMode mode = PayloadMode.Auto)
    {
        var payload = message.Payload.ToArray();
        if (payload.Length == 0) throw new InvalidDataException("收到空载荷。");
        if (payload.Length > 65536) throw new InvalidDataException("应用载荷超过 64 KiB 限制。");
        var text = Encoding.UTF8.GetString(payload).Trim();
        if (mode == PayloadMode.Auto && text.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var cmd) || !cmd.TryGetInt32(out var command) || command != 100) return null;
            if (!root.TryGetProperty("data", out _) && (root.TryGetProperty("uc", out var uc) && uc.ValueKind == JsonValueKind.Object || root.TryGetProperty("bc", out var bc) && bc.ValueKind == JsonValueKind.Object)) return null;
            if (!root.TryGetProperty("src", out var sourceElement)) throw new InvalidDataException("网关消息缺少 src，无法识别设备。");
            var source = Address(sourceElement);
            if (source is 0 or uint.MaxValue) throw new InvalidDataException("终端 src 不能为保留地址。");
            if (!root.TryGetProperty("fmt", out var fmt) || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("网关消息缺少 fmt 或字符串 data。");
            var encoded = data.GetString()!;
            var bytes = fmt.GetString()?.ToLowerInvariant() switch
            {
                "hex" => Hex(encoded),
                "base64" => Convert.FromBase64String(encoded),
                _ => throw new InvalidDataException("仅支持 fmt=hex/base64；二进制协议不能使用有损字符串编码。")
            };
            var match = GatewayTopic().Match(message.Topic);
            var gateway = match.Success ? match.Groups[2].Value : "未识别网关";
            var scope = match.Success ? $"{match.Groups[1].Value}/sgw/{gateway}" : message.Topic;
            return new($"{scope}/{source}", $"0x{source:X8}", gateway, message.Topic, bytes, message.Retain, source,
                match.Success ? $"{match.Groups[1].Value}/down/sgw/{gateway}" : null);
        }
        var binary = mode == PayloadMode.Binary ? payload : mode == PayloadMode.Hex || IsHex(text) ? Hex(text) : payload;
        return new($"raw:{message.Topic}", message.Topic, "直接透传", message.Topic, binary, message.Retain);
    }

    public static byte[] Hex(string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length == 0 || compact.Length % 2 != 0 || !compact.All(Uri.IsHexDigit))
            throw new InvalidDataException("Hex 必须由完整字节组成，只允许十六进制数字和空白。");
        return Convert.FromHexString(compact);
    }
    private static bool IsHex(string text) => text.Length != 0 && text.All(c => char.IsWhiteSpace(c) || Uri.IsHexDigit(c));
    private static uint Address(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var number)) return number;
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number)) return number;
            if (uint.TryParse(text, out number)) return number;
        }
        throw new InvalidDataException("src 必须是 uint32 地址或十进制 / 0x 十六进制字符串。");
    }
    [GeneratedRegex(@"^(.+)/up/sgw/([0-9]+)(?:/.*)?$")]
    private static partial Regex GatewayTopic();
}
