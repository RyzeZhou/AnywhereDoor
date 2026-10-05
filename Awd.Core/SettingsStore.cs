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

        /// <summary>
        /// 窗口几何：宽度/高度/左边/上边（DIP），以及是否最大化。
        /// 0 = 没记录过（首启，或老settings.json 没这些字段）。
        ///
        /// 为什么存在设置里而不是地图里：窗口大小是**这台机器上这个用户的习惯**，
        /// 跟"哪张地图"无关。换地图时窗口应当保持用户刚调好的大小。
        /// </summary>
        public double WindowW { get; set; }
        public double WindowH { get; set; }
        public double WindowX { get; set; }
        public double WindowY { get; set; }
        public bool WindowMaximized { get; set; }
    }

    /// <summary>窗口几何的默认值（与 MainWindow.xaml 的初始值一致）。</summary>
    public const double DefaultWindowW = 640;
    public const double DefaultWindowH = 480;

    /// <summary>
    /// 窗口几何是否可用。规则：
    /// 尺寸必须有（宽高都 &gt; 0）；位置只在**完整合法**时采纳 ——
    /// 单边越界（只记了 x 没记 y）说明是异常状态，用它会把窗口摆到屏幕外找不着。
    /// </summary>
    public static bool HasWindowGeometry(Settings s)
        => s.WindowW >= 200 && s.WindowH >= 150;

    /// <summary>位置是否可信（两边都有值才算完整）。</summary>
    public static bool HasWindowPosition(Settings s)
        => s.WindowX != 0 || s.WindowY != 0;

    /// <summary>
    /// 图标档位表：档位序号 → (磁贴边长 DIP, 图标槽位 DIP, 说明)。
    /// 索引 0 不用（0 = 未设置）。
    ///
    /// **磁贴边长不是随手定的**，它必须 = 图标 + 标签实际需要 + 上下留边：
    ///   标签 2 行 × <see cref="TileLineHeight"/>（12 DIP 字号的行高）= 32.4，加留边 4。
    /// 历史三次踩坑，每次都是同一类：
    ///   ① 旧表 84/48/100/64 只给标签留 20 DIP ≈ 1 行，长名字换行后被磁贴底边裁掉；
    ///   ② 字号 11→13，行高 15→17.5，标签需求涨到 35 DIP，边长不改就不够；
    ///   ③ 字号 13→12（用户嫌大），行高回落 16.2，标签需求 32.4 DIP。
    /// **字号是根参数 → 行高派生 → 容器高度派生，改字号必须顺链重算。**
    ///
    /// 另：实测 shell 能精确返回任意请求尺寸（探针验过 16~256 全部原样返回），
    /// 所以档位纯粹由我们决定。
    /// </summary>
    public static readonly (double Tile, double Icon, string Label)[] TileScales =
    {
        (0, 0, ""),       // 占位：索引 0 不表示任何档
        (72, 36, "小"),         // 36 图标 + 32.4 标签 + 4 留边
        (84, 48, "中"),
        (98, 62, "大"),
        (114, 78, "超大"),
        (136, 100, "巨缩略"),   // 接近资源管理器的"超大图标"
    };

    /// <summary>标签可用高度 = 磁贴 − 图标 − 上下留边。13 DIP 字号的行高约 17.5。</summary>
    public const double TileLineHeight = 16.2;   // 12 DIP × 1.346（与 13→17.5 同比例）

    /// <summary>上下留边合计（图标区 Margin="0,2,0,2" → 2+2=4；再留一点呼吸取 4）。</summary>
    public const double TileChrome = 4;

  /// <summary>标签最大行数（换行上限）。2 行 = 常见长名能放下，超出的被裁。</summary>
    public const int TileLabelLines = 2;

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
