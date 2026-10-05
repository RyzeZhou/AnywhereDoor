using System.IO;
using Awd.Core.Interop;

namespace Awd.Core;

/// <summary>
/// 文件夹批量扫描：把一个目录（含子目录）里的 exe 与 .lnk 快捷方式收成程序清单条目。
///
/// 存在的理由：「添加 exe 文件…」一次只能选一个，而便携程序、绿单游戏、批量装的小工具
/// 常常是"一整个文件夹十几个 exe"。逐个点文件对话框不现实。
///
/// 与 <see cref="AppInventory"/> 的分工：那边扫的是"系统认可的应用"（开始菜单 + 商店），
/// 这边扫的是"这个目录里有什么"，两者互补 —— 便携程序没有开始菜单 lnk，只有这条路能进来。
/// </summary>
public static class FolderScan
{
    /// <summary>一次扫描最多返回多少项：防止误指到 C:\ 或游戏库时把几万项塞进对话框。</summary>
    public const int MaxResults = 400;

    /// <summary>默认递归深度。软件目录通常 1~2 层；再深的多半是资源/缓存目录，扫了也没用。</summary>
    public const int DefaultMaxDepth = 3;

    public sealed class Result
    {
        public List<AppEntry> Entries { get; } = new();
        /// <summary>达到 <see cref="MaxResults"/> 上限就为 true —— 调用方应告诉用户"只取了前 N 项"。</summary>
        public bool Truncated { get; set; }
        public int ScannedFiles { get; set; }
    }

    /// <summary>
    /// 扫目录下的 *.exe 与 *.lnk。.lnk 会解析出真实目标与参数（解析不动的按"启动 lnk 自身"处理），
    /// 再按 <b>解析后的目标</b> 去重 —— 一个文件夹里常有同一个程序的多个快捷方式。
    /// </summary>
    public static Result Scan(string root, int maxDepth = DefaultMaxDepth)
    {
        var result = new Result();
        if (!Directory.Exists(root)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = maxDepth > 0,
            MaxRecursionDepth = Math.Max(0, maxDepth),
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var ext = Path.GetExtension(file);
            bool isExe = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
            bool isLnk = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            if (!isExe && !isLnk) continue;

            result.ScannedFiles++;

            if (isLnk)
            {
                ShellInterop.LnkInfo info;
                try
                {
                    info = ShellInterop.ResolveLnk(file);
                }
                catch
                {
                    continue;   // 坏 lnk：解析不动就不要，免得导入一堆点不开的东西
                }
                var target = info.Target.Length > 0 ? info.Target : file;
                var args = info.Arguments;
                if (!seen.Add(target + "|" + args)) continue;

                result.Entries.Add(new AppEntry
                {
                    Id = AppEntry.MakeId(target),
                    Name = Path.GetFileNameWithoutExtension(file),
                    Kind = AppKind.Lnk,
                    Target = target,
                    Arguments = args,
                    WorkingDir = string.IsNullOrWhiteSpace(info.WorkingDirectory) ? null : info.WorkingDirectory,
                });
            }
            else
            {
                if (!seen.Add(file)) continue;
                result.Entries.Add(new AppEntry
                {
                    Id = AppEntry.MakeId(file),
                    Name = Path.GetFileNameWithoutExtension(file),
                    Kind = AppKind.Exe,
                    Target = file,
                    // WorkingDir 留空 = 启动时默认 exe 所在目录（便携程序语义，见 AppLauncher）
                });
            }

            if (result.Entries.Count >= MaxResults)
            {
                result.Truncated = true;
                break;
            }
        }

        return result;
    }
}