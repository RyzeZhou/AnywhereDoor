using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Awd.Core;

/// <summary>
/// 收藏持久化：`%APPDATA%\AnywhereDoor\favorites.json`（跟规划文档一致）。
/// 图标缓存放 %LOCALAPPDATA%（机器本地），收藏放 %APPDATA%（随用户漫游）——两者性质不同，故意分开。
/// </summary>
public sealed class FavoritesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // 不转义非 ASCII：文件是给人看、也可能给人手改的，中文写成 \uXXXX 没法读（读取不受影响）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnywhereDoor");

    public static string FilePath => Path.Combine(DataDir, "favorites.json");

    private sealed class FavoritesFile
    {
        public int Version { get; set; } = 1;
        public List<AppEntry> Apps { get; set; } = new();
    }

    public List<AppEntry> Load()
    {
        if (!File.Exists(FilePath))
            return new List<AppEntry>();
        var json = File.ReadAllText(FilePath);
        var file = JsonSerializer.Deserialize<FavoritesFile>(json, JsonOptions) ?? new FavoritesFile();
        return file.Apps;
    }

    public void Save(IReadOnlyList<AppEntry> apps)
    {
        Directory.CreateDirectory(DataDir);
        var file = new FavoritesFile { Apps = apps.ToList() };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
    }
}
