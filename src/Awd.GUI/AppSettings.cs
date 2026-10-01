using System.IO;
using System.Text.Json;

namespace Awd.GUI;

/// <summary>
/// GUI 的轻量设置：%APPDATA%\AnywhereDoor\settings.json。
/// 坏文件/缺字段给默认 —— 设置永远不该挡住启动。
/// </summary>
public static class AppSettingsStore
{
    public sealed class Settings
    {
        /// <summary>false = 单击打开（手机式默认）；true = 双击打开（桌面习惯）。</summary>
        public bool DoubleClickOpen { get; set; }

        /// <summary>远程站点颜色覆盖（站点清单属于易远传只读，颜色这本子记在任意门侧）：站点名 → #RRGGBB。</summary>
        public Dictionary<string, string>? SiteColors { get; set; }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AnywhereDoor", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文照写，别转 \uXXXX
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
            return new Settings();
        }
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
