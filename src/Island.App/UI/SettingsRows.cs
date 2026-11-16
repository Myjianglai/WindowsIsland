using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;

namespace Island.App.UI;

internal static class SettingsRows
{
    public static readonly Brush Ink = Brush("#202938");
    public static readonly Brush Muted = Brush("#606B7D");
    public static readonly Brush Rule = Brush("#E3E6EC");
    public static readonly Brush Canvas = Brush("#F8F8FA");

    public static void Section(Panel panel, string title)
    {
        panel.Children.Add(new Border
        {
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 26, 0, 0),
            Padding = new Thickness(0, 0, 0, 8), BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock { Text = title, Foreground = Muted, FontSize = 12 }
        });
    }

    public static void StyleToggle(CheckBox check, string label)
    {
        check.Content = null;
        check.SetResourceReference(FrameworkElement.StyleProperty, "SettingsToggle");
        AutomationProperties.SetName(check, label);
    }

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
            StyleToggle(check, title.Text);
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

    public static Border Choices(int selected, System.Action<int> choose, params object[] labels)
    {
        var group = new UniformGrid { Rows = 1 };
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var button = new RadioButton { Content = labels[i], IsChecked = i == selected,
                HorizontalContentAlignment = HorizontalAlignment.Center };
            button.SetResourceReference(FrameworkElement.StyleProperty, "SettingsChoice");
            button.Checked += (_, _) => choose(index);
            group.Children.Add(button);
        }
        return new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3),
            Background = Brush("#ECEEF2"), Child = group };
    }

    public static void SelectChoice(Border choices, int index) =>
        ((RadioButton)((UniformGrid)choices.Child).Children[index]).IsChecked = true;

    public static Button ResetButton(System.Action reset)
    {
        var button = new Button { Content = "恢复默认", Background = Brushes.Transparent, Foreground = Muted,
            BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4) };
        button.Click += (_, _) => reset();
        return button;
    }
}
