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
                     list | move <id> <位置> | remove <id>

            目标写法：exe / .lnk 用完整路径；UWP 用 AUMID（形如 Xyz_hash!App）；
            @id 表示按收藏列表里的 id 引用。
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
            Console.Error.WriteLine("用法：awd fav add|list|move|remove ...（awd help 看参数）");
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

                // UWP 的显示名从应用清单里查，比裸 AUMID 好看
                if (p.Option("name") == null)
                {
                    var hit = FindInInventory(entry.Target);
                    if (hit != null) entry.Name = hit.Name;
                }
                entry.Name = p.Option("name") ?? entry.Name;
                entry.Group = p.Option("group");
                entry.Position = favs.Count == 0 ? 0 : favs.Max(f => f.Position) + 1;
                entry.IconPath = TryCacheIcon(entry); // 添加时就提取图标，网格加载不用等

                favs.Add(entry);
                store.Save(favs);
                Console.WriteLine($"已添加 [{entry.Id}] {entry.Name}（位置 {entry.Position}）");
                return 0;
            }

            case "list":
            {
                if (favs.Count == 0)
                {
                    Console.WriteLine("收藏为空");
                    return 0;
                }
                foreach (var f in favs.OrderBy(f => f.Position))
                {
                    var icon = f.IconPath == null ? "" : "  [icon OK]";
                    Console.WriteLine($"#{f.Position,-3} [{f.Id}] ({f.Kind}) {f.Name}  ←  {f.Target}{icon}");
                }
                return 0;
            }

            case "move":
            {
                var id = p.Positional.ElementAtOrDefault(0)
                    ?? throw new InvalidOperationException("缺少 id");
                var posText = p.Positional.ElementAtOrDefault(1)
                    ?? throw new InvalidOperationException("缺少目标位置");
                var pos = int.Parse(posText);

                var entry = TakeById(favs, id);
                pos = Math.Clamp(pos, 0, favs.Count);
                favs.Insert(pos, entry);
                Renumber(favs);
                store.Save(favs);
                Console.WriteLine($"已把 {entry.Name} 移到位置 {entry.Position}");
                return 0;
            }

            case "remove":
            {
                var id = p.Positional.FirstOrDefault()
                    ?? throw new InvalidOperationException("缺少 id");
                var entry = TakeById(favs, id); // 内部已 Remove + Renumber
                store.Save(favs);
                Console.WriteLine($"已移除 {entry.Name}");
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
        Renumber(favs);
        return entry;
    }

    private static void Renumber(List<AppEntry> favs)
    {
        for (var i = 0; i < favs.Count; i++)
            favs[i].Position = i;
    }

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
