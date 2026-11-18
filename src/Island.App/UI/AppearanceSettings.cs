using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Island.App.Persistence;

namespace Island.App.UI;

internal static class AppearanceSettings
{
    public static UniformGrid ShapeCards(IslandShape[] shapes, IslandShape selected, Action<IslandShape> select)
    {
        var cards = new UniformGrid { Rows = 1, Margin = new Thickness(-5, 8, -5, 0) };
        foreach (var shape in shapes)
        {
            var label = shape switch { IslandShape.Notch => "刘海", IslandShape.Capsule => "胶囊", _ => "指示条" };
            var button = new RadioButton
            {
                IsChecked = shape == selected, Margin = new Thickness(5, 0, 5, 0),
                Content = new StackPanel
                {
                    Children =
                    {
                        Preview(shape),
                        new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 3) }
                    }
                }
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "SettingsChoice");
            button.Padding = new Thickness(3);
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(button, label);
            button.Checked += (_, _) => select(shape);
            cards.Children.Add(button);
        }
        return cards;
    }

    private static FrameworkElement Preview(IslandShape shape)
    {
        var desktop = new Canvas { Width = 230, Height = 112, Background = SettingsRows.Brush("#CDDEEA"), ClipToBounds = true };
        var wallpaper = new Path
        {
            Fill = SettingsRows.Brush("#B5CBDC"),
            Data = Geometry.Parse("M0,104 C52,60 93,103 130,51 C162,10 208,21 230,0 L230,112 L0,112 Z")
        };
        desktop.Children.Add(wallpaper);
        var mockWindow = new Border
        {
            Width = 130, Height = 75, CornerRadius = new CornerRadius(8),
            Background = SettingsRows.Brush("#F1F5F9"), Padding = new Thickness(10),
            Child = new StackPanel
            {
                Children = { PreviewLine(36), PreviewLine(72), PreviewLine(54) }
            }
        };
        Canvas.SetLeft(mockWindow, 99);
        Canvas.SetTop(mockWindow, 62);
        desktop.Children.Add(mockWindow);
        var indicator = shape == IslandShape.Indicator;
        var island = new Border
        {
            Width = indicator ? 33 : 88, Height = indicator ? 3 : 15,
            CornerRadius = shape == IslandShape.Notch ? new CornerRadius(0, 0, 6, 6) : new CornerRadius(8),
            Background = SettingsRows.Brush(indicator ? "#84939E" : "#101216")
        };
        if (!indicator)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var color in new[] { "#EBAC80", "#9BD9AF", "#A6BCF8" })
                dots.Children.Add(new Ellipse { Width = 5, Height = 5, Stroke = SettingsRows.Brush(color),
                    Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
            island.Child = dots;
        }
        Canvas.SetLeft(island, (230 - island.Width) / 2);
        Canvas.SetTop(island, shape == IslandShape.Notch ? 0 : 5);
        desktop.Children.Add(island);
        return new Border
        {
            Background = desktop.Background, CornerRadius = new CornerRadius(8),
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = desktop }
        };
    }

    private static Border PreviewLine(double width) => new()
    {
        Width = width, Height = 3, HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(2),
        Background = SettingsRows.Brush("#BDC9D5")
    };

    public static Border OrderRow<T>(string title, CheckBox value, T item, ObservableCollection<T> order,
        Action refresh, Func<bool>? canReorder = null) where T : struct, Enum
    {
        var toggle = new CheckBox();
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked))
        {
            Source = value, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        var row = SettingsRows.Row(title, toggle);
        var columns = (Grid)row.Child;
        columns.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = new GridLength(34) });
        Grid.SetColumn(columns.Children[0], 1);
        Grid.SetColumn(toggle, 2);
        var grip = new Button
        {
            Content = "⠿", Width = 24, Padding = new Thickness(0), Foreground = SettingsRows.Muted,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.SizeAll,
            ToolTip = "拖动排序，也可按 Alt + ↑ / ↓ 移动"
        };
        AutomationProperties.SetName(grip, $"调整{title}顺序");
        columns.Children.Add(grip);
        row.Background = Brushes.Transparent;
        bool Allowed() => canReorder?.Invoke() ?? true;
        void Move(int from, int to)
        {
            if (from < 0 || to < 0 || to >= order.Count || from == to) return;
            order.Move(from, to);
            refresh();
        }
        AnimatedRowDrag.Attach(row, grip, Move, SettingsRows.Brush("#286FE1"), Allowed);
        grip.PreviewKeyDown += (_, e) =>
        {
            if (!Allowed() || Keyboard.Modifiers != ModifierKeys.Alt) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is not (Key.Up or Key.Down)) return;
            int from = order.IndexOf(item);
            var panel = row.Parent as Panel;
            Move(from, from + (key == Key.Up ? -1 : 1));
            if (panel?.Children[order.IndexOf(item)] is Border moved &&
                moved.Child is Grid movedColumns && movedColumns.Children[movedColumns.Children.Count - 1] is Button movedGrip)
                movedGrip.Focus();
            e.Handled = true;
        };
        return row;
    }

    public static Slider Number(Panel panel, string label, int min, int max, int value, string unit = "%")
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1,
            IsSnapToTickEnabled = true, MinWidth = 105, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, label);
        var number = new TextBlock { Width = 60, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        number.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) { Source = slider, StringFormat = "{0:0}" + unit });
        var controls = new DockPanel { Width = 250, LastChildFill = true };
        DockPanel.SetDock(number, Dock.Right);
        controls.Children.Add(number);
        controls.Children.Add(slider);
        panel.Children.Add(SettingsRows.Row(label, controls));
        return slider;
    }
}
