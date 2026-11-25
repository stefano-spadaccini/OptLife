using System.Windows;
using System.Windows.Controls;

namespace OptLife.Panel;

/// <summary>
/// Two children: the main content and what goes beside it (state words, buttons). Side by side while the main part keeps
/// at least <see cref="MinMain"/> of room; below it, indented by <see cref="BelowIndent"/>, when the page is narrower
/// (a small window, a big text size). Nothing ever overlaps or gets cut.
/// </summary>
public sealed class SideOrBelow : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty MinMainProperty = DependencyProperty.Register(nameof(MinMain), typeof(double), typeof(SideOrBelow),
        new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(SideOrBelow),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BelowIndentProperty = DependencyProperty.Register(nameof(BelowIndent), typeof(double), typeof(SideOrBelow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The least width the main part needs before the side part moves below it.</summary>
    public double MinMain { get => (double)GetValue(MinMainProperty); set => SetValue(MinMainProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double BelowIndent { get => (double)GetValue(BelowIndentProperty); set => SetValue(BelowIndentProperty, value); }

    /// <summary>Whether the side part is under the main one right now (for tests and styles).</summary>
    public bool IsStacked { get; private set; }

    private UIElement? Main => InternalChildren.Count > 0 ? InternalChildren[0] : null;
    private UIElement? Side => InternalChildren.Count > 1 ? InternalChildren[1] : null;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
        var side = Side;
        var sideSize = new Size();
        if (side is not null)
        {
            side.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sideSize = side.Visibility == Visibility.Collapsed ? new Size() : side.DesiredSize;
        }
        var hasSide = sideSize.Width > 0;
        IsStacked = hasSide && width - sideSize.Width - Gap < MinMain;
        var mainWidth = !hasSide ? width : IsStacked ? width : width - sideSize.Width - Gap;
        Main?.Measure(new Size(Math.Max(0, mainWidth), available.Height));
        var mainSize = Main?.DesiredSize ?? new Size();
        if (IsStacked)
        {
            // Below: the side part may wrap within the width it has.
            side!.Measure(new Size(Math.Max(0, width - BelowIndent), double.PositiveInfinity));
            var below = side.DesiredSize;
            return new Size(Math.Min(width, Math.Max(mainSize.Width, below.Width + BelowIndent)), mainSize.Height + (below.Height > 0 ? 8 + below.Height : 0));
        }
        return new Size(Math.Min(width, mainSize.Width + (hasSide ? Gap + sideSize.Width : 0)), Math.Max(mainSize.Height, sideSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var side = Side;
        var sideSize = side?.DesiredSize ?? new Size();
        if (IsStacked && side is not null)
        {
            var mainHeight = Main?.DesiredSize.Height ?? 0;
            Main?.Arrange(new Rect(0, 0, final.Width, mainHeight));
            side.Arrange(new Rect(BelowIndent, mainHeight + 8, Math.Max(0, final.Width - BelowIndent), sideSize.Height));
            return final;
        }
        var sideWidth = sideSize.Width;
        Main?.Arrange(new Rect(0, 0, Math.Max(0, final.Width - (sideWidth > 0 ? sideWidth + Gap : 0)), final.Height));
        side?.Arrange(new Rect(final.Width - sideWidth, 0, sideWidth, final.Height));
        return final;
    }
}
