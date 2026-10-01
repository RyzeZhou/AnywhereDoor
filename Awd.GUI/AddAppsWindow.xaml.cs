using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Awd.Core;

namespace Awd.GUI;

public partial class AddAppsWindow : Window
{
    /// <summary>清单行：图标只从缓存读（20px 小图），不在列表里做 COM 提取 —— 提取只发生在"添加"那一刻。</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        public Row(AppEntry app, bool owned, ImageSource? icon)
        {
            App = app;
            _owned = owned;
            Icon = icon;
        }

        public AppEntry App { get; }
        public ImageSource? Icon { get; }

        private bool _owned;
        public bool Owned
        {
            get => _owned;
            set
            {
                _owned = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OwnedText)));
            }
        }

        public string OwnedText => _owned ? "已收藏" : "";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>跨过滤输入共享的图标小图缓存：key = 条目 Id，值可为 null（没缓存过图）。</summary>
    private static readonly Dictionary<string, ImageSource?> SmallIconCache = new();

    private static readonly SemaphoreSlim AddGate = new(1, 1);

    private readonly MapDoc _doc = MapStore.OpenActive();
    private List<AppEntry> _all = new();
    private List<Row> _rows = new();
    private HashSet<string> _ownedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>有没有新增过收藏；主窗口据此决定要不要重载。</summary>
    public bool Changed { get; private set; }

    public AddAppsWindow()
    {
        InitializeComponent();
        _all = AppInventory.EnumerateAll()
            .OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
        _ownedIds = new HashSet<string>(
            _doc.Entries.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
        ApplyFilter("");
        SearchBox.Focus();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string query)
    {
        query = query.Trim();
        IEnumerable<AppEntry> hit = _all;
        if (query.Length > 0)
            hit = _all.Where(a =>
                a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                a.Target.Contains(query, StringComparison.OrdinalIgnoreCase));

        _rows = hit.Select(a => new Row(
            a,
            _ownedIds.Contains(AppEntry.MakeId(a.Target)),
            IconFor(a))).ToList();
        AppList.ItemsSource = _rows;
        HitText.Text = $"{_rows.Count} 项";
    }

    private static ImageSource? IconFor(AppEntry app)
    {
        var id = AppEntry.MakeId(app.Target);
        if (SmallIconCache.TryGetValue(id, out var cached)) return cached;
        var png = IconCache.PathFor(app.Target, 256);
        var img = TileVm.Decode(png, 20);
        SmallIconCache[id] = img;
        return img;
    }

    // ================== 添加 ==================

    private async void OnListDoubleClick(object sender, MouseButtonEventArgs e) => await AddSelectedAsync();

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await AddSelectedAsync();
        e.Handled = true;
    }

    private async Task AddSelectedAsync()
    {
        if (AppList.SelectedItem is not Row row) return;
        if (row.Owned)
        {
            SetHint($"{row.App.Name} 已在收藏中");
            return;
        }

        // 添加期间锁住：防手快双击两条进同一 pending 状态
        await AddGate.WaitAsync();
        try
        {
            var favs = _doc.Entries;
            var id = AppEntry.MakeId(row.App.Target);
            if (favs.Any(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                _ownedIds.Add(id);
                row.Owned = true;
                SetHint($"{row.App.Name} 已在收藏中");
                return;
            }

            var entry = new AppEntry
            {
                Id = id,
                Name = row.App.Name,
                Kind = row.App.Kind,
                Target = row.App.Target,
                Arguments = row.App.Arguments,
                WorkingDir = row.App.WorkingDir,
                Position = favs.Count == 0 ? 0 : favs.Max(f => f.Position) + 1,
            };

            try
            {
                // Extract 自带缓存命中短路
                entry.IconPath = (await Task.Run(() => IconCache.Extract(row.App.Target, 256))).CachePath;
            }
            catch (Exception ex)
            {
                entry.IconPath = null; // 图标失败不拦收藏，网格有占位字形
                SetHint($"图标没拿到（不影响添加）：{ex.Message}");
            }

            favs.Add(entry);
            MapStore.Save(_doc);
            _ownedIds.Add(entry.Id);
            Changed = true;
            row.Owned = true;
            SetHint($"已添加 {entry.Name}");
        }
        finally
        {
            AddGate.Release();
        }
    }

    private void SetHint(string text) => HintText.Text = text;
}
