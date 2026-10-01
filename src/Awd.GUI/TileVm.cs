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

    public TileVm(AppEntry entry) => Entry = entry;

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
        var img = Decode(Entry.IconPath, px);
        if (img == null)
        {
            await gate.WaitAsync();
            try
            {
                img = Decode(Entry.IconPath, px); // 拿到闸后复查，可能别的磁贴刚提取完
                if (img == null)
                {
                    var probe = await Task.Run(() => IconCache.Extract(Entry.Target, 256));
                    Entry.IconPath = probe.CachePath;
                    persist(Entry);
                    img = Decode(Entry.IconPath, px);
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
