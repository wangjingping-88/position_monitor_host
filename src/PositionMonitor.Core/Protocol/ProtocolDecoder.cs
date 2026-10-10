using System.Buffers.Binary;

namespace PositionMonitor.Core.Protocol;

public sealed record Sample(uint? Utc, double? Longitude, double? Latitude, double? Altitude,
    double? Hdop, ushort? BatteryMillivolts, double? AngleX, double? AngleY, double? AngleZ,
    bool GnssValid, bool ClockValid, bool SensorValid)
{
    public bool HasBatteryMeasurement { get; init; }
}

public sealed record Frame(byte Property, byte Number, byte Command, uint[] Destinations,
    string Name, Sample? Sample, IReadOnlyDictionary<string, string> Fields,
    uint? Period = null, string? HardwareVersion = null, string? AppVersion = null,
    string? WiotaVersion = null, sbyte? Snr = null, byte? Status = null)
{
    public bool IsResponse => (Property & 8) != 0;
    public bool NeedsAcknowledgment => (Property & 0x10) != 0 && !IsResponse && Sample is not null;
}

/// <summary>倾角定位监测仪 V0.1.1；不套用 OTA 应用层头部。</summary>
public static class ProtocolDecoder
{
    public static Frame Decode(ReadOnlySpan<byte> bytes)
    {
        Require(bytes.Length >= 3, "报文不足 3 字节。");
        var property = bytes[0];
        Require((property & 0x67) == 0, "Property 保留位或压缩位必须为 0。");
        var response = (property & 8) != 0;
        Require(!response || (property & 0x90) == 0, "响应不能要求响应或携带目标地址。");
        var offset = 1;
        uint[] destinations = [];
        if ((property & 0x80) != 0)
        {
            var count = bytes[offset++];
            Require(count != 0 && bytes.Length >= offset + count * 4 + 2, "目标地址数量或长度错误。");
            destinations = new uint[count];
            for (var i = 0; i < count; i++)
            {
                destinations[i] = U32(bytes, offset);
                offset += 4;
                Require(destinations[i] is not (0 or uint.MaxValue), "目标地址使用了保留值。");
            }
            Require(destinations.Distinct().Count() == count, "目标地址不能重复。");
        }
        var number = bytes[offset++];
        var command = bytes[offset++];
        Require(number != 0, "包序号必须为 1～255。");
        var data = bytes[offset..];
        Require(data.Length <= 298, "Data 超过 298 字节。");
        var fields = new Dictionary<string, string>();
        Sample? sample = null;
        uint? period = null;
        string? hw = null, app = null, wiota = null;
        sbyte? snr = null;
        byte? status = null;
        string name;
        switch (command)
        {
            case 1:
                name = response ? "设备信息响应" : data.Length == 0 ? "设备信息查询" : "设备注册";
                if (data.Length == 29)
                {
                    Require(destinations.Length == 0, "设备注册或信息响应不能携带目标地址。");
                    sample = ReadSample(data, false);
                    hw = Version(U16(data, 22));
                    app = Version(U16(data, 24));
                    wiota = Version(U16(data, 26));
                    snr = data[28] == 0x80 ? null : (sbyte)data[28];
                    fields["硬件版本"] = hw;
                    fields["应用版本"] = app;
                    fields["WIoTa 版本"] = wiota;
                    fields["WIoTa SNR"] = snr is null ? "无效" : $"{snr} dB";
                }
                else if (response) { Length(data, 1); status = ReadStatus(data[0]); name = "注册确认"; }
                else Length(data, 0);
                break;
            case 2:
                name = response ? "上报确认" : "倾角定位上报";
                Require(destinations.Length == 0, "倾角上报不能携带目标地址。");
                if (response) { Length(data, 1); status = ReadStatus(data[0]); }
                else
                {
                    Length(data, 25);
                    Require((data[0] & 0xF8) == 0, "Flags 保留位必须为 0。");
                    fields["Flags"] = $"0x{data[0]:X2}";
                    sample = ReadSample(data, true);
                }
                break;
            case 3:
            case 4:
                name = command == 3 ? "查询上报周期" : "设置上报周期";
                if (response || command == 4)
                {
                    Length(data, 4);
                    period = U32(data, 0);
                    Require(period is >= 2 and <= 259200, "上报周期超出 2～259200 秒。");
                    fields["上报周期"] = $"{period} 秒";
                }
                else Length(data, 0);
                break;
            case 5:
                name = response ? "重启响应" : "重启请求";
                Length(data, response ? 1 : 0);
                if (response) status = ReadStatus(data[0]);
                break;
            case 6:
                name = response ? "通信配置响应" : "通信配置请求";
                var start = response ? 1 : 0;
                Require(data.Length >= start + 3, "功率和频点数据过短。");
                if (response) status = ReadStatus(data[0]);
                var power = (sbyte)data[start];
                var count = data[start + 1];
                Require(power is >= -16 and <= 22, "发射功率超出 -16～22 dBm。");
                Require(count is >= 1 and <= 8, "频点数量必须为 1～8。");
                Length(data, start + 2 + count);
                var frequencies = data[(start + 2)..].ToArray();
                Require(frequencies.All(f => f <= 200) && frequencies.Distinct().Count() == count, "频点超出 0～200 或存在重复。");
                fields["发射功率"] = $"{power} dBm";
                fields["频点列表"] = string.Join("、", frequencies);
                break;
            case 7:
                name = response ? "量程配置响应" : "量程配置请求";
                Length(data, response ? 2 : 1);
                if (response) status = ReadStatus(data[0]);
                var range = data[response ? 1 : 0];
                Require(range <= 3, "量程枚举必须为 0～3。");
                fields["加速度计量程"] = $"±{2 << range}g";
                break;
            default: throw new InvalidDataException($"未知命令 0x{command:X2}，已保留原始报文。");
        }
        if (response) fields["方向"] = "响应";
        else fields["方向"] = "请求 / 主动上报";
        if (status is not null) fields["执行状态"] = StatusText(status.Value);
        return new(property, number, command, destinations, name, sample, fields, period, hw, app, wiota, snr, status);
    }

    private static Sample ReadSample(ReadOnlySpan<byte> data, bool report)
    {
        var flags = report ? data[0] : (byte)7;
        var start = report ? 1 : 0;
        var utc = U32(data, start);
        var longitude = I32(data, start + 4);
        var latitude = I32(data, start + 8);
        var altitude = I24(data[(start + 12)..(start + 15)]);
        var hdop = data[start + 15];
        var angleOffset = report ? 19 : 16;
        var gnss = (flags & 1) != 0;
        var clock = (flags & 2) != 0 && utc != 0;
        var sensor = (flags & 4) != 0;
        double? lon = gnss && longitude != int.MinValue && Math.Abs((long)longitude) <= 180_000_000 ? longitude / 1e6 : null;
        double? lat = gnss && latitude != int.MinValue && Math.Abs((long)latitude) <= 90_000_000 ? latitude / 1e6 : null;
        var battery = report ? U16(data, 17) : ushort.MaxValue;
        var x = Angle(data, angleOffset, sensor);
        var y = Angle(data, angleOffset + 2, sensor);
        var z = Angle(data, angleOffset + 4, sensor);
        return new(clock ? utc : null, lon, lat,
            gnss && altitude != -8388608 ? altitude / 10.0 : null,
            gnss && hdop != 255 ? hdop / 10.0 : null,
            battery == ushort.MaxValue ? null : battery, x, y, z,
            gnss && lon is not null && lat is not null, clock,
            sensor && x is not null && y is not null && z is not null) { HasBatteryMeasurement = report };
    }

    private static double? Angle(ReadOnlySpan<byte> data, int offset, bool valid)
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
        return valid && value is >= -9000 and <= 9000 ? value / 100.0 : null;
    }
    public static int I24(ReadOnlySpan<byte> bytes)
    {
        var value = bytes[0] | bytes[1] << 8 | bytes[2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
    public static string Version(ushort version) => $"V{version >> 12}.{(version >> 8) & 15}.{version & 255}";
    public static string StatusText(byte status) => status switch
    {
        0 => "成功", 1 => "命令不支持", 2 => "数据长度错误", 3 => "参数错误",
        4 => "执行失败", 5 => "持久化失败", 6 => "设备忙", _ => $"未知状态 {status}"
    };
    private static byte ReadStatus(byte status) { Require(status <= 6, "未知状态码。"); return status; }
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static int I32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static void Length(ReadOnlySpan<byte> data, int expected) => Require(data.Length == expected, $"Data 长度应为 {expected}，实际为 {data.Length}。");
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
