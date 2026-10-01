using System.Windows;
using Awd.Core;

namespace Awd.GUI;

public partial class SettingsWindow : Window
{
    /// <summary>保存后的设置；主窗口据此热更新点击行为（无需重启）。</summary>
    public SettingsStore.Settings Result { get; private set; }

    /// <summary>地图在设置里被改名了：非 null 即新名，主窗口据此刷新。</summary>
    public string? RenamedMapTo { get; private set; }

    private readonly string _activeMap;

    public SettingsWindow(SettingsStore.Settings current, string activeMapName)
    {
        InitializeComponent();
        _activeMap = activeMapName;
        Result = new SettingsStore.Settings { DoubleClickOpen = current.DoubleClickOpen };
        RbSingle.IsChecked = !current.DoubleClickOpen;
        RbDouble.IsChecked = current.DoubleClickOpen;
        MapNameText.Text = activeMapName;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (RbDouble == null) return; // InitializeComponent 期间的首次赋值
        Result.DoubleClickOpen = RbDouble.IsChecked == true;
    }

    private void OnRenameMap(object sender, RoutedEventArgs e)
    {
        var dlg = new NamePromptWindow("重命名地图", $"把地图「{_activeMap}」改名为：", _activeMap);
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value;
        if (name == _activeMap) return;
        try
        {
            MapStore.Rename(_activeMap, name); // 内部会把 ActiveMap 指针一起改
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"改名失败：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MapNameText.Text = name;
        RenamedMapTo = name;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(Result);
        DialogResult = true;
    }
}
