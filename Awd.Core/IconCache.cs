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
 /// 裁剪时在字形外留的呼吸边，按**字形自身最大边**的比例。
    /// 0.15 = 字形 44px → 留 6px（实测 7-Zip 裁成 60×50，磁贴里清晰可辨）。
    ///
    /// 曾经用过的错法：字形不足 <c>MinKeepRatio(0.55)</c> 时改走"正方形限比例裁"。
    /// 那条规则保护了留白，却把小字形**压死**了 —— 7-Zip 源512×512 里"7z"字形只有
  /// 44×34（占 8.5%），限比例裁出 282×282 后字形**仍只占 12%**，
    /// 在磁贴里小到几乎看不见；贴字形裁得到 60×50，一眼可辨。
    /// > 教训：一个规则"在已测样本上好看"不等于它对**未测样本**也成立。
    /// 当时的四档对比全是同一类图（字形居中、底色透明），看不出这个偏差。
    /// </summary>
    private const double PadRatio = 0.15;

    public static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnywhereDoor", "iconcache");

/// <summary>缓存文件的路径 —— 纯算路径，不碰 COM 也不建目录。
    /// 地图里因此**不需要存 iconPath**：由 target 就能推出来（它占了老文件 28% 的字节）。
    /// t1 = 裁剪版；t2 = 裁剪加限比例；t3 = 内容判定 alpha 16→128（治半透明晕圈撑满包围盒）；
    /// t4 = 取图与显示解耦（一律存 512 档）；t5 = 裁剪改贴字形 + 留白按字形比例（限比例裁会把小字形压死）。
    /// 版本号在文件名里，改裁剪/取图策略必须升版本 —— 否则旧缓存永远命中，改动看不见。
    ///
    /// <b>为什么不带尺寸参数</b>：曾经按尺寸分档存过，实测发现两个问题 ——
    /// ① 不同槽位各存一份、磁盘重复；② 裁剪后尺寸由内容决定，与请求尺寸无关，
    /// 分档存根本对不齐（77 和 210 裁出来都是同一张 282）。
    /// 一次取最大、显示时采样，磁盘一份就够，画质也不差（WPF 的双三次滤波够好）。</summary>
    public static string PathFor(string target) =>
        Path.Combine(CacheDir, $"{AppEntry.MakeId(target)}_{DefaultSize}t6.png");

    /// <summary>
    /// 缓存与取图都用这个尺寸（物理像素）。
    /// 取 512 而不是 256：巨缩略档 120 DIP 在 175% 下要 210 物理像素，200% 缩放要 240，
    /// 256 就该不够了。往上也用不着更大 —— 真档位到 512 就封顶，见 <see cref="FetchSizeCap"/>。
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

/// <summary>
    /// 提取图标并落缓存。<b>缓存的一律是"取最大 + 裁剪归一化"那份，不按请求尺寸分档。</b>
    ///
    /// 理由（探针实测）：shell 的真档位到 512 就封顶 —— 把 1024 缩回 512 与直接请求 512
    /// 逐像素一致（差 0.28~0.87），说明再大都是插值。所以取 512 就拿到了全部真细节。
    /// 显示尺寸交给 WPF 的 <c>DecodePixelWidth</c> 做一次高质量降采样即可 ——
    /// 比"按每个槽位尺寸各存一份"更省磁盘，而且 WPF 的双三次滤波比我们自己做的好。
    ///
/// <paramref name="size"/> 只影响"向 shell 要多大的图"（见 <see cref="FetchSizeCap"/>），
    /// 不影响落盘尺寸；调用方要按显示尺寸缩放就自己传给 Decode。
    /// </summary>
    public static ProbeResult Extract(string target, int size = DefaultSize)
    {
   Directory.CreateDirectory(CacheDir);
        var cachePath = PathFor(target);

        // 缓存命中直接回（读 PNG 实际尺寸当 natural），省掉每次启动的全量 COM 重跑
        if (File.Exists(cachePath))
     {
  var (w0, h0) = PngSize(cachePath);
          return new ProbeResult(w0, h0, cachePath, $"{w0}x{h0}");
        }

    // UWP：包 logo 是透明底原图，先试；不行再走 shell 磁贴图（带磁贴底色）
    if (IsUwp(target))
 {
            var logo = TryPackageLogo(target, FetchSizeCap);
  if (logo != null)
            {
                var trimmed = TrimTransparent(logo) ?? logo;
        File.WriteAllBytes(cachePath, trimmed);
          var (lw, lh) = PngSize(cachePath);
  return new ProbeResult(lw, lh, cachePath, $"{lw}x{lh}");
            }
}

        var parseName = ToParseName(target);
     var (nw, nh) = ProbeNatural(parseName, FetchSizeCap);
        var (cw, ch) = SavePng(parseName, FetchSizeCap, cachePath);

   // shell 图标同样要裁：exe 内嵌的图标常常"满幅画布 + 中间小字形"
        //（实测 7-Zip 512×512 里字形只占 20%），不裁它在磁贴里小得离谱。
        // 包 logo 那条路裁了、这条没裁，是之前"有的图标大有的小"的另一半原因。
      TryTrimCachedFile(cachePath);
        var (fw, fh) = PngSize(cachePath);
        return new ProbeResult(nw, nh, cachePath, $"{fw}x{fh}");
    }

    /// <summary>
    /// 向 shell 取图用的尺寸（物理像素）。
    ///
    /// 为什么不是"越大越好"：真档位到 512 就封顶，多要只是浪费内存和时间。
    /// 取 512 这档：覆盖到巨缩略档（120 DIP × 200% = 240 物理像素）仍有真细节。
    /// </summary>
    private const int FetchSizeCap = 512;

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
    public static List<ProbeItem> ProbeAll(IEnumerable<AppEntry> apps, int size = DefaultSize)
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
    /// 找"图标内容"的紧致包围盒。三条判据组合，覆盖三类底色：
    ///
    /// 1. **alpha &gt; <see cref="ContentAlpha"/>** —— 治透明底的半透明晕圈
    ///    （7-Zip 512×512 四周那圈 alpha 16~79 的抗锯齿残留会把包围盒撑满整张）。
    /// 2. **与四边中位色差异够大** —— 治不透明纯色底。
    /// 3. **局部梯度够强** —— 治**品牌色底**（最阴的一种，实测 7-Zip 是橙色底 + 中间小白标）。
    ///
    /// 第 3 条是关键：橙色底上从四边取样，取到的就是橙色本身，于是"与底色有差异"
 /// 只会在中间的白色字形处成立 —— 而字形只占 20%，剩下大片橙色被判成背景，
 /// 裁剪把画布从 512 缩到 282 但**字形仍只占 20%**，在磁贴里还是小得离谱。
    ///
    /// 改用**梯度**（与右/下邻居的亮度差）就稳了：平坦的橙色底梯度≈0，
    /// 字形轮廓处梯度剧烈。实测 7-Zip 这样能把 20% 的字形完整框出来。
  /// </summary>
    private static RectInt ContentBox(byte[] px, int stride, int w, int h)
    {
        var bg = EstimateBackground(px, stride, w, h);
        int minX = w, minY = h, maxX = -1, maxY = -1;
 for (int y = 0; y < h; y++)
        {
            int row = y * stride;
          for (int x = 0; x < w; x++)
            {
       var i = row + x * 4;
    if (!IsContent(px, i, bg, stride, x, y, w, h)) continue;
         if (x < minX) minX = x;
    if (x > maxX) maxX = x;
       if (y < minY) minY = y;
  if (y > maxY) maxY = y;
   }
        }
        return maxX < 0 ? new RectInt(0, 0, 0, 0) : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>整数矩形。不用 WPF 的 Int32Rect —— 那个允许负宽，出错时不报反而更难查。</summary>
  private readonly record struct RectInt(int X, int Y, int Width, int Height);

    /// <summary>
    /// 这个像素算不算"图标内容"：alpha 够高，且**要么**与背景底色差异够大、
    /// **要么**局部梯度够强（后者治品牌色底，见 <see cref="ContentBox"/>）。
    /// </summary>
    private static bool IsContent(byte[] px, int i, byte[] bg, int stride, int x, int y, int w, int h)
{
        if (px[i + 3] <= ContentAlpha) return false;
        var d = Math.Abs(px[i] - bg[0]) + Math.Abs(px[i + 1] - bg[1]) + Math.Abs(px[i + 2] - bg[2])
    + Math.Abs(px[i + 3] - bg[3]);
   if (d >= ContentThreshold) return true;

        // 梯度：与右邻居、下邻居的亮度差。右/下边界像素不算（没有邻居）。
    if (x + 1 >= w || y + 1 >= h) return false;
  var right = i + 4;
        var down = i + stride * 4;
        int lum = px[i] + px[i + 1] + px[i + 2];
        int lumR = px[right] + px[right + 1] + px[right + 2];
        int lumD = px[down] + px[down + 1] + px[down + 2];
        return Math.Abs(lum - lumR) + Math.Abs(lum - lumD) >= GradientThreshold;
    }

    /// <summary>内容判定的 alpha 下限。128 = 半透明晕圈之上、实心之上。</summary>
    private const byte ContentAlpha = 128;

    /// <summary>内容判定的色差阈值：RGB 之和 + alpha 差，24 约等于每通道差 8 级。</summary>
    private const int ContentThreshold = 24;

    /// <summary>
    /// 梯度阈值：与右+下邻居的亮度差之和，60 约等于每通道差 20 级。
    /// 定得偏高以免把抗锯齿边缘当内容（那是噪声），偏低会漏掉低对比度字形。
    /// </summary>
    private const int GradientThreshold = 60;

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
    /// 规则：**贴字形裁 + 按字形自身比例留白**（<see cref="PadRatio"/>）。
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

       // 内容包围盒：三条判据（alpha / 底色差 / 梯度）合起来判定，见 ContentBox。
        var content = ContentBox(px, stride, w, h);
        if (content.Width <= 0 || content.Height <= 0) return null;
        var box = new Int32Rect(content.X, content.Y, content.Width, content.Height);

        // 贴字形裁 + 按字形尺寸的百分比留白。
      //
        // 为什么不用"限比例裁"（曾试过：字形不足 55% 就取画布×55% 的正方形）：
        // 那个规则保护了留白，却把小字形**压死**了 —— 实测 7-Zip 源 512×512 里
      // "7z" 字形只有 44×34（占 8.5%），限比例裁出 282×282 后字形仍占 12%，
        // 在磁贴里小到几乎看不见。而贴字形裁得到 60×50，字形清晰可辨。
      //
        // 留白按**字形自身**的比例给（PadRatio），不按画布：字形小就少留、
        // 字形本就满幅就多留，两种情况观感统一。
        var glyphMax = Math.Max(box.Width, box.Height);
        var pad = Math.Max(2, (int)(glyphMax * PadRatio));
        var x0 = Math.Max(0, box.X - pad);
     var y0 = Math.Max(0, box.Y - pad);
        var crop = new Int32Rect(
   x0, y0,
            Math.Min(w, box.X + box.Width + pad) - x0,
            Math.Min(h, box.Y + box.Height + pad) - y0);

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
