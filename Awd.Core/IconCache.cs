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
    /// <summary>
    /// 字形占画布的最小比例。低于这个值就不贴边裁（见 <see cref="TrimTransparent"/>），
    /// 改走"正方形限比例裁"—— 否则图标会顶满白卡、边缘发紧。
    /// 实测四档对比后取0.55：字形够大，又留得住呼吸，与系统图标视图最接近。
    /// </summary>
    private const double MinKeepRatio = 0.55;

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnywhereDoor", "iconcache");

    /// <summary>缓存文件的路径 —— 纯算路径，不碰 COM 也不建目录。
    /// 地图里因此**不需要存 iconPath**：由 target 就能推出来（它占了老文件 28% 的字节）。
    /// t1 = 裁剪版（v1 没裁透明留白）；t2 = 裁剪加限比例（t1 会贴边裁，图标顶满白卡发紧）；
    /// t3 = 内容判定的 alpha 阈值从 16 提到 128（治半透明晕圈把包围盒撑满，如 7-Zip）。
    /// 版本号在文件名里，改裁剪策略必须升版本 —— 否则旧缓存永远命中，改动看不见。
    ///
    /// <paramref name="size"/> 是**物理像素**（调用方负责按 DPI 换算，见 GUI 侧 DpiScale）。
    /// 尺寸进了文件名，所以换档位天然是"另一份缓存"，不会拿旧尺寸拉伸糊掉。</summary>
    public static string PathFor(string target, int size = DefaultSize) =>
   Path.Combine(CacheDir, $"{AppEntry.MakeId(target)}_{size}t3.png");

    /// <summary>
    /// 默认缓存档（物理像素）。取 512 而不是 256：巨缩略档120 DIP 在 175% 下
    /// 要210 物理像素，256 只够刚够，200% 缩放就超了 —— 缓存宁大勿小，
    /// 反正解码时 DecodePixelWidth 会按需降采样，放大存储不花钱。
    /// </summary>
    public const int DefaultSize = 512;

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

    public static ProbeResult Extract(string target, int size = DefaultSize)
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

        // shell 图标同样要裁：exe 内嵌的图标常常"满幅画布 + 中间小字形"
        //（实测 7-Zip 512×512 里字形只占 20%），不裁它在磁贴里小得离谱。
        // 包 logo 那条路裁了、这条没裁，是之前"有的图标大有的小"的另一半原因。
        if (TryTrimCachedFile(cachePath))
        {
            var (tw, th) = PngSize(cachePath);
            return new ProbeResult(nw, nh, cachePath, $"{tw}x{th}");
        }
        return new ProbeResult(nw, nh, cachePath, $"{cw}x{ch}");
    }

    /// <summary>
    /// 就地裁剪已落盘的缓存 PNG。成功返回 true（尺寸可能变小）。
    /// 读回字节重走一遍 <see cref="TrimTransparent"/> —— 复用同一套内容判定，
    /// 免得两条路各写一份、以后只改一处。
    /// </summary>
    private static bool TryTrimCachedFile(string cachePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(cachePath);
            var trimmed = TrimTransparent(bytes);
            if (trimmed == null) return false;
            // 裁完反而更大/没变小就别写了，免得白占 IO
            if (trimmed.Length >= bytes.Length) return false;
            File.WriteAllBytes(cachePath, trimmed);
            return true;
        }
        catch
        {
            return false;   // 裁剪失败就用原图，不拦提取
        }
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

/// <summary>背景基准色（四边中位色）。返回 b,g,r,a 四个分量。</summary>
    private static byte[] EstimateBackground(byte[] px, int stride, int w, int h)
    {
      var samples = new List<byte[]>(8);
        // 采样四边中点附近的一圈像素：角上常常正好压在边框/圆角上，中点更能代表底色
        int[] xs = { 0, w / 4, w / 2, w * 3 / 4, w - 1 };
        int[] ys = { 0, h / 4, h / 2, h * 3 / 4, h - 1 };
  foreach (var x in xs)
  {
         samples.Add(SampleAt(px, stride, x, 0));
            samples.Add(SampleAt(px, stride, x, h - 1));
        }
        foreach (var y in ys)
        {
     samples.Add(SampleAt(px, stride, 0, y));
            samples.Add(SampleAt(px, stride, w - 1, y));
        }
      var med = new byte[4];
        for (int c = 0; c < 4; c++)
     {
            var arr = samples.Select(s => s[c]).OrderBy(v => v).ToArray();
 med[c] = arr[arr.Length / 2];
    }
        return med;
    }

    private static byte[] SampleAt(byte[] px, int stride, int x, int y)
    {
        var i = y * stride + x * 4;
        return new[] { px[i], px[i + 1], px[i + 2], px[i + 3] };
    }

/// <summary>
    /// 这个像素算不算"图标内容"：alpha 高于 <see cref="ContentAlpha"/>，
    /// **且**与背景基准色差异够大。
    ///
    /// alpha 阈值取 128 而不是常见的 16，实测逼出来的：7-Zip 的 512×512 图四周有一圈
    /// alpha 16~79 的**半透明晕圈**（抗锯齿残留），按 16 算包围盒会被撑满整张 512×512，
    /// 于是字形（真身只有 44×34）被当成满幅图，裁剪彻底不起作用 —— 这就是 7z 字形
    /// 在磁贴里一直很小的真凶。提到 128 后它从 98% 骤降到 7%，另两个图标（计算器/Skype）
    /// 在 16 与 128 之间稳定在 48~63%，不受影响。
    ///
    /// 色差条件是补充：万一周围是**不透明**的深灰底（另有这类图标），
    /// 只查 alpha 同样会漏判，两者一起用覆盖得更全。
    /// </summary>
    private static bool IsContent(byte[] px, int i, byte[] bg)
    {
        if (px[i + 3] <= ContentAlpha) return false;
     var d = Math.Abs(px[i] - bg[0]) + Math.Abs(px[i + 1] - bg[1]) + Math.Abs(px[i + 2] - bg[2]);
        var da = Math.Abs(px[i + 3] - bg[3]);
        // 灰度底色时色差可能很小，放宽一点；色底色（品牌色）自然就超阈值了
 return d + da >= ContentThreshold;
    }

    /// <summary>内容判定的 alpha 下限。128 = 半透明晕圈之上、实心之上。</summary>
    private const byte ContentAlpha = 128;

    /// <summary>内容判定的色差阈值：RGB 之和 + alpha 差，24 约等于每通道差 8 级。</summary>
    private const int ContentThreshold = 24;

    /// <summary>
    /// 透明底 logo 常自带大片留白（实测计算器字形只占画布 11%），照原样铺进网格会显得小而空。
    /// 按"有效内容"边界裁 + 呼吸边即可解决 —— 但**不能贴边裁**：
    /// 裁到只剩字形时图标会顶满白卡、边缘发紧，与 Windows 图标视图的观感反着来。
    ///
 /// 实测四档对比（计算器，源 300×300，字形 90×114）：
    ///   贴字形+5% → 100×124  字形最大但顶满边缘（现状，观感"发紧"）
    ///   正方形55% → 165×165  字形偏大但四周有呼吸（最接近系统图标视图）
    ///   不裁     → 300×300  字形只占 1/3 画布（观感"空"）
    ///
    /// 所以规则是：内容占画布不足 <see cref="MinKeepRatio"/> 时，**放弃贴边裁**，
    /// 改成以内容中心裁一个正方形、边长为画布的 <see cref="MinKeepRatio"/>——
    /// 既把留白去掉一部分，又保证四周留得住呼吸。
    /// 全透明（裁不出内容）返回 null，交给调用方用原图。
    ///
    /// <b>内容边界不能只看 alpha</b>：实测 7-Zip 的 512×512 图里"7z"字形只占 20%，
    /// 但四周是**不透明**的深灰底（不是透明），只查 alpha 会判成"满幅图"完全不裁，
    /// 于是字形在磁贴里小得离谱。所以要先取四角的底色做参考，
    /// 把"与底色差异够大"的像素才算内容。
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

// 背景基准色 = 四角像素的中位色。透明底算出来的中位色 alpha≈0，
        // 于是"与它差异大"退化成"alpha 高"，正好覆盖原来只查 alpha 的那条路径；
        // 不透明深灰底的图标（7-Zip）则得到那个灰，比较时能把大片底色排除掉。
     var bg = EstimateBackground(px, stride, w, h);

      int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
    var row = y * stride;
     for (int x = 0; x < w; x++)
    {
         var i = row + x * 4;
     if (!IsContent(px, i, bg)) continue;
        if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
    if (y > maxY) maxY = y;
          }
        }
      if (maxX < 0) return null;

      var box = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
     var shortSide = (double)Math.Min(w, h);
     var glyphRatio = (double)Math.Min(box.Width, box.Height) / shortSide;

            Int32Rect crop;
            if (glyphRatio >= MinKeepRatio)
            {
                // 字形已经占得够大，贴边裁 + 5% 呼吸边即可
                var pad = Math.Max(2, Math.Max(box.Width, box.Height) / 20);
                var x0 = Math.Max(0, box.X - pad);
                var y0 = Math.Max(0, box.Y - pad);
                crop = new Int32Rect(
                    x0, y0,
                    Math.Min(w, box.X + box.Width + pad) - x0,
                    Math.Min(h, box.Y + box.Height + pad) - y0);
            }
            else
            {
                // 字形太小（logo 自带大片留白）：不贴边裁，改以字形中心取一个正方形，
// 边长 = 画布 × MinKeepRatio。这样去掉了部分留白，四周还留得住呼吸。
          int side = (int)Math.Ceiling(shortSide * MinKeepRatio);
          side = Math.Max(side, Math.Max(box.Width, box.Height));   // 至少装得下内容
          side = Math.Min(side, Math.Min(w, h));                // 不能超出画布
      // 内容比画布还大（异常图/极端比例）：退化为整幅，交给上面那条贴边裁或原图
          if (side < 1) return null;
       var cx = box.X + box.Width / 2;
      var cy = box.Y + box.Height / 2;
  var x0 = Math.Max(0, Math.Min(w - side, cx - side / 2));
    var y0 = Math.Max(0, Math.Min(h - side, cy - side / 2));
   crop = new Int32Rect(x0, y0, side, side);
         }

        // 最后一道闸：CroppedBitmap 对越界/非正尺寸直接抛
      // "Value does not fall within the expected range"，宁可原图也不让它炸整条提取链。
        if (crop.Width <= 0 || crop.Height <= 0 ||
       crop.X < 0 || crop.Y < 0 ||
     crop.X + crop.Width > w || crop.Y + crop.Height > h)
      return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bgra, crop)));
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
