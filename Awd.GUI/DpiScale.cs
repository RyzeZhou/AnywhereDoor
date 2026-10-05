using System.Runtime.InteropServices;

namespace Awd.GUI;

/// <summary>
/// DIP → 物理像素换算。
///
/// WPF 的尺寸单位是 DIP（1 DIP = 1/96 英寸），而 <see cref="System.Windows.Media.Imaging.BitmapImage.DecodePixelWidth"/>
/// 要的是**物理像素**。在 175% 缩放的机器上两者差1.75 倍：按 DIP 数去解码，
/// 等于先把高分辨率图标降采样掉细节，再让 WPF 拉伸回物理尺寸 —— 必然发糊。
///
/// 这就是"图标比资源管理器模糊"的根因：资源管理器始终按物理像素取图。
///
/// 注意必须在 UI 元素建好之后问 DPI 才准（<c>VisualTreeHelper.GetDpi</c>）；
/// 构造期没有 Visual，就直接问 Win32的 <c>GetDpiForSystem</c>（进程已声明
/// PER_MONITOR_AWARE_V2，见 app.manifest）。
/// </summary>
internal static class DpiScale
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static double? _cached;

    /// <summary>当前缩放系数，1.0 = 96dpi。读失败退回 1.0（宁可少解一点，也不能读错成放大）。</summary>
    public static double Factor
    {
        get
        {
            if (_cached is { } v) return v;
            double scale;
            try
            {
                var dpi = GetDpiForSystem();
                scale = dpi <= 0 ? 1.0 : dpi / 96.0;
            }
            catch
            {
                scale = 1.0;
            }
            _cached = scale;
            return scale;
        }
    }

    /// <summary>DIP 尺寸 → 该给解码器多少物理像素（向上取整，不做欠采样）。</summary>
    public static int ToPixels(double dip) => (int)System.Math.Ceiling(dip * Factor);
}