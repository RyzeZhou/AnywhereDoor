using System.IO;
using System.Windows;
using Awd.Core;

namespace Awd.GUI;

/// <summary>
/// 编辑任意一条收藏：名称/分组/目标通用；参数与工作目录只对 exe/lnk 有意义。
/// 目标被改动时重算 Id 并作废图标缓存（下轮重新提取）；AUMID（Uwp）只读。
/// </summary>
public partial class EditEntryWindow : Window
{
    private sealed record GroupOpt(string? Value, string Label);

    private readonly AppEntry _entry;
    private readonly Func<string, bool> _idTaken;

    public bool Changed { get; private set; }

    public EditEntryWindow(AppEntry entry, Func<string, bool> idTaken, IEnumerable<string> groups)
    {
        InitializeComponent();
        _entry = entry;
        _idTaken = idTaken;

        NameBox.Text = entry.Name;
        TargetBox.Text = entry.Target;
        ArgsBox.Text = entry.Arguments ?? "";
        WorkdirBox.Text = entry.WorkingDir ?? "";

        // 分组下拉：未分类 + 本页全部分组（登记的 ∪ 数据里出现的 ∪ 当前条目所在）
        var opts = new List<GroupOpt> { new(null, "（未分类）") };
        foreach (var g in groups.Distinct().OrderBy(g => g, StringComparer.CurrentCulture))
            opts.Add(new GroupOpt(g, g));
        GroupBox.ItemsSource = opts;
        GroupBox.SelectedValue = entry.Group ?? "";

        if (entry.Kind == AppKind.Uwp)
        {
            TargetBox.IsReadOnly = true;
            TargetBox.ToolTip = "AUMID 由系统清单决定，不可手改";
        }
        if (entry.Kind is not (AppKind.Exe or AppKind.Lnk))
        {
            ArgsLabel.Visibility = Visibility.Collapsed;
            ArgsBox.Visibility = Visibility.Collapsed;
            WorkdirLabel.Visibility = Visibility.Collapsed;
            WorkdirBox.Visibility = Visibility.Collapsed;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var target = TargetBox.Text.Trim();
        if (name.Length == 0) { ShowError("名称不能为空"); return; }
        if (target.Length == 0) { ShowError("目标不能为空"); return; }

        var newId = AppEntry.MakeId(target);
        if (newId != _entry.Id && _idTaken(newId)) { ShowError("同页已有这个目标的收藏"); return; }

        bool targetChanged = !target.Equals(_entry.Target, StringComparison.OrdinalIgnoreCase);
        _entry.Name = name;
        _entry.Target = target;
        _entry.Group = GroupBox.SelectedValue as string;
        if (ArgsBox.Visibility == Visibility.Visible)
            _entry.Arguments = ArgsBox.Text;
        if (WorkdirBox.Visibility == Visibility.Visible)
            _entry.WorkingDir = WorkdirBox.Text.Trim().Length > 0 ? WorkdirBox.Text.Trim() : null;
        if (targetChanged)
        {
            _entry.Id = newId;
            _entry.IconPath = null; // 目标变了，旧缓存图作废，重载时重新提取
        }
        Changed = true;
        DialogResult = true;
    }

    private void ShowError(string text) => ErrorText.Text = text;
}
