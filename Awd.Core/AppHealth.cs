using System.IO;

namespace Awd.Core;

/// <summary>
/// 收藏目标的"还在不在"判定。
///
/// 立项时把数据模型定成"应用清单"（存 AUMID / exe 绝对路径），但清单会过期：
/// 程序被卸载、文件夹被挪走、换机器后 C:\ 下的路径全变。原先这些情况只有一种表现——
/// 双击时报错、或图标提取失败变成占位字形，磁贴看上去和好的混在一起，用户不知道哪个是真坏了。
///
/// 判定按 Kind 分流，判不出来的一律当"活着"（绝不误伤）：
/// - Exe / Lnk：文件还在吗
/// - Folder：目录还在吗
/// - Uwp：包族还在吗（AUMID 的 "!" 前半段）
/// - Web / Remote：不做判定（favicon 要联网、远程要问易远传，都不属于"本地可达性"）
/// </summary>
public static class AppHealth
{
    private static readonly Lazy<HashSet<string>> InstalledFamilies = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            // FindPackageForUser('') 在本机实测返回 NULL（见 IconCache 同处注释），枚举过滤才可靠
            foreach (var p in pm.FindPackagesForUser(string.Empty))
                set.Add(p.Id.FamilyName);
        }
        catch
        {
            // 包管理不可用（极老系统 / 权限）：集合为空 → Uwp 一律按"判不出来"处理，见下
        }
        return set;
    });

    /// <summary>这台机器上"确实已卸载"的包族。空集合表示枚举失败，不代表什么都没装。</summary>
    public static IReadOnlyCollection<string> KnownFamilies => InstalledFamilies.Value;

    /// <summary>
    /// 目标是否已失效。<paramref name="reason"/> 在失效时给出人话原因，给状态栏/提示用。
    /// 判不出来（枚举失败、形态不适用）返回 false —— 宁可漏报失灵，不可错报失效把好条目标灰。
    /// </summary>
    public static bool IsMissing(AppEntry entry, out string reason)
    {
        reason = "";
        switch (entry.Kind)
        {
            case AppKind.Exe:
            case AppKind.Lnk:
                if (!File.Exists(entry.Target))
                {
                    reason = "文件不在了（卸载、移动，或换机后路径变了）";
                    return true;
                }
                return false;

            case AppKind.Folder:
                if (!Directory.Exists(entry.Target))
                {
                    reason = "文件夹不在了（移动或删除）";
                    return true;
                }
                return false;

            case AppKind.Uwp:
                var families = InstalledFamilies.Value;
                if (families.Count == 0) return false;   // 枚举失败：判不出来，按活着处理
                var bang = entry.Target.IndexOf('!');
                if (bang <= 0) return false;             // 不是 AUMID 形态：判不出来
                if (!families.Contains(entry.Target[..bang]))
                {
                    reason = "应用已卸载";
                    return true;
                }
                return false;

            default:
                return false;   // Web / Remote：不在本地可达性范围内
        }
    }

    public static bool IsMissing(AppEntry entry) => IsMissing(entry, out _);
}