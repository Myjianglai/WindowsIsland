using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Island.App.UI;

internal static class SettingsRows
{
    public static readonly Brush Ink = Brush("#202938");
    public static readonly Brush Muted = Brush("#606B7D");
    public static readonly Brush Rule = Brush("#E3E6EC");
    public static readonly Brush Canvas = Brush("#F8F9FB");

    public static Border Row(string label, FrameworkElement control)
    {
        return Row(new TextBlock { Text = label }, control);
    }

    public static Border Row(TextBlock title, FrameworkElement control)
    {
        title.FontSize = 14;
        title.Foreground = Ink;
        title.Margin = new Thickness(0, 0, 14, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextWrapping = TextWrapping.Wrap;
        control.Margin = new Thickness(0);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is CheckBox check)
        {
            check.Content = null;
            check.SetResourceReference(FrameworkElement.StyleProperty, "SettingsToggle");
        }
        if (control is ComboBox or TextBox or PasswordBox)
        {
            control.MinWidth = 165;
            control.MaxWidth = 280;
        }
        if (control is DockPanel)
            control.Width = 250;
        if (control is Slider)
            control.Width = 220;
        System.Windows.Automation.AutomationProperties.SetName(control, title.Text);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(control, 1);
        columns.Children.Add(title);
        columns.Children.Add(control);
        var row = new Border { BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1),
            MinHeight = 46, Padding = new Thickness(0, 6, 0, 6), Child = columns };
        row.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding(nameof(UIElement.Visibility)) { Source = title });
        return row;
    }

    public static Brush Brush(string value)
    {
        var result = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        result.Freeze();
        return result;
    }
}
