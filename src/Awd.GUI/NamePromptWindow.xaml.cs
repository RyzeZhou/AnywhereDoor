using System.Windows;
using System.Windows.Input;

namespace Awd.GUI;

/// <summary>单行文本输入小对话框：新建分组 / 重命名共用。</summary>
public partial class NamePromptWindow : Window
{
    public string Value => Box.Text.Trim();

    public NamePromptWindow(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Box.Text = initial;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Accept();
        e.Handled = true;
    }

    private void Accept()
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }
}
