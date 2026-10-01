using System.IO;
using System.Text.Json;

namespace Awd.Core;

/// <summary>
/// 分组登记表：%APPDATA%\AnywhereDoor\groups.json。程序/本地/网站三页各管各的分组；
/// 条目靠 AppEntry.Group（分组名）挂靠，删分组 = 条目脱归未分类，不删条目。
/// 颜色可空（空 = 中性灰蓝）。远程站点不属于这里 —— 站点清单是易远传的，任意门只读；
/// 站点颜色存 settings.json 的 SiteColors。
/// 原来住 Awd.GUI，为了让 CLI 与 GUI 共用同一份读写（不各写一遍解析）下沉到这里。
/// </summary>
public static class GroupStore
{
    /// <summary>未分类在侧栏上的显示名。它不是分组，所以不进登记表 —— 这个名字要留给它。</summary>
    public const string UnfiledName = "(未分类)";

    public sealed class GroupDef
    {
        public int Page { get; set; }
        public string Name { get; set; } = "";
        public string? Color { get; set; }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnywhereDoor", "groups.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文照写，别转 \uXXXX
    };

    public static List<GroupDef> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<GroupDef>();
            return JsonSerializer.Deserialize<List<GroupDef>>(File.ReadAllText(FilePath), JsonOptions) ?? new List<GroupDef>();
        }
        catch
        {
            return new List<GroupDef>();
        }
    }

    public static void Save(List<GroupDef> groups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(groups, JsonOptions));
    }
}
