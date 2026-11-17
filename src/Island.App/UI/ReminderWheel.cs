using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Island.App.UI;

internal sealed class ReminderWheel : Grid
{
    private const int StepHeight = 19;
    private readonly TextBlock[] _labels = new TextBlock[3];
    private readonly Button[] _rows = new Button[3];
    private readonly int _count;
    private readonly bool _wrap;
    private readonly Func<int, string> _caption;
    private Point _origin;
    private bool _pressed;

    public int SelectedIndex { get; private set; }

    public ReminderWheel(int count, int selected, Func<int, string> caption, string name, bool wrap)
    {
        _count = count;
        _wrap = wrap;
        _caption = caption;
        SelectedIndex = selected;
        Focusable = true;
        Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(StepHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(StepHeight) });
        for (int row = 0; row < _rows.Length; row++)
        {
            int offset = row - 1;
            var label = ExpandedTabs.Label("", row == 1 ? 17 : 13, row == 1 ? IslandTheme.Ink : IslandTheme.Muted);
            label.FontWeight = row == 1 ? FontWeights.SemiBold : FontWeights.Normal;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            _labels[row] = label;
            var button = ExpandedTabs.MakeButton("");
            button.Content = label;
            button.Background = Brushes.Transparent;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.Click += (_, _) => { Focus(); Move(offset); };
            _rows[row] = button;
            SetRow(button, row);
            Children.Add(button);
        }
        PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta == 0) return;
            Move(-Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120));
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            Move(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        };
        PreviewMouseLeftButtonDown += (_, e) => { _origin = e.GetPosition(this); _pressed = true; };
        PreviewMouseMove += (_, e) =>
        {
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(this);
            int steps = (int)((_origin.Y - position.Y) / StepHeight);
            if (steps == 0) return;
            if (!IsMouseCaptured && !CaptureMouse()) return;
            _origin.Y -= steps * StepHeight;
            Move(steps);
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            _pressed = false;
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            e.Handled = true;
        };
        Refresh();
    }

    private void Move(int steps)
    {
        SelectedIndex = IndexAt(steps);
        Refresh();
    }

    private int IndexAt(int offset) => _wrap ? ((SelectedIndex + offset) % _count + _count) % _count
        : Math.Clamp(SelectedIndex + offset, 0, _count - 1);

    private void Refresh()
    {
        for (int row = 0; row < _rows.Length; row++)
        {
            int offset = row - 1;
            int index = IndexAt(offset);
            bool visible = offset == 0 || index != SelectedIndex;
            _rows[row].Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            _labels[row].Text = visible ? _caption(index) : "";
        }
    }
}
