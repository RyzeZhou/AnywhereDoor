using System.IO;
using System.Windows;               // Int32Rect
using System.Windows.Interop;       // Imaging
using System.Windows.Media.Imaging; // PngBitmapEncoder
using Awd.Core.Interop;

namespace Awd.Core;

/// <summary>
/// 图标提取与缓存。立项笔记 P0 要点 1 的"先验证可得性"在这里落地：
/// 探测（ICONONLY|BIGGERSIZEOK，不拉伸）报"源里最大的真实尺寸"，回答"老程序是不是只有 32px"；
/// 缓存（RESIZETOFIT）落一份指定尺寸的 PNG，网格加载直接用文件，不再碰 COM。
/// </summary>
public static class IconCache
{
    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnywhereDoor", "iconcache");

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
        var parseName = ToParseName(target);
        Directory.CreateDirectory(CacheDir);

        var (nw, nh) = ProbeNatural(parseName, size);
        var cachePath = Path.Combine(CacheDir, $"{AppEntry.MakeId(target)}_{size}.png");
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

    /// <summary>UWP 的 AUMID（形如 Xyz_hash!App）要走 shell:AppsFolder 才能拿到 IShellItem。</summary>
    private static string ToParseName(string target)
    {
        if (!target.Contains('\\') && target.Contains('!'))
            return "shell:AppsFolder\\" + target;
        return target;
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
