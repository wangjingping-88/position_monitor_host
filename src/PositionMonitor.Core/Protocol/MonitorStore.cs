namespace PositionMonitor.Core.Protocol;

public sealed record SampleRecord(DateTimeOffset Received, string Device, byte Number, Sample Sample, string Hex, bool Retained)
{
    public DateTimeOffset PlotTime => Sample.Utc is { } utc ? DateTimeOffset.FromUnixTimeSeconds(utc) : Received;
}

public sealed class DeviceState(string key, string label, string gateway)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Gateway { get; } = gateway;
    public DateTimeOffset LastReceived { get; private set; }
    public DateTimeOffset? LastInformationReceived { get; private set; }
    public SampleRecord? Latest { get; private set; }
    public string? HardwareVersion { get; private set; }
    public string? AppVersion { get; private set; }
    public string? WiotaVersion { get; private set; }
    public sbyte? Snr { get; private set; }
    public uint? Period { get; private set; }
    public long Reports { get; private set; }
    public List<SampleRecord> History { get; } = [];
    public Dictionary<string, string> Configuration { get; } = [];
    private readonly Dictionary<string, DateTimeOffset> _recent = [];

    public bool Apply(Frame frame, MessageEnvelope envelope, DateTimeOffset received)
    {
        LastReceived = received;
        if (frame.HardwareVersion is not null || frame.IsResponse) LastInformationReceived = received;
        var fingerprint = Convert.ToHexString(envelope.Bytes);
        foreach (var entry in _recent.Where(p => received - p.Value > TimeSpan.FromMinutes(5)).ToArray()) _recent.Remove(entry.Key);
        if (_recent.ContainsKey(fingerprint)) return false;
        _recent[fingerprint] = received;
        if (_recent.Count > 512) _recent.Remove(_recent.MinBy(p => p.Value).Key);
        if (frame.Sample is { } sample)
        {
            Reports++;
            var record = new SampleRecord(received, Label, frame.Number, sample, fingerprint, envelope.Retained);
            // 保留消息和迟到采样留在记录中，但不覆盖较新的实时采样。
            if (Latest is null || record.PlotTime >= Latest.PlotTime) Latest = record;
            History.Add(record);
            if (History.Count > 2000) History.RemoveAt(0);
        }
        if (frame.HardwareVersion is not null)
        {
            HardwareVersion = frame.HardwareVersion;
            AppVersion = frame.AppVersion;
            WiotaVersion = frame.WiotaVersion;
            Snr = frame.Snr;
        }
        if (frame.IsResponse)
        {
            if (frame.Period is not null) Period = frame.Period;
            foreach (var entry in frame.Fields) Configuration[entry.Key] = entry.Value;
        }
        return true;
    }

    public string Freshness(DateTimeOffset now)
    {
        if (Latest is null) return "尚无采样";
        var seconds = Math.Max(0, (now - Latest.Received).TotalSeconds);
        if (Latest.Retained) return "保留消息";
        if (Period is { } period && seconds > Math.Max(10, period * 2)) return "上报超时";
        return seconds < 60 ? $"{seconds:F0} 秒前" : seconds < 3600 ? $"{seconds / 60:F0} 分钟前" : $"{seconds / 3600:F1} 小时前";
    }
}
