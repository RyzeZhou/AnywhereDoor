using System.Windows;

namespace Awd.GUI;

public partial class SettingsWindow : Window
{
    /// <summary>保存后的设置；主窗口据此热更新点击行为（无需重启）。</summary>
    public AppSettingsStore.Settings Result { get; private set; }

    public SettingsWindow(AppSettingsStore.Settings current)
    {
        InitializeComponent();
        Result = new AppSettingsStore.Settings { DoubleClickOpen = current.DoubleClickOpen };
        RbSingle.IsChecked = !current.DoubleClickOpen;
        RbDouble.IsChecked = current.DoubleClickOpen;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (RbDouble == null) return; // InitializeComponent 期间的首次赋值
        Result.DoubleClickOpen = RbDouble.IsChecked == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        AppSettingsStore.Save(Result);
        DialogResult = true;
    }
}
