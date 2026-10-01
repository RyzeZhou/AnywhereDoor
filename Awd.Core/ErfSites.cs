using System.IO;
using System.Text.Json;

namespace Awd.Core;

/// <summary>
/// 易远传（ERF）站点清单的只读复用：读 %APPDATA%\ExplorerRemoteFs\connections.json
/// （与 ERF 客户端/配置器共享同一份文件、同一格式）。只取展示需要的字段，
/// 缺字段给默认，文件缺失或损坏返回空列表 —— 任意门绝不反过来写这份文件。
/// 密码不在 JSON 里（Windows 凭据管理器），这里也不碰。
/// </summary>
public static class ErfSites
{
    public sealed record Site(string Name, string Type, string Host, int? Port, string Username, string StartPath);

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerRemoteFs", "connections.json");

    public static List<Site> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Site>();
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var list = new List<Site>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string S(string n) =>
                    el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                int? I(string n) =>
                    el.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

                var name = S("Name");
                if (name.Length == 0) continue;
                list.Add(new Site(name, S("Type"), S("Host"), I("Port"), S("Username"), S("StartPath")));
            }
            return list;
        }
        catch
        {
            return new List<Site>();
        }
    }
}
