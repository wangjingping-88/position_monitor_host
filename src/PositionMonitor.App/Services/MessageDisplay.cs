using System.Text;
using System.Text.Json;
using PositionMonitor.Core.Mqtt;

namespace PositionMonitor.App.Services;

public static class MessageDisplay
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string Payload(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        try { using var document = JsonDocument.Parse(text); return JsonSerializer.Serialize(document.RootElement, Options); }
        catch (JsonException) { return text; }
    }
    public static string Message(MqttApplicationMessage message) => $"主题：{message.Topic}\n{Payload(message.Payload.Span)}";
}
