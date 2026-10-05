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
        (0, 0, ""),           // 占位：索引 0 不表示任何档
        (74, 36, "小"),      // 36 + 37.92 → 74
        (86, 48, "中"),      // 48 + 37.92 → 86
        (100, 62, "大"),     // 62 + 37.92 → 100
        (116, 78, "超大"),   // 78 + 37.92 → 116
        (138, 100, "巨缩略"), // 100 + 37.92 → 138
    };

  /// <summary>
    /// 12 DIP 字号的**实测**单行高度（不是估算）。
    /// 量法：TextBlock + 本项目真实字体（Segoe UI Variable Text / 微软雅黑 混排）
    /// → Measure 后读 DesiredSize.Height。实测 12 DIP = 15.96，13 DIP = 17.29。
 ///
    /// 为什么必须实测：行高由**字体的 ascent+descent+linegap** 决定，
    /// 中英混排还要在两种字体里取更大的那个 —— 靠"字号 × 系数"估不准
    /// （曾估 16.2，偏大 0.24，两行就差 0.5，累积起来直接裁字）。
    /// 换字体或改字号后**必须重量**。
    /// </summary>
    public const double TileLineHeight = 15.96;

    /// <summary>标签最大行数（换行上限）。2 行 = 常见长名能放下，超出的被裁。</summary>
    public const int TileLabelLines = 2;

    /// <summary>
    /// 磁贴里除图标与标签外的固定开销（DIP）：图标区 <c>Margin="0,2,0,2"</c>。
    ///
 /// 边长需求公式：<c>图标 + TileChrome + 行数 × 行高 + TileBorder</c>。
    /// 探针实测 48 + 4 + 31.92 + 2 = 85.92 → 边长 86。
    ///
    /// **以前漏算了 <see cref="TileBorder"/> 那 2 DIP**，磁贴比实际需要少2，
    /// 表现为两行标签第二行**底部被裁**（用户截图实测差 1.92 DIP）。
/// 修的时候顺手把公式提成方法，别再手算 —— 算错一次就裁字一次。
    /// </summary>
    public const double TileChrome = 4;

    /// <summary>外层 Border 1px 边框占掉的上下高度（2 DIP = 上下各 1）。</summary>
    public const double TileBorder = 2;

    /// <summary>
    /// 某档位装下"图标 + <see cref="TileLabelLines"/> 行标签"所需的磁贴边长。
    /// 档位表就是照这个算的（再向上取偶数）。
    /// 探针实测 48 图标 + 4 图标边距 + 31.92 标签 + 2 边框 = 85.92 → 取 86。
    /// </summary>
    public static double RequiredTileSide(double icon)
        => icon + TileChrome + TileLabelLines * TileLineHeight + TileBorder;

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
