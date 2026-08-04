using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BCSTool.Services;

namespace BCSTool.Controls;

/// <summary>
/// Lightweight WPF renderer for the styled ConPTY terminal screen.
///
/// Unlike TextBox/TextBlock, this control draws each ANSI style run at an exact
/// monospace cell position. That preserves terminal geometry while allowing
/// different colors inside the same line.
///
/// It intentionally remains non-scrollable: v1.4 adaptive ConPTY sizing makes
/// the pseudo console itself match this visible viewport.
/// </summary>
public sealed class TerminalDisplayControl : Control
{
    private readonly Dictionary<uint, SolidColorBrush> _brushCache =
        new();

    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(
            nameof(Snapshot),
            typeof(TerminalScreenSnapshot),
            typeof(TerminalDisplayControl),
            new FrameworkPropertyMetadata(
                TerminalScreenSnapshot.Empty,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public TerminalScreenSnapshot Snapshot
    {
        get =>
            (TerminalScreenSnapshot)GetValue(
                SnapshotProperty);

        set =>
            SetValue(
                SnapshotProperty,
                value);
    }


    public TerminalDisplayControl()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }


    protected override void OnRender(
        DrawingContext drawingContext)
    {
        base.OnRender(
            drawingContext);

        drawingContext.DrawRectangle(
            Background ?? Brushes.Black,
            null,
            new Rect(
                0,
                0,
                ActualWidth,
                ActualHeight));

        var snapshot =
            Snapshot;

        if (
            snapshot is null ||
            snapshot.Lines.Count == 0)
        {
            return;
        }

        var dpi =
            VisualTreeHelper.GetDpi(
                this);

        var normalTypeface =
            new Typeface(
                FontFamily,
                FontStyle,
                FontWeights.Normal,
                FontStretch);

        var boldTypeface =
            new Typeface(
                FontFamily,
                FontStyle,
                FontWeights.Bold,
                FontStretch);

        // Measuring "M" mirrors MainWindow's viewport-to-grid calculation.
        var sample =
            new FormattedText(
                "M",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                normalTypeface,
                FontSize,
                Foreground ?? Brushes.White,
                dpi.PixelsPerDip);

        var cellWidth =
            Math.Max(
                1.0,
                sample.WidthIncludingTrailingWhitespace);

        var cellHeight =
            Math.Max(
                1.0,
                sample.Height);

        drawingContext.PushClip(
            new RectangleGeometry(
                new Rect(
                    0,
                    0,
                    ActualWidth,
                    ActualHeight)));

        try
        {
            var y =
                Padding.Top;

            foreach (
                var line in snapshot.Lines)
            {
                if (
                    y + cellHeight >
                    ActualHeight - Padding.Bottom)
                {
                    break;
                }

                var x =
                    Padding.Left;

                foreach (
                    var run in line.Runs)
                {
                    if (run.Text.Length == 0)
                        continue;

                    var brush =
                        ResolveForeground(
                            run.Style.Foreground);

                    var typeface =
                        run.Style.Bold
                            ? boldTypeface
                            : normalTypeface;

                    var formatted =
                        new FormattedText(
                            run.Text,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            FontSize,
                            brush,
                            dpi.PixelsPerDip);

                    drawingContext.DrawText(
                        formatted,
                        new Point(
                            x,
                            y));

                    // Advance by terminal cells rather than FormattedText's
                    // measured width. This keeps ANSI run boundaries from
                    // accumulating sub-pixel measurement drift.
                    x +=
                        run.Text.Length *
                        cellWidth;
                }

                y +=
                    cellHeight;
            }
        }
        finally
        {
            drawingContext.Pop();
        }
    }


    private Brush ResolveForeground(
        TerminalColor color)
    {
        if (color.IsDefault)
        {
            return
                Foreground ??
                Brushes.White;
        }

        var key =
            ((uint)color.R << 16) |
            ((uint)color.G << 8) |
            color.B;

        if (
            _brushCache.TryGetValue(
                key,
                out var cached))
        {
            return cached;
        }

        var brush =
            new SolidColorBrush(
                Color.FromRgb(
                    color.R,
                    color.G,
                    color.B));

        brush.Freeze();

        _brushCache[key] =
            brush;

        return brush;
    }
}
