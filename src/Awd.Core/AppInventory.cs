using System.IO;
using Awd.Core.Interop;

namespace Awd.Core;

/// <summary>
/// 应用清单枚举：开始菜单 .lnk（传统程序）+ shell:AppsFolder（UWP）。
/// 只做元数据，不做图标提取（那是 IconCache 的事），保证枚举几百项也是亚秒级。
/// </summary>
public static class AppInventory
{
    public static IReadOnlyList<AppEntry> EnumerateAll()
    {
        var list = new List<AppEntry>();
        AddLnkEntries(list);
        AddUwpEntries(list);
        return list;
    }

    private static void AddLnkEntries(List<AppEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lnk in StartMenuLnkFiles())
        {
            ShellInterop.LnkInfo info;
            try
            {
                info = ShellInterop.ResolveLnk(lnk);
            }
            catch
            {
                // 个别坏 lnk：解析不动就按"启动 lnk 自身"处理
                info = new ShellInterop.LnkInfo("", "", "");
            }

            var target = info.Target.Length > 0 ? info.Target : lnk;
            var key = target + "|" + info.Arguments;
            // 用户目录与所有用户目录常有同一份 lnk，按 目标+参数 去重
            if (!seen.Add(key)) continue;

            list.Add(new AppEntry
            {
                Id = AppEntry.MakeId(key),
                Name = Path.GetFileNameWithoutExtension(lnk),
                Kind = AppKind.Lnk,
                Target = target,
                Arguments = info.Arguments,
                WorkingDir = string.IsNullOrWhiteSpace(info.WorkingDirectory) ? null : info.WorkingDirectory,
            });
        }
    }

    private static IEnumerable<string> StartMenuLnkFiles()
    {
        foreach (var dir in ShellInterop.StartMenuProgramDirs())
        {
            if (!Directory.Exists(dir)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk", options))
                yield return lnk;
        }
    }

    private static void AddUwpEntries(List<AppEntry> list)
    {
        foreach (var (name, aumid) in ShellInterop.EnumerateAppsFolderItems())
        {
            list.Add(new AppEntry
            {
                Id = AppEntry.MakeId(aumid),
                Name = string.IsNullOrWhiteSpace(name) ? aumid : name,
                Kind = AppKind.Uwp,
                Target = aumid,
            });
        }
    }
}
