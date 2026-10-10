namespace PositionMonitor.Core.Mqtt;

/// <summary>逐行订阅配置，保持顺序，忽略空行和完全相同的主题。</summary>
public static class SubscriptionFilters
{
    public static string[] Parse(string text) => Normalize(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    public static string[] Normalize(IEnumerable<string> topics)
    {
        var filters = topics.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (filters.Length == 0) throw new ArgumentException("请至少填写一个订阅主题，每行一个。");
        if (filters.Length > 64) throw new ArgumentException("一次连接最多订阅 64 个主题。");
        for (var i = 0; i < filters.Length; i++)
        {
            try { Mqtt311Client.ValidateTopic(filters[i]); }
            catch (ArgumentException error) { throw new ArgumentException($"第 {i + 1} 个订阅主题无效：{error.Message}", error); }
        }
        return filters;
    }
}
