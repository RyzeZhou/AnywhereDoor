namespace Awd.Core;

/// <summary>
/// 任意门的四个页面（四类目标各占一页、每页独立收藏）。
/// Programs=0 是刻意安排：旧版 favorites.json 没有 page 字段时反序列化落回 0，
/// 存量收藏自然归入程序页。
/// </summary>
public enum PageKind
{
    Programs,
    Local,
    Web,
    Remote,
}
