using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Awd.Core;

/// <summary>
/// 地图仓库：<c>%APPDATA%\AnywhereDoor\maps\&lt;地图名&gt;.awdmap</c>（紧凑二进制）。
///
/// 三件事是这层的全部职责：
/// 1. **自动扫描** —— 目录就是清单，不另立索引文件；往目录里丢一张地图，下次扫描就看见。
/// 2. **原子写** —— 先写 .tmp 再替换，替换前把上一版留成 .bak。断电/崩溃不会留下半个文件。
/// 3. **读失败绝不静默降级** —— 老代码里"读不出来就当空表"配下一次保存，等于一次损坏清空数据。
///    这里坏了就回退 .bak，回退不了就抛错并**不写任何东西**。
/// </summary>
public static class MapStore
{
    public const string Ext = ".awdmap";

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnywhereDoor");

    public static string MapsDir => Path.Combine(DataDir, "maps");

    /// <summary>扫描到的一张地图。Error 非空表示文件在但读不出来（菜单里照样列出来，别让人以为图丢了）。</summary>
    public sealed record Found(string Name, string FilePath, string? Error);

    private static readonly char[] Unsafe = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ' }).ToArray();

    /// <summary>地图名 → 文件名。名字本身存在文件里，文件名只是落点，所以可以激进地替换。</summary>
    public static string FileSafe(string mapName)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in mapName.Trim()) sb.Append(Unsafe.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim('.', '_');
        return s.Length == 0 ? "未命名" : s;
    }

    public static string PathOf(string mapName) => Path.Combine(MapsDir, FileSafe(mapName) + Ext);

    /// <summary>扫 maps 目录。按名字排序，读不出内容的排在后面但仍列出。</summary>
    public static List<Found> Scan()
    {
        var list = new List<Found>();
        if (!Directory.Exists(MapsDir)) return list;
        foreach (var file in Directory.GetFiles(MapsDir, "*" + Ext).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            var fallback = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = MapDoc.FromBin(File.ReadAllBytes(file));
                list.Add(new Found(string.IsNullOrWhiteSpace(doc.Name) ? fallback : doc.Name, file, null));
            }
            catch (Exception ex)
            {
                list.Add(new Found(fallback, file, ex.Message));
            }
        }
        return list;
    }

    public static bool Exists(string mapName) => Locate(mapName) != null;

    /// <summary>
    /// 名字 → 文件。**先按扫描结果匹配**：地图名存在文件里，文件名只是落点，
    /// 手工改过文件名之后两者会分家 —— 只按名字拼路径就找不到刚扫到的那张。
    /// </summary>
    private static string? Locate(string mapName)
        => Scan().FirstOrDefault(f => f.Name == mapName)?.FilePath
           ?? (File.Exists(PathOf(mapName)) ? PathOf(mapName) : null);

    /// <summary>读**指定文件**（同样有 .bak 回退）。窗口只该盯着自己打开的那一张 ——
    /// 跟着"活动指针"跑的话，别人把指针改走或那张图坏了，本窗口会连启动一起崩。</summary>
    public static MapDoc LoadFrom(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"地图文件不见了：{path}");
        try
        {
            return MapDoc.FromBin(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var doc = MapDoc.FromBin(File.ReadAllBytes(bak));
                    if (string.IsNullOrWhiteSpace(doc.Name))
                        doc.Name = Path.GetFileNameWithoutExtension(path);
                    return doc;
                }
                catch { /* 备份也坏了，往下抛 */ }
            }
            throw new InvalidDataException(
                $"地图文件读不出来（{ex.Message}），备份也不可用 —— 已保留原文件未做任何修改：{path}");
        }
    }

    public static MapDoc Load(string mapName) => Load(mapName, out _);

    /// <summary>读地图，并说清读的是哪个文件 —— 切换时"顺手治愈"要写回**刚读的这一个**，
    /// 不能按名字猜路径（猜错就是把内容写进另一张图，坏的那张仍然坏着）。</summary>
    public static MapDoc Load(string mapName, out string loadedFrom)
    {
        loadedFrom = Locate(mapName) ?? throw new FileNotFoundException($"没有地图「{mapName}」");
        var doc = LoadFrom(loadedFrom);
        doc.Name = mapName;     // 身份跟着用户选的名字走，Save/治愈才落回同一个文件
        return doc;
    }

    /// <summary>原子写：.tmp → 上一版转存 .bak → 替换。任何一步失败都不会让原文件变成半截。</summary>
    public static void Save(MapDoc doc)
    {
        Directory.CreateDirectory(MapsDir);
        SaveTo(doc, PathOf(doc.Name));
    }

    public static void SaveTo(MapDoc doc, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, doc.ToBin());
        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); } catch { /* 留不下备份也要继续写 */ }
            File.Delete(path);
        }
        File.Move(tmp, path);
    }

    /// <summary>新建：seed 传 null 得到空白地图，传现有地图则复制它的内容。</summary>
    public static MapDoc Create(string mapName, MapDoc? seed)
    {
        if (Exists(mapName)) throw new InvalidOperationException($"地图「{mapName}」已存在");
        var doc = new MapDoc { Name = mapName };
        if (seed != null)
        {
            foreach (var g in seed.Groups)
                doc.Groups.Add(new GroupDef { Page = g.Page, Name = g.Name, Color = g.Color });
            foreach (var e in seed.Entries)
                doc.Entries.Add(e.Clone());
            foreach (var (site, hex) in seed.SiteColors) doc.SiteColors[site] = hex;
        }
        Save(doc);
        return doc;
    }

    public static void Rename(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException("地图名是空的");
        if (from == to) return;
        if (!Exists(from)) throw new FileNotFoundException($"没有地图「{from}」");
        if (Exists(to)) throw new InvalidOperationException($"地图「{to}」已存在");
        var doc = Load(from);
        doc.Name = to;
        Save(doc);
        Delete(from);
        if (SettingsStore.Load().ActiveMap == from) SettingsStore.SetActive(from, to);
    }

    /// <summary>删除地图（连备份一起）。活动地图不许删 —— 否则下次启动没有可加载的东西。</summary>
    public static void Delete(string mapName)
    {
        if (SettingsStore.Load().ActiveMap == mapName)
            throw new InvalidOperationException($"「{mapName}」是当前地图，先切到别的再删");
        var path = Locate(mapName) ?? throw new FileNotFoundException($"没有地图「{mapName}」");
        File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
    }

    public static string ExportJson(string mapName) => Load(mapName).ToJson();

    /// <summary>导入一份地图 JSON 成新地图（不改当前地图）。</summary>
    public static MapDoc ImportJson(string mapName, string json)
    {
        if (Exists(mapName)) throw new InvalidOperationException($"地图「{mapName}」已存在，导入会覆盖 —— 换个名字");
        var doc = MapDoc.FromJson(json);
        doc.Name = mapName;
        Save(doc);
        return doc;
    }

    // ================== 取当前地图 ==================

    /// <summary>
    /// 打开当前地图。三种起步情况一次处理：老仓库还在就先迁移成「默认地图」；
    /// 什么都没有就建一张空的；指针指向的地图不见了（被删/被改名）就落到扫到的第一张。
    /// </summary>
    public static MapDoc OpenActive() => OpenActive(out _);

    public static MapDoc OpenActive(out string loadedFrom)
    {
        var settings = SettingsStore.Load();
        var found = Scan();
        if (found.Count == 0)
        {
            var migrated = MigrateFromLegacy() ?? Create("默认地图", null);
            settings = SettingsStore.Load();      // 迁移会把老 settings.json 改名留档，重读一次
            settings.ActiveMap = migrated.Name;
            SettingsStore.Save(settings);
            loadedFrom = PathOf(migrated.Name);
            return migrated;
        }
        var name = found.Any(f => f.Name == settings.ActiveMap) ? settings.ActiveMap! : found[0].Name;
        if (name != settings.ActiveMap)
        {
            settings.ActiveMap = name;
            SettingsStore.Save(settings);
        }
        return Load(name, out loadedFrom);
    }

    /// <summary>
    /// 启动用取图。<see cref="OpenActive"/> 读不出来时**换一张能读的**，把话说清楚后照常开工：
    /// 断电把 .awdmap 零化过真实发生过（见笔记 AWD-2026-10-01-02），而"双击没反应、也没有任何提示"
    /// 是最坏的结局 —— 尤其这是一扇"门"，打不开就等于程序消失。
    /// **不改活动指针**：那是用户的意图，坏文件修好后重启就回来了。一张都读不出来才返回 false。
    /// </summary>
    public static bool TryOpenActive(out MapDoc? doc, out string path, out string note)
    {
        try
        {
            doc = OpenActive(out path);
            note = "";
            return true;
        }
        catch (Exception ex)
        {
            var wanted = SettingsStore.Load().ActiveMap;
            // 备选按"最近写过"排：正在用的那张通常就是最新的那张。
            // 早先按目录名顺序取第一张，实测会挑中一张空白的测试图，把用户真正在用的那张留在身后。
            // 不按 Scan 的 Error 过滤 —— 主文件坏了但 .bak 完好的图，Load 能救回来，照样是好候选。
            var candidates = Scan().Where(f => f.Name != wanted)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f.FilePath))
                .ToList();
            foreach (var f in candidates)
            {
                try
                {
                    doc = Load(f.Name, out path);
                    note = $"地图「{wanted}」读不出来（{ex.Message}）\n" +
                           $"本次先打开「{doc.Name}」；修好原文件后重启就切回去。当前地图指针没有改。";
                    return true;
                }
                catch { path = ""; /* 这张也坏了，接着找 */ }
            }
            doc = null;
            path = "";
            note = $"地图「{wanted}」读不出来（{ex.Message}），其余地图也都不行。\n" +
                   $"没有改动任何文件 —— 请检查目录：{MapsDir}";
            return false;
        }
    }

    // ================== 老仓库迁移 ==================

    private static readonly string LegacyFavoritesPath = Path.Combine(DataDir, "favorites.json");
    private static readonly string LegacyGroupsPath = Path.Combine(DataDir, "groups.json");
    private static readonly string LegacySettings = Path.Combine(DataDir, "settings.json");

    /// <summary>还没有任何地图、且老仓库文件还在，就合成一张「默认地图」。返回 null 表示无需迁移。</summary>
    public static MapDoc? MigrateFromLegacy(string mapName = "默认地图")
    {
        if (Scan().Count > 0 || !File.Exists(LegacyFavoritesPath)) return null;

        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var doc = new MapDoc { Name = mapName };

        var file = JsonSerializer.Deserialize<LegacyFavoritesPathFile>(File.ReadAllText(LegacyFavoritesPath), json);
        foreach (var e in file?.Apps ?? new List<AppEntry>())
        {
            e.Id = AppEntry.MakeId(e.Target);              // 一律重算：老文件里的 id 口径不认第二次
            e.IconPath = IconCache.PathFor(e.Target);
            e.Group = string.IsNullOrEmpty(e.Group) ? null : e.Group;
            doc.Entries.Add(e);
        }
        if (File.Exists(LegacyGroupsPath))
            foreach (var g in JsonSerializer.Deserialize<List<LegacyGroup>>(File.ReadAllText(LegacyGroupsPath), json)
                     ?? new List<LegacyGroup>())
                doc.Groups.Add(new GroupDef { Page = g.Page, Name = g.Name, Color = g.Color });
        if (File.Exists(LegacySettings))
        {
            var s = JsonSerializer.Deserialize<LegacySettingsFile>(File.ReadAllText(LegacySettings), json);
            foreach (var (site, hex) in s?.SiteColors ?? new Dictionary<string, string>())
                doc.SiteColors[site] = hex;
        }

        // 老文件的 position 是"页内 0 起"（GUI 的 Persist 一直如此），这里只把每页重排成连续，口径不变
        foreach (var byPage in doc.Entries.GroupBy(e => e.Page))
        {
            var i = 0;
            foreach (var e in byPage.OrderBy(x => x.Position)) e.Position = i++;
        }

        Save(doc);
        // 老文件改名留档（可回退），不删 —— 地图是新的唯一事实源，留着原名最容易让人改错地方
        foreach (var old in new[] { LegacyFavoritesPath, LegacyGroupsPath, LegacySettings })
            if (File.Exists(old)) File.Move(old, old + ".pre-map", overwrite: true);
        return doc;
    }

    private sealed class LegacyFavoritesPathFile
    {
        public int Version { get; set; }
        public List<AppEntry> Apps { get; set; } = new();
    }

    private sealed class LegacyGroup
    {
        public int Page { get; set; }
        public string Name { get; set; } = "";
        public string? Color { get; set; }
    }

    private sealed class LegacySettingsFile
    {
        public bool DoubleClickOpen { get; set; }
        public Dictionary<string, string>? SiteColors { get; set; }
    }
}
