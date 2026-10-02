using System.Windows;
using System.Windows.Media;
using Awd.Core;

namespace Awd.GUI;

public partial class App : Application
{
    /// <summary>启动时"活动地图坏了、临时换了另一张"要告诉用户的话，主窗口拿它写状态条。</summary>
    public static string StartupNote { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 跟随系统强调色（个性化颜色）；DWM 没给就用系列默认蓝。
        // 只在启动时读一次 —— 运行中改主题色的场景不值得为它挂系统事件。
        var c = SystemParameters.WindowGlassColor;
        if (c.A > 0 && (c.R > 0 || c.G > 0 || c.B > 0))
        {
            var rgb = Color.FromRgb(c.R, c.G, c.B);
            var dict = Resources.MergedDictionaries[0];
            dict["Brush.Accent"] = new SolidColorBrush(rgb);
            dict["Brush.Selection"] = new SolidColorBrush(Color.FromArgb(0x24, rgb.R, rgb.G, rgb.B));
            dict["Brush.Card.Hover"] = new SolidColorBrush(Color.FromArgb(0x0C, rgb.R, rgb.G, rgb.B));
            dict["Brush.Card.Border.Hover"] = new SolidColorBrush(Color.FromArgb(0x9C, rgb.R, rgb.G, rgb.B));
        }

        // 窗口没建之前先把地图落实：以前 _doc 是 MainWindow 的字段初始化器，
        // 活动地图坏了就整条构造链抛出去 —— 双击启动的人看到的是"点了没反应"，
        // 只有从控制台起才看得见那段栈。VM 黑屏那晚真就把一批 .awdmap 变成了全零文件。
        if (!MapStore.TryOpenActive(out var doc, out var mapPath, out var note))
        {
            MessageBox.Show(note + $"\n\n想手工抢救：把坏文件改名留档，或从 {MapStore.DataDir} 下的 .pre-map 备份重新迁移。",
                "任意门打不开", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        StartupNote = note;
        new MainWindow(doc!, mapPath).Show();
    }
}
