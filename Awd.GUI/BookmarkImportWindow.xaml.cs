using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Awd.Core;

namespace Awd.GUI;

/// <summary>
/// 导入浏览器书签：来源（Chrome/Edge 直读、导出的 HTML 文件）→ 文件夹勾选 → 批量生成网站收藏。
/// 与既有收藏去重由主窗口在落库时统一做（这里只按 URL 批内去重）。
/// </summary>
public partial class BookmarkImportWindow : Window
{
    private sealed class SourceItem
    {
        public SourceItem(string label, string? path) { Label = label; Path = path; }
        public string Label { get; }
        public string? Path { get; }
        public override string ToString() => Label;
    }

    private sealed class FolderVm : INotifyPropertyChanged
    {
        private bool _checked;
        public FolderVm(BookmarkReader.Folder folder) { Folder = folder; _checked = true; }
        public BookmarkReader.Folder Folder { get; }
        public string CountText => $"{Folder.Items.Count} 个";
        public bool Checked
        {
            get => _checked;
            set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly HashSet<string> _existingTargets;
    private List<BookmarkReader.Folder> _folders = new();

    /// <summary>导入结果（批内已去重、已剔除既有收藏）；主窗口负责落库。</summary>
    public List<AppEntry> Result { get; private set; } = new();

    public BookmarkImportWindow(HashSet<string> existingTargets)
    {
        InitializeComponent();
        _existingTargets = existingTargets;

        var sources = new List<SourceItem>();
        foreach (var (label, path) in BookmarkReader.Detect())
            sources.Add(new SourceItem(label, path));
        sources.Add(new SourceItem("导出的书签 HTML 文件…", null));
        SourceBox.ItemsSource = sources;
        if (sources.Count > 1) SourceBox.SelectedIndex = 0; // 有探测结果就先读第一个
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is not SourceItem s) return;
        var path = s.Path;
        if (path == null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择书签 HTML 导出文件",
                Filter = "书签 HTML|*.html;*.htm|所有文件|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        Load(path);
    }

    private void Load(string path)
    {
        try
        {
            List<BookmarkReader.Folder> folders;
            try
            {
                folders = BookmarkReader.FromChromeJson(path); // Chrome/Edge 的 Bookmarks 是 JSON
            }
            catch (JsonException)
            {
                folders = BookmarkReader.FromHtml(path);       // 否则按 Netscape HTML 解
            }
            _folders = folders;
            FolderList.ItemsSource = folders.Select(f => new FolderVm(f)).ToList();
            var total = folders.Sum(f => f.Items.Count);
            SummaryText.Text = $"{path}\n{folders.Count} 个文件夹，共 {total} 个书签";
        }
        catch (Exception ex)
        {
            _folders = new List<BookmarkReader.Folder>();
            FolderList.ItemsSource = null;
            SummaryText.Text = $"读取失败：{ex.Message}";
        }
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (FolderList.ItemsSource is not IEnumerable<FolderVm> vms) return;
        var count = vms.Where(v => v.Checked).Sum(v => v.Folder.Items.Count);
        ImportHint.Text = $"将导入 {count} 个书签（已收藏的会自动跳过）";
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (FolderList.ItemsSource is not IEnumerable<FolderVm> vms) return;
        var result = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vm in vms.Where(v => v.Checked))
        {
            foreach (var item in vm.Folder.Items)
            {
                if (item.Url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(item.Url)) continue;                       // 批内去重
                if (_existingTargets.Contains(item.Url.ToLowerInvariant())) continue; // 已收藏
                result.Add(new AppEntry
                {
                    Id = AppEntry.MakeId(item.Url),
                    Name = item.Name,
                    Kind = AppKind.Web,
                    Page = PageKind.Web,
                    Target = item.Url,
                    Group = vm.Folder.Path, // 记住来源书签文件夹，侧栏按它分类
                });
            }
        }
        Result = result;
        DialogResult = true;
    }
}
