using System.Windows;
using System.Windows.Media;

namespace OptLife.Panel;

/// <summary>A bare line chart: no axes, no labels, just the shape of the last hour.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 2 || ActualHeight <= 2) return;

        var (min, max) = (values.Min(), values.Max());
        // A nearly flat series must look flat: never stretch tiny wobbles to the full height.
        var span = Math.Max(max - min, Math.Max(1, Math.Abs(max) * 0.1));
        var mid = (max + min) / 2;
        var (low, high) = (mid - span / 2, mid + span / 2);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = 1 + (ActualWidth - 2) * i / (values.Count - 1);
                var y = 1 + (ActualHeight - 2) * (1 - (values[i] - low) / (high - low));
                if (i == 0) g.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                else g.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.25), geometry);
    }
}
