using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.UI;

internal static class QuotaDisplay
{
    public static IEnumerable<QuotaMetric?> RingsFor(QuotaAccountStatus account, int count)
    {
        var metrics = account.Metrics ?? [];
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var keys = account.RingKeys ?? [];
        var reserved = keys.Take(count).Where(key => !string.IsNullOrWhiteSpace(key) && key != "-").ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            var key = keys.ElementAtOrDefault(index);
            if (key == "-") continue;
            var metric = string.IsNullOrWhiteSpace(key)
                ? metrics.FirstOrDefault(item => !selected.Contains(item.Key) && !reserved.Contains(item.Key))
                : metrics.FirstOrDefault(item => item.Key == key);
            if (metric is not null && selected.Add(metric.Key))
                yield return metric;
            else if (!string.IsNullOrWhiteSpace(key))
                yield return null;
        }
    }

    public static FrameworkElement Badge(QuotaAccountStatus account, IslandPreferences settings)
    {
        var rings = RingsFor(account, settings.MultipleRings ? 3 : 1).ToArray();
        var caption = $"{account.Service} · {account.Name}";
        if (account.Metrics is null || account.Metrics.Count == 0 || rings.Length == 0)
        {
            var label = account.Metrics is { Count: > 0 } ? account.Name : $"{account.Name} {account.Summary}";
            var text = ExpandedTabs.Label(label, 10, IslandTheme.Account(account.Color));
            text.Margin = new Thickness(4, 0, 4, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.MaxWidth = 72;
            text.ToolTip = caption;
            return text;
        }

        var circle = new Canvas { Width = 34, Height = 34, Margin = new Thickness(1, 0, 1, 0), ToolTip = caption };
        var summary = new List<string> { caption };
        const double center = 17;
        for (int ring = 0; ring < rings.Length; ring++)
        {
            double radius = 14 - 3.6 * ring;
            var track = new Ellipse { Width = radius * 2, Height = radius * 2, StrokeThickness = 2 };
            track.SetResourceReference(Shape.StrokeProperty, IslandTheme.Edge);
            Canvas.SetLeft(track, center - radius);
            Canvas.SetTop(track, center - radius);
            circle.Children.Add(track);
            var metric = rings[ring];
            var progress = Value(metric?.Used, settings.ShowRemaining);
            summary.Add($"{metric?.Label ?? "所选额度"} · {Describe(progress, settings.ShowRemaining)}");
            if (progress is > 0)
            {
                var stroke = new Path
                {
                    Data = Arc(center, radius, Math.Clamp(progress.Value / 100, 0, 1)),
                    StrokeThickness = 2.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                stroke.SetResourceReference(Shape.StrokeProperty, IslandTheme.Account(account.Color));
                stroke.Opacity = 1 - ring * .20;
                circle.Children.Add(stroke);
            }
        }
        circle.ToolTip = string.Join(Environment.NewLine, summary);
        if (settings.RingInitial && account.Name.Length > 0)
        {
            var initial = StringInfo.GetNextTextElement(account.Name);
            var name = ExpandedTabs.Label(initial.ToUpperInvariant(), 9);
            name.Width = name.Height = 16;
            name.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(name, 9);
            Canvas.SetTop(name, 10);
            circle.Children.Add(name);
        }
        return circle;
    }

    public static double? Value(double? used, bool remaining) => used is { } number
        ? (remaining ? Math.Clamp(100 - number, 0, 100) : Math.Max(0, number)) : null;

    public static string Describe(double? value, bool remaining) =>
        value is { } number ? $"{(remaining ? "剩余" : "已用")} {number:0.#}%" : "额度未知";

    internal static Geometry Arc(double center, double radius, double fraction)
    {
        if (fraction >= .9999)
            return new EllipseGeometry(new Point(center, center), radius, radius);
        var angle = 2 * Math.PI * fraction;
        var result = new StreamGeometry();
        using (var path = result.Open())
        {
            path.BeginFigure(new Point(center, center - radius), false, false);
            path.ArcTo(new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, fraction > .5, SweepDirection.Clockwise, true, false);
        }
        result.Freeze();
        return result;
    }
}
