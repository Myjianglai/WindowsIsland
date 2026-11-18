using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Island.App.UI;

internal static class AnimatedRowDrag
{
    private const string Format = "WindowsIsland.RowOrder";
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(150);

    public static void Attach(Border row, Button grip, Action<int, int> move, Brush marker, Func<bool>? enabled = null)
    {
        Point start = default;
        grip.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(grip);
        grip.PreviewMouseMove += (_, e) =>
        {
            if (!grip.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed ||
                enabled?.Invoke() == false || row.Parent is not Panel panel) return;
            var delta = e.GetPosition(grip) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            e.Handled = true;
            Drag(panel, row, grip, move, marker);
        };
    }

    private static void Drag(Panel panel, Border source, Button grip, Action<int, int> move, Brush marker)
    {
        var rows = panel.Children.OfType<Border>().ToArray();
        int from = Array.IndexOf(rows, source);
        if (from < 0 || rows.Length < 2) return;
        var offsets = rows.Select(_ => new TranslateTransform()).ToArray();
        var originalTransforms = rows.Select(row => row.RenderTransform).ToArray();
        for (int i = 0; i < rows.Length; i++) rows[i].RenderTransform = offsets[i];
        var data = new DataObject(Format, true);
        var layer = AdornerLayer.GetAdornerLayer(panel);
        var indicator = new DropIndicator(panel, marker) { IsHitTestVisible = false };
        layer?.Add(indicator);
        int target = from;
        bool dropped = false;
        bool allowedDrop = panel.AllowDrop;
        var background = panel.Background;
        double opacity = source.Opacity;
        int zIndex = Panel.GetZIndex(source);
        source.Opacity = 0.65;
        Panel.SetZIndex(source, zIndex + 1);
        panel.AllowDrop = true;
        if (background is null) panel.SetCurrentValue(Panel.BackgroundProperty, Brushes.Transparent);

        int Position(Point point)
        {
            for (int i = 0; i < rows.Length; i++)
                if (point.Y < LayoutInformation.GetLayoutSlot(rows[i]).Bottom) return i;
            return rows.Length - 1;
        }
        void Preview(int next)
        {
            if (next == target) return;
            target = next;
            var origin = LayoutInformation.GetLayoutSlot(source);
            var slot = LayoutInformation.GetLayoutSlot(rows[next]);
            for (int i = 0; i < rows.Length; i++)
            {
                double shift = i == from
                    ? next > from ? slot.Bottom - origin.Bottom : slot.Top - origin.Top
                    : from < i && i <= next ? -origin.Height
                    : next <= i && i < from ? origin.Height : 0;
                offsets[i].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(shift, Duration)
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
            indicator.Show(next == from ? null : next > from ? slot.Bottom : slot.Top);
        }
        DragEventHandler over = (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;
            Preview(Position(e.GetPosition(panel)));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        DragEventHandler drop = (_, e) =>
        {
            if (!e.Data.GetDataPresent(Format)) return;
            target = Position(e.GetPosition(panel));
            dropped = true;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        };
        panel.PreviewDragOver += over;
        panel.PreviewDrop += drop;
        try { DragDrop.DoDragDrop(grip, data, DragDropEffects.Move); }
        finally
        {
            panel.PreviewDragOver -= over;
            panel.PreviewDrop -= drop;
            panel.AllowDrop = allowedDrop;
            if (background is null) panel.SetCurrentValue(Panel.BackgroundProperty, null);
            source.Opacity = opacity;
            Panel.SetZIndex(source, zIndex);
            layer?.Remove(indicator);
            for (int i = 0; i < rows.Length; i++) rows[i].RenderTransform = originalTransforms[i];
        }
        if (dropped && from != target) move(from, target);
    }

    private sealed class DropIndicator(UIElement element, Brush brush) : Adorner(element)
    {
        private double? _y;
        public void Show(double? y)
        {
            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_y is not { } y || ActualWidth <= 8) return;
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(4, y - 2, ActualWidth - 8, 4), 2, 2);
        }
    }
}
