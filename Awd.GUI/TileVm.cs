using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Awd.Core;

namespace Awd.GUI;

/// <summary>
/// 一块磁贴的视图状态：包着 AppEntry，负责把图标缓存 PNG 解码成小图；
/// 没缓存就后台走 IconCache.Extract 提取（并发闸限制 COM 压力），提取完回写收藏文件。
/// </summary>
public sealed class TileVm : INotifyPropertyChanged
{
    public AppEntry Entry { get; }

    private ImageSource? _iconImage;

public ImageSource? IconImage
    {
        get => _iconImage;
        private set
   {
       _iconImage = value;
       Raise(nameof(IconImage));
      Raise(nameof(IconVisibility));
            Raise(nameof(PlaceholderVisibility));
        }
 }

    public Visibility IconVisibility => _iconImage == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlaceholderVisibility => _iconImage == null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 目标是否已失效（程序被卸载 / 文件被挪走 / 换机后路径变了）。
    /// 判不出来的一律按"在位"处理 —— 宁可漏标，也别把好条目灰掉（见 AppHealth）。
    /// </summary>
    public bool IsMissing { get; private set; }

    /// <summary>失效原因（人话，给状态栏与提示用）；在位时为空串。</summary>
    public string MissingReason { get; private set; } = "";

    public Visibility MissingBadgeVisibility =>
        IsMissing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>整块磁贴压暗，失效与有效一眼能分开（只看图标容易漏看）。</summary>
    public double TileOpacity => IsMissing ? 0.45 : 1.0;

    /// <summary>
    /// 标签可用高度 = 磁贴高 − 图标槽位 − 上下留边。
    /// 不能写死（原 30 是配 84磁贴 / 44 图标算的）：换档后图标变大，留给文字的空间就变少，
    /// 写死会让长名字被截成半行（"7-Zip ZS File Manager" 只剩 "Manager"）。
    ///
    /// 构造时从应用资源现算，所以四个创建点都不用关心 —— 漏一处就有一处标签被截。
    /// </summary>
    public double TileLabelMaxHeight { get; }

    public TileVm(AppEntry entry)
    {
        Entry = entry;
        TileLabelMaxHeight = ComputeLabelMaxHeight();
    }

    /// <summary>
    /// 标签**固定**高度（不是上限！）。
    ///
    /// 之前用 <c>MaxHeight</c> 是错的：MaxHeight 只限制"最多多高"，**不保证高度**。
    /// 于是单行名字（"计算器"）只占 15.96 DIP、两行的占 31.92，StackPanel 随之收缩，
    /// 磁贴高度跟着变 —— 实测同一行里"计算器"矮、"7-Zip ZS File Manager" 高，
    /// 参差不齐。固定成"按行数算满"才能让所有磁贴一样高。
    ///
    /// 行高是**实测值**不是估算：12 DIP + 中英混排（Segoe UI Variable / 微软雅黑）
 /// 单行实测 15.96 DIP（估算值 16.2 偏大 0.24，两行就差 0.5）。
    /// 详见 <see cref="SettingsStore.TileLineHeight"/>。
    /// </summary>
    private double ComputeLabelMaxHeight()
    {
        var tile = ReadResource("Tile.Size", 86.0);
        var icon = ReadResource("Tile.Icon", 48.0);
        var lines = SettingsStore.TileLabelLines * SettingsStore.TileLineHeight;
        // 取"按行数算满"与"磁贴剩余空间"的较小者：既保证等高，又不超出磁贴。
        return Math.Min(lines, tile - icon - SettingsStore.TileChrome);
    }

    private double ReadResource(string key, double fallback)
    {
        var v = Application.Current?.TryFindResource(key);
        return v is double d ? d : fallback;
    }

    /// <summary>体检一次，结果缓存进 VM：加载时统一跑一遍，别在模板里反复问文件系统。</summary>
    public void RefreshHealth()
    {
        var missing = AppHealth.IsMissing(Entry, out var reason);
        if (missing == IsMissing && reason == MissingReason) return;
        IsMissing = missing;
        MissingReason = reason;
        Raise(nameof(IsMissing));
        Raise(nameof(MissingReason));
        Raise(nameof(MissingBadgeVisibility));
        Raise(nameof(TileOpacity));
    }

    /// <summary>没真图标时的占位字形：按目标形态给（程序/文件夹/网址/远程）。</summary>
    public string GlyphChar => Entry.Kind switch
    {
        AppKind.Web => "\uE774",     // Globe
        AppKind.Remote => "\uE968",  // Remote
        AppKind.Folder => "\uE8B7",  // Folder
        _ => "\uECA5",               // Apps
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>按显示尺寸解码 PNG，失败（含文件缺失）返回 null。Freeze 后可跨线程。</summary>
    public static BitmapSource? Decode(string? path, int px)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 不锁文件
            bmp.DecodePixelWidth = px;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

public async Task LoadIconAsync(SemaphoreSlim gate, Action<AppEntry> persist, int px)
    {
        // 网址/远程没有本地图标源（favicon 要联网，违背"不联网"原则），永远占位字形
        if (Entry.Kind is AppKind.Web or AppKind.Remote) return;

        // 缓存要的是**当前槽位这个物理像素**的图，不是"最大图再降采样"。
        // 探针实测：shell 对请求尺寸原样返回（32→32、96→96），
        // 而真档位到 512 就封顶（1024 缩回 512 与直接请求 512 逐像素一致）。
        // 所以直接向 shell 要 px 既是"真细节"，又省掉一次自己做的重采样 —— 
        // 自己降采样用的插值滤波不如 shell 那套（它给的是图标自己的高分档）。
      var cached = IconCache.PathFor(Entry.Target);
        var img = Decode(cached, px);
        if (img == null)
        {
            await gate.WaitAsync();
   try
  {
      img = Decode(cached, px); // 拿到闸后复查，可能别的磁贴刚提取完
        if (img == null)
     {
            var probe = await Task.Run(() => IconCache.Extract(Entry.Target, px));
   Entry.IconPath = probe.CachePath;
       persist(Entry);
     img = Decode(probe.CachePath, px);
      }
   }
            finally
            {
                gate.Release();
            }
        }
        if (img != null) IconImage = img;
    }
}
