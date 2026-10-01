using System.Security.Cryptography;
using System.Text;

namespace Awd.Core;

/// <summary>
/// 一条程序收藏 —— P0 的核心数据模型：数据是"应用清单"不是"文件夹"。
/// 四页（本地/远程/网站/程序）将来全部复用这套"收藏 + 摆放"结构。
/// </summary>
public sealed class AppEntry
{
    /// <summary>SHA256(Target) 前 8 位十六进制，稳定且天然去重。</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public AppKind Kind { get; set; }

    /// <summary>所属页面（本地/远程/网站/程序四页各管各的收藏与摆放）。缺省 Programs 兼容旧 JSON。</summary>
    public PageKind Page { get; set; }

    /// <summary>exe / .lnk 的完整路径，或 UWP 的 AUMID（形如 Xyz_hash!App）。</summary>
    public string Target { get; set; } = "";

    public string Arguments { get; set; } = "";

    /// <summary>exe 直启必须显式给工作目录；.lnk 交 shell 执行时以 lnk 自身设置为准。</summary>
    public string? WorkingDir { get; set; }

    public string? Group { get; set; }

    /// <summary>网格摆放位置，0 起、连续。</summary>
    public int Position { get; set; }

    /// <summary>图标缓存 PNG 路径；null 表示还没提取过。</summary>
    public string? IconPath { get; set; }

    public static string MakeId(string target)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(target.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    /// <summary>换地图时的副本。id 与 iconPath 都是 target 的函数，照抄即可。</summary>
    public AppEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        Page = Page,
        Target = Target,
        Arguments = Arguments,
        WorkingDir = WorkingDir,
        Group = Group,
        Position = Position,
        IconPath = IconPath,
    };
}
