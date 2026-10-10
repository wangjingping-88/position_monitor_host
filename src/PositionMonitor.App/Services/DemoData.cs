using System.Buffers.Binary;
using System.Text.Json;
using PositionMonitor.Core.Mqtt;

namespace PositionMonitor.App.Services;

public static class DemoData
{
    public static MqttApplicationMessage Report(int device, int tick, DateTimeOffset time)
    {
        var bytes = new byte[28];
        bytes[1] = (byte)(tick % 255 + 1);
        bytes[2] = 2;
        bytes[3] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)time.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 116397428 + device * 230);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 39909230 + device * 150);
        bytes[16] = 0xF4; bytes[17] = 1; bytes[19] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)(3300 - device * 50));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), (short)((12.34 + device + Math.Sin(tick * 0.15) * 0.8) * 100));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(24), (short)((-5.67 + Math.Cos(tick * 0.12) * 0.5) * 100));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), (short)(Math.Sin(tick * 0.08) * 30));
        return Wrap(device, bytes);
    }
    public static MqttApplicationMessage Registration(int device) => Wrap(device, Convert.FromHexString("100101A4E6E9677415F0066EF76002F4010008D204C9FD0C000010031200150A"));
    public static MqttApplicationMessage Period(int device) => Wrap(device, [8, 2, 3, 2, 0, 0, 0]);
    private static MqttApplicationMessage Wrap(int device, byte[] bytes) => new($"ucchip/up/sgw/{(device == 2 ? 1002 : 1001)}/{device}",
        JsonSerializer.SerializeToUtf8Bytes(new { cmd = 100, src = 0x12345678u + (uint)device, fmt = "hex", data = Convert.ToHexString(bytes) }));
}
