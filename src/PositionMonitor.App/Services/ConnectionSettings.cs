using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PositionMonitor.Core.Mqtt;
using PositionMonitor.Core.Protocol;

namespace PositionMonitor.App.Services;

public sealed class ConnectionSettings
{
    public string Host { get; set; } = "117.172.29.2";
    public int Port { get; set; } = 36106;
    public string ClientId { get; set; } = $"position-monitor-{Guid.NewGuid():N}"[..29];
    public string Company { get; set; } = "ucchip";
    public string GatewayId { get; set; } = "+";
    public string Topic { get; set; } = "ucchip/up/sgw/+/#";
    [JsonIgnore]
    public string[] SubscriptionTopics => SubscriptionFilters.Parse(Topic);
    public string UserName { get; set; } = "";
    public bool UseTls { get; set; }
    public bool RememberPassword { get; set; }
    public byte Qos { get; set; } = 1;
    public PayloadMode PayloadMode { get; set; }
    public bool AutoAck { get; set; } = true;
    public string RawReplyTopic { get; set; } = "";
    [JsonIgnore]
    public string CredentialKey => "PositionMonitorHost/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Host}:{Port}/{UserName}")));
    public MqttClientOptions Options(string password) => new(Host, Port, ClientId, UseTls,
        string.IsNullOrEmpty(UserName) && string.IsNullOrEmpty(password) ? null : UserName, password);
    public void Validate(string password)
    {
        Options(password).Validate();
        if (Host.Contains("://") || Host.Contains('/') || Host.Any(char.IsWhiteSpace)) throw new ArgumentException("服务器地址只填写主机名或 IP，不包含协议前缀和路径。");
        var topics = SubscriptionTopics;
        if (RawReplyTopic.Length > 0)
        {
            Mqtt311Client.ValidateTopic(RawReplyTopic, false);
            if (topics.Contains(RawReplyTopic, StringComparer.Ordinal)) throw new ArgumentException("直接透传回执主题不能与任何上行订阅主题相同。");
        }
    }
}

public static class SettingsStore
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "data", "connection.json");
    public static ConnectionSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        return JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(FilePath)) ?? new();
    }
    public static void Save(ConnectionSettings settings, string password, ConnectionSettings? previous)
    {
        var credentials = new WindowsCredentialStore();
        if (settings.RememberPassword && password.Length > 0) credentials.Save(settings.CredentialKey, password);
        else credentials.Delete(settings.CredentialKey);
        if (previous is not null && previous.CredentialKey != settings.CredentialKey) credentials.Delete(previous.CredentialKey);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
    public static string Password(ConnectionSettings settings) => settings.RememberPassword && new WindowsCredentialStore().TryGet(settings.CredentialKey, out var value) ? value ?? "" : "";
}
