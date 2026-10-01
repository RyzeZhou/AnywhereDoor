using System.IO;
using System.Text.Json;

namespace Awd.Core;

/// <summary>
/// 跨会话的偏好：<c>%APPDATA%\AnywhereDoor\settings.json</c>。
/// 只放"用哪张地图"和"怎么打开"这类**与地图内容无关**的东西 —— 配色与条目都属于地图。
/// 坏文件/缺字段给默认：设置永远不该挡住启动。
/// </summary>
public static class SettingsStore
{
    public sealed class Settings
    {
        /// <summary>false = 单击打开（手机式默认）；true = 双击打开（桌面习惯）。</summary>
        public bool DoubleClickOpen { get; set; }

        /// <summary>当前使用的地图名。null = 还没定（启动时取扫到的第一张）。</summary>
        public string? ActiveMap { get; set; }
    }

    private static string FilePath => Path.Combine(MapStore.DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Settings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch
        {
            return new Settings();   // 这里可以静默给默认：坏了最多回到"用第一张地图"，不会丢内容
        }
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(MapStore.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>改活动地图名（地图改名时要用，避免整个 Settings 被并发覆盖）。</summary>
    public static void SetActive(string? from, string to)
    {
        var s = Load();
        if (from == null || s.ActiveMap == from || s.ActiveMap == null) s.ActiveMap = to;
        Save(s);
    }
}
