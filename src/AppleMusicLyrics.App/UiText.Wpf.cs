using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AppleMusicLyrics.App;

public static partial class UiText
{
    public static void Localize(DependencyObject root)
    {
        if (!IsChinese) return;
        var visited = new HashSet<DependencyObject>();
        void Walk(DependencyObject node)
        {
            if (!visited.Add(node)) return;
            if (node is Window window) window.Title = T(window.Title);
            if (node is TextBlock text && text.Inlines.Count <= 1
                && !BindingOperations.IsDataBound(text, TextBlock.TextProperty))
            {
                var translated = T(text.Text);
                if (translated != text.Text) text.Text = translated;
            }
            if (node is ContentControl content && content.Content is string label) content.Content = T(label);
            if (node is HeaderedContentControl header && header.Header is string title) header.Header = T(title);
            if (node is FrameworkElement { ToolTip: string tip } element) element.ToolTip = T(tip);
            // Logical children are available before layout, including hidden edit controls.
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToArray()) Walk(child);
        }
        Walk(root);
    }
}
