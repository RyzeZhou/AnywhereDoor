using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Awd.Core.Interop;

/// <summary>
/// 手写的 shell COM/PInvoke 互操作（零外部依赖）。两条铁律：
/// ① ComImport 接口的方法必须按原生 vtable 顺序排列，且只声明到用到的最后一个槽位
///    （中间槽位即使不调用也要占位声明，尾部槽位一律省略）；
/// ② 显示名一律走 IShellItem.GetDisplayName，不碰 STRRET——托管侧结构体大小对不齐会写穿内存。
/// </summary>
internal static class ShellInterop
{
    // ---- 图标提取标志（SIIGBF_）----
    internal const int SIIGBF_RESIZETOFIT = 0x0;   // 拉伸/缩放到请求尺寸
    internal const int SIIGBF_BIGGERSIZEOK = 0x1;  // 源图更大就给原图；更小则给原图尺寸（不放大）
    internal const int SIIGBF_ICONONLY = 0x4;      // 只取图标，不取文件缩略图

    // ---- 显示名标志（SIGDN_）----
    internal const uint SIGDN_NORMALDISPLAY = 0x0;                // 界面显示名
    internal const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001; // 相对父文件夹的解析名 = AppsFolder 里的 AUMID
    internal const uint SIGDN_FILESYSPATH = 0x80058000;           // 真实文件系统路径（UWP/商店应用没有，返回失败）

    private const uint SHCONTF_FOLDERS = 0x20;
    private const uint SHCONTF_NONFOLDERS = 0x40;
    private const uint SHCONTF_INCLUDEHIDDEN = 0x80;

    private static readonly Guid FolderIdPrograms = new("a77f5d77-2e2b-44c3-a6a2-aba601054a51");        // FOLDERID_Programs：当前用户 开始菜单\Programs
    private static readonly Guid FolderIdCommonPrograms = new("0139d44e-6afe-49f2-8690-3dafcae6ffb8");  // FOLDERID_CommonPrograms：所有用户 开始菜单\Programs
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    // ================== COM 接口 ==================

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, in Guid bhid, in Guid riid, out IntPtr ppv);  // 槽位 0，占位不调用
        [PreserveSig] int GetParent(out IShellItem ppsi);                                         // 槽位 1，占位不调用
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName); // 槽位 2
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        // 启动 UWP 的唯一正道。槽位 1、2（ActivateForFile / ActivateForProtocol）不调用，省略。
        [PreserveSig] int ActivateApplication(string appUserModelId, string arguments, int options, out uint processId);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName, out uint pchEaten, out IntPtr ppidl, IntPtr pdwAttributes); // 槽位 0，占位不调用
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IEnumIDList ppenumIDList);  // 槽位 1
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        // 槽位 0..15 必须严格按 IShellLink 原生顺序；Resolve（槽位 16）不调用，省略。
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        public int Width;
        public int Height;
    }

    /// <summary>GDI GetObject 用的 BITMAP 结构，只为读宽高。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public short Planes;
        public short BitsPixel;
        public IntPtr Bits;
    }

    internal sealed record LnkInfo(string Target, string WorkingDirectory, string Arguments);

    // ================== 原生函数 ==================

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemWithParent(IntPtr pidlParent, IShellFolder psfParent, IntPtr pidlChild, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    // psfParent 传 NULL 表示从桌面开始绑定
    [DllImport("shell32", PreserveSig = false)]
    private static extern void SHBindToObject(IntPtr psfParent, IntPtr pidl, IntPtr pbc, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr pszPath);

    [DllImport("ole32")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("gdi32")]
    private static extern int DeleteObject(IntPtr hObj);

    [DllImport("gdi32")]
    private static extern int GetObject(IntPtr hGdiObj, int cbBuffer, out NativeBitmap lpObject);

    // ================== 封装 ==================

    /// <summary>把解析名（文件路径、shell:AppsFolder\AUMID 等）变成图标工厂。</summary>
    internal static IShellItemImageFactory CreateItemImageFactory(string parseName)
    {
        SHCreateItemFromParsingName(parseName, IntPtr.Zero, IidShellItemImageFactory, out var obj);
        return (IShellItemImageFactory)obj;
    }

    internal static IShellItem CreateShellItem(string parseName)
    {
        SHCreateItemFromParsingName(parseName, IntPtr.Zero, IidShellItem, out var obj);
        return (IShellItem)obj;
    }

    /// <summary>绑定 shell:AppsFolder 自身（psfParent = NULL 即桌面）。</summary>
    internal static IShellFolder BindAppsFolder()
    {
        SHParseDisplayName("shell:AppsFolder", IntPtr.Zero, out var pidl, 0, out _);
        try
        {
            SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, IidShellFolder, out var obj);
            return (IShellFolder)obj;
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }

    /// <summary>枚举 shell:AppsFolder 一级子项，返回 (显示名, AUMID)。个别取不到名字的项直接跳过。</summary>
    internal static IEnumerable<(string Name, string Aumid)> EnumerateAppsFolderItems()
    {
        var folder = BindAppsFolder();
        folder.EnumObjects(IntPtr.Zero, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, out var items);

        while (items.Next(1, out var pidl, out var fetched) == 0 && fetched == 1)
        {
            (string Name, string Aumid)? found = null;
            try
            {
                // pidlParent 为 NULL 且 psf 给定时，pidlChild 按 psf 的子项解释
                SHCreateItemWithParent(IntPtr.Zero, folder, pidl, IidShellItem, out var obj);
                var item = (IShellItem)obj;
                item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
                item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out var aumid);
                if (!string.IsNullOrWhiteSpace(aumid))
                    found = (string.IsNullOrWhiteSpace(name) ? aumid : name, aumid);
            }
            catch
            {
                // 个别项取不到名字：跳过，不让整个清单挂掉
            }
            finally
            {
                CoTaskMemFree(pidl);
            }

            if (found != null)
                yield return found.Value;
        }
    }

    /// <summary>两个"开始菜单\Programs"目录：当前用户 + 所有用户。</summary>
    internal static IEnumerable<string> StartMenuProgramDirs()
    {
        if (GetKnownFolderPath(FolderIdPrograms) is { } userDir)
            yield return userDir;
        if (GetKnownFolderPath(FolderIdCommonPrograms) is { } commonDir)
            yield return commonDir;
    }

    internal static string? GetKnownFolderPath(Guid folderId)
    {
        var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out var ptr);
        if (hr != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            CoTaskMemFree(ptr);
        }
    }

    /// <summary>解析 .lnk：目标 / 工作目录 / 参数。取不到目标的 lnk（比如指向 PIDL 的）Target 返回空串。</summary>
    internal static LnkInfo ResolveLnk(string lnkPath)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellLink))!;
        var persist = (IPersistFile)link;
        persist.Load(lnkPath, 0 /* STGM_READ */);

        var target = new StringBuilder(1024);
        link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
        var workDir = new StringBuilder(1024);
        link.GetWorkingDirectory(workDir, workDir.Capacity);
        var args = new StringBuilder(1024);
        link.GetArguments(args, args.Capacity);

        return new LnkInfo(target.ToString(), workDir.ToString(), args.ToString());
    }

    internal static IApplicationActivationManager CreateActivationManager()
        => (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidApplicationActivationManager))!;

    internal static void FreeHBitmap(IntPtr hbm)
    {
        if (hbm != IntPtr.Zero)
            DeleteObject(hbm);
    }

    internal static NativeBitmap GetBitmapInfo(IntPtr hbm)
    {
        GetObject(hbm, Marshal.SizeOf<NativeBitmap>(), out var bm);
        return bm;
    }
}
