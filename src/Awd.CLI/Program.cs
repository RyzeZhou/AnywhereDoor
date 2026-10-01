using System.IO;
using System.Text;
using Awd.Core;

namespace Awd.Cli;

/// <summary>
/// 任意门（AWD）后端的命令行驱动：先在命令行把"枚举 / 图标 / 启动 / 收藏"全链路调通，
/// WPF 网格（GUI）等验证过了再接——和 FSY 同一条"先 CLI 后 GUI"的路线。
/// </summary>
internal static class Program
{
    [STAThread] // shell COM 与 WPF 位图编码都偏爱 STA，控制台主线程标一下最稳
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return args.Length == 0 ? Usage() : Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            任意门（AWD）命令行后端 —— P0 程序启动台的核心验证工具

            用法: awd <命令> [参数]
              paths                                显示收藏与图标缓存路径（目录不存在则创建）
              apps   [--find 关键词] [--kind lnk|exe|uwp]       枚举已安装程序
              icon   <目标|AUMID|@id> [--size 256]              图标探测 + 生成缓存 PNG
              icons  [--find 关键词] [--kind lnk|exe|uwp] [--limit N] [--size 256]
                                                   批量探测：真实尺寸分档统计
              launch <目标|AUMID|@id>                           启动（打印通道与 PID）
              fav    add <目标|AUMID> [--name X] [--group G]    收藏管理
                     [--page 程序|本地|网站|远程] [--kind 名]
                     list [--page P] [--group G]
                                                   无状态筛选：--group 日历 只看这组，- 表示未分类
                     move <id> <页内位置>          位置是"该页内"的序号（与 GUI 摆放同一口径）
                     group <id> <分组名|->         归组 / 脱离（等价于 GUI 把条目拖到侧栏）
                     remove <id>
              group  list [--page P]              分组登记表（含只在条目上出现过的）
                     add <名称> [--page P]
                     rename <旧> <新> [--page P]   连带改这些条目的归属
                     remove <名称> [--page P]      条目回未分类，不删条目
                     color <名称> <颜色|-> [--page P]
                                                   颜色按中英对照预设名给，也收 #RRGGBB 或序号

            颜色预设（GUI 右键色盘就是这十项，两边同名）：
              1红red 2橙orange 3黄gold 4绿green 5青teal 6蓝blue 7紫purple 8粉hotpink 9棕brown 10灰gray

            目标写法：exe / .lnk 用完整路径；UWP 用 AUMID（形如 Xyz_hash!App）；
            @id 表示按收藏列表里的 id 引用。
            页面写法：程序/programs/0，本地/local/1，网站/web/2，远程/remote/3。
            注意：GUI 启动时才读这三个 JSON，CLI 改完要重启 GUI 才看得见。
            """);
        return 0;
    }

    private static int Dispatch(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        return cmd switch
        {
            "paths" => CmdPaths(),
            "apps" => CmdApps(rest),
            "icon" => CmdIcon(rest),
            "icons" => CmdIcons(rest),
            "launch" => CmdLaunch(rest),
            "fav" => CmdFav(rest),
            "group" => CmdGroup(rest),
            "help" or "--help" or "-h" => Usage(),
            _ => Unknown(cmd),
        };
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令：{cmd}（awd help 看用法）");
        return 1;
    }

    // ================== 命令 ==================

    private static int CmdPaths()
    {
        Directory.CreateDirectory(FavoritesStore.DataDir);
        Directory.CreateDirectory(IconCache.CacheDir);
        Console.WriteLine($"收藏文件   {FavoritesStore.FilePath}");
        Console.WriteLine($"图标缓存   {IconCache.CacheDir}");
        Console.WriteLine("（两个目录已确保存在）");
        return 0;
    }

    private static int CmdApps(string[] args)
    {
        var p = Parse(args);
        var find = p.Option("find");
        var kind = p.Option("kind");

        IEnumerable<AppEntry> query = AppInventory.EnumerateAll();
        if (find != null)
            query = query.Where(a => a.Name.Contains(find, StringComparison.OrdinalIgnoreCase));
        if (kind != null)
            query = query.Where(a => a.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase));

        var list = query
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var a in list)
        {
            var extra = a.Kind == AppKind.Uwp ? "" : $"  工作目录={a.WorkingDir ?? "(exe 同目录)"}";
            var suffix = string.IsNullOrEmpty(a.Arguments) ? "" : " " + a.Arguments;
            Console.WriteLine($"[{a.Kind,-3}] {a.Name}  ←  {a.Target}{suffix}{extra}");
        }
        Console.WriteLine($"共 {list.Count} 项（lnk=开始菜单快捷方式，exe=直接路径，uwp=AUMID）");
        return 0;
    }

    private static int CmdIcon(string[] args)
    {
        var p = Parse(args);
        var spec = p.Positional.FirstOrDefault()
            ?? throw new InvalidOperationException("缺少目标（路径 / AUMID / @id）");
        var size = p.IntOption("size", 256);

        var app = ResolveEntry(spec, lookupName: true);
        var result = IconCache.Extract(app.Target, size);
        Console.WriteLine($"名称     {app.Name}");
        Console.WriteLine($"目标     {app.Target}（{app.Kind}）");
        Console.WriteLine($"真实尺寸 {result.NaturalWidth}x{result.NaturalHeight}   ← 源里最大的图标，\"是不是只有小图标\"看这里");
        Console.WriteLine($"缓存 PNG {result.CachePath}（{result.CacheSizeText}）");
        return 0;
    }

    private static int CmdIcons(string[] args)
    {
        var p = Parse(args);
        var size = p.IntOption("size", 256);
        var find = p.Option("find");
        var kind = p.Option("kind");
        var limit = p.IntOption("limit", 0);

        IEnumerable<AppEntry> query = AppInventory.EnumerateAll();
        if (find != null)
            query = query.Where(a => a.Name.Contains(find, StringComparison.OrdinalIgnoreCase));
        if (kind != null)
            query = query.Where(a => a.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (limit > 0)
            query = query.Take(limit);

        var probes = IconCache.ProbeAll(query, size);
        var ok = probes.Where(x => x.Result != null).ToList();
        var big = ok.Count(x => x.Result!.NaturalWidth >= size);
        var mid = ok.Count(x => x.Result!.NaturalWidth >= 64 && x.Result!.NaturalWidth < size);
        var small = ok.Count(x => x.Result!.NaturalWidth < 64);
        var fail = probes.Where(x => x.Result == null).ToList();

        Console.WriteLine($"探测 {probes.Count} 项，请求尺寸 {size}px：");
        Console.WriteLine($"  >= {size}px（理想）      {big}");
        Console.WriteLine($"  64 ~ {size - 1}px（可拉伸） {mid}");
        Console.WriteLine($"  < 64px（模糊风险）    {small}");
        Console.WriteLine($"  失败                 {fail.Count}");
        foreach (var x in probes)
        {
            var r = x.Result;
            Console.WriteLine(r == null
                ? $"  [失败] {x.App.Name}  ←  {x.App.Target}  :  {x.Error}"
                : $"  [{r.NaturalWidth}x{r.NaturalHeight}] {x.App.Name}");
        }
        return 0;
    }

    private static int CmdLaunch(string[] args)
    {
        var spec = Parse(args).Positional.FirstOrDefault()
            ?? throw new InvalidOperationException("缺少目标（路径 / AUMID / @id）");
        var app = ResolveEntry(spec, lookupName: true);
        var result = AppLauncher.Launch(app);
        Console.WriteLine($"已启动：{app.Name}");
        Console.WriteLine($"通道   {result.Method}");
        Console.WriteLine($"PID    {result.ProcessId?.ToString() ?? "（shell 启动，拿不到）"}");
        return 0;
    }

    private static int CmdFav(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：awd fav add|list|move|group|remove ...（awd help 看参数）");
            return 1;
        }

        var sub = args[0].ToLowerInvariant();
        var p = Parse(args.Skip(1).ToArray());
        var store = new FavoritesStore();
        var favs = store.Load();

        switch (sub)
        {
            case "add":
            {
                var spec = p.Positional.FirstOrDefault()
                    ?? throw new InvalidOperationException("缺少目标（路径 / AUMID）");
                var entry = Describe(spec);

                var existing = favs.FirstOrDefault(f =>
                    f.Target.Equals(entry.Target, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    Console.WriteLine($"已在收藏（id={existing.Id}），不重复添加");
                    return 0;
                }

                var page = ParsePage(p.Option("page"));
                entry.Page = page;
                // 不写 --kind 时按页推：本地=文件夹、网站=web、远程=erf:；程序页沿用路径/AUMID 的形态猜测
                entry.Kind = p.Option("kind") != null
                    ? ParseKind(p.Option("kind")!)
                    : page switch
                    {
                        PageKind.Local => AppKind.Folder,
                        PageKind.Web => AppKind.Web,
                        PageKind.Remote => AppKind.Remote,
                        _ => entry.Kind,
                    };

                // UWP 的显示名从应用清单里查，比裸 AUMID 好看（只有程序页有这份清单）
                if (p.Option("name") == null && page == PageKind.Programs)
                {
                    var hit = FindInInventory(entry.Target);
                    if (hit != null) entry.Name = hit.Name;
                }
                entry.Name = p.Option("name") ?? entry.Name;
                entry.Group = NormalizeGroup(p.Option("group"));
                entry.Position = favs.Count(f => f.Page == page);   // 追加到该页末尾（页内 0 起）
                entry.IconPath = page == PageKind.Programs ? TryCacheIcon(entry) : null;

                favs.Add(entry);
                RenumberByPage(favs);   // 新条目在该页列表末尾，顺手把这一页的序号归整
                store.Save(favs);
                Console.WriteLine($"已添加 [{entry.Id}] {entry.Name}（{PageLabel(page)} 第 {entry.Position} 位，" +
                                  $"组={entry.Group ?? GroupStore.UnfiledName}）");
                WarnIfUnregistered(entry.Page, entry.Group);
                return 0;
            }

            case "list":
            {
                var pageSpec = p.Option("page");
                var groupSpec = p.Option("group");
                IEnumerable<AppEntry> q = favs;
                if (pageSpec != null) { var pg = ParsePage(pageSpec); q = q.Where(e => e.Page == pg); }
                if (groupSpec != null)
                {
                    var want = NormalizeGroup(groupSpec);
                    q = want == null ? q.Where(e => string.IsNullOrEmpty(e.Group))
                                     : q.Where(e => e.Group == want);
                }

                var list = q.OrderBy(e => e.Page).ThenBy(e => e.Position).ToList();
                if (list.Count == 0)
                {
                    Console.WriteLine(groupSpec != null
                        ? $"没有落在「{groupSpec}」的收藏（awd group list 看这页有哪些分组）"
                        : "收藏为空");
                    return 0;
                }
                foreach (var f in list)
                {
                    var icon = f.IconPath == null ? "" : "  [icon OK]";
                    Console.WriteLine($"#{f.Position,-3} [{f.Id}] ({f.Kind}/{PageLabel(f.Page)}) {f.Name}" +
                                      $"  ←  {f.Target}  组={f.Group ?? GroupStore.UnfiledName}{icon}");
                }
                var scope = (pageSpec != null ? PageLabel(ParsePage(pageSpec)) : "未限页") +
                            (groupSpec != null ? $" × 「{groupSpec}」" : "");
                Console.WriteLine($"共 {list.Count} 项（{scope}）");
                return 0;
            }

            case "move":
            {
                var id = p.Positional.ElementAtOrDefault(0)
                    ?? throw new InvalidOperationException("缺少 id");
                var posText = p.Positional.ElementAtOrDefault(1)
                    ?? throw new InvalidOperationException("缺少目标位置（该页内的序号）");
                var pos = int.Parse(posText);

                var entry = favs.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"收藏里没有 id={id}");
                var pageSeq = favs.Where(e => e.Page == entry.Page).OrderBy(e => e.Position).ToList();
                var from = pageSeq.IndexOf(entry);
                pos = Math.Clamp(pos, 0, pageSeq.Count - 1);
                if (pos == from)
                {
                    Console.WriteLine($"{entry.Name} 本来就在 {PageLabel(entry.Page)} 第 {pos} 位");
                    return 0;
                }

                // 只在同一页里挪：先摘出来，再插到"该页第 pos 个邻居"之前
                var rest = favs.Where(e => e != entry).ToList();
                var neighbors = rest.Where(e => e.Page == entry.Page).ToList();
                int at = pos < neighbors.Count ? rest.IndexOf(neighbors[pos]) : rest.Count;
                rest.Insert(at, entry);
                RenumberByPage(rest);
                store.Save(rest);
                Console.WriteLine($"已把 {entry.Name} 移到 {PageLabel(entry.Page)} 第 {pos} 位");
                return 0;
            }

            case "group":
            {
                var id = p.Positional.ElementAtOrDefault(0)
                    ?? throw new InvalidOperationException("缺少 id");
                if (p.Positional.Count < 2)
                    throw new InvalidOperationException("缺少分组名（要脱离就写 -）");
                var entry = favs.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"收藏里没有 id={id}");
                var target = NormalizeGroup(p.Positional[1]);
                var old = entry.Group;
                if (old == target)
                {
                    Console.WriteLine($"「{entry.Name}」本来就在「{old ?? GroupStore.UnfiledName}」里");
                    return 0;
                }
                entry.Group = target;
                store.Save(favs);
                Console.WriteLine($"已把「{entry.Name}」从「{old ?? GroupStore.UnfiledName}」移到「{target ?? GroupStore.UnfiledName}」");
                WarnIfUnregistered(entry.Page, entry.Group);
                return 0;
            }

            case "remove":
            {
                var id = p.Positional.FirstOrDefault()
                    ?? throw new InvalidOperationException("缺少 id");
                var entry = TakeById(favs, id); // 内部已 Remove + 重排
                store.Save(favs);
                Console.WriteLine($"已移除 {entry.Name}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"未知子命令：{sub}");
                return 1;
        }
    }

    /// <summary>分组登记表：与 GUI 侧栏逐条同语义 —— 改名连带改条目、删除让条目回未分类、
    /// 只在条目上出现过的名字也算分组（未登记，颜色中性）。</summary>
    private static int CmdGroup(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：awd group list|add|rename|remove|color ...（awd help 看参数）");
            return 1;
        }

        var sub = args[0].ToLowerInvariant();
        var p = Parse(args.Skip(1).ToArray());
        var page = (int)ParsePage(p.Option("page"));
        var groups = GroupStore.Load();
        var store = new FavoritesStore();
        var favs = store.Load();
        var onPage = favs.Where(e => (int)e.Page == page).ToList();

        switch (sub)
        {
            case "list":
            {
                var reg = groups.Where(g => g.Page == page).ToList();
                var names = reg.Select(g => g.Name)
                    .Concat(onPage.Where(e => !string.IsNullOrEmpty(e.Group)).Select(e => e.Group!))
                    .Distinct().ToList();
                if (names.Count == 0)
                {
                    Console.WriteLine($"{PageLabel((PageKind)page)} 页还没有分组 —— " +
                                      $"awd group add <名称> --page {PageLabel((PageKind)page)}");
                    return 0;
                }
                foreach (var n in names)
                {
                    var def = reg.FirstOrDefault(g => g.Name == n);
                    Console.WriteLine($"{n}  {onPage.Count(e => e.Group == n)} 项  " +
                                      $"{GroupPalette.DisplayName(def?.Color)}" +
                                      (def == null ? "  [未登记：只写在条目上]" : ""));
                }
                Console.WriteLine($"共 {names.Count} 个分组（{PageLabel((PageKind)page)} 页；" +
                                  $"{GroupStore.UnfiledName} {onPage.Count(e => string.IsNullOrEmpty(e.Group))} 项）");
                return 0;
            }

            case "add":
            {
                var name = Need(p, 0, "分组名称");
                if (name == GroupStore.UnfiledName)
                {
                    Console.Error.WriteLine($"「{GroupStore.UnfiledName}」是未分类的占位名，不能当分组名");
                    return 1;
                }
                if (groups.Any(g => g.Page == page && g.Name == name) || onPage.Any(e => e.Group == name))
                {
                    Console.Error.WriteLine($"分组「{name}」在 {PageLabel((PageKind)page)} 页已存在");
                    return 1;
                }
                groups.Add(new GroupStore.GroupDef { Page = page, Name = name });
                GroupStore.Save(groups);
                Console.WriteLine($"已新建分组「{name}」（{PageLabel((PageKind)page)} 页，颜色中性）—— " +
                                  $"归条目：awd fav group <id> \"{name}\"");
                return 0;
            }

            case "rename":
            {
                var oldName = Need(p, 0, "旧分组名");
                var newName = Need(p, 1, "新分组名");
                if (newName == GroupStore.UnfiledName)
                {
                    Console.Error.WriteLine($"「{GroupStore.UnfiledName}」是未分类的占位名，不能当分组名");
                    return 1;
                }
                if (oldName == newName) { Console.WriteLine("名字没变"); return 0; }
                var hit = groups.FirstOrDefault(g => g.Page == page && g.Name == oldName);
                var moved = onPage.Where(e => e.Group == oldName).ToList();
                if (hit == null && moved.Count == 0)
                {
                    Console.Error.WriteLine($"{PageLabel((PageKind)page)} 页没有分组「{oldName}」（awd group list --page ... 看清单）");
                    return 1;
                }
                if (groups.Any(g => g.Page == page && g.Name == newName) || onPage.Any(e => e.Group == newName))
                {
                    Console.Error.WriteLine($"分组「{newName}」已存在，改名会撞车");
                    return 1;
                }
                if (hit != null) hit.Name = newName;
                else groups.Add(new GroupStore.GroupDef { Page = page, Name = newName });   // 未登记的组改名后补登记，颜色仍中性
                foreach (var e in moved) e.Group = newName;
                GroupStore.Save(groups);
                store.Save(favs);
                Console.WriteLine($"分组已改名为「{newName}」（{PageLabel((PageKind)page)} 页，连带 {moved.Count} 条收藏）");
                return 0;
            }

            case "remove":
            {
                var name = Need(p, 0, "分组名称");
                var hit = groups.FirstOrDefault(g => g.Page == page && g.Name == name);
                var freed = onPage.Where(e => e.Group == name).ToList();
                if (hit == null && freed.Count == 0)
                {
                    Console.Error.WriteLine($"{PageLabel((PageKind)page)} 页没有分组「{name}」");
                    return 1;
                }
                if (hit != null) groups.Remove(hit);
                foreach (var e in freed) e.Group = null;   // 条目不删，回未分类
                GroupStore.Save(groups);
                store.Save(favs);
                Console.WriteLine($"已删除分组「{name}」，{freed.Count} 条收藏回到未分类");
                return 0;
            }

            case "color":
            {
                var name = Need(p, 0, "分组名称");
                var colorSpec = Need(p, 1, "颜色名（或 - 清除）");
                var def = groups.FirstOrDefault(g => g.Page == page && g.Name == name);
                if (def == null && !onPage.Any(e => e.Group == name))
                {
                    Console.Error.WriteLine($"{PageLabel((PageKind)page)} 页没有分组「{name}」（先 awd group add）");
                    return 1;
                }
                string? hex = null;
                if (colorSpec != "-" && colorSpec != GroupStore.UnfiledName)
                {
                    var swatch = GroupPalette.Find(colorSpec)
                        ?? throw new InvalidOperationException(
                            $"不认识颜色「{colorSpec}」—— 用预设名（红/blue/…）、序号 1-{GroupPalette.Swatches.Length}，或 #RRGGBB");
                    hex = swatch.Hex;
                }
                if (def == null)
                {
                    def = new GroupStore.GroupDef { Page = page, Name = name, Color = hex };
                    groups.Add(def);
                }
                else def.Color = hex;
                GroupStore.Save(groups);
                Console.WriteLine($"分组「{name}」颜色 = {GroupPalette.DisplayName(def.Color)}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"未知子命令：{sub}");
                return 1;
        }
    }

    // ================== 辅助 ==================

    private static AppEntry TakeById(List<AppEntry> favs, string id)
    {
        var entry = favs.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"收藏里没有 id={id}");
        favs.Remove(entry);
        RenumberByPage(favs);
        return entry;
    }

    /// <summary>position 的口径与 GUI 的 Persist() 一致：**每页各自 0 起连续**，
    /// 且以"列表里的先后"为准 —— 调用方（move/add）排好的顺序就是摆放顺序。
    /// 早先是全局 0..N-1，GUI 与 CLI 对同一个字段两套理解；这里若改成按旧 Position 重排，
    /// move 刚插好的位置会被立刻冲回原样（实测踩过）。</summary>
    private static void RenumberByPage(List<AppEntry> favs)
    {
        foreach (var byPage in favs.GroupBy(e => e.Page))
        {
            var seq = byPage.ToList();
            for (var i = 0; i < seq.Count; i++) seq[i].Position = i;
        }
    }

    private static string Need(Parsed p, int index, string what)
        => p.Positional.Count > index && p.Positional[index].Length > 0
            ? p.Positional[index]
            : throw new InvalidOperationException($"缺少{what}");

    /// <summary>--group 的取值：- / 空 / (未分类) 都表示"不在任何分组"。</summary>
    private static string? NormalizeGroup(string? spec)
        => string.IsNullOrEmpty(spec) || spec == "-" || spec == GroupStore.UnfiledName ? null : spec;

    private static void WarnIfUnregistered(PageKind page, string? group)
    {
        if (group == null) return;
        if (GroupStore.Load().Any(g => g.Page == (int)page && g.Name == group)) return;
        Console.WriteLine($"  注：分组「{group}」未登记（侧栏照样显示，颜色中性）—— " +
                          $"配色：awd group color \"{group}\" <颜色> --page {PageLabel(page)}");
    }

    private static PageKind ParsePage(string? spec)
    {
        if (spec == null) return PageKind.Programs;
        return spec.Trim().ToLowerInvariant() switch
        {
            "0" or "programs" or "program" or "prog" or "app" or "程序" or "应用" => PageKind.Programs,
            "1" or "local" or "本地" => PageKind.Local,
            "2" or "web" or "网站" => PageKind.Web,
            "3" or "remote" or "远程" => PageKind.Remote,
            _ => throw new InvalidOperationException($"不认识页面「{spec}」（程序/本地/网站/远程，或 0-3）"),
        };
    }

    private static string PageLabel(PageKind p) => p switch
    {
        PageKind.Programs => "程序",
        PageKind.Local => "本地",
        PageKind.Web => "网站",
        _ => "远程",
    };

    private static AppKind ParseKind(string spec) => spec.Trim().ToLowerInvariant() switch
    {
        "lnk" => AppKind.Lnk,
        "exe" => AppKind.Exe,
        "uwp" => AppKind.Uwp,
        "folder" or "目录" or "文件夹" => AppKind.Folder,
        "web" or "网站" => AppKind.Web,
        "remote" or "远程" => AppKind.Remote,
        _ => throw new InvalidOperationException($"不认识类型「{spec}」（lnk/exe/uwp/folder/web/remote）"),
    };

    /// <summary>按目标查应用清单，用于把 AUMID 换成人看的名字。</summary>
    private static AppEntry? FindInInventory(string target)
        => AppInventory.EnumerateAll().FirstOrDefault(a =>
            a.Target.Equals(target, StringComparison.OrdinalIgnoreCase));

    /// <summary>把目标字符串解释成条目：含 ! 且无 \ 视为 AUMID，.lnk 结尾视为快捷方式，其余按 exe。</summary>
    private static AppEntry Describe(string spec)
    {
        var kind = spec.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? AppKind.Lnk
            : !spec.Contains('\\') && spec.Contains('!') ? AppKind.Uwp
            : AppKind.Exe;
        return new AppEntry
        {
            Id = AppEntry.MakeId(spec),
            Name = Path.GetFileNameWithoutExtension(spec),
            Kind = kind,
            Target = spec,
        };
    }

    /// <summary>解析目标：@id 查收藏表；其余按字面目标解释。lookupName 时用应用清单补友好名。</summary>
    private static AppEntry ResolveEntry(string spec, bool lookupName)
    {
        if (spec.StartsWith('@'))
        {
            var id = spec[1..];
            var favs = new FavoritesStore().Load();
            return favs.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"收藏里没有 id={id}");
        }

        var entry = Describe(spec);
        if (lookupName)
        {
            var hit = FindInInventory(entry.Target);
            if (hit != null) entry.Name = hit.Name;
        }
        return entry;
    }

    private static string? TryCacheIcon(AppEntry entry)
    {
        try
        {
            var r = IconCache.Extract(entry.Target, 256);
            Console.WriteLine($"  图标 {r.NaturalWidth}x{r.NaturalHeight} -> {r.CachePath}");
            return r.CachePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  图标提取失败（不阻塞收藏）：{ex.Message}");
            return null;
        }
    }

    private sealed class Parsed
    {
        public List<string> Positional { get; } = new();
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Option(params string[] names)
            => names.Select(n => Options.TryGetValue(n, out var v) ? v : null)
                .FirstOrDefault(v => v != null);

        public int IntOption(string name, int fallback)
            => Options.TryGetValue(name, out var v) && int.TryParse(v, out var parsed) ? parsed : fallback;
    }

    private static Parsed Parse(string[] args)
    {
        var p = new Parsed();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                p.Options[args[i].TrimStart('-')] = args[++i];
            else if (args[i].StartsWith("--"))
                p.Options[args[i].TrimStart('-')] = "";
            else
                p.Positional.Add(args[i]);
        }
        return p;
    }
}
