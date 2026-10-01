namespace Awd.Core;

/// <summary>一个颜色预设：中文名、英文名（CSS 命名色）、存进 JSON 的 #RRGGBB。</summary>
public readonly record struct ColorSwatch(string Zh, string En, string Hex);

/// <summary>
/// 分组/站点颜色的中英对照预设（web 常见色，取 CSS 命名色的实际值）。
/// GUI 的右键色盘与 CLI 的 --color 共用这一份，两边看到的名字必须一致。
/// **存进 JSON 的永远是 #RRGGBB** —— 名字只是入口，改这份表不会动到已有数据。
/// </summary>
public static class GroupPalette
{
    public static readonly ColorSwatch[] Swatches =
    {
        new("红", "red", "#FF0000"),
        new("橙", "orange", "#FFA500"),
        new("黄", "gold", "#FFD700"),
        new("绿", "green", "#008000"),
        new("青", "teal", "#008080"),
        new("蓝", "blue", "#0000FF"),
        new("紫", "purple", "#800080"),
        new("粉", "hotpink", "#FF69B4"),
        new("棕", "brown", "#A52A2A"),
        new("灰", "gray", "#808080"),
    };

    /// <summary>按中文名 / 英文名（不分大小写）/ #RGB 或 #RRGGBB / 1 起的序号找色；找不到返回 null。</summary>
    public static ColorSwatch? Find(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var s = spec.Trim();
        if (int.TryParse(s, out var n) && n >= 1 && n <= Swatches.Length) return Swatches[n - 1];
        var hit = Swatches.FirstOrDefault(x =>
            x.Zh == s || x.En.Equals(s, StringComparison.OrdinalIgnoreCase));
        if (hit != default) return hit;
        return IsHex(s) ? new ColorSwatch(s, "custom", NormalizeHex(s)) : null;
    }

    /// <summary>反查：hex → 中文名；不是预设里的色就原样返回（自定义色照样能显示）。</summary>
    public static string DisplayName(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return "中性";
        var hit = Swatches.FirstOrDefault(x => NormalizeHex(x.Hex).Equals(NormalizeHex(hex), StringComparison.OrdinalIgnoreCase));
        return hit == default ? hex : $"{hit.Zh}/{hit.En}";
    }

    private static bool IsHex(string s)
        => (s.Length == 4 || s.Length == 7) && s[0] == '#'
           && s[1..].All(Uri.IsHexDigit);

    private static string NormalizeHex(string hex)
    {
        if (hex.Length == 4) return $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}".ToUpperInvariant();
        return hex.ToUpperInvariant();
    }
}
