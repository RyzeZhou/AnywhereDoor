using System.Windows;

namespace Awd.GUI;

/// <summary>侧栏落点态：拖拽悬停时"松手就归到这一项"的那一项为 true，样式据此染底。
/// 不可归的项（「全部」、列表空白）保持 false —— 用"不亮"表达拒绝。</summary>
public static class SideDropTarget
{
    public static readonly DependencyProperty IsHotProperty =
        DependencyProperty.RegisterAttached("IsHot", typeof(bool), typeof(SideDropTarget),
            new PropertyMetadata(false));

    public static void SetIsHot(DependencyObject element, bool value) => element.SetValue(IsHotProperty, value);

    public static bool GetIsHot(DependencyObject element) => (bool)element.GetValue(IsHotProperty);
}
