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

        /// <summary>
        /// 程序页图标档位（1~5 → 小/ 中 / 大 / 超大 / 巨缩略，对应 32/48/64/96/128 DIP）。
        /// 0 或越界 = 没设过，走默认档。
        ///
        /// 为什么按"档位"而不是直接给像素：Windows 资源管理器的经典图标视图就是
        /// 固定档位（小 16/中 32/大 48/超大 96/巨 256），图标始终撑满槽位，
    /// 用户换档时换来的是"整套比例一起变"，不是单个图标大小乱跳。
        /// </summary>
        public int TileIconScale { get; set; }
    }

    /// <summary>图标档位表：档位序号 → (磁贴边长 DIP, 图标槽位 DIP, 说明)。</summary>
    /// 索引 0 不用（0 = 未设置）。实测 shell 能精确返回任意请求尺寸
  /// （探针验过 16/24/32/48/64/96/128/256 全部原样返回），所以档位纯粹由我们决定，
    /// 缓存源图必须跟着档位走 —— 换档后旧尺寸的缓存要么重新生成，要么被拉伸。
    public static readonly (double Tile, double Icon, string Label)[] TileScales =
    {
        (0, 0, ""),              // 占位：索引 0 不表示任何档
        (72, 32, "小"),    // 32 DIP ≈ 手机图标
        (84, 48, "中"),           // 48 DIP
        (100, 64, "大"),          // 64 DIP
   (120, 88, "超大"),   // 88 DIP
        (148, 120, "巨缩略"),     // 120 DIP，接近资源管理器的"超大图标"
    };

    /// <summary>默认档位（中）。</summary>
    public const int DefaultTileIconScale = 2;

    /// <summary>把任意输入夹到合法档位号。</summary>
    public static int ClampScale(int scale)
        => scale < 1 || scale > TileScales.Length - 1 ? DefaultTileIconScale : scale;

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
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        // 老settings.json 没有这个字段（0），归一到默认档，别让它渲染成"最小档"
            s.TileIconScale = ClampScale(s.TileIconScale == 0 ? DefaultTileIconScale : s.TileIconScale);
            return s;
        }
        catch
        {
     // 这里可以静默给默认：坏了最多回到"用第一张地图"，不会丢内容
    var d = new Settings();
          d.TileIconScale = DefaultTileIconScale;
       return d;
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
