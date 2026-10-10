using System.Globalization;

namespace PositionMonitor.Core.Protocol;

/// <summary>界面使用的数值标识格式；不参与协议序列化和路由。</summary>
public static class IdentifierDisplay
{
    public static string Format(uint id) => id.ToString(CultureInfo.InvariantCulture);
    public static string Format(string id) => uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? Format(number) : id;
    public static string Tooltip(uint id) => $"{Format(id)}（0x{id:X}）";
    public static string Tooltip(string id) => uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? Tooltip(number) : id;
}
