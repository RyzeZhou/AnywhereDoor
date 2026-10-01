using System.Windows;
using System.Windows.Media;

namespace Awd.GUI;

public partial class App : Application
{
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
    }
}
