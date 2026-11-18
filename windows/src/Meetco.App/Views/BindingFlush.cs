using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetcoApp.Views;

/// <summary>Pushes pending text edits to their bindings (used before a window closes).</summary>
internal static class BindingFlush
{
    public static void UpdateSources(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            switch (child)
            {
                case TextBox textBox:
                    textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    break;
                case ComboBox { IsEditable: true } comboBox:
                    comboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                    break;
            }
            UpdateSources(child);
        }
    }
}
