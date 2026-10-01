using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Awd.Core;

/// <summary>
/// 分组定义。Name 是条目挂靠的键（<see cref="AppEntry.Group"/> 存的就是这个名字）。
/// Page 保持 int 而不是 PageKind —— groups.json 时代它就是整数，别换一种写法。
/// </summary>
public sealed class GroupDef
{
    public int Page { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
}

/// <summary>
/// 一张「任意门地图」= 四页各自的分组、条目、配色与顺序。
///
/// 落盘是**紧凑二进制**（<see cref="ToBin"/>）：顺序由数组先后表达（不存 position）、
/// <c>id</c> 与 <c>iconPath</c> 都由 target 现算（不存）、颜色压成 3 字节、
/// 条目按分组序号引用（不重复写分组名）。
/// JSON 只作"导出给人看、可手改、再导入"的交换格式（<see cref="ToJson"/>）。
/// </summary>
public sealed class MapDoc
{
    public const byte FormatVersion = 1;
    private static readonly byte[] Magic = { (byte)'A', (byte)'W', (byte)'D', (byte)'M' };

    /// <summary>分组引用写不进表时（分组数爆表）用这个值表示"未分类"。</summary>
    private const ushort NoGroup = ushort.MaxValue;

    /// <summary>未分类在侧栏与 CLI 里的占位名。它不是分组，所以不进登记表 —— 这个名字留给它。</summary>
    public const string UnfiledName = "(未分类)";

    public string Name { get; set; } = "";
    public List<GroupDef> Groups { get; set; } = new();
    public List<AppEntry> Entries { get; set; } = new();

    /// <summary>远程页站点配色。站点清单属于易远传（只读），颜色这本子属于地图。</summary>
    public Dictionary<string, string> SiteColors { get; set; } = new();

    /// <summary>某页的分组顺序：登记表里的按登记顺序在前，只在条目上出现过的名字补在后面。</summary>
    public List<GroupDef> GroupsOf(int page) => MaterializedGroups().Where(g => g.Page == page).ToList();

    /// <summary>
    /// 保存时把"只在条目上出现过的分组名"补进登记表 —— 地图里分组一律显式存在，
    /// 这样"顺序"才有处可存。副作用：转正后的分组颜色是中性（本来也没有颜色）。
    /// </summary>
    public List<GroupDef> MaterializedGroups()
    {
        var list = Groups.Select(g => new GroupDef { Page = g.Page, Name = g.Name, Color = g.Color }).ToList();
        foreach (var e in Entries.OrderBy(x => x.Position))
        {
            if (string.IsNullOrEmpty(e.Group)) continue;
            if (list.Any(g => g.Page == (int)e.Page && g.Name == e.Group)) continue;
            list.Add(new GroupDef { Page = (int)e.Page, Name = e.Group });
        }
        return list;
    }

    // ================== 二进制 ==================

    public byte[] ToBin()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            WriteStr(w, Name);

            var groups = MaterializedGroups();
            Check(groups.Count, "分组数");
            w.Write((ushort)groups.Count);
            foreach (var g in groups)
            {
                w.Write((byte)g.Page);
                WriteStr(w, g.Name);
                WriteColor(w, g.Color);
            }

            var ordered = Entries.OrderBy(e => e.Page).ThenBy(e => e.Position).ToList();
            Check(ordered.Count, "条目数");
            w.Write((ushort)ordered.Count);
            foreach (var e in ordered)
            {
                w.Write((byte)e.Page);
                w.Write((byte)e.Kind);
                WriteStr(w, e.Name);
                WriteStr(w, e.Target);
                WriteStr(w, e.Arguments);
                WriteStr(w, e.WorkingDir ?? "");
                var gi = e.Group == null ? -1 : groups.FindIndex(g => g.Page == (int)e.Page && g.Name == e.Group);
                w.Write((ushort)(gi < 0 ? NoGroup : gi));
            }

            Check(SiteColors.Count, "站点配色数");
            w.Write((ushort)SiteColors.Count);
            foreach (var (site, hex) in SiteColors)
            {
                WriteStr(w, site);
                WriteColor(w, hex);
            }
        }
        return ms.ToArray();
    }

    public static MapDoc FromBin(byte[] bytes)
    {
        var doc = new MapDoc();
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);

        var head = r.ReadBytes(Magic.Length);
        if (head.Length != Magic.Length || !head.SequenceEqual(Magic))
            throw new InvalidDataException("不是任意门地图文件（文件头不认）");
        var ver = r.ReadByte();
        if (ver != FormatVersion)
            throw new InvalidDataException($"地图格式版本 {ver}，本程序只认 {FormatVersion}");

        doc.Name = ReadStr(r);
        var groupCount = r.ReadUInt16();
        for (var i = 0; i < groupCount; i++)
            doc.Groups.Add(new GroupDef
            {
                Page = r.ReadByte(),
                Name = ReadStr(r),
                Color = ReadColor(r),
            });

        var entryCount = r.ReadUInt16();
        var perPage = new int[4];
        for (var i = 0; i < entryCount; i++)
        {
            var page = r.ReadByte();
            if (page > 3) throw new InvalidDataException($"页面号 {page} 越界");
            var kind = r.ReadByte();
            var name = ReadStr(r);
            var target = ReadStr(r);
            var args = ReadStr(r);
            var workDir = ReadStr(r);
            var gi = r.ReadUInt16();
            if (gi != NoGroup && gi >= groupCount) throw new InvalidDataException("分组引用越界");
            doc.Entries.Add(new AppEntry
            {
                // id 与 iconPath 都是 target 的函数：不存，读的时候现算
                Id = AppEntry.MakeId(target),
                Name = name,
                Kind = (AppKind)kind,
                Page = (PageKind)page,
                Target = target,
                Arguments = args,
                WorkingDir = workDir.Length == 0 ? null : workDir,
                Group = gi == NoGroup ? null : doc.Groups[gi].Name,
                Position = perPage[page]++,        // 顺序 = 文件里的先后
                IconPath = IconCache.PathFor(target),
            });
        }

        var siteCount = r.ReadUInt16();
        for (var i = 0; i < siteCount; i++)
        {
            var site = ReadStr(r);
            var hex = ReadColor(r);
            if (hex != null) doc.SiteColors[site] = hex;
        }
        return doc;
    }

    private static void Check(int count, string what)
    {
        if (count > NoGroup - 1)
            throw new InvalidOperationException($"{what} {count} 超过地图格式上限 {NoGroup - 1}");
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var len = bytes.Length;
        while (len >= 0x80) { w.Write((byte)(len | 0x80)); len >>= 7; }
        w.Write((byte)len);
        w.Write(bytes);
    }

    private static string ReadStr(BinaryReader r)
    {
        int len = 0, shift = 0;
        while (true)
        {
            var b = r.ReadByte();
            len |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("字符串长度前缀坏了");
        }
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private static void WriteColor(BinaryWriter w, string? hex)
    {
        var rgb = ToRgb(hex);
        if (rgb == null) { w.Write((byte)0); return; }
        w.Write((byte)1);
        w.Write(rgb);
    }

    private static string? ReadColor(BinaryReader r)
    {
        if (r.ReadByte() == 0) return null;
        var b = r.ReadBytes(3);
        return $"#{b[0]:X2}{b[1]:X2}{b[2]:X2}";
    }

    private static byte[]? ToRgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var s = hex.Trim();
        if (s[0] == '#') s = s[1..];
        if (s.Length == 3) s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length == 8) s = s[2..];          // 带 alpha 的 #AARRGGBB 取 RGB
        if (s.Length != 6 || !s.All(Uri.IsHexDigit)) return null;
        return new[]
        {
            Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16),
        };
    }

    // ================== 交换用 JSON（给人看、可手改） ==================

    private static readonly JsonSerializerOptions ExchangeJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Export
    {
        public int Version { get; set; } = FormatVersion;
        public string Name { get; set; } = "";
        public List<ExportGroup> Groups { get; set; } = new();
        public List<ExportEntry> Entries { get; set; } = new();
        public Dictionary<string, string> SiteColors { get; set; } = new();
    }

    private sealed class ExportGroup
    {
        public string Page { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Color { get; set; }
    }

    private sealed class ExportEntry
    {
        public string Page { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Target { get; set; } = "";
        public string? Group { get; set; }
        public string? Arguments { get; set; }
        public string? WorkingDir { get; set; }
    }

    public string ToJson()
    {
        var ex = new Export { Name = Name, SiteColors = new Dictionary<string, string>(SiteColors) };
        foreach (var g in MaterializedGroups())
            ex.Groups.Add(new ExportGroup { Page = PageName(g.Page), Name = g.Name, Color = g.Color });
        foreach (var e in Entries.OrderBy(x => x.Page).ThenBy(x => x.Position))
            ex.Entries.Add(new ExportEntry
            {
                Page = PageName((int)e.Page),
                Name = e.Name,
                Kind = e.Kind.ToString().ToLowerInvariant(),
                Target = e.Target,
                Group = string.IsNullOrEmpty(e.Group) ? null : e.Group,
                Arguments = string.IsNullOrEmpty(e.Arguments) ? null : e.Arguments,
                WorkingDir = string.IsNullOrEmpty(e.WorkingDir) ? null : e.WorkingDir,
            });
        return JsonSerializer.Serialize(ex, ExchangeJson);
    }

    public static MapDoc FromJson(string json)
    {
        var ex = JsonSerializer.Deserialize<Export>(json, ExchangeJson)
            ?? throw new InvalidDataException("地图 JSON 读不出来");
        if (ex.Version != FormatVersion)
            throw new InvalidDataException($"地图 JSON 版本 {ex.Version}，本程序只认 {FormatVersion}");
        var doc = new MapDoc { Name = ex.Name };
        foreach (var g in ex.Groups)
            doc.Groups.Add(new GroupDef { Page = ParsePage(g.Page), Name = g.Name, Color = g.Color });
        var perPage = new int[4];
        foreach (var e in ex.Entries)
        {
            var page = ParsePage(e.Page);
            doc.Entries.Add(new AppEntry
            {
                Id = AppEntry.MakeId(e.Target),
                Name = e.Name,
                Kind = ParseKind(e.Kind),
                Page = (PageKind)page,
                Target = e.Target,
                Arguments = e.Arguments ?? "",
                WorkingDir = e.WorkingDir,
                Group = string.IsNullOrEmpty(e.Group) ? null : e.Group,
                Position = perPage[page]++,
                IconPath = IconCache.PathFor(e.Target),
            });
        }
        foreach (var (site, hex) in ex.SiteColors) doc.SiteColors[site] = hex;
        return doc;
    }

    private static string PageName(int page) => ((PageKind)page) switch
    {
        PageKind.Programs => "programs",
        PageKind.Local => "local",
        PageKind.Web => "web",
        _ => "remote",
    };

    private static int ParsePage(string page) => page.Trim().ToLowerInvariant() switch
    {
        "0" or "programs" or "program" or "程序" or "应用" => 0,
        "1" or "local" or "本地" => 1,
        "2" or "web" or "网站" => 2,
        "3" or "remote" or "远程" => 3,
        _ => throw new InvalidDataException($"不认识页面「{page}」（programs/local/web/remote）"),
    };

    private static AppKind ParseKind(string kind) => Enum.TryParse<AppKind>(kind, true, out var k)
        ? k
        : throw new InvalidDataException($"不认识类型「{kind}」（lnk/exe/uwp/folder/web/remote）");
}
