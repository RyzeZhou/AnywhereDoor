using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Awd.Core;

/// <summary>
/// 浏览器书签读取：Chrome/Edge 的 Bookmarks(JSON) + 通用书签 HTML 导出（Netscape 格式）。
/// 输出统一为「文件夹 → 条目」的有序列表，给导入对话框勾选用。只读，不联网。
/// </summary>
public static partial class BookmarkReader
{
    public sealed record Item(string Name, string Url);

    public sealed class Folder
    {
        public string Path { get; }
        public List<Item> Items { get; } = new();
        public Folder(string path) => Path = path;
    }

    /// <summary>探测本机可直读的书签文件（Chrome / Edge；Firefox 的 places.sqlite 需要 SQLite，不在这里做）。</summary>
    public static List<(string Label, string FilePath)> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<(string, string)>();
        void Add(string label, string p)
        {
            if (File.Exists(p)) list.Add((label, p));
        }
        Add("Chrome 书签", Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Bookmarks"));
        Add("Edge 书签", Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Bookmarks"));
        return list;
    }

    // ── Chrome / Edge：User Data\Default\Bookmarks（JSON） ──

    public static List<Folder> FromChromeJson(string filePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var folders = new List<Folder>();
        if (!doc.RootElement.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Object)
            return folders;
        foreach (var root in roots.EnumerateObject())
        {
            if (root.Value.ValueKind != JsonValueKind.Object) continue;
            WalkFolder(root.Value, RootName(root.Name), folders);
        }
        return folders.Where(f => f.Items.Count > 0).ToList();
    }

    private static string RootName(string key) => key switch
    {
        "bookmark_bar" => "书签栏",
        "other" => "其他书签",
        "synced" => "移动设备书签",
        _ => key,
    };

    private static void WalkFolder(JsonElement el, string path, List<Folder> folders)
    {
        var folder = new Folder(path);
        folders.Add(folder);
        if (!el.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return;
        foreach (var c in children.EnumerateArray())
        {
            string S(string n) =>
                c.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var t = S("type");
            if (t == "url")
            {
                var url = S("url");
                if (url.Length > 0 && !url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    folder.Items.Add(new Item(S("name").Length > 0 ? S("name") : url, url));
            }
            else if (t == "folder")
            {
                var name = S("name");
                WalkFolder(c, path + "/" + (name.Length > 0 ? name : "(未命名)"), folders);
            }
        }
    }

    // ── 通用书签 HTML（Netscape 格式，各家浏览器"导出书签"都是它） ──

    [GeneratedRegex("<a\\s[^>]*href\\s*=\\s*\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReA();

    [GeneratedRegex("<h3[^>]*>(.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReH3();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex ReTags();

    public static List<Folder> FromHtml(string filePath)
    {
        var folders = new List<Folder>();
        var stack = new List<Folder> { new("(根目录)") };
        folders.Add(stack[0]);
        string? pendingFolder = null;

        foreach (var line in File.ReadLines(filePath))
        {
            var h3 = ReH3().Match(line);
            if (h3.Success)
                pendingFolder = Unescape(ReTags().Replace(h3.Groups[1].Value, ""));

            if (line.Contains("<DL", StringComparison.OrdinalIgnoreCase))
            {
                // Netscape 的 DL 有两种：跟着 H3 的（真文件夹）和最外层包裹的（无名容器，层级不变）
                if (pendingFolder != null)
                {
                    var name = pendingFolder.Length > 0 ? pendingFolder : "(未命名)";
                    pendingFolder = null;
                    var f = new Folder(stack[^1].Path + "/" + name);
                    folders.Add(f);
                    stack.Add(f);
                }
            }
            if (line.Contains("</DL", StringComparison.OrdinalIgnoreCase) && stack.Count > 1)
                stack.RemoveAt(stack.Count - 1);

            var a = ReA().Match(line);
            if (a.Success)
            {
                var url = a.Groups[1].Value;
                if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue; // 书签小程序跳过
                var name = Unescape(ReTags().Replace(a.Groups[2].Value, ""));
                stack[^1].Items.Add(new Item(name.Length > 0 ? name : url, url));
            }
        }
        return folders.Where(f => f.Items.Count > 0).ToList();
    }

    private static string Unescape(string s) => s
        .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'");
}
