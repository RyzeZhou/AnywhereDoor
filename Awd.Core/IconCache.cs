using System.IO;
using System.Windows;               // Int32Rect
using System.Windows.Media;         // PixelFormats
using System.Windows.Interop;       // Imaging
using System.Windows.Media.Imaging; // PngBitmapEncoder
using Awd.Core.Interop;

namespace Awd.Core;

/// <summary>
/// 图标提取与缓存。立项笔记 P0 要点 1 的"先验证可得性"在这里落地：
/// 探测（ICONONLY|BIGGERSIZEOK，不拉伸）报"源里最大的真实尺寸"，回答"老程序是不是只有 32px"；
/// 缓存（RESIZETOFIT）落一份指定尺寸的 PNG，网格加载直接用文件，不再碰 COM。
/// UWP 优先走包清单 logo（透明底，GetLogo 自动挑 unplated 变体与最大 scale），
/// 拿不到再退化 shell 磁贴图 —— 否则网格里全是整块磁贴底色。
/// </summary>
public static class IconCache
{
    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnywhereDoor", "iconcache");

    /// <summary>缓存文件的路径 —— 纯算路径，不碰 COM 也不建目录。
    /// 地图里因此**不需要存 iconPath**：由 target 就能推出来（它占了老文件 28% 的字节）。
    /// t1 = 裁剪版缓存（v1 没裁透明留白，计算器字形只占画布 32%）。</summary>
    public static string PathFor(string target, int size = 256) =>
        Path.Combine(CacheDir, $"{AppEntry.MakeId(target)}_{size}t1.png");

    /// <summary>探测 + 落缓存。NaturalWidth/Height 是真实可得尺寸，CachePath 是请求尺寸的 PNG。</summary>
    public sealed record ProbeResult(int NaturalWidth, int NaturalHeight, string? CachePath, string CacheSizeText);

    public sealed class ProbeItem
    {
        public ProbeItem(AppEntry app, ProbeResult? result, string? error)
        {
            App = app;
            Result = result;
            Error = error;
        }

        public AppEntry App { get; }
        public ProbeResult? Result { get; }
        public string? Error { get; }
    }

    public static ProbeResult Extract(string target, int size = 256)
    {
        Directory.CreateDirectory(CacheDir);
        var cachePath = PathFor(target, size);

        // 缓存命中直接回（读 PNG 实际尺寸当 natural），省掉每次启动的全量 COM 重跑
        if (File.Exists(cachePath))
        {
            var (w0, h0) = PngSize(cachePath);
            return new ProbeResult(w0, h0, cachePath, $"{w0}x{h0}");
        }

        // UWP：包 logo 是透明底原图，先试；不行再走 shell 磁贴图（带磁贴底色）
        if (IsUwp(target))
        {
            var logo = TryPackageLogo(target, size);
            if (logo != null)
            {
                var trimmed = TrimTransparent(logo) ?? logo;
                File.WriteAllBytes(cachePath, trimmed);
                var (lw, lh) = PngSize(cachePath);
                return new ProbeResult(lw, lh, cachePath, $"{lw}x{lh}");
            }
        }

        var parseName = ToParseName(target);
        var (nw, nh) = ProbeNatural(parseName, size);
        var (cw, ch) = SavePng(parseName, size, cachePath);
        return new ProbeResult(nw, nh, cachePath, $"{cw}x{ch}");
    }

    /// <summary>批量探测：单个失败不中断，错误收集进 Error。给"图标可得性分档统计"出证据。</summary>
    public static List<ProbeItem> ProbeAll(IEnumerable<AppEntry> apps, int size = 256)
    {
        var results = new List<ProbeItem>();
        foreach (var app in apps)
        {
            try
            {
                results.Add(new ProbeItem(app, Extract(app.Target, size), null));
            }
            catch (Exception ex)
            {
                results.Add(new ProbeItem(app, null, ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// 透明底 logo 常自带大片留白（实测计算器字形只占画布 32%）：按 alpha 裁到字形边界，
    /// 留 ~5% 呼吸边。全透明（裁不出东西）返回 null 交给调用方用原图。
    /// </summary>
    private static byte[]? TrimTransparent(byte[] pngBytes)
    {
        try
        {
            using var ms = new MemoryStream(pngBytes);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                var row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4 + 3] <= 16) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0) return null;
            var pad = Math.Max(2, Math.Max(maxX - minX, maxY - minY) / 20);
            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            var cw = Math.Min(w - 1, maxX + pad) - minX + 1;
            var ch = Math.Min(h - 1, maxY + pad) - minY + 1;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bgra, new Int32Rect(minX, minY, cw, ch))));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null; // 裁剪失败就用原图，不拦提取
        }
    }

    private static bool IsUwp(string target) =>
        !target.Contains('\\') && target.Contains('!');

    /// <summary>UWP 的 AUMID（形如 Xyz_hash!App）要走 shell:AppsFolder 才能拿到 IShellItem。</summary>
    private static string ToParseName(string target)
    {
        if (IsUwp(target))
            return "shell:AppsFolder\\" + target;
        return target;
    }

    /// <summary>
    /// UWP 透明底原图：AUMID → 包族 → AppListEntry → GetLogo(请求尺寸)。
    /// GetLogo 会挑 unplated 变体和满足请求的最大 scale，返回的就是图片文件原始字节（png）。
    /// 任何一步失败都返回 null，由调用方退化到 shell 提取 —— 这里绝不能抛。
    /// </summary>
    /// <summary>最近一次包 logo 提取的失败原因（诊断用；成功后清空）。</summary>
    public static string? LastLogoError;

    private static byte[]? TryPackageLogo(string aumid, int size)
    {
        try
        {
            var family = aumid[..aumid.IndexOf('!')];
            var pm = new Windows.Management.Deployment.PackageManager();
            // FindPackageForUser('') 在本机实测返回 NULL（探针证据），枚举过滤才是可靠路径
            Windows.ApplicationModel.Package? pkg = null;
            foreach (var p in pm.FindPackagesForUser(string.Empty))
            {
                if (p.Id.FamilyName != family) continue;
                pkg = p;
                break;
            }
            if (pkg == null) { LastLogoError = "枚举里没有这个包族"; return null; }
            foreach (var entry in pkg.GetAppListEntries())
            {
                if (!string.Equals(entry.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase)) continue;
                var logoRef = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(size, size));
                var stream = logoRef.OpenReadAsync().GetAwaiter().GetResult();
                var len = (uint)stream.Size;
                var reader = new Windows.Storage.Streams.DataReader(stream);
                reader.LoadAsync(len).AsTask().GetAwaiter().GetResult();
                var bytes = new byte[len];
                reader.ReadBytes(bytes);
                LastLogoError = null;
                return bytes;
            }
            LastLogoError = "AppListEntries 里没有这个 AUMID";
        }
        catch (Exception ex)
        {
            LastLogoError = $"{ex.GetType().Name}: {ex.Message}";
        }
        return null;
    }

    private static (int Width, int Height) PngSize(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static (int Width, int Height) ProbeNatural(string parseName, int size)
    {
        var factory = ShellInterop.CreateItemImageFactory(parseName);
        var hr = factory.GetImage(new ShellInterop.NativeSize { Width = size, Height = size },
            ShellInterop.SIIGBF_ICONONLY | ShellInterop.SIIGBF_BIGGERSIZEOK, out var hbm);
        if (hr != 0 || hbm == IntPtr.Zero)
            throw new InvalidOperationException($"拿不到图标：HRESULT 0x{hr:X8}");
        try
        {
            var bm = ShellInterop.GetBitmapInfo(hbm);
            return (bm.Width, bm.Height);
        }
        finally
        {
            ShellInterop.FreeHBitmap(hbm);
        }
    }

    private static (int Width, int Height) SavePng(string parseName, int size, string filePath)
    {
        var factory = ShellInterop.CreateItemImageFactory(parseName);

        // UWP 的磁贴图标可能不支持 ICONONLY：先带 ICONONLY 试，失败退化重试一次
        var hr = factory.GetImage(new ShellInterop.NativeSize { Width = size, Height = size },
            ShellInterop.SIIGBF_RESIZETOFIT | ShellInterop.SIIGBF_ICONONLY, out var hbm);
        if (hr != 0 || hbm == IntPtr.Zero)
            hr = factory.GetImage(new ShellInterop.NativeSize { Width = size, Height = size },
                ShellInterop.SIIGBF_RESIZETOFIT, out hbm);
        if (hr != 0 || hbm == IntPtr.Zero)
            throw new InvalidOperationException($"拿不到图标（缓存图）：HRESULT 0x{hr:X8}");

        try
        {
            // CreateBitmapSourceFromHBitmap 会复制位图内容，之后必须 DeleteObject，否则 GDI 句柄泄漏
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(filePath);
            encoder.Save(stream);
            return (source.PixelWidth, source.PixelHeight);
        }
        finally
        {
            ShellInterop.FreeHBitmap(hbm);
        }
    }
}
